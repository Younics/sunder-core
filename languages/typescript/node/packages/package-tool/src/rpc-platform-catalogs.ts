import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import { canonicalRpcDescriptorBytes, isPackageId, isSemanticVersion, isVersionInRange } from "@sunder/sdk";
import { packageToolCompatibility } from "./compatibility";
import { verifyPlatformContractCatalog, type PlatformContractPins, type VerifiedPlatformContractCatalog } from "./platform-catalog";
import { cachePath, contractCacheFiles as files } from "./rpc-contract-cache";
import { contractError, digest, parseContractWireJson, registryOrigin, type ArtifactContract, type ContractLock, type RegistryOriginOptions } from "./rpc-contract-lock";
import type { OuterObject } from "./rpc-baseline-json";
import { readResponse } from "./rpc-contract-restore";
import { withOutputLeases } from "./output";
import { publishCatalogPair, readOwnedCatalogPair } from "./rpc-platform-cache";

export const MAXIMUM_PLATFORM_CATALOG = 67_108_864;
const DIRECTORY = "platform-catalogs";
export interface PlatformRequirement extends PlatformContractPins { readonly registryOrigin: string | undefined; readonly descriptorBytes: number }
export function platformToolRange(): string {
  const [major, minor] = packageToolCompatibility().sdkVersion.split(".");
  return `>=${major}.${minor}.0 <${major}.${BigInt(minor!) + 1n}.0`;
}
export function defaultPlatformKey(contractId: string): string {
  const parts = contractId.split("."); if (parts[0] === "sunder" && parts.length > 1) parts.shift();
  return parts[0]! + parts.slice(1).map(part => part[0]!.toUpperCase() + part.slice(1)).join("");
}
export async function selectPlatformContract(contractId: string, origin: string | undefined, request: typeof fetch = fetch, signal?: AbortSignal,
  allowDevelopmentRegistryOrigin = false, localCatalog?: string): Promise<OuterObject> {
  if (!isPackageId(contractId)) contractError("platform-api.import-not-found", "A canonical platform contract ID is required.");
  if (origin !== undefined) registryOrigin(origin, allowDevelopmentRegistryOrigin);
  const range = platformToolRange(); let catalog: VerifiedPlatformContractCatalog;
  if (localCatalog !== undefined) {
    signal?.throwIfAborted();
    catalog = await verifyPlatformContractCatalog(await files.read(await cachePath(localCatalog), MAXIMUM_PLATFORM_CATALOG));
    if (!isVersionInRange(catalog.releaseVersion, range))
      contractError("platform-api.catalog-version-unsupported", "The local catalog must be within the coordinated SDK/tooling line.");
  } else {
    if (origin === undefined) contractError("platform-api.local-input-required", "A local platform selection requires an explicit catalog; no Registry is inferred.");
    const url = new URL("api/v1/platform-contracts/sunder.core/resolve?versionRange=" + encodeURIComponent(range), origin);
    const metadata = record(parseContractWireJson(await readResponse(url, "application/json", 1_048_576, undefined, request, signal, undefined, "platform-api.import-not-found", allowDevelopmentRegistryOrigin), 1_048_576));
    const release = metadata.releaseVersion;
    if (typeof release !== "string" || !isSemanticVersion(release) || release.split("+", 1)[0]!.includes("-")
      || !isVersionInRange(release, range) || metadata.lifecycleState !== "Supported") invalid("Resolution must select a supported stable catalog within the coordinated SDK/tooling line.");
    catalog = await readMetadataCatalog(metadata, origin, release, [], request, signal, allowDevelopmentRegistryOrigin);
  }
  const contract = catalog.contracts.find(item => item.contractId === contractId);
  if (!contract) contractError("platform-api.import-not-found", "The selected catalog does not contain the requested contract.");
  return { authorityKind: "platform", authorityId: "sunder.core", authorityReleaseRange: range, includePrerelease: false,
    selectedAuthorityRelease: catalog.releaseVersion, selectedAuthorityContentIdentity: catalog.contentIdentity,
    contractId, resolvedContractVersion: contract.contractVersion, ...(origin === undefined ? {} : { registryOrigin: origin }), descriptorBytes: contract.descriptorBytes,
    descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256, etag: `"${contract.descriptorSha256}"` };
}
export function validatePlatformToolRange(lock: ContractLock): void {
  const [major, minor] = packageToolCompatibility().sdkVersion.split(".");
  const lower = `${major}.${minor}.0`; const upper = `${major}.${BigInt(minor!) + 1n}.0`;
  for (const item of platformRequirements(lock)) if (!isVersionInRange(item.releaseVersion, `>=${lower} <${upper}`))
    contractError("platform-api.catalog-version-unsupported", "The platform catalog is outside the coordinated tooling minor line. Upgrade Core tooling.");
  for (const item of lock.platformImports.values()) {
    let below = false; let above = false;
    for (const token of (item.authorityReleaseRange as string).split(" ")) {
      const value = token.replace(/^[<>=]+/u, ""); const exact = !token.startsWith("<") && !token.startsWith(">");
      below ||= (exact || token.startsWith(">")) && isVersionInRange(value, ">=" + lower);
      above ||= (exact || token.startsWith("<")) && (isVersionInRange(value, "<" + upper)
        || token.startsWith("<") && !token.startsWith("<=") && isVersionInRange(value, "=" + upper));
    }
    if (!below || !above) contractError("rpc-authoring.authority-mismatch", "Platform ranges must be contained by the coordinated tooling minor line.");
  }
}
export function platformRequirements(lock: ContractLock): readonly PlatformRequirement[] {
  const result: PlatformRequirement[] = [];
  const add = (contract: ArtifactContract, origin: string | undefined): void => {
    result.push({ registryOrigin: origin, releaseVersion: contract.selectedAuthorityRelease, contentIdentity: contract.selectedAuthorityContentIdentity,
      contractId: contract.contractId, contractVersion: contract.contractVersion, descriptorBytes: contract.descriptorBytes,
      descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256 });
  };
  for (const artifact of lock.packageArtifacts.values()) for (const contract of artifact.contracts.values()) if (contract.authorityKind === "platform") add(contract, artifact.registryOrigin);
  for (const contract of lock.platformImports.values()) result.push({ registryOrigin: contract.registryOrigin as string | undefined,
    releaseVersion: contract.selectedAuthorityRelease as string, contentIdentity: contract.selectedAuthorityContentIdentity as string,
    contractId: contract.contractId as string, contractVersion: contract.resolvedContractVersion as string, descriptorBytes: contract.descriptorBytes as number,
    descriptorSha256: contract.descriptorSha256 as string, abiSha256: contract.abiSha256 as string });
  return [...new Map(result.map(item => [JSON.stringify(item), item])).values()].sort((a, b) => ordinal(a.contentIdentity, b.contentIdentity) || ordinal(a.contractId, b.contractId) || ordinal(a.registryOrigin ?? "", b.registryOrigin ?? ""));
}
export async function verifyPinnedPlatformCatalog(bytes: Buffer, requirements: readonly PlatformRequirement[]): Promise<VerifiedPlatformContractCatalog> {
  let catalog: VerifiedPlatformContractCatalog;
  try { catalog = await verifyPlatformContractCatalog(bytes); }
  catch (error) { throw new Error("platform-api.catalog-invalid: The Registry catalog failed canonical verification.", { cause: error }); }
  for (const requirement of requirements) {
    let descriptor;
    try { descriptor = catalog.verifyPinned(requirement); }
    catch (error) { throw new Error("rpc-authoring.authority-mismatch: Registry catalog differs from the exact lock pins.", { cause: error }); }
    if (canonicalRpcDescriptorBytes(descriptor).byteLength !== requirement.descriptorBytes) invalid("Catalog descriptor length differs from the lock.");
  }
  return catalog;
}
export async function readPlatformCatalogs(lock: ContractLock, root: string): Promise<ReadonlyMap<string, VerifiedPlatformContractCatalog>> {
  const requirements = platformRequirements(lock); const result = new Map<string, VerifiedPlatformContractCatalog>();
  if (!requirements.length) return result;
  root = await cachePath(root);
  if (!await files.exists(root)) missing();
  return withOutputLeases([root], async () => {
    await files.validateRoot(root, true);
    for (const hash of new Set(requirements.map(item => item.contentIdentity))) {
      const path = catalogPath(root, hash); if (!await files.exists(path)) missing();
      result.set(hash, await verifyPinnedPlatformCatalog(await readOwnedCatalogPair(join(root, DIRECTORY), hash), requirements.filter(item => item.contentIdentity === hash)));
    }
    return result;
  }, 30_000);
}

