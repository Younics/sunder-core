import { authoringBindingModel, generateTypeScriptBindings } from "@sunder/sdk";
import { readContractCache } from "./rpc-contract-cache";
import { contractError, type ArtifactContract, type ContractLock } from "./rpc-contract-lock";
import type { ContractUseConfig, LoadedContract, PackageRole, ProviderConfig } from "./model";
import { readPlatformCatalogs, validatePlatformToolRange } from "./rpc-platform-catalogs";

export interface PackageImportAnalysis {
  readonly lock: ContractLock;
  readonly contracts: readonly LoadedContract[];
  readonly providerSurfaces: ReadonlyMap<string, LoadedContract>;
  readonly sources: ReadonlyMap<string, string>;
  readonly facadeSource: string;
  readonly uses: readonly ContractUseConfig[];
  readonly dependencies: readonly { packageId: string; versionRange: string; presence: "required" | "optional" }[];
}
export interface ImportedProviderBinding { readonly key: string; readonly provider: ProviderConfig }
export const contractSymbol = (id: string): string => "C" + id.split(".").map(part => part[0]!.toUpperCase() + part.slice(1)).join("_");
export function authority(contract: ArtifactContract) {
  return { authorityKind: contract.authorityKind, authorityId: contract.authorityId, selectedAuthorityRelease: contract.selectedAuthorityRelease,
    selectedAuthorityContentIdentity: contract.selectedAuthorityContentIdentity };
}
export async function preparePackageImports(lock: ContractLock, cacheRoot: string, ownerId: string, authoredDependencies: readonly { packageId: string }[]): Promise<PackageImportAnalysis> {
  validatePlatformToolRange(lock);
  await readPlatformCatalogs(lock, cacheRoot);
  const cache = await readContractCache(cacheRoot, lock.requirements);
  const contracts = new Map<string, LoadedContract>(); const sources = new Map<string, string>();
  const dependencies = new Map<string, { packageId: string; versionRange: string; presence: "required" | "optional" }>();
  const uses = new Map<string, ContractUseConfig>(); const facadeImports: string[] = []; const facades: string[] = [];
  const providerSurfaces = new Map<string, LoadedContract>(); const providerFactories: string[] = [];
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
      descriptorSha256: record.descriptorSha256, abiSha256: record.abiSha256, exposure: "public", imported: record };
    contracts.set(key, contract);
    sources.set(`imported/${key}.ts`, generateTypeScriptBindings(cached.descriptor, { descriptorSha256: record.descriptorSha256, abiSha256: record.abiSha256, strictNames: true, valueModuleSpecifier: "@sunder/sdk/browser" }));
    return contract;
  };
  for (const imported of lock.packageImports.values()) {
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
      uses.set(key, { role: "runtime", contractId: key, contractVersion: record.contractVersion, required: imported.requiredContracts.includes(key) || uses.get(key)?.required === true, actions });
      members.push(`${symbol}: new ${symbol}(rpc, ${JSON.stringify(imported.packageId)}, ${JSON.stringify(artifact.requestedVersionRange)}, ${artifact.includePrerelease})`);
    }
    facades.push(`${JSON.stringify(imported.localName)}: (rpc: import("@sunder/sdk").RpcClient | import("@sunder/sdk/browser").SunderBrowserRpc) => Object.freeze({${members.join(", ")}})`);
  }
  for (const imported of lock.providerImports.values()) {
    const record = lock.packageArtifacts.get(imported.packageId)!.contracts.get(imported.contractId)!;
    addDependency(imported.packageId, "required"); addDependency(record.authorityId, "required");
    addProvider(imported.key, materialize(record));
  }
  for (const [key, imported] of lock.platformImports) {
    const contract: ArtifactContract = { artifactSource: "imported", authorityKind: "platform", authorityId: "sunder.core",
      selectedAuthorityRelease: imported.selectedAuthorityRelease as string, selectedAuthorityContentIdentity: imported.selectedAuthorityContentIdentity as string,
      contractId: imported.contractId as string, contractVersion: imported.resolvedContractVersion as string, descriptorBytes: imported.descriptorBytes as number,
      descriptorSha256: imported.descriptorSha256 as string, abiSha256: imported.abiSha256 as string, etag: imported.etag as string };
    addProvider(key, materialize(contract));
  }
  function addProvider(key: string, contract: LoadedContract): void {
    providerSurfaces.set(key, contract);
    sources.set(`providers/${key}.ts`, providerBuilderSource(key, contract));
    const alias = `$provider${providerFactories.length}`;
    facadeImports.push(`import { provider as ${alias} } from "./providers/${key}.js";`);
    providerFactories.push(`${JSON.stringify(key)}: Object.freeze({provider: ${alias}})`);
  }
  if (dependencies.size + authoredDependencies.length > 128 || [...sources.values()].reduce((size, source) => size + Buffer.byteLength(source), 0) > 67_108_864)
    contractError("rpc-authoring.aggregate-materializability-overflow", "Imported dependency or generated-source projection exceeds its bound.");
  return { lock, contracts: [...contracts.values()].sort((a, b) => ordinal(a.contractKey!, b.contractKey!)), providerSurfaces, sources,
    facadeSource: `${facadeImports.join("\n")}\nexport const packageApis = Object.freeze({${facades.join(",\n")}});\nexport const rpcImports = Object.freeze({${providerFactories.join(",\n")}});\n`,
    uses: [...uses.values()].sort((a, b) => ordinal(a.contractId, b.contractId)), dependencies: [...dependencies.values()].sort((a, b) => ordinal(a.packageId, b.packageId)) };
}

