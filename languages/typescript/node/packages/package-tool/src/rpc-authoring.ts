import { createHash } from "node:crypto";
import { realpath } from "node:fs/promises";
import { dirname, resolve as nativeResolve } from "node:path";
import ts from "typescript";
import { authoringBindingModel, canonicalRpcDescriptorBytes, computeRpcAbiSha256, generateTypeScriptBindings } from "@sunder/sdk";
import type { ContractUseConfig, LoadedContract, LoadedProject, ProviderConfig, PackageRole } from "./model";
import { readProjectDescriptorFile } from "./descriptor-files";
import { packageToolCompatibility } from "./compatibility";
import { captureRpcProducer } from "./rpc-producer";
import { captureOwnedRuntimeInputs, type OwnedRuntimeInputs } from "./rpc-runtime-inputs";
import { captureRpcCompilerInputs } from "./rpc-compiler-inputs";
import { assertBaselineFreeEvidence, captureBaseline, exists, type BaselineInput } from "./rpc-baseline-store";
import { emptyBaseline, validateBaselineSource, type OwnedBaseline } from "./rpc-baseline";
import { ownedAppUses } from "./rpc-owned-app";
import { ownedRuntimeUses, validateServiceReferenceRoleUses, coalesceRoleUses, importedRuntimeUses, roleUsesBytes, roleUsesOuter, validateRoleUseEvidence, type SourceRoleUse } from "./rpc-owned-uses";
import { parseContractLock, type ContractLock, type RegistryOriginOptions } from "./rpc-contract-lock";
import { DEFAULT_CONTRACT_CACHE } from "./rpc-contract-cache";
import { authority, contractSymbol, importedFacadeReceipt, importProjection, importReceiptArtifacts, preparePackageImports, importedContractsForRoles, type ImportedProviderBinding, type PackageImportAnalysis } from "./rpc-package-imports";
import { analyzeServices, pascal } from "./service-source";
import { serviceAdapterSource, serviceApiSource, serviceWorkerSource, serviceReferenceTypes } from "./service-bindings";
import { MODEL_PROJECTION_PATH, modelProjectionBytes, modelProjectionOuter, methodPoliciesBytes, methodPoliciesOuter, projectionDocumentBytes, validateModelProjectionSet } from "./rpc-model-projection";
import { outerBytes, type Outer } from "./rpc-baseline-json";

