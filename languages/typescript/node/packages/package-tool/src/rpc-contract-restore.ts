import { join } from "node:path";
import { readProjectDescriptorFile } from "./descriptor-files";
import { cachePath, DEFAULT_CONTRACT_CACHE, publishContractCache, readContractCache, verifyCacheDescriptor } from "./rpc-contract-cache";
import { contractError, inventoryBytes, parseContractLock, parseContractWireJson, registryOrigin, type CacheRequirement, type ContractLock, type RegistryOriginOptions } from "./rpc-contract-lock";
import { inspectContractCandidate } from "./rpc-contract-candidate";
import { platformRequirements, publishPlatformCatalogs, restorePlatformCatalogs } from "./rpc-platform-catalogs";

export interface ContractRestoreOptions extends RegistryOriginOptions {
  readonly cacheRoot?: string; readonly candidate?: string; readonly platformCatalog?: string;
  readonly signal?: AbortSignal; readonly fetchImplementation?: typeof fetch;
}

/** Exact existing-lock restore. No source module is evaluated and no selection is rewritten. */
export async function restoreContracts(project: string, options: ContractRestoreOptions = {}): Promise<number> {
  try { return await restoreCore(project, options); }
  catch (error) {
    if (error instanceof Error && /^(?:package-api|contract-cache|rpc-authoring|platform-api)\.[a-z-]+:/u.test(error.message)) throw error;
    throw new Error("package-api.public-inventory-invalid: Restore could not validate its bounded Registry, lock or cache input.", { cause: error });
  }
}

async function restoreCore(project: string, options: ContractRestoreOptions): Promise<number> {
  if (options.candidate !== undefined && (typeof options.candidate !== "string" || !options.candidate.trim()))
    return contractError("package-api.public-inventory-invalid", "An explicit candidate path must be nonempty; offline restore cannot fall back to network.");
  if (options.platformCatalog !== undefined && (typeof options.platformCatalog !== "string" || !options.platformCatalog.trim()))
    return contractError("platform-api.catalog-invalid", "An explicit platform catalog input must be nonempty; offline restore cannot fall back to network.");
  project = await cachePath(project);
  const lockPath = join(project, "sunder-contracts.lock.json");
  const readLock = (): Promise<Buffer> => readProjectDescriptorFile(project, lockPath, "Contract lock", 33_554_432);
  const locked = parseContractLock(await readLock(), options.allowDevelopmentRegistryOrigin);
  return hydrateContractLock(locked, options, async () => {
    if (!(await readLock()).equals(locked.exactBytes)) contractError("rpc-authoring.evidence-mismatch", "The contract lock changed during restore.");
  });
}

/** Tool-private proposed-lock hydration; callers retain the physical selection commit fence. */
export async function hydrateContractLock(input: ContractLock, options: ContractRestoreOptions, validateInputs: () => Promise<void>): Promise<number> {
  const locked = parseContractLock(input.exactBytes, options.allowDevelopmentRegistryOrigin);
  const platforms = await restorePlatformCatalogs(locked, options.cacheRoot ?? DEFAULT_CONTRACT_CACHE, options);
  const entries = new Map<string, Buffer>();
  for (const catalog of platforms.values()) for (const contract of catalog.contracts) {
    const requirement = locked.requirements.get(contract.descriptorSha256);
    if (requirement) await add(entries, requirement, Buffer.from(contract.canonicalDescriptor));
  }
  let verifyCandidate: (() => Promise<void>) | undefined;
  if (options.candidate !== undefined) {
    const inspected = await inspectContractCandidate(options.candidate, options.signal);
    const artifact = locked.packageArtifacts.get(String(inspected.inventory.packageId));
    if (!artifact) contractError("package-api.import-not-found", "Candidate must identify an existing locked package artifact.");
    if (inspected.publicInventorySha256 !== artifact.publicInventorySha256 || !Buffer.from(JSON.stringify(inspected.inventory)).equals(inventoryBytes(artifact)))
      contractError("package-api.public-inventory-invalid", "Candidate does not reproduce the committed complete artifact inventory.");
    for (const contract of artifact.contracts.values()) {
      const required = locked.requirements.get(contract.descriptorSha256); if (!required) continue;
      const bytes = inspected.descriptors.get(contract.descriptorSha256);
      if (!bytes) contractError("contract-cache.entry-missing", "Candidate omitted a locked public descriptor.");
      await add(entries, required, bytes);
    }
    verifyCandidate = inspected.revalidate;
  } else if (options.platformCatalog === undefined) {
    const request = options.fetchImplementation ?? fetch;
    for (const artifact of locked.packageArtifacts.values()) {
      if (artifact.registryOrigin === undefined) continue;
      const route = `api/v1/packages/${encodeURIComponent(artifact.packageId)}/versions/${encodeURIComponent(artifact.selectedPackageVersion)}/public-api`;
      const envelope = record(parseContractWireJson(await readResponse(new URL(route, artifact.registryOrigin), "application/json", 1_048_576, undefined, request, options.signal, undefined, undefined, options.allowDevelopmentRegistryOrigin), 1_048_576));
      if (JSON.stringify(Object.keys(envelope)) !== JSON.stringify(["inventory", "publicInventorySha256"])
        || envelope.publicInventorySha256 !== artifact.publicInventorySha256
        || !Buffer.from(JSON.stringify(envelope.inventory)).equals(inventoryBytes(artifact)))
        contractError("package-api.public-inventory-invalid", "Registry input does not reproduce the committed complete artifact inventory.");
      for (const contract of artifact.contracts.values()) {
        const requirement = locked.requirements.get(contract.descriptorSha256); if (!requirement || contract.authorityKind !== "package") continue;
        const metadataRoute = `api/v1/contracts/${encodeURIComponent(contract.contractId)}/versions/${encodeURIComponent(contract.contractVersion)}`;
        const metadata = record(parseContractWireJson(await readResponse(new URL(metadataRoute, artifact.registryOrigin), "application/json", 65_536, undefined, request, options.signal, undefined, undefined, options.allowDevelopmentRegistryOrigin), 65_536));
        if (metadata.contractId !== contract.contractId || metadata.version !== contract.contractVersion
          || metadata.descriptorSha256 !== contract.descriptorSha256 || metadata.abiSha256 !== contract.abiSha256
          || metadata.exposure !== "public" || metadata.firstPublisherPackageId !== contract.authorityId
          || metadata.size !== contract.descriptorBytes || metadata.etag !== contract.etag)
          contractError("package-api.public-inventory-invalid", "Registry descriptor metadata differs from locked public authority.");
        const bytes = await readResponse(new URL(metadataRoute + "/descriptor", artifact.registryOrigin), "application/schema+json", contract.descriptorBytes, contract.etag, request, options.signal, undefined, undefined, options.allowDevelopmentRegistryOrigin);
        await add(entries, requirement, bytes);
      }
    }
  }
  // Explicit offline inputs never trigger acquisition of other imports. They
  // still must leave a complete materializable selection, using verified cache
  // evidence for any descriptors not supplied by this invocation.
  const remaining = new Map([...locked.requirements].filter(([hash]) => !entries.has(hash)));
  try { await readContractCache(options.cacheRoot ?? DEFAULT_CONTRACT_CACHE, remaining); }
  catch (error) {
    if (error instanceof Error && error.message.startsWith("contract-cache.entry-missing:"))
      throw new Error("contract-cache.entry-missing: Supply the exact local package candidate and platform catalog, or restore the remaining descriptor cache evidence; no Registry is inferred.", { cause: error });
    throw error;
  }
  await validateInputs();
  await verifyCandidate?.();
  if (entries.size) await publishContractCache(options.cacheRoot ?? DEFAULT_CONTRACT_CACHE, entries, locked.requirements,
    options.signal === undefined ? {} : { signal: options.signal });
  if (platforms.size) await publishPlatformCatalogs(options.cacheRoot ?? DEFAULT_CONTRACT_CACHE,
    new Map([...platforms].map(([hash, catalog]) => [hash, Buffer.from(catalog.getCanonicalUtf8())])), platformRequirements(locked),
    options.signal === undefined ? {} : { signal: options.signal });
  return entries.size;
}