export function importProjection(imports: PackageImportAnalysis, roles: ReadonlySet<PackageRole>, bindings: readonly ImportedProviderBinding[] = []) {
  if (!roles.has("runtime")) return { packageArtifactImports: [], packageApiImports: [], providerImports: [], platformImports: [] };
  return {
    packageArtifactImports: [...imports.lock.packageArtifacts.values()].map(artifact => ({ packageId: artifact.packageId,
      requestedVersionRange: artifact.requestedVersionRange, includePrerelease: artifact.includePrerelease, selectedPackageVersion: artifact.selectedPackageVersion,
      selectedPackageContentIdentity: artifact.selectedPackageContentIdentity, publicContractCount: artifact.publicContractCount, publicInventorySha256: artifact.publicInventorySha256,
      authorityConstraints: [...new Map([...artifact.contracts.values()].map(contract => [contract.authorityKind + "/" + contract.authorityId, authority(contract)])).entries()]
        .sort(([a], [b]) => ordinal(a, b)).map(([, value]) => value) })),
    packageApiImports: [...imports.lock.packageImports.values()].map(imported => {
      const references = [...imports.lock.packageArtifacts.get(imported.packageId)!.contracts.values()].filter(contract => contract.authorityKind === "package")
        .map(contract => ({ contractId: contract.contractId, contractVersion: contract.contractVersion }));
      return { packageId: imported.packageId, presence: imported.presence, bundleRefs: references, useRefs: references.map(reference => ({ role: "runtime", ...reference })) };
    }), providerImports: [...imports.lock.providerImports.values()].map(imported => {
      const contractVersion = imports.providerSurfaces.get(imported.key)!.descriptor.version;
      return { key: imported.key, packageId: imported.packageId, contractId: imported.contractId, contractVersion,
        bundleRef: { contractId: imported.contractId, contractVersion }, providerRefs: bindings.filter(binding => binding.key === imported.key)
          .map(({ provider }) => ({ role: provider.role, providerId: provider.providerId, contractId: provider.contractId, contractVersion: provider.contractVersion })) };
    }), platformImports: [...imports.lock.platformImports].map(([key, value]) => ({ key,
      authorityKind: value.authorityKind, authorityId: value.authorityId, authorityReleaseRange: value.authorityReleaseRange, includePrerelease: value.includePrerelease,
      selectedAuthorityRelease: value.selectedAuthorityRelease, selectedAuthorityContentIdentity: value.selectedAuthorityContentIdentity,
      contractId: value.contractId, contractVersion: value.resolvedContractVersion, bundleRef: { contractId: value.contractId, contractVersion: value.resolvedContractVersion },
      providerRefs: bindings.filter(binding => binding.key === key).map(({ provider }) => ({ role: provider.role, providerId: provider.providerId,
        contractId: provider.contractId, contractVersion: provider.contractVersion })),
    })),
  };
}
export function importReceiptArtifacts(imports: PackageImportAnalysis): unknown[] {
  const raw = JSON.parse(imports.lock.exactBytes.toString("utf8")) as { packageArtifacts: Record<string, Record<string, unknown> & { contracts: Record<string, unknown> }> };
  return [...imports.lock.packageArtifacts.keys()].map(packageId => ({ packageId, ...raw.packageArtifacts[packageId],
    contracts: [...imports.lock.packageArtifacts.get(packageId)!.contracts.keys()].map(contractId => ({ contractId, ...(raw.packageArtifacts[packageId]!.contracts[contractId] as object) })) }));
}
export function importedFacadeReceipt(imports: PackageImportAnalysis) {
  return [...imports.lock.packageImports.values()].map(value => ({ packageId: value.packageId, localName: value.localName, logicalName: `packageApis.${value.localName}` }));
}