/** Restore exact selected catalogs; restoration never creates a selection. */
export async function restorePlatformCatalogs(lock: ContractLock, root: string, options: RegistryOriginOptions & {
  readonly platformCatalog?: string; readonly candidate?: string; readonly fetchImplementation?: typeof fetch; readonly signal?: AbortSignal;
}): Promise<ReadonlyMap<string, VerifiedPlatformContractCatalog>> {
  const requirements = platformRequirements(lock); const catalogs = new Map<string, VerifiedPlatformContractCatalog>();
  if (!requirements.length) {
    if (options.platformCatalog !== undefined) contractError("platform-api.import-not-found", "Offline catalog restore requires existing platform constraints.");
    return catalogs;
  }
  if (options.platformCatalog !== undefined) {
    const bytes = await files.read(await cachePath(options.platformCatalog), MAXIMUM_PLATFORM_CATALOG);
    const catalog = await verifyPinnedPlatformCatalog(bytes, requirements); catalogs.set(catalog.contentIdentity, catalog); return catalogs;
  }
  if (options.candidate !== undefined) return readPlatformCatalogs(lock, root);
  const request = options.fetchImplementation ?? fetch;
  const groups = new Map<string, PlatformRequirement[]>();
  for (const item of requirements) { const key = item.contentIdentity + "/" + item.registryOrigin; groups.set(key, [...groups.get(key) ?? [], item]); }
  for (const pins of groups.values()) {
    const selected = pins[0]!;
    if (selected.registryOrigin === undefined) {
      const local = (await readPlatformCatalogs(lock, root)).get(selected.contentIdentity);
      if (!local) missing();
      catalogs.set(selected.contentIdentity, local); continue;
    }
    const metadataUri = new URL(`api/v1/platform-contracts/sunder.core/releases/${encodeURIComponent(selected.releaseVersion)}`, selected.registryOrigin);
    const metadata = record(parseContractWireJson(await readResponse(metadataUri, "application/json", 1_048_576, undefined, request, options.signal,
      undefined, "platform-api.import-not-found", options.allowDevelopmentRegistryOrigin), 1_048_576));
    const catalog = await readMetadataCatalog(metadata, selected.registryOrigin, selected.releaseVersion, pins, request, options.signal, options.allowDevelopmentRegistryOrigin);
    const bytes = Buffer.from(catalog.getCanonicalUtf8());
    const previous = catalogs.get(catalog.contentIdentity);
    if (previous && !Buffer.from(previous.getCanonicalUtf8()).equals(bytes)) invalid("Registries disagree on immutable catalog bytes.");
    catalogs.set(catalog.contentIdentity, catalog);
  }
  return catalogs;
}