function record(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return contractError("package-api.public-inventory-invalid", "Registry metadata must be an object.");
  return value as Record<string, unknown>;
}
async function add(entries: Map<string, Buffer>, requirement: CacheRequirement, bytes: Buffer): Promise<void> {
  await verifyCacheDescriptor(requirement, bytes);
  const previous = entries.get(requirement.descriptorSha256);
  if (previous && !previous.equals(bytes)) contractError("contract-cache.entry-corrupt", "Restored sources disagree on an immutable descriptor.");
  entries.set(requirement.descriptorSha256, bytes);
}
export async function readResponse(url: URL, mediaType: string, maximum: number, etag: string | undefined, request: typeof fetch, caller?: AbortSignal,
  validateHeaders?: (response: Response) => void, notFoundCode?: string, allowDevelopmentRegistryOrigin = false): Promise<Buffer> {
  if (url.username || url.password) contractError("package-api.registry-origin-invalid", "Registry origins cannot contain credentials.");
  registryOrigin(url.origin + "/", allowDevelopmentRegistryOrigin);
  const timeout = AbortSignal.timeout(15_000); const signal = caller === undefined ? timeout : AbortSignal.any([caller, timeout]);
  const response = await request(url, { headers: { accept: mediaType }, redirect: "error", signal });
  if (response.status === 404 && notFoundCode !== undefined) { await response.body?.cancel(); contractError(notFoundCode, "The selected Registry package was not found."); }
  const length = response.headers.get("content-length");
  const valid = response.status === 200 && response.url === url.href && !response.redirected
    && response.headers.get("content-type")?.split(";", 1)[0]?.trim() === mediaType
    && (length === null || /^(?:0|[1-9][0-9]*)$/u.test(length) && length.length <= 10 && Number(length) <= maximum)
    && (etag === undefined || response.headers.get("etag") === etag);
  if (!valid || !response.body) {
    await response.body?.cancel();
    return contractError("package-api.public-inventory-invalid", "Registry response status, origin, media type, length or immutable ETag is invalid.");
  }
  try { validateHeaders?.(response); } catch (error) { await response.body.cancel(); throw error; }
  const reader = response.body.getReader(); const chunks: Buffer[] = []; let received = 0;
  const abort = (): void => { void reader.cancel(signal.reason).catch(() => undefined); };
  signal.addEventListener("abort", abort, { once: true });
  try {
    while (true) {
      signal.throwIfAborted(); const next = await reader.read(); signal.throwIfAborted();
      if (next.done) break;
      received += next.value.byteLength;
      if (received > maximum) contractError("contract-cache.entry-corrupt", "Restored response exceeds its byte limit.");
      chunks.push(Buffer.from(next.value));
    }
    if (length !== null && received !== Number(length)) contractError("contract-cache.entry-corrupt", "Restored response differs from its declared length.");
    return Buffer.concat(chunks, received);
  } finally {
    signal.removeEventListener("abort", abort); await reader.cancel().catch(() => undefined); reader.releaseLock();
  }
}
