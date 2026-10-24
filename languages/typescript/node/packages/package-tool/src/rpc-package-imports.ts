import { authoringBindingModel, generateTypeScriptBindings } from "./generator";
import { readContractVerificationCache } from "./rpc-contract-cache";
import { contractError, type ArtifactContract, type ContractLock } from "./rpc-contract-lock";
import type { ContractUseConfig, LoadedContract, PackageRole, ProviderConfig } from "./model";
import { readPlatformCatalogs, validatePlatformToolRange } from "./rpc-platform-catalogs";
import { readModelProjectionCache } from "./rpc-model-projection-cache";
import { emptyModelProjection, modelProjectionBytes, methodPoliciesBytes, validateModelProjectionSet, type RpcModelProjectionContract } from "./rpc-model-projection";
import { serviceReferenceTypes } from "./service-bindings";

export interface PackageImportAnalysis {
  readonly lock: ContractLock;
  readonly contracts: readonly LoadedContract[];
  readonly providerSurfaces: ReadonlyMap<string, LoadedContract>;
  readonly sources: ReadonlyMap<string, string>;
  readonly facadeSource: string;
  readonly uses: readonly ContractUseConfig[];
  readonly dependencies: readonly { packageId: string; versionRange: string; presence: "required" | "optional" }[];
  readonly presentRoles: ReadonlySet<PackageRole>;
}
export interface ImportedProviderBinding { readonly key: string; readonly provider: ProviderConfig }
export const contractSymbol = (id: string): string => "C" + id.split(".").map(part => part[0]!.toUpperCase() + part.slice(1)).join("_");
export function authority(contract: ArtifactContract) {
  return { authorityKind: contract.authorityKind, authorityId: contract.authorityId, selectedAuthorityRelease: contract.selectedAuthorityRelease,
    selectedAuthorityContentIdentity: contract.selectedAuthorityContentIdentity };
}
export async function preparePackageImports(lock: ContractLock, cacheRoot: string, ownerId: string, authoredDependencies: readonly { packageId: string }[], presentRoles: ReadonlySet<PackageRole> = new Set(["runtime"])): Promise<PackageImportAnalysis> {
  validatePlatformToolRange(lock);
  await readPlatformCatalogs(lock, cacheRoot);
  const requirements = new Map(lock.requirements);
  for (const artifact of lock.packageArtifacts.values()) for (const contract of artifact.contracts.values()) requirements.set(contract.descriptorSha256, contract);
  const cache = await readContractVerificationCache(cacheRoot, requirements);
  const documents = await readModelProjectionCache(cacheRoot, lock.packageArtifacts, new Map([...cache].map(([hash, entry]) => [hash, entry.canonicalBytes])));
  const projections = new Map<string, RpcModelProjectionContract>();
  for (const artifact of lock.packageArtifacts.values()) for (const entry of documents.get(artifact.packageId)!.contracts) {
    const previous = projections.get(entry.contractId);
    if (previous && (!modelProjectionBytes(previous.modelProjection).equals(modelProjectionBytes(entry.modelProjection))||!methodPoliciesBytes(previous.methodPolicies).equals(methodPoliciesBytes(entry.methodPolicies)))) contractError("rpc-authoring.evidence-mismatch", "Reexport projection or method policy differs from its selected origin.");
    projections.set(entry.contractId, entry);
  }
  const contracts = new Map<string, LoadedContract>(); const sources = new Map<string, string>();
  const dependencies = new Map<string, { packageId: string; versionRange: string; presence: "required" | "optional" }>();
  const uses = new Map<string, ContractUseConfig>(); const facadeImports: string[] = []; const facades: string[] = [];
  const providerSurfaces = new Map<string, LoadedContract>();
  const addDependency = (id: string, presence: "required" | "optional"): void => {
    if (id === ownerId) contractError("package-api.authority-release-conflict", "A package cannot import its own package authority.");
    const artifact = lock.packageArtifacts.get(id);
    if (!artifact) contractError("package-api.authority-release-conflict", "An ultimate package authority has no explicitly reviewed range selection; select it first.");
    if (authoredDependencies.some(dependency => dependency.packageId === id)) contractError("package-api.dependency-source-duplicate", "Authored dependencies must not duplicate lock-derived dependencies.");
    dependencies.set(id, { packageId: id, versionRange: artifact.requestedVersionRange, presence: presence === "required" || dependencies.get(id)?.presence === "required" ? "required" : "optional" });
  };
  const materialize = (record: ArtifactContract): LoadedContract => {
    const key = record.contractId; const previous = contracts.get(key);
    if (previous) {
      if (previous.descriptorSha256 !== record.descriptorSha256 || previous.abiSha256 !== record.abiSha256) contractError("rpc-authoring.identity-collision", "Imported contract selects more than one exact identity.");
      return previous;
    }
    const cached = cache.get(record.descriptorSha256)!;
    const contract: LoadedContract = { contractKey: key, configuredPath: "sunder-contracts.lock.json", sourcePath: "sunder-contracts.lock.json",
      descriptorPath: `contracts/${key}.rpc.json`, descriptor: cached.descriptor, canonicalBytes: cached.canonicalBytes, canonical: cached.canonicalBytes.toString("utf8"),
      descriptorSha256: record.descriptorSha256, abiSha256: record.abiSha256, exposure: "public", imported: record,
      naturalContent:true,methodPolicies:projections.get(record.contractId)?.methodPolicies,modelProjection: record.authorityKind === "platform" ? emptyModelProjection() : projections.get(record.contractId)!.modelProjection };
    contracts.set(key, contract);
    return contract;
  };
  for (const imported of lock.packageImports.values()) {
    const roles = imported.consumerRoles.filter(role => presentRoles.has(role));
    if (!roles.length) contractError("package-api.consumer-roles-unavailable", `${imported.packageId}: no canonical project role matches [${imported.consumerRoles}]; add a matching role or explicitly review consumer intent.`);
    const artifact = lock.packageArtifacts.get(imported.packageId)!; addDependency(imported.packageId, imported.presence);
    const members: string[] = [];
    for (const record of artifact.contracts.values()) {
      if (record.authorityKind !== "package") continue;
      addDependency(record.authorityId, imported.presence);
      const key = record.contractId; const symbol = contractSymbol(key);
      const contract = materialize(record);
      if (!sources.has(`apis/${key}.ts`)) {
        sources.set(`apis/${key}.ts`, contractApiSource(contract));
        facadeImports.push(`import { Contract as ${symbol} } from "./apis/${key}.js";`);
      }
      const methods = Object.values(contract.descriptor.services).flatMap(service => Object.values(service.methods));
      const actions: Array<"discover" | "invoke" | "subscribe"> = ["discover"];
      if (methods.some(method => method.kind === "unary")) actions.push("invoke");
      if (methods.some(method => method.kind === "server-stream")) actions.push("subscribe");
      for (const role of roles) uses.set(`${role}/${key}`, { role, contractId: key, contractVersion: record.contractVersion, required: imported.requiredContracts.includes(key) || uses.get(`${role}/${key}`)?.required === true, actions });
      members.push(`${symbol}: new ${symbol}(rpc, ${JSON.stringify(imported.packageId)}, ${JSON.stringify(artifact.requestedVersionRange)}, ${artifact.includePrerelease})`);
    }
    facades.push(`${JSON.stringify(imported.localName)}: (rpc: import("@sunder/sdk/browser").RpcClient | import("@sunder/sdk/browser").SunderBrowserRpc) => Object.freeze({${members.join(", ")}})`);
  }
  for (const imported of lock.providerImports.values()) {
    const record = lock.packageArtifacts.get(imported.packageId)!.contracts.get(imported.contractId)!;
    addDependency(imported.packageId, imported.presence); addDependency(record.authorityId, imported.presence);
    addProvider(imported.key, materialize(record));
  }
  for (const [key, imported] of lock.platformImports) {
    const contract: ArtifactContract = { artifactSource: "imported", authorityKind: "platform", authorityId: "sunder.core",
      selectedAuthorityRelease: imported.selectedAuthorityRelease as string, selectedAuthorityContentIdentity: imported.selectedAuthorityContentIdentity as string,
      contractId: imported.contractId as string, contractVersion: imported.resolvedContractVersion as string, descriptorBytes: imported.descriptorBytes as number,
      descriptorSha256: imported.descriptorSha256 as string, abiSha256: imported.abiSha256 as string, etag: imported.etag as string };
    addProvider(key, materialize(contract));
  }
  // A provider root may need other types/services from the same pinned public
  // artifact. Materialize that recursive closure without creating consumer uses.
  for (const {artifactId,contract} of packageReferenceClosure(lock,projections,presentRoles)) {
    materialize(contract);
    const presence=selectedPresence(lock,artifactId,presentRoles);
    addDependency(contract.authorityId,presence);
  }
  function addProvider(key: string, contract: LoadedContract): void {
    providerSurfaces.set(key, contract);
  }
  validateModelProjectionSet([...contracts.values()].map(contract => ({ contractId: contract.descriptor.contractId, contractVersion: contract.descriptor.version,
    descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256, descriptor: contract.descriptor, authorityKind: contract.imported!.authorityKind, authorityId: contract.imported!.authorityId, modelProjection: contract.modelProjection,methodPolicies:contract.methodPolicies })));
  // Role validation follows source analysis: explicit typed Runtime intent is
  // not available during this permission-free declaration prepass.
  const modelTypes = new Map<string, string>();
  for (const contract of [...contracts.values()].sort((a, b) => ordinal(a.contractKey!, b.contractKey!))) {
    const model = authoringBindingModel(contract.descriptor); const definitionTypes: Record<string, string> = Object.assign(Object.create(null),serviceReferenceTypes(contract)) as Record<string, string>;
    for (const [definition, name] of Object.entries(contract.modelProjection.models).sort(([a],[b])=>ordinal(a,b))) {
      const identity = contract.imported!.authorityId + "/" + name; const previous = modelTypes.get(identity);
      if (previous) definitionTypes[definition] = previous;
      else modelTypes.set(identity, `import(${JSON.stringify("./" + contract.contractKey + ".js")}).${model.definitionNames.get(definition)!}`);
    }
    sources.set(`imported/${contract.contractKey}.ts`, generateTypeScriptBindings(contract.descriptor, { definitionTypes,resources:{content:true,serviceReferences:contract.modelProjection.serviceReferences,optionalServiceReferenceMembers:contract.modelProjection.optionalServiceReferenceMembers},
      descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256, strictNames: true, moduleSpecifier: "@sunder/sdk/browser", valueModuleSpecifier: "@sunder/sdk/generated", coreModuleSpecifier: "@sunder/sdk/core" }));
  }
  if (dependencies.size + authoredDependencies.length > 128 || [...sources.values()].reduce((size, source) => size + Buffer.byteLength(source), 0) > 67_108_864)
    contractError("rpc-authoring.aggregate-materializability-overflow", "Imported dependency or generated-source projection exceeds its bound.");
  return { lock, presentRoles, contracts: [...contracts.values()].sort((a, b) => ordinal(a.contractKey!, b.contractKey!)), providerSurfaces, sources,
    facadeSource: `${facadeImports.join("\n")}\nexport const packageApis = Object.freeze({${facades.join(",\n")}});\n`,
    uses: [...uses.values()].sort((a, b) => ordinal(a.contractId, b.contractId)), dependencies: [...dependencies.values()].sort((a, b) => ordinal(a.packageId, b.packageId)) };
}