const resolve = (...paths: string[]): string => nativeResolve(...paths).replaceAll("\\", "/");
const producer = captureRpcProducer([__filename, require.resolve("@sunder/sdk"), require.resolve("typescript"), ...["./rpc-model-projection", "./rpc-model-projection-cache", "./rpc-contract-acquisition", "./service-source", "./service-bindings", "./model", "./module", "./compatibility", "./package", "./rpc-bindings", "./rpc-authoring-plugin", "./rpc-owned-app", "./output", "./notices", "./rpc-runtime-inputs", "./rpc-baseline", "./rpc-baseline-json", "./rpc-baseline-store", "./rpc-evolution", "./rpc-contracts", "./rpc-contract-lock", "./rpc-contract-cache", "./rpc-contract-cache-gc", "./rpc-contract-candidate", "./rpc-contract-restore", "./rpc-package-imports", "./rpc-package-selection", "./rpc-package-commands", "./rpc-publication", "./rpc-platform-catalogs", "./platform-catalog", "./rpc-platform-cache", "./cli"].map(path => require.resolve(path)), require.resolve("@sunder/package-tool/package.json"), resolve(dirname(require.resolve("@sunder/sdk")), "../package.json"), require.resolve("typescript/package.json")]);
export interface OwnedRpcAnalysis {
  readonly imports: PackageImportAnalysis; readonly producerFiles: readonly string[];
  readonly baselineInput: BaselineInput; readonly baseline?: OwnedBaseline;
  readonly physicalFingerprint: string; readonly modulePath: string; readonly exportName: string; readonly source: string;
  readonly files: ReadonlyMap<string, string>; readonly contracts: readonly LoadedContract[];
  readonly providers: readonly ProviderConfig[]; readonly importedProviders: readonly ImportedProviderBinding[];
  readonly appUses: readonly ContractUseConfig[]; readonly runtimeUses: readonly ContractUseConfig[];
  readonly roleUses: readonly SourceRoleUse[];
  readonly inputFingerprint: string; readonly runtime: OwnedRuntimeInputs; readonly revalidate: () => Promise<void>;
}
export interface OwnedAnalysisOptions extends RegistryOriginOptions {
  readonly contractLock?: ContractLock; readonly contractCacheRoot?: string;
  readonly allowUnboundImportedProviderKeys?: ReadonlySet<string>;
  readonly physicalBaseline?: BaselineInput; readonly identityBaseline?: OwnedBaseline;
  readonly allowDrift?: boolean; readonly requireBaseline?: boolean;
}
function compilerOptions(sdk: string, generated: string): ts.CompilerOptions {
  if (ts.version !== "6.0.3") fail("unsupported-source", "Service analysis requires the pinned TypeScript 6.0.3 compiler.");
  return { target: ts.ScriptTarget.ES2023, module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler,
    customConditions: ["node"], strict: true, exactOptionalPropertyTypes: true, noUncheckedIndexedAccess: true,
    allowJs: true, checkJs: true, noEmit: true, skipLibCheck: true, types: ["node"], typeRoots: [dirname(dirname(require.resolve("@types/node/package.json")))],
    paths: { "@sunder/sdk": [resolve(sdk, "index.d.ts")], "@sunder/sdk/core": [resolve(sdk, "core.d.ts")], "@sunder/sdk/browser": [resolve(sdk, "browser.d.ts")], "#sunder/api": [resolve(generated, "api.ts")] } };
}
/** One captured compiler host for source analysis, generated checking and declaration emit. */
function compilerFrame(root: string, canonical: string, sdk: string, generated: string, virtual: Map<string, string>) {
  const inputs = captureRpcCompilerInputs(root, canonical); const options = compilerOptions(sdk, generated); const host = ts.createCompilerHost(options);
  const canonicalGenerated = resolve(canonical, ".sunder/generated/rpc");
  const overlay = (path: string): string | undefined => {
    path = resolve(path); for (const base of [generated, canonicalGenerated]) if (path === base || path.startsWith(base + "/")) return generated + path.slice(base.length); return undefined;
  };
  const exists = host.fileExists.bind(host); const directory = host.directoryExists!.bind(host); const list = host.getDirectories!.bind(host);
  const probes = new Map<string, boolean>(); const directories = new Map<string, boolean>(); const listings = new Map<string, string>();
  const bound = (): void => { if (probes.size + directories.size + listings.size > 8192) fail("source-limit-exceeded", "Service resolution exceeds 8192 probes."); };
  host.readFile = path => { const mapped = overlay(path); return mapped === undefined ? inputs.readFile(path) : virtual.get(mapped); };
  host.fileExists = path => { const mapped = overlay(path); if (mapped !== undefined) return virtual.has(mapped); bound(); const result = exists(path); probes.set(path, result); return result; };
  host.directoryExists = path => {
    if ([generated, canonicalGenerated].some(base => base === resolve(path) || base.startsWith(resolve(path) + "/"))) return true;
    const mapped = overlay(path); if (mapped !== undefined) return [...virtual.keys()].some(file => file.startsWith(mapped + "/"));
    bound(); const result = directory(path); directories.set(path, result); return result;
  };
  host.getDirectories = path => { if (overlay(path) !== undefined) return []; bound(); const result = list(path); listings.set(path, JSON.stringify(result.sort())); return result; };
  host.realpath = path => overlay(path) ?? inputs.realpath(path);
  const revalidate = (): void => {
    inputs.revalidate();
    for (const [path, value] of probes) if (exists(path) !== value) fail("unsupported-source", `Service resolution membership changed: ${path}`);
    for (const [path, value] of directories) if (directory(path) !== value) fail("unsupported-source", `Service directory changed: ${path}`);
    for (const [path, value] of listings) if (JSON.stringify(list(path).sort()) !== value) fail("unsupported-source", `Service directory membership changed: ${path}`);
  };
  return { inputs, options, host, revalidate };
}
export async function prepareInitialApiSelection(project: LoadedProject, configBytes: Buffer, lock: ContractLock, cacheRoot: string, baseline: BaselineInput): Promise<() => Promise<void>> {
  if (!project.config.services) fail("unsupported-source", "Package API selection requires services.module.");
  const generated = resolve(project.root, ".sunder/generated/rpc"); const sdk = resolve(dirname(require.resolve("@sunder/sdk")));
  const imports = await preparePackageImports(lock, cacheRoot, project.config.id, project.config.dependencies ?? [], presentRoles(project));
  const virtual = new Map([...imports.sources].map(([name, source]) => [resolve(generated, name), source]));
  virtual.set(resolve(generated, "catalogs.ts"), imports.facadeSource);
  virtual.set(resolve(generated, "api.ts"), serviceApiSource(project.config.id, imports.contracts, [], imports, project.config.version));
  const frame = compilerFrame(project.root, await realpath(project.root), sdk, generated, virtual);
  ts.createProgram([resolve(project.root, project.config.services.module), ...virtual.keys()], frame.options, frame.host); frame.inputs.assertValid();
  const verify = async (): Promise<void> => { producer.revalidate(); await baseline.revalidate(); frame.revalidate();
    if (!(await readProjectDescriptorFile(project.root, project.configPath, "Package config")).equals(configBytes)) fail("unsupported-source", "Package config changed during selection."); };
  await verify(); return verify;
}
export async function analyzeOwnedRpc(project: LoadedProject, configBytes: Buffer, options: OwnedAnalysisOptions = {}): Promise<OwnedRpcAnalysis> {
  producer.revalidate();
  const modulePath = resolve(project.root, project.config.services!.module); const generated = resolve(project.root, ".sunder/generated/rpc");
  const sdk = resolve(dirname(require.resolve("@sunder/sdk"))); const virtual = new Map<string, string>();
  const frame = compilerFrame(project.root, await realpath(project.root), sdk, generated, virtual);
  const source = frame.inputs.readFile(modulePath);
  if (source === undefined || source.charCodeAt(0) === 0xfeff) fail("unsupported-source", "Service module must be regular UTF-8 source without BOM.");
  const lockPath = resolve(project.root, "sunder-contracts.lock.json");
  const physicalLock = await readProjectDescriptorFile(project.root, lockPath, "Services require canonical sunder-contracts.lock.json", 33_554_432);
  const lock = options.contractLock ?? parseContractLock(physicalLock, options.allowDevelopmentRegistryOrigin);
  const baselineInput = options.physicalBaseline ?? await captureBaseline(project.root); const baseline = options.identityBaseline ?? baselineInput.baseline;
  if (baseline && baseline.packageId !== project.config.id) fail("baseline-drift", "Baseline belongs to a different package.");
  const verifyBaselineEvidence = async (): Promise<void> => {
    if (baselineInput.baseline) return;
    const marker = resolve(generated, "owned-baseline.sha256");
    if (await exists(marker) && (await readProjectDescriptorFile(project.root, marker, "Baseline marker", 128)).toString() !== "absent\n") fail("baseline-drift", "Restore the missing public owned baseline; generated output is not baseline authority.");
    for (const name of ["authoring-receipt.v1.json", "app-authoring-receipt.v1.json", "package-export.v1.json"]) {
      const path = resolve(generated, name); if (!await exists(path)) continue;
      assertBaselineFreeEvidence(await readProjectDescriptorFile(project.root, path, "Prior service receipt", 67_108_864), path, project.config.id, name === "package-export.v1.json" ? "export" : "receipt");
    }
  };
  await verifyBaselineEvidence();
  const imports = await preparePackageImports(lock, options.contractCacheRoot ?? DEFAULT_CONTRACT_CACHE, project.config.id, project.config.dependencies ?? [], presentRoles(project));
  for (const [name, text] of imports.sources) virtual.set(resolve(generated, name), text);
  virtual.set(resolve(generated, "catalogs.ts"), imports.facadeSource);
  virtual.set(resolve(generated, "api.ts"), serviceApiSource(project.config.id, imports.contracts, [], imports, project.config.version));
  const program = ts.createProgram([modulePath, ...virtual.keys()], frame.options, frame.host); frame.inputs.assertValid();
  const file = program.getSourceFile(modulePath); if (!file) fail("unsupported-source", "Service module could not be parsed.");
  let nodes = 0; let bytes = 0;
  for (const input of program.getSourceFiles()) if (!input.isDeclarationFile && !input.fileName.startsWith(generated + "/")) {
    if (/@ts-(nocheck|ignore|expect-error)\b/u.test(input.text)) fail("unsupported-source", `Service source must typecheck without suppression: ${input.fileName}`);
    bytes += Buffer.byteLength(input.text.replaceAll("\r\n", "\n")); if (bytes > 4_194_304) fail("source-limit-exceeded", "Service source exceeds 4 MiB.");
    const visit = (node: ts.Node): void => { if (++nodes > 65_536) fail("source-limit-exceeded", "Service source exceeds 65,536 nodes."); ts.forEachChild(node, visit); }; visit(input);
  }
  const services = analyzeServices(program, file, sdk, project.config.id, project.config.version, baseline, imports.providerSurfaces, nodes, {root:generated,contracts:imports.contracts});
  const contracts: LoadedContract[] = []; const seen = new Set<string>();
  for (const service of services) {
    if (service.importKey || seen.has(service.key)) continue; seen.add(service.key);
    const canonicalBytes = Buffer.from(canonicalRpcDescriptorBytes(service.descriptor));
    contracts.push({ contractKey: service.key, configuredPath: project.config.services!.module, sourcePath: modulePath, descriptorPath: `contracts/${service.key}.rpc.json`,
      descriptor: service.descriptor, canonicalBytes, canonical: canonicalBytes.toString(), descriptorSha256: sha(canonicalBytes), abiSha256: await computeRpcAbiSha256(service.descriptor),
      methodPolicies:service.methodPolicies,modelProjection: service.modelProjection,naturalContent:service.naturalContent, exposure: service.exposure, ...(service.appUse ? { appUse: service.appUse } : {}), ...(service.runtimeUse ? { runtimeUse: service.runtimeUse } : {}) });
  }
  const allContracts = [...contracts, ...imports.contracts].sort((a, b) => ordinal(a.contractKey!, b.contractKey!));
  if (allContracts.length > 128 || new Set(allContracts.map(contract => contract.contractKey!.toLowerCase())).size !== allContracts.length || new Set(allContracts.map(contract => contract.descriptor.contractId)).size !== allContracts.length) fail("identity-collision", "Owned/imported services collide or exceed 128 contracts.");
  const evidence = allContracts.map(contract => ({ contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version,
    descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256, modelProjection: contract.modelProjection,methodPolicies:contract.methodPolicies, descriptor: contract.descriptor,
    authorityKind: contract.imported?.authorityKind ?? "package", authorityId: contract.imported?.authorityId ?? project.config.id }));
  validateModelProjectionSet(evidence);
  const providers: ProviderConfig[] = []; const importedProviders: ImportedProviderBinding[] = [];
  for (const service of services) if (service.providerId) {
    const provider: ProviderConfig = { providerId: service.providerId, role: "runtime", contractId: service.descriptor.contractId, contractVersion: service.descriptor.version, audience: service.audience };
    providers.push(provider); if (service.importKey) importedProviders.push({ key: service.importKey, provider });
  }
  for (const key of imports.providerSurfaces.keys()) if (!importedProviders.some(provider => provider.key === key) && !options.allowUnboundImportedProviderKeys?.has(key)) fail("handler-incomplete", `Selected provider '${key}' has no defineService implementation; implement it or explicitly unimplement the selection.`);
  providers.sort((a, b) => ordinal(a.providerId, b.providerId));
  const roleUses=coalesceRoleUses(services.flatMap(service=>service.roleUses));
  const typedUses=importedRuntimeUses(roleUses,allContracts,imports.uses);
  const reviewedUses=[...imports.uses,...(project.config.app?ownedAppUses(contracts):[]),...ownedRuntimeUses(contracts),...typedUses];
  validateServiceReferenceRoleUses(allContracts, reviewedUses, providers, presentRoles(project));
  for (const contract of contracts) virtual.set(resolve(generated, `owned/${contract.contractKey}.ts`), generateTypeScriptBindings(contract.descriptor, {
    descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256, strictNames: true, moduleSpecifier: "@sunder/sdk/browser", coreModuleSpecifier: "@sunder/sdk/core", valueModuleSpecifier: "@sunder/sdk/browser",
    resources:{content:contract.naturalContent===true,serviceReferences:contract.modelProjection.serviceReferences,optionalServiceReferenceMembers:contract.modelProjection.optionalServiceReferenceMembers},
    definitionTypes: {...serviceReferenceTypes(contract),...Object.fromEntries(Object.entries(contract.modelProjection.models).flatMap(([key, name]) => {
      const type = services.find(service => service.key === contract.contractKey)?.sourceModels.get(name); return type ? [[key, type]] : [];
    }))},
  }));
  virtual.set(resolve(generated, "api.ts"), serviceApiSource(project.config.id, allContracts, services, imports, project.config.version));
  virtual.set(resolve(generated, "adapter.ts"), serviceAdapterSource(modulePath, services, allContracts));
  const moduleSymbol = program.getTypeChecker().getSymbolAtLocation(file)!;
  virtual.set(resolve(generated, "worker.ts"), serviceWorkerSource(modulePath, program.getTypeChecker().getExportsOfModule(moduleSymbol).some(symbol => symbol.name === "configure")));
  if ([...virtual.values()].reduce((sum, value) => sum + Buffer.byteLength(value), 0) > 67_108_864) fail("aggregate-materializability-overflow", "Generated service source exceeds 64 MiB.");
  const files = new Map<string, string>();
  for (const [path, text] of virtual) {
    const name = path.slice(generated.length + 1); files.set(name, text); files.set(name.replace(/\.ts$/u, ".js"), transpile(text));
  }
  for (const contract of allContracts) files.set(contract.descriptorPath, contract.canonical);
  const publicEvidence = evidence.filter(entry => allContracts.find(contract => contract.descriptor.contractId === entry.contractId)!.exposure === "public");
  validateModelProjectionSet(publicEvidence);
  files.set(MODEL_PROJECTION_PATH, projectionDocumentBytes(project.config.id, project.config.version, publicEvidence).toString());
  const runtime = await captureOwnedRuntimeInputs(project.root, resolve(generated, "worker.ts"), modulePath, "", source, files);
  const checked = ts.createProgram([modulePath, ...runtime.sourceFiles, ...virtual.keys()], frame.options, frame.host); frame.inputs.assertValid();
  const diagnostics = ts.getPreEmitDiagnostics(checked);
  if (diagnostics.length) fail("handler-incomplete", ts.formatDiagnostics(diagnostics, { getCanonicalFileName: path => path, getCurrentDirectory: () => project.root, getNewLine: () => "\n" }));
  const checkedServices = analyzeServices(checked, checked.getSourceFile(modulePath)!, sdk, project.config.id, project.config.version, baseline, imports.providerSurfaces, nodes, {root:generated,contracts:imports.contracts});
  if(!roleUsesBytes(roleUses).equals(roleUsesBytes(coalesceRoleUses(checkedServices.flatMap(service=>service.roleUses)))))fail("evidence-mismatch","Generated-member recompilation changed typed Runtime source intent.");
  if (checkedServices.length !== services.length || checkedServices.some((service, index) => service.exportName !== services[index]!.exportName
    || !Buffer.from(canonicalRpcDescriptorBytes(service.descriptor)).equals(Buffer.from(canonicalRpcDescriptorBytes(services[index]!.descriptor)))
    || !methodPoliciesBytes(service.methodPolicies).equals(methodPoliciesBytes(services[index]!.methodPolicies)) || service.naturalContent!==services[index]!.naturalContent || !modelProjectionBytes(service.modelProjection).equals(modelProjectionBytes(services[index]!.modelProjection)))) fail("evidence-mismatch", "Generated-member recompilation changed source-derived descriptor or model projection.");
  if (!options.allowDrift) validateBaselineSource(baseline ?? emptyBaseline(project.config.id), contracts);
  // Public declarations are a portable projection, not a transitive type import
  // of the Worker's implementation/Node ambient environment. The checked owning
  // compilation above reuses authored symbols; standalone browser/checkJs users
  // share one portable model declaration per identity through these aliases.
  const sourceVirtual = new Map(virtual); const publicModels = new Map<string, string>();
  for (const contract of contracts) {
    const model = authoringBindingModel(contract.descriptor); const definitionTypes: Record<string, string> = Object.assign(Object.create(null),serviceReferenceTypes(contract)) as Record<string, string>;
    for (const [definition, name] of Object.entries(contract.modelProjection.models).sort(([a],[b])=>ordinal(a,b))) {
      const previous = publicModels.get(name);
      if (previous) definitionTypes[definition] = previous;
      else publicModels.set(name, `import(${JSON.stringify("./" + contract.contractKey + ".js")}).${model.definitionNames.get(definition)!}`);
    }
    virtual.set(resolve(generated, `owned/${contract.contractKey}.ts`), generateTypeScriptBindings(contract.descriptor, {
      descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256, strictNames: true, definitionTypes,
      moduleSpecifier: "@sunder/sdk/browser", coreModuleSpecifier: "@sunder/sdk/core", valueModuleSpecifier: "@sunder/sdk/browser",
      resources:{content:contract.naturalContent===true,serviceReferences:contract.modelProjection.serviceReferences,optionalServiceReferenceMembers:contract.modelProjection.optionalServiceReferenceMembers},
    }));
  }
  virtual.set(resolve(generated, "api.ts"), serviceApiSource(project.config.id, allContracts, services, imports, project.config.version, false));
  const declarations = ts.createProgram([...virtual.keys()], { ...frame.options, noEmit: false, declaration: true, emitDeclarationOnly: true }, frame.host);
  for (const path of virtual.keys()) {
    const emitted = declarations.emit(declarations.getSourceFile(path), (output, text) => {
      const name = output.slice(generated.length + 1); const portable = portableDeclarationImports(text);
      files.set(name, portable);
      // A distinct declaration-only module name avoids TS extension substitution
      // selecting the owning .ts source instead of its browser-safe snapshot.
      files.set(name.replace(/\.d\.ts$/u, ".types.d.ts"), portable);
    });
    if (emitted.emitSkipped || emitted.diagnostics.length) fail("handler-incomplete", "Generated service declarations could not be emitted.");
  }
  for (const [path, text] of sourceVirtual) virtual.set(path, text);
  frame.inputs.assertValid();
  const appUses = project.config.app ? ownedAppUses(contracts) : []; const runtimeUses = [...ownedRuntimeUses(contracts),...typedUses];
  if (appUses.length + runtimeUses.length + imports.uses.length > 128) fail("aggregate-materializability-overflow", "Present-role service use projection exceeds 128 entries.");
  const physicalFingerprint = sha(JSON.stringify({ config: configBytes.toString(), physicalLock: physicalLock.toString(), baseline: baselineInput.fingerprint,
    compiler: frame.inputs.fingerprint(), producer: producer.fingerprint, runtime: runtime.physicalFingerprint }));
  const fingerprint = sha(JSON.stringify({ physicalFingerprint, lock: lock.sha256, contracts: allContracts.map(contract => [contract.descriptor.contractId, contract.descriptor.version, contract.descriptorSha256, modelProjectionBytes(contract.modelProjection).toString(),methodPoliciesBytes(contract.methodPolicies).toString()]),...roleUsesOuter(roleUses) }));
  const { toolVersion } = packageToolCompatibility();
  for (const role of presentRoles(project)) {
    const selectedImports = importedContractsForRoles(imports, new Set([role]));
    const selected = allContracts.filter(contract => !contract.imported || selectedImports.has(contract.descriptor.contractId));
    const uses = [...imports.uses.filter(use => use.role === role), ...(role === "app" ? appUses : runtimeUses)];
    const receipt = { schemaVersion: 1, producer: { name: "@sunder/package-tool", version: toolVersion, language: modulePath.endsWith(".js") ? "javascript" : "typescript" },
      inputFingerprint: fingerprint, lockSha256: lock.sha256, packageId: project.config.id, packageVersion: project.config.version, ...(role === "app" ? { role } : {}),
      contracts: selected.map(contract => ({ contractKey: contract.contractKey!, contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version, exposure: contract.exposure,
        descriptorBytes: contract.canonicalBytes.length, canonicalDescriptor: contract.canonical, descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256, modelProjection: modelProjectionOuter(contract.modelProjection),
        ...methodPoliciesOuter(contract.methodPolicies),
        ...(contract.appUse ? { appUse: contract.appUse } : {}), ...(role === "runtime" && contract.runtimeUse ? { runtimeUse: contract.runtimeUse } : {}) })),
      packageArtifacts: importReceiptArtifacts(imports, new Set([role])), bundles: selected.map(contract => contract.imported ? { contractKey: contract.contractKey!, source: "imported", ...authority(contract.imported) } : { contractKey: contract.contractKey!, source: "owned", authorityKind: "package", authorityId: project.config.id }),
      ...importProjection(imports, new Set([role]), importedProviders), providers: role === "app" ? [] : providers.map(provider => ({ providerId: provider.providerId, role, audience: provider.audience, contractKey: allContracts.find(contract => contract.descriptor.contractId === provider.contractId)!.contractKey! })),
      uses: uses.map(use => ({ role, required: use.required, actions: use.actions, contractKey: allContracts.find(contract => contract.descriptor.contractId === use.contractId)!.contractKey! })).sort((a, b) => ordinal(a.contractKey, b.contractKey)),
      generatedPackageFacades: importedFacadeReceipt(imports, new Set([role])), generatedBindings: selected.map(contract => ({ contractKey: contract.contractKey!, logicalName: contract.imported ? contractSymbol(contract.contractKey!) : pascal(contract.contractKey!) })),...(role==="runtime"?roleUsesOuter(roleUses):{}) };
    validateRoleUseEvidence(receipt as unknown as import("./rpc-baseline-json").OuterObject,"receipt",role==="runtime"?roleUses:[]);
    const text = outerBytes(receipt as unknown as Outer).toString(); if (Buffer.byteLength(text) > 67_108_864) fail("aggregate-materializability-overflow", "Service receipt exceeds 64 MiB.");
    files.set(role === "app" ? "app-authoring-receipt.v1.json" : "authoring-receipt.v1.json", text);
  }
  files.set("package-export.v1.json", outerBytes({ schemaVersion: 1,
    producer: { name: "@sunder/package-tool", version: toolVersion, language: modulePath.endsWith(".js") ? "javascript" : "typescript" },
    projectIdentity: project.config.id, configuration: "Release", targetFramework: "node24", inputFingerprint: fingerprint, lockSha256: lock.sha256,
    packageId: project.config.id, packageVersion: project.config.version,
    contracts: [...contracts].sort((a, b) => ordinal(a.contractKey!, b.contractKey!)).map(contract => ({ contractKey: contract.contractKey!, contractId: contract.descriptor.contractId,
      contractVersion: contract.descriptor.version, exposure: contract.exposure, source: "owned", authorityKind: "package", authorityId: project.config.id,
      descriptorBytes: contract.canonicalBytes.length, canonicalDescriptor: contract.canonical, descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256,
      modelProjection: modelProjectionOuter(contract.modelProjection),...methodPoliciesOuter(contract.methodPolicies), ...(contract.appUse ? { appUse: contract.appUse } : {}), ...(contract.runtimeUse ? { runtimeUse: contract.runtimeUse } : {}) })),
    ...roleUsesOuter(roleUses),
  } as unknown as Outer).toString());
  files.set("owned-baseline.sha256", baselineInput.fingerprint + "\n");
  if ([...files.values()].reduce((sum, value) => sum + Buffer.byteLength(value), 0) > 67_108_864) fail("aggregate-materializability-overflow", "Complete generated model/binding/evidence output exceeds 64 MiB.");
  const revalidate = async (): Promise<void> => {
    producer.revalidate(); await baselineInput.revalidate(); await verifyBaselineEvidence(); frame.revalidate();
    for (const [path, bytes] of [[project.configPath, configBytes], [lockPath, physicalLock]] as const) if (!(await readProjectDescriptorFile(project.root, path, "Service input", 33_554_432)).equals(bytes)) fail("unsupported-source", `Service input changed: ${path}`);
    await runtime.revalidate(); producer.revalidate();
  };
  await revalidate();
  return { imports, roleUses, producerFiles: producer.files, baselineInput, ...(baseline ? { baseline } : {}), physicalFingerprint, modulePath, exportName: "", source, files, contracts, providers, importedProviders, appUses, runtimeUses, inputFingerprint: fingerprint, runtime, revalidate };
}
function presentRoles(project: LoadedProject): ReadonlySet<PackageRole> { return new Set(project.config.app ? ["app", "runtime"] : ["runtime"]); }
function transpile(source: string): string { return ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2023, module: ts.ModuleKind.ESNext } }).outputText; }
function portableDeclarationImports(source: string): string {
  const file = ts.createSourceFile("binding.d.ts", source, ts.ScriptTarget.Latest, true);
  const transformed = ts.transform(file, [context => {
    const visit: ts.Visitor = node => {
      if (ts.isStringLiteral(node) && (ts.isImportDeclaration(node.parent) && node.parent.moduleSpecifier === node
        || ts.isExportDeclaration(node.parent) && node.parent.moduleSpecifier === node
        || ts.isLiteralTypeNode(node.parent) && ts.isImportTypeNode(node.parent.parent)) && /^\.{1,2}\//u.test(node.text) && node.text.endsWith(".js"))
        return context.factory.createStringLiteral(node.text.slice(0, -3) + ".types.js");
      return ts.visitEachChild(node, visit, context);
    };
    return root => ts.visitNode(root, visit) as ts.SourceFile;
  }]);
  try { return ts.createPrinter({ newLine: ts.NewLineKind.LineFeed }).printFile(transformed.transformed[0]!); }
  finally { transformed.dispose(); }
}
function sha(value: string | Uint8Array): string { return createHash("sha256").update(value).digest("hex"); }
function ordinal(a: string, b: string): number { return a < b ? -1 : a > b ? 1 : 0; }
function fail(code: string, message: string): never { throw new Error(`rpc-authoring.${code}: ${message}`); }
