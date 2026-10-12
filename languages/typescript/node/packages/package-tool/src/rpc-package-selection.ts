import { isPackageId, isPackageVersionRange, isSemanticVersion, isVersionInRange } from "@sunder/sdk";
import { contractError, inventoryBytes, parseContractLock, parseContractWireJson, registryOrigin, type PackageArtifact, type RegistryOriginOptions } from "./rpc-contract-lock";
import { ordered, outerBytes, type Outer, type OuterObject } from "./rpc-baseline-json";
import { inspectContractCandidate, type InspectedContractCandidate } from "./rpc-contract-candidate";
import { readResponse } from "./rpc-contract-restore";
import { acquireContractCandidate } from "./rpc-contract-acquisition";

export interface PackageSelectionOptions extends RegistryOriginOptions {
  readonly versionRange: string; readonly includePrerelease: boolean; readonly registryOrigin?: string | undefined; readonly candidate?: string;
  readonly fetchImplementation?: typeof fetch; readonly signal?: AbortSignal;
  /** Tool-private operation lifetime; publication waits for the complete proposed lock. */
  readonly candidates?: Map<string, InspectedContractCandidate>;
}
export async function selectPackageArtifact(packageId: string, options: PackageSelectionOptions): Promise<OuterObject> {
  if (!isPackageId(packageId) || !isPackageVersionRange(options.versionRange) || options.versionRange.includes("  "))
    contractError("package-api.no-compatible-version", "Selection requires a canonical package ID and explicit reviewed range.");
  const origin = options.registryOrigin === undefined ? undefined : registryOrigin(options.registryOrigin, options.allowDevelopmentRegistryOrigin);
  if (origin === undefined && options.candidate === undefined)
    contractError("package-api.local-input-required", "A local package selection requires an explicit candidate archive; no Registry is inferred.");
  const selectedVersion = options.candidate === undefined ? await resolveVersion(packageId, options, options.fetchImplementation ?? fetch) : undefined;
  const candidate = options.candidate !== undefined ? await inspectContractCandidate(options.candidate, options.signal)
    : await acquireContractCandidate(packageId, selectedVersion!, origin!, options);
  try {
  const inventory = candidate.inventory; const inventoryHash = candidate.publicInventorySha256;
  if (selectedVersion !== undefined && inventory.packageVersion !== selectedVersion) invalid("Canonical candidate differs from the selected exact version.");
  const source = record(inventory);
  if (source.packageId !== packageId || !Array.isArray(source.contracts) || source.contracts.length > 128) invalid("Selected inventory identity/count is invalid.");
  if (source.contracts.length === 0) contractError("package-api.public-api-empty", "Selected artifact has no public RPC contracts.");
  const contractEntries: Array<[string, Outer]> = [];
  for (const value of source.contracts) {
    const contract = record(value);
    if (typeof contract.contractId !== "string" || contractEntries.some(([id]) => id === contract.contractId)) invalid("Inventory has duplicate or missing contract IDs.");
    contractEntries.push([contract.contractId, { artifactSource: contract.artifactSource, authorityKind: contract.authorityKind, authorityId: contract.authorityId,
      selectedAuthorityRelease: contract.selectedAuthorityRelease, selectedAuthorityContentIdentity: contract.selectedAuthorityContentIdentity,
      resolvedContractVersion: contract.contractVersion, descriptorBytes: contract.descriptorBytes, descriptorSha256: contract.descriptorSha256,
      abiSha256: contract.abiSha256, etag: `"${String(contract.descriptorSha256)}"` } as OuterObject]);
  }
  const artifact: OuterObject = { requestedVersionRange: options.versionRange, includePrerelease: options.includePrerelease,
    selectedPackageVersion: source.packageVersion, selectedPackageContentIdentity: source.packageContentIdentity, ...(origin === undefined ? {} : { registryOrigin: origin }),
    publicContractCount: source.publicContractCount, publicInventorySha256: inventoryHash,
    modelProjectionBytes: candidate.modelProjectionBytes.length, modelProjectionSha256: candidate.modelProjectionSha256, contracts: ordered(contractEntries) } as OuterObject;
  const check = parseContractLock(outerBytes({ lockVersion: 1, packageArtifacts: ordered([[packageId, artifact]]),
    packageImports: ordered([[packageId, { localName: "selected", presence: "required", requiredContracts: [], consumerRoles: ["app", "runtime"] }]]), providerImports: {}, platformImports: {} }), options.allowDevelopmentRegistryOrigin);
  if (!inventoryBytes(check.packageArtifacts.get(packageId)!).equals(Buffer.from(JSON.stringify(inventory)))) invalid("Inventory contains omitted, extra or noncanonical metadata.");
  await candidate.revalidate();
  options.candidates?.set(packageId, candidate);
  return artifact;
  } finally { if (options.candidates?.get(packageId) !== candidate && "dispose" in candidate) await (candidate.dispose as () => Promise<void>)(); }
}
async function resolveVersion(packageId: string, options: PackageSelectionOptions, request: typeof fetch): Promise<string> {
  const versions = new Set<string>(); let total: number | undefined; let best: string | undefined;
  for (let skip = 0; skip < 4096; skip += 100) {
    const url = new URL(`api/v1/packages/${encodeURIComponent(packageId)}/versions?skip=${skip}&take=100`, options.registryOrigin);
    const bytes = await readResponse(url, "application/json", 1_048_576, undefined, request, options.signal, response => {
      const count = header(response, "X-Total-Count");
      if (count > 4096 || total !== undefined && total !== count || header(response, "X-Skip") !== skip || header(response, "X-Take") !== 100) invalid("Registry pagination changed or exceeded its bound.");
      total = count;
    }, "package-api.package-not-found", options.allowDevelopmentRegistryOrigin);
    const page = parseContractWireJson(bytes, 1_048_576);
    if (!Array.isArray(page) || page.length !== Math.min(100, total! - skip)) invalid("Registry page disagrees with its pagination.");
    for (const value of page) {
      const item = record(value); const version = item.version;
      if (typeof version !== "string" || !isSemanticVersion(version) || versions.has(version) || typeof item.isYanked !== "boolean") invalid("Registry version history contains invalid or duplicate entries.");
      versions.add(version);
      if (item.isYanked || !options.includePrerelease && version.split("+", 1)[0]!.includes("-") || !isVersionInRange(version, options.versionRange)) continue;
      if (best === undefined || isVersionInRange(version, ">" + best) || isVersionInRange(version, "=" + best) && version > best) best = version;
    }
    if (versions.size === total) break;
  }
  return best ?? contractError("package-api.no-compatible-version", "No non-yanked release satisfies the reviewed range and prerelease policy.");
}
function header(response: Response, key: string): number {
  const value = response.headers.get(key);
  if (value === null || !/^(?:0|[1-9][0-9]*)$/u.test(value) || !Number.isSafeInteger(Number(value))) return invalid("Registry pagination headers must be canonical nonnegative integers."); return Number(value);
}
function record(value: unknown): Record<string, unknown> { if (value === null || typeof value !== "object" || Array.isArray(value)) return invalid("Inventory metadata must be an object."); return value as Record<string, unknown>; }
function invalid(message: string): never { return contractError("package-api.public-inventory-invalid", message); }

export function sameReviewedSelection(artifact: PackageArtifact, request: { versionRange?: string; includePrerelease?: boolean; registryOrigin?: string }): void {
  if (request.versionRange !== undefined && request.versionRange !== artifact.requestedVersionRange
    || request.includePrerelease !== undefined && request.includePrerelease !== artifact.includePrerelease
    || request.registryOrigin !== undefined && request.registryOrigin !== artifact.registryOrigin)
    contractError("package-api.authority-release-conflict", "Shared artifact has different reviewed intent; use targeted update.");
}