async function readMetadataCatalog(metadata: Record<string, unknown>, origin: string, release: string,
  pins: readonly PlatformRequirement[], request: typeof fetch, signal?: AbortSignal, allowDevelopmentRegistryOrigin = false): Promise<VerifiedPlatformContractCatalog> {
  const catalogUri = new URL(`api/v1/platform-contracts/sunder.core/releases/${encodeURIComponent(release)}/catalog`, origin);
  const length = metadata.catalogBytes; const hash = digest(metadata.catalogSha256);
  if (metadata.authorityId !== "sunder.core" || metadata.releaseVersion !== release || metadata.catalogDownloadUrl !== catalogUri.href
    || typeof length !== "number" || !Number.isSafeInteger(length) || length < 2 || length > MAXIMUM_PLATFORM_CATALOG) invalid("Catalog metadata differs from the immutable release and canonical URL.");
  const bytes = await readResponse(catalogUri, "application/json", length, `"${hash}"`, request, signal, response => {
    if (response.headers.get("content-length") !== String(length)) invalid("The catalog requires its exact Content-Length.");
  }, undefined, allowDevelopmentRegistryOrigin);
  const catalog = await verifyPinnedPlatformCatalog(bytes, pins);
  if (bytes.length !== length || catalog.contentIdentity !== hash || catalog.releaseVersion !== release) invalid("Registry catalog identity differs from release metadata.");
  const expected = catalog.contracts.map(contract => ({ contractId: contract.contractId, contractVersion: contract.contractVersion,
    descriptorBytes: contract.descriptorBytes, descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256 }));
  if (!Array.isArray(metadata.contracts) || metadata.contracts.length !== expected.length
    || metadata.contracts.some((item, index) => !sameRecord(record(item), expected[index]!))
    || metadata.lifecycleState !== "Supported" && metadata.lifecycleState !== "Revoked") invalid("Registry metadata differs from the complete canonical catalog.");
  return catalog;
}