export function importProjection(imports: PackageImportAnalysis, roles: ReadonlySet<PackageRole>, bindings: readonly ImportedProviderBinding[] = []) {
  const consumers = [...imports.lock.packageImports.values()].filter(value => value.consumerRoles.some(role => roles.has(role) && imports.presentRoles.has(role)));
  const providers = roles.has("runtime") ? [...imports.lock.providerImports.values()] : [];
  const selected = new Set([...consumers.map(value => value.packageId), ...providers.map(value => value.packageId)]);
  const pendingArtifacts=[...selected];
  for(let index=0;index<pendingArtifacts.length;index++)for(const contract of imports.lock.packageArtifacts.get(pendingArtifacts[index]!)!.contracts.values())
    if(contract.authorityKind==="package"&&imports.lock.packageArtifacts.has(contract.authorityId)&&!selected.has(contract.authorityId)){selected.add(contract.authorityId);pendingArtifacts.push(contract.authorityId);}
  return {
    packageArtifactImports: [...imports.lock.packageArtifacts.values()].filter(artifact => selected.has(artifact.packageId)).map(artifact => ({ packageId: artifact.packageId,
      requestedVersionRange: artifact.requestedVersionRange, includePrerelease: artifact.includePrerelease, selectedPackageVersion: artifact.selectedPackageVersion,
      selectedPackageContentIdentity: artifact.selectedPackageContentIdentity, publicContractCount: artifact.publicContractCount, publicInventorySha256: artifact.publicInventorySha256,
      authorityConstraints: [...new Map([...artifact.contracts.values()].map(contract => [contract.authorityKind + "/" + contract.authorityId, authority(contract)])).entries()]
        .sort(([a], [b]) => ordinal(a, b)).map(([, value]) => value) })),
    packageApiImports: consumers.map(imported => {
      const references = [...imports.lock.packageArtifacts.get(imported.packageId)!.contracts.values()].filter(contract => contract.authorityKind === "package")
        .map(contract => ({ contractId: contract.contractId, contractVersion: contract.contractVersion }));
      return { packageId: imported.packageId, presence: imported.presence, bundleRefs: references, useRefs: imported.consumerRoles.filter(role => roles.has(role) && imports.presentRoles.has(role)).flatMap(role => references.map(reference => ({ role, ...reference }))) };
    }), providerImports: providers.map(imported => {
      const contractVersion = imports.providerSurfaces.get(imported.key)!.descriptor.version;
      return { key: imported.key, packageId: imported.packageId, contractId: imported.contractId, contractVersion,
        bundleRef: { contractId: imported.contractId, contractVersion }, providerRefs: bindings.filter(binding => binding.key === imported.key)
          .map(({ provider }) => ({ role: provider.role, providerId: provider.providerId, contractId: provider.contractId, contractVersion: provider.contractVersion })),
        ...(imported.presence === "optional" ? { presence: "optional" } : {}) };
    }), platformImports: (roles.has("runtime") ? [...imports.lock.platformImports] : []).map(([key, value]) => ({ key,
      authorityKind: value.authorityKind, authorityId: value.authorityId, authorityReleaseRange: value.authorityReleaseRange, includePrerelease: value.includePrerelease,
      selectedAuthorityRelease: value.selectedAuthorityRelease, selectedAuthorityContentIdentity: value.selectedAuthorityContentIdentity,
      contractId: value.contractId, contractVersion: value.resolvedContractVersion, bundleRef: { contractId: value.contractId, contractVersion: value.resolvedContractVersion },
      providerRefs: bindings.filter(binding => binding.key === key).map(({ provider }) => ({ role: provider.role, providerId: provider.providerId,
        contractId: provider.contractId, contractVersion: provider.contractVersion })),
    })),
  };
}
export function importedContractsForRoles(imports: PackageImportAnalysis, roles: ReadonlySet<PackageRole>): ReadonlySet<string> {
  const projection = importProjection(imports, roles);
  const projected = new Map(imports.contracts.map(contract=>[contract.descriptor.contractId,{modelProjection:contract.modelProjection}]));
  return new Set([...packageReferenceClosure(imports.lock,projected,new Set([...roles].filter(role=>imports.presentRoles.has(role)))).map(value=>value.contract.contractId),
    ...projection.platformImports.map(group => String(group.contractId))]);
}
function packageReferenceClosure(lock:ContractLock,projections:ReadonlyMap<string,Pick<RpcModelProjectionContract,"modelProjection">>,roles:ReadonlySet<PackageRole>):readonly {artifactId:string;contract:ArtifactContract}[]{
  const pending:Array<{artifactId:string;contractId:string}>=[];
  for(const selection of lock.packageImports.values())if(selection.consumerRoles.some(role=>roles.has(role)))
    for(const contract of lock.packageArtifacts.get(selection.packageId)!.contracts.values())if(contract.authorityKind==="package")pending.push({artifactId:selection.packageId,contractId:contract.contractId});
  if(roles.has("runtime"))for(const selection of lock.providerImports.values())pending.push({artifactId:selection.packageId,contractId:selection.contractId});
  const visited=new Set<string>();const result:Array<{artifactId:string;contract:ArtifactContract}>=[];const identities=new Set<string>();
  for(let index=0;index<pending.length;index++){
    const current=pending[index]!,key=current.artifactId+"\0"+current.contractId;if(visited.has(key))continue;visited.add(key);
    const artifact=lock.packageArtifacts.get(current.artifactId)!,contract=artifact.contracts.get(current.contractId)!;
    const projection=projections.get(current.contractId)?.modelProjection;
    if(!projection)contractError("rpc-authoring.model-projection-invalid","Pinned artifact has no projection for "+current.contractId);
    identities.add(contract.contractId);if(identities.size>128)contractError("rpc-authoring.aggregate-materializability-overflow","Imported reference closure exceeds 128 contracts.");
    result.push({artifactId:current.artifactId,contract});
    for(const target of Object.values(projection.serviceReferences)){
      const selected=artifact.contracts.get(target.contractId);
      if(!selected||selected.authorityKind!=="package")contractError("rpc-authoring.service-reference-target-unselected",`${current.contractId} references ${target.contractId}/${target.serviceId}, absent from the same pinned package-authority inventory ${current.artifactId}.`);
      if(!lock.packageArtifacts.has(selected.authorityId))contractError("package-api.authority-release-conflict","Service-reference authority requires an explicitly reviewed artifact selection: "+selected.authorityId);
      pending.push({artifactId:current.artifactId,contractId:target.contractId});
    }
  }
  return result;
}
export function importedDependenciesForRoles(imports: PackageImportAnalysis, roles: ReadonlySet<PackageRole>): PackageImportAnalysis["dependencies"] {
  const values = new Map<string, "required" | "optional">();
  const add = (id: string, presence: "required" | "optional"): void => { values.set(id, presence === "required" || values.get(id) === "required" ? "required" : "optional"); };
  for (const selection of imports.lock.packageImports.values()) if (selection.consumerRoles.some(role => roles.has(role) && imports.presentRoles.has(role))) {
    add(selection.packageId, selection.presence);
    for (const contract of imports.lock.packageArtifacts.get(selection.packageId)!.contracts.values()) if (contract.authorityKind === "package") add(contract.authorityId, selection.presence);
  }
  if (roles.has("runtime")) for (const selection of imports.lock.providerImports.values()) {
    add(selection.packageId, selection.presence); add(imports.lock.packageArtifacts.get(selection.packageId)!.contracts.get(selection.contractId)!.authorityId, selection.presence);
  }
  for(const item of packageReferenceClosure(imports.lock,new Map(imports.contracts.map(contract=>[contract.descriptor.contractId,{modelProjection:contract.modelProjection}])),roles))
    add(item.contract.authorityId,selectedPresence(imports.lock,item.artifactId,roles));
  return [...values].sort(([a], [b]) => ordinal(a, b)).map(([packageId, presence]) => ({ packageId, presence, versionRange: imports.lock.packageArtifacts.get(packageId)!.requestedVersionRange }));
}
export function importReceiptArtifacts(imports: PackageImportAnalysis, roles?: ReadonlySet<PackageRole>): unknown[] {
  const raw = JSON.parse(imports.lock.exactBytes.toString("utf8")) as { packageArtifacts: Record<string, Record<string, unknown> & { contracts: Record<string, unknown> }> };
  const included = roles ? new Set(importProjection(imports, roles).packageArtifactImports.map(value => value.packageId)) : undefined;
  return [...imports.lock.packageArtifacts.keys()].filter(packageId => !included || included.has(packageId)).map(packageId => ({ packageId, ...raw.packageArtifacts[packageId],
    contracts: [...imports.lock.packageArtifacts.get(packageId)!.contracts.keys()].map(contractId => ({ contractId, ...(raw.packageArtifacts[packageId]!.contracts[contractId] as object) })) }));
}

