import { createHash } from "node:crypto";
import { isPackageId, isPackageVersionRange, isSemanticVersion, isVersionInRange } from "@sunder/sdk";
import { fields, object, outerKeys, parseOuter, string, type OuterObject } from "./rpc-baseline-json";

export interface RegistryOriginOptions { readonly allowDevelopmentRegistryOrigin?: boolean }

export interface CacheRequirement {
  readonly contractId: string; readonly contractVersion: string; readonly descriptorBytes: number;
  readonly descriptorSha256: string; readonly abiSha256: string;
}
export interface ArtifactContract extends CacheRequirement {
  readonly artifactSource: string; readonly authorityKind: string; readonly authorityId: string;
  readonly selectedAuthorityRelease: string; readonly selectedAuthorityContentIdentity: string;
  readonly etag: string;
}
export interface PackageArtifact {
  readonly packageId: string; readonly requestedVersionRange: string; readonly includePrerelease: boolean;
  readonly selectedPackageVersion: string; readonly selectedPackageContentIdentity: string; readonly registryOrigin: string | undefined;
  readonly publicContractCount: number; readonly publicInventorySha256: string; readonly contracts: ReadonlyMap<string, ArtifactContract>;
}
export interface PackageImport { readonly packageId: string; readonly localName: string; readonly presence: "required" | "optional"; readonly requiredContracts: readonly string[] }
export interface ProviderImport { readonly key: string; readonly packageId: string; readonly contractId: string }
export interface ContractLock {
  readonly exactBytes: Buffer; readonly sha256: string;
  readonly packageArtifacts: ReadonlyMap<string, PackageArtifact>; readonly packageImports: ReadonlyMap<string, PackageImport>;
  readonly providerImports: ReadonlyMap<string, ProviderImport>; readonly platformImports: ReadonlyMap<string, OuterObject>;
  readonly requirements: ReadonlyMap<string, CacheRequirement>;
}
const contractFields = ["artifactSource", "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
  "resolvedContractVersion", "descriptorBytes", "descriptorSha256", "abiSha256", "etag"];
export const hashBytes = (bytes: Uint8Array): string => createHash("sha256").update(bytes).digest("hex");
export function contractError(code: string, message: string): never { throw new Error(`${code}: ${message}`); }
function invalid(message: string): never { return contractError("rpc-authoring.evidence-mismatch", message); }
export function digest(value: unknown): string { if (typeof value !== "string" || !/^[a-f0-9]{64}$/u.test(value)) return invalid("Expected a lowercase SHA-256 digest."); return value; }
function id(value: unknown): string { if (typeof value !== "string" || !isPackageId(value)) return invalid("Expected a canonical package/contract ID."); return value; }
function version(value: unknown): string { if (typeof value !== "string" || !isSemanticVersion(value)) return invalid("Expected an exact semantic version."); return value; }
function integer(value: unknown, min: number, max: number): number { if (typeof value !== "number" || !Number.isSafeInteger(value) || value < min || value > max) return invalid("Integer exceeds its admitted bound."); return value; }
function bool(value: unknown): boolean { if (typeof value !== "boolean") return invalid("Expected an explicit Boolean."); return value; }
function name(value: unknown): string { if (typeof value !== "string" || !/^[a-z][a-zA-Z0-9]{0,127}$/u.test(value)) return invalid("Expected a portable local name."); return value; }
function selected(range: unknown, release: unknown, prerelease: boolean): void {
  if (typeof range !== "string" || !isPackageVersionRange(range) || range.includes("  ") || typeof release !== "string"
    || !isVersionInRange(version(release), range) || !prerelease && release.split("+", 1)[0]!.includes("-")) invalid("Selected release differs from reviewed range/prerelease intent.");
}
export function registryOrigin(value: unknown, allowDevelopment = false): string {
  if (typeof allowDevelopment !== "boolean") return contractError("package-api.registry-origin-invalid", "Development Registry mode must be an explicit Boolean.");
  if (typeof value !== "string") return contractError("package-api.registry-origin-invalid", "An explicit canonical Registry origin is required.");
  let url: URL;
  try { url = new URL(value); } catch { return contractError("package-api.registry-origin-invalid", "Registry origin is invalid."); }
  const dns = url.hostname.length <= 253 && /[a-z]/u.test(url.hostname) && url.hostname.split(".").every(part =>
    part.length <= 63 && !part.startsWith("xn--") && /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/u.test(part));
  if (url.href !== value || url.pathname !== "/" || url.search || url.hash || url.username || url.password || url.port === "0"
    || !(url.protocol === "https:" && dns || allowDevelopment && url.protocol === "http:" && url.hostname === "localhost")) {
    return contractError("package-api.registry-origin-invalid", "Registry origin must be canonical HTTPS or explicitly enabled Development localhost.");
  }
  return value;
}
function optionalRegistryOrigin(value: OuterObject, allowDevelopment: boolean): string | undefined {
  return Object.hasOwn(value, "registryOrigin") ? registryOrigin(value.registryOrigin, allowDevelopment) : undefined;
}
function map(value: OuterObject, field: string): readonly (readonly [string, OuterObject])[] {
  const records = object(value[field]); const keys = outerKeys(records);
  if (keys.length > 128 || keys.some((key, index) => index > 0 && keys[index - 1]! >= key)) invalid("Lock maps must be bounded ordinal sets.");
  return keys.map(key => [key, object(records[key])] as const);
}