function providerBuilderSource(key: string, contract: LoadedContract): string {
  const model = authoringBindingModel(contract.descriptor);
  const methods = Object.fromEntries(model.services.map(service => [service.descriptor.serviceId, service.methods.map(method => method.descriptor.methodId)]));
  const handlers = model.services.map(service => `${JSON.stringify(service.descriptor.serviceId)}: {${service.methods.map(method => {
    const type = `binding.${service.providerTypeName}[${JSON.stringify(method.methodName)}]`;
    return `${JSON.stringify(method.descriptor.methodId)}: ${method.descriptor.kind === "unary" ? `(...args: Parameters<${type}>) => Awaited<ReturnType<${type}>> | ReturnType<${type}>` : type}`;
  }).join("; ")}}`).join("; ");
  return `// <auto-generated />
import type * as binding from "../imported/${contract.descriptor.contractId}.js";
import type { RpcImportedProvider } from "@sunder/sdk";
export type Handlers = {${handlers}};
export function provider(value: { readonly id?: string; readonly audience?: "owner" | "public"; readonly handlers: Handlers }): RpcImportedProvider<Handlers> {
  const record = (input: unknown): Record<string, unknown> => {
    if (input === null || typeof input !== "object" || Array.isArray(input)) throw new Error("RPC imported provider shape mismatch.");
    const descriptors = Object.getOwnPropertyDescriptors(input);
    if (Reflect.ownKeys(input).length !== Object.keys(descriptors).length || Object.values(descriptors).some(item => !("value" in item))) throw new Error("RPC imported provider shape mismatch.");
    return input as Record<string, unknown>;
  };
  const keys = (input: Record<string, unknown>, expected: readonly string[]): void => {
    if (JSON.stringify(Object.getOwnPropertyNames(input).sort()) !== JSON.stringify([...expected].sort())) throw new Error("RPC imported provider shape mismatch.");
  };
  const input = record(value);
  keys(input, ["handlers", ...["id", "audience"].filter(name => Object.hasOwn(input, name))]);
  const handlers = record(input.handlers);
  const methods: Readonly<Record<string, readonly string[]>> = ${JSON.stringify(methods)};
  keys(handlers, Object.keys(methods));
  for (const [service, names] of Object.entries(methods)) {
    const implementation = record(handlers[service]); keys(implementation, names);
    if (names.some(name => typeof implementation[name] !== "function")) throw new Error("RPC imported provider shape mismatch.");
    Object.freeze(implementation);
  }
  Object.freeze(handlers); Object.freeze(input);
  return Object.freeze({importKey: ${JSON.stringify(key)}, ...value});
}
`;
}

function contractApiSource(contract: LoadedContract): string {
  const client = authoringBindingModel(contract.descriptor).clientFacadeTypeName;
  return `// <auto-generated />
import * as binding from "../imported/${contract.descriptor.contractId}.js";
import { RpcError, isVersionInRange } from "@sunder/sdk/browser";
import type { RpcClient, RpcProviderSnapshot, RpcCallScope } from "@sunder/sdk";
import type { SunderBrowserRpc, BrowserRpcProvider } from "@sunder/sdk/browser";
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
  async *watch(afterRevision: number, afterSequence: number, options?: RpcCallOptions) {
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