function selectedPresence(lock: ContractLock, artifactId: string, roles: ReadonlySet<PackageRole>): "required" | "optional" {
  const consumer = lock.packageImports.get(artifactId);
  if (consumer?.presence === "required" && consumer.consumerRoles.some(role => roles.has(role))) return "required";
  if (roles.has("runtime") && [...lock.providerImports.values()].some(value => value.packageId === artifactId && value.presence === "required")) return "required";
  return "optional";
}
export function importedFacadeReceipt(imports: PackageImportAnalysis, roles?: ReadonlySet<PackageRole>) {
  return [...imports.lock.packageImports.values()].filter(value => !roles || value.consumerRoles.some(role => roles.has(role) && imports.presentRoles.has(role)))
    .map(value => ({ packageId: value.packageId, localName: value.localName, logicalName: `packageApis.${value.localName}` }));
}

function contractApiSource(contract: LoadedContract): string {
  const client = authoringBindingModel(contract.descriptor).clientFacadeTypeName;
  return `// <auto-generated />
import * as binding from "../imported/${contract.descriptor.contractId}.js";
import { RpcError, isVersionInRange } from "@sunder/sdk/browser";
import type { RpcClient, RpcProviderSnapshot, RpcCallScope } from "@sunder/sdk/browser";
import type { SunderBrowserRpc, BrowserRpcProvider, BrowserRpcCatalogEventKind } from "@sunder/sdk/browser";
import type { RpcCallOptions, RpcInvoker } from "@sunder/sdk/core";
type Raw = RpcProviderSnapshot | BrowserRpcProvider;
type Transport = RpcClient | SunderBrowserRpc;
export type Provider = Readonly<Pick<Raw, "packageId" | "packageVersion" | "providerId" | "contractId" | "contractVersion" | "descriptorSha256" | "abiSha256" | "catalogRevision" | "state" | "faultCode">>;
const handles = new WeakMap<Provider, Raw>();
function matches(value: Raw): boolean { return value.contractId === binding.CONTRACT_ID && value.contractVersion === binding.CONTRACT_VERSION && value.descriptorSha256 === binding.CONTRACT_DESCRIPTOR_SHA256 && value.abiSha256 === binding.CONTRACT_ABI_SHA256; }
function target(value: Raw): string { return "endpointReference" in value ? value.endpointReference : value.providerHandle; }
function wrap(value: Raw): Provider {
  if (!matches(value)) throw new RpcError({kind: "validation", code: "rpc.provider-invalid", message: "Provider does not match the exact generated RPC identity."});
  const source = Object.freeze({...value});
  const provider = Object.freeze({packageId: source.packageId, packageVersion: source.packageVersion, providerId: source.providerId, contractId: source.contractId,
    contractVersion: source.contractVersion, descriptorSha256: source.descriptorSha256, abiSha256: source.abiSha256, catalogRevision: source.catalogRevision, state: source.state, faultCode: source.faultCode});
  handles.set(provider, source); return provider;
}
function raw(provider: Provider): Raw {
  const value = handles.get(provider); if (!value) throw new RpcError({kind: "validation", code: "rpc.provider-invalid", message: "Provider must come from this generated catalog."}); return value;
}
function same(left: Raw, right: Raw): boolean {
  return matches(right) && target(left) === target(right) && left.packageId === right.packageId && left.packageVersion === right.packageVersion && left.providerId === right.providerId
    && (!("endpointReference" in left) || "endpointReference" in right && left.activationId === right.activationId && left.activationEpoch === right.activationEpoch && left.sessionGeneration === right.sessionGeneration);
}
export class Contract {
  readonly #rpc: Transport; readonly #packageId: string; readonly #range: string; readonly #prerelease: boolean;
  constructor(rpc: Transport, packageId: string, range: string, prerelease: boolean) { this.#rpc = rpc; this.#packageId = packageId; this.#range = range; this.#prerelease = prerelease; Object.freeze(this); }
  async list(options?: RpcCallOptions) {
    const snapshot = await this.#rpc.discover(binding.CONTRACT_ID, options);
    return Object.freeze({revision: snapshot.revision, sequence: snapshot.sequence, resetRequired: snapshot.resetRequired, providers: Object.freeze(snapshot.providers.filter(matches).map(wrap))});
  }
  async get(provider: Provider, options?: RpcCallOptions): Promise<Provider | null> {
    const source = raw(provider);
    if ("getProvider" in this.#rpc) { const current = await this.#rpc.getProvider(target(source), options); return current && same(source, current) ? wrap(current) : null; }
    const snapshot = await this.#rpc.discover(binding.CONTRACT_ID, options);
    if (snapshot.resetRequired) return null;
    const current = snapshot.providers.filter(value => same(source, value)); return current.length === 1 ? wrap(current[0]!) : null;
  }
  async *watch(afterRevision: number, afterSequence: number, options?: RpcCallOptions): AsyncIterable<{ readonly revision: number; readonly sequence: number; readonly kind: BrowserRpcCatalogEventKind; readonly provider: Provider | null }> {
    for await (const event of this.#rpc.watch(afterRevision, afterSequence, options)) {
      if (event.kind === "reset-required") yield Object.freeze({...event, provider: null});
      else if (event.provider && matches(event.provider)) yield Object.freeze({...event, provider: wrap(event.provider)});
    }
  }
  createClient(scope: RpcCallScope | RpcInvoker<string>, provider: Provider) { return new binding.${client}(scope, target(raw(provider))); }
  async resolveSelectedPackage(options?: RpcCallOptions): Promise<Provider> {
    const snapshot = await this.list(options);
    if (snapshot.resetRequired) throw new RpcError({kind: "unavailable", code: "rpc.catalog-reset-required", message: "RPC catalog requires a fresh snapshot."});
    const matches = snapshot.providers.filter(provider => provider.packageId === this.#packageId && provider.state === "active"
      && isVersionInRange(provider.packageVersion, this.#range) && (this.#prerelease || !provider.packageVersion.split("+", 1)[0]!.includes("-")));
    if (matches.length !== 1) throw new RpcError({kind: matches.length ? "validation" : "not-found", code: matches.length ? "rpc.provider-ambiguous" : "rpc.provider-not-found", message: matches.length ? "Selected package has multiple matching providers." : "Selected package has no matching active provider."});
    return matches[0]!;
  }
}
`;
}
function ordinal(a: string, b: string): number { return a < b ? -1 : a > b ? 1 : 0; }