export function parseContractLock(bytes: Buffer, allowDevelopment = false): ContractLock {
  if (typeof allowDevelopment !== "boolean") return contractError("package-api.registry-origin-invalid", "Development Registry mode must be an explicit Boolean.");
  if (bytes.length > 33_554_432) invalid("Contract lock exceeds its byte bound.");
  const root = fields(parseOuter(bytes), ["lockVersion", "packageArtifacts", "packageImports", "providerImports", "platformImports"]);
  integer(root.lockVersion, 1, 1);
  const artifacts = new Map<string, PackageArtifact>(); const consumers = new Map<string, PackageImport>();
  const providers = new Map<string, ProviderImport>(); const platforms = new Map<string, OuterObject>();
  const authorities = new Map<string, string>(); const tuples = new Map<string, string>(); const dependencies = new Set<string>();
  const requirements = new Map<string, CacheRequirement>(); const referenced = new Set<string>();
  const authority = (kind: string, identity: string, release: string, content: string): void => {
    join(authorities, `${kind}/${identity}`, JSON.stringify([release, content]), "rpc-authoring.authority-mismatch");
  };
  const evidence = (contract: ArtifactContract): void => {
    authority(contract.authorityKind, contract.authorityId, contract.selectedAuthorityRelease, contract.selectedAuthorityContentIdentity);
    join(tuples, `${contract.contractId}/${contract.contractVersion}`, JSON.stringify([contract.descriptorBytes, contract.descriptorSha256, contract.abiSha256,
      contract.authorityKind, contract.authorityId, contract.selectedAuthorityRelease, contract.selectedAuthorityContentIdentity]), "rpc-authoring.evidence-mismatch");
  };
  const require = (contract: CacheRequirement): void => {
    const previous = requirements.get(contract.descriptorSha256);
    if (previous && (previous.contractId !== contract.contractId || previous.contractVersion !== contract.contractVersion
      || previous.descriptorBytes !== contract.descriptorBytes || previous.abiSha256 !== contract.abiSha256)) invalid("Conflicting descriptor cache requirements.");
    requirements.set(contract.descriptorSha256, { contractId: contract.contractId, contractVersion: contract.contractVersion,
      descriptorBytes: contract.descriptorBytes, descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256 });
  };
  for (const [packageId, raw] of map(root, "packageArtifacts")) {
    id(packageId);
    const order = ["requestedVersionRange", "includePrerelease", "selectedPackageVersion", "selectedPackageContentIdentity", "registryOrigin", "publicContractCount", "publicInventorySha256", "contracts"];
    const value = fields(raw, order.filter(key => key !== "registryOrigin"), ["registryOrigin"], order);
    const requestedVersionRange = string(value.requestedVersionRange); const includePrerelease = bool(value.includePrerelease);
    const selectedPackageVersion = version(value.selectedPackageVersion); const selectedPackageContentIdentity = digest(value.selectedPackageContentIdentity);
    selected(requestedVersionRange, selectedPackageVersion, includePrerelease);
    authority("package", packageId, selectedPackageVersion, selectedPackageContentIdentity);
    const contracts = new Map<string, ArtifactContract>();
    for (const [contractId, rawContract] of map(value, "contracts")) {
      const platform = rawContract.authorityKind === "platform";
      const record = fields(rawContract, contractFields);
      const requirement = readRequirement(record, id(contractId), "resolvedContractVersion");
      const contract: ArtifactContract = { ...requirement, artifactSource: string(record.artifactSource), authorityKind: string(record.authorityKind),
        authorityId: id(record.authorityId), selectedAuthorityRelease: version(record.selectedAuthorityRelease), selectedAuthorityContentIdentity: digest(record.selectedAuthorityContentIdentity),
        etag: string(record.etag) };
      if (!["owned", "imported"].includes(contract.artifactSource) || !["package", "platform"].includes(contract.authorityKind)
        || platform && contract.authorityId !== "sunder.core" || contract.etag !== `"${contract.descriptorSha256}"`
        || contract.artifactSource === "owned" && (platform || contract.authorityId !== packageId || contract.selectedAuthorityRelease !== selectedPackageVersion || contract.selectedAuthorityContentIdentity !== selectedPackageContentIdentity)) invalid("Invalid artifact contract authority or immutable ETag.");
      evidence(contract); contracts.set(contractId, contract);
    }
    const artifact: PackageArtifact = { packageId, requestedVersionRange, includePrerelease, selectedPackageVersion, selectedPackageContentIdentity,
      registryOrigin: optionalRegistryOrigin(value, allowDevelopment), publicContractCount: integer(value.publicContractCount, 1, 128), publicInventorySha256: digest(value.publicInventorySha256), contracts };
    if (contracts.size !== artifact.publicContractCount) invalid("Public inventory count mismatch.");
    const inventoryHash = createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(inventoryBytes(artifact)).digest("hex");
    if (inventoryHash !== artifact.publicInventorySha256) contractError("rpc-authoring.hash-mismatch", "Complete public inventory hash mismatch.");
    artifacts.set(packageId, artifact);
  }
  const consumerNames = new Set<string>(); const providerNames = new Set<string>(); const providerTuples = new Set<string>(); const platformTuples = new Set<string>();
  for (const [packageId, raw] of map(root, "packageImports")) {
    const value = fields(raw, ["localName", "presence", "requiredContracts"]); const artifact = artifacts.get(id(packageId));
    if (!artifact) invalid("Consumer intent has no artifact.");
    const localName = name(value.localName); unique(consumerNames, localName.toLowerCase(), "package-api.local-name-collision");
    const presence = value.presence; if (presence !== "required" && presence !== "optional") invalid("Explicit required/optional presence is mandatory.");
    const requiredContracts = value.requiredContracts;
    if (!Array.isArray(requiredContracts) || requiredContracts.length > 128) invalid("Required contract set exceeds its bound.");
    const required = requiredContracts.map(item => id(item));
    if (required.some((item, index) => index > 0 && required[index - 1]! >= item || artifact.contracts.get(item)?.authorityKind !== "package")) invalid("Required contract set is not a sorted subset of the public package API.");
    const consumable = [...artifact.contracts.values()].filter(contract => contract.authorityKind === "package");
    if (!consumable.length) contractError("package-api.consumer-api-empty", "Selected inventory has no public package-authority contracts.");
    referenced.add(packageId); dependencies.add(packageId);
    for (const contract of consumable) { require(contract); dependencies.add(contract.authorityId); }
    consumers.set(packageId, { packageId, localName, presence, requiredContracts: required });
  }
  for (const [key, raw] of map(root, "providerImports")) {
    name(key); unique(providerNames, key.toLowerCase(), "rpc-authoring.duplicate-projected-key");
    const value = fields(raw, ["packageId", "contractId"]); const packageId = id(value.packageId); const contractId = id(value.contractId);
    unique(providerTuples, `${packageId}/${contractId}`, "rpc-authoring.duplicate-projected-key");
    const contract = artifacts.get(packageId)?.contracts.get(contractId);
    if (!contract || contract.authorityKind !== "package") contractError("package-api.provider-contract-not-public", "Provider intent must select a public package-authority contract.");
    require(contract); referenced.add(packageId); dependencies.add(packageId); dependencies.add(contract.authorityId);
    providers.set(key, { key, packageId, contractId });
  }
  for (const [key, raw] of map(root, "platformImports")) {
    name(key); unique(providerNames, key.toLowerCase(), "rpc-authoring.duplicate-projected-key");
    const order = ["authorityKind", "authorityId", "authorityReleaseRange", "includePrerelease", "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
      "contractId", "resolvedContractVersion", "registryOrigin", "descriptorBytes", "descriptorSha256", "abiSha256", "etag"];
    const value = fields(raw, order.filter(key => key !== "registryOrigin"), ["registryOrigin"], order);
    const requirement = readRequirement(value, id(value.contractId), "resolvedContractVersion");
    if (value.authorityKind !== "platform" || value.authorityId !== "sunder.core" || value.includePrerelease !== false || value.etag !== `"${requirement.descriptorSha256}"`) invalid("Invalid direct platform authority.");
    selected(value.authorityReleaseRange, value.selectedAuthorityRelease, false); optionalRegistryOrigin(value, allowDevelopment);
    unique(platformTuples, requirement.contractId, "rpc-authoring.duplicate-projected-key");
    evidence({ ...requirement, artifactSource: "imported", authorityKind: "platform", authorityId: "sunder.core", etag: string(value.etag),
      selectedAuthorityRelease: version(value.selectedAuthorityRelease), selectedAuthorityContentIdentity: digest(value.selectedAuthorityContentIdentity) });
    require(requirement); platforms.set(key, value);
  }
  if ([...artifacts.keys()].some(key => !referenced.has(key))) contractError("rpc-authoring.unreferenced-artifact", "Unreferenced artifact metadata is not canonical.");
  if (requirements.size > 128 || dependencies.size > 128 || providers.size + platforms.size > 128) contractError("rpc-authoring.aggregate-materializability-overflow", "Lock exceeds the aggregate materialization bound.");
  return { exactBytes: Buffer.from(bytes), sha256: hashBytes(bytes), packageArtifacts: artifacts, packageImports: consumers, providerImports: providers, platformImports: platforms, requirements };
}