export async function publishPlatformCatalogs(root: string, envelopes: ReadonlyMap<string, Buffer>, requirements: readonly PlatformRequirement[],
  options: { readonly signal?: AbortSignal; readonly checkpoint?: (point: string) => void | Promise<void> } = {}): Promise<void> {
  if (envelopes.size < 1 || envelopes.size > 128) invalid("Catalog publication exceeds its bound.");
  const selected: Array<{ hash: string; bytes: Buffer }> = [];
  for (const [hash, original] of [...envelopes].sort(([a], [b]) => ordinal(a, b))) {
    const bytes = Buffer.from(original); const pins = requirements.filter(item => item.contentIdentity === hash);
    if (!pins.length) invalid("Unrequested catalog publication.");
    await verifyPinnedPlatformCatalog(bytes, pins); selected.push({ hash, bytes });
  }
  root = await cachePath(root);
  await withOutputLeases([root], async () => {
    options.signal?.throwIfAborted(); await files.initializeRoot(root); await files.validateRoot(root, true);
    const directory = join(root, DIRECTORY); await files.safePath(directory); await mkdir(directory, { recursive: true }); await files.regularDirectory(directory);
    for (const item of selected) {
      await publishCatalogPair(directory, item.hash, item.bytes, options);
    }
  }, 30_000);
}
export function catalogPath(root: string, hash: string): string { return join(root, DIRECTORY, digest(hash) + ".json"); }
function missing(): never { return contractError("contract-cache.entry-missing", "The exact selected platform catalog is absent. Supply --platform-catalog to contracts restore for local selections."); }
function invalid(message: string): never { return contractError("platform-api.catalog-invalid", message); }
function record(value: unknown): Record<string, unknown> { if (value === null || typeof value !== "object" || Array.isArray(value)) return invalid("Expected catalog metadata object."); return value as Record<string, unknown>; }
function sameRecord(left: Record<string, unknown>, right: Record<string, unknown>): boolean { return Object.keys(left).length === Object.keys(right).length && Object.keys(right).every(key => Object.hasOwn(left, key) && left[key] === right[key]); }
function ordinal(left: string, right: string): number { return left < right ? -1 : left > right ? 1 : 0; }