export function readRequirement(value: OuterObject, contractId = id(value.contractId), versionField = "contractVersion"): CacheRequirement {
  return { contractId, contractVersion: version(value[versionField]), descriptorBytes: integer(value.descriptorBytes, 2, 4_194_304), descriptorSha256: digest(value.descriptorSha256), abiSha256: digest(value.abiSha256) };
}
export function inventoryBytes(artifact: PackageArtifact): Buffer {
  return Buffer.from(JSON.stringify({ schemaVersion: 1, packageId: artifact.packageId, packageVersion: artifact.selectedPackageVersion,
    packageContentIdentity: artifact.selectedPackageContentIdentity, publicContractCount: artifact.publicContractCount,
    contracts: [...artifact.contracts.values()].map(contract => ({ contractId: contract.contractId, contractVersion: contract.contractVersion,
      descriptorBytes: contract.descriptorBytes, descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256, artifactSource: contract.artifactSource,
      authorityKind: contract.authorityKind, authorityId: contract.authorityId, selectedAuthorityRelease: contract.selectedAuthorityRelease,
      selectedAuthorityContentIdentity: contract.selectedAuthorityContentIdentity })) }));
}
function join(map: Map<string, string>, key: string, value: string, diagnostic: string): void {
  if (map.has(key) && map.get(key) !== value) contractError(diagnostic, "Conflicting immutable authority or RPC evidence."); map.set(key, value);
}
function unique(set: Set<string>, value: string, diagnostic: string): void { if (set.has(value)) contractError(diagnostic, "Duplicate projected identity."); set.add(value); }

// Bounded HTTP JSON, with duplicate/case-colliding properties rejected before allocation.
export function parseContractWireJson(bytes: Buffer, maximum: number): unknown {
  if (bytes.length < 2 || bytes.length > maximum || bytes.subarray(0, 3).equals(Buffer.from([239, 187, 191]))) invalid("Response byte bounds or UTF-8 BOM are invalid.");
  const text = new TextDecoder("utf-8", { fatal: true }).decode(bytes); const stack: Array<Set<string> | null> = [];
  let budget = 0;
  for (let index = 0; index < text.length; index++) {
    const ch = text[index];
    if (ch === '{' || ch === '[') { stack.push(ch === '{' ? new Set() : null); if (stack.length > 16) invalid("Response depth exceeded."); budget++; }
    else if (ch === '}' || ch === ']') stack.pop();
    else if (ch === '"') {
      const start = index++;
      for (; index < text.length; index++) { if (text[index] === '\\') index++; else if (text[index] === '"') break; }
      const value: unknown = JSON.parse(text.slice(start, index + 1));
      if (typeof value !== "string" || /[\ud800-\udfff]/u.test(value)) invalid("Response string is not scalar Unicode.");
      let next = index + 1; while (/\s/u.test(text[next] ?? "x")) next++;
      if (text[next] === ':') {
        const keys = stack[stack.length - 1]; const folded = value.replace(/[A-Z]/gu, ch => ch.toLowerCase());
        if (!keys || keys.has(folded)) invalid("Response has duplicate or case-colliding properties."); keys.add(folded);
      }
      budget++;
    } else if (ch && !/[\s,:]/u.test(ch)) {
      budget++; const start = index; while (index + 1 < text.length && !/[\s,}\]]/u.test(text[index + 1]!)) index++;
      const token = text.slice(start, index + 1);
      if (/^[-0-9]/u.test(token) && (!/^(?:0|[1-9][0-9]*)$/u.test(token) || !Number.isSafeInteger(Number(token)))) invalid("Response numbers must be canonical nonnegative safe integers.");
    }
    if (budget > 32_768) invalid("Response structural budget exceeded.");
  }
  return JSON.parse(text) as unknown;
}
