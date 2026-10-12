import { join } from "node:path";
import { readProjectDescriptorFile } from "./descriptor-files";
import { cachePath, DEFAULT_CONTRACT_CACHE, publishContractCache, readContractCache, readContractVerificationCache, verifyCacheDescriptor } from "./rpc-contract-cache";
import { contractError, inventoryBytes, parseContractLock, registryOrigin, type CacheRequirement, type ContractLock, type RegistryOriginOptions } from "./rpc-contract-lock";
import { inspectContractCandidate, type InspectedContractCandidate } from "./rpc-contract-candidate";
import { platformRequirements, publishPlatformCatalogs, restorePlatformCatalogs } from "./rpc-platform-catalogs";
import { acquireContractCandidate } from "./rpc-contract-acquisition";
import { publishModelProjectionCache, readModelProjectionCache } from "./rpc-model-projection-cache";

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
export async function hydrateContractLock(input: ContractLock, options: ContractRestoreOptions, validateInputs: () => Promise<void>, selectedCandidates: ReadonlyMap<string, InspectedContractCandidate> = new Map()): Promise<number> {
  const locked = parseContractLock(input.exactBytes, options.allowDevelopmentRegistryOrigin);
  const platforms = await restorePlatformCatalogs(locked, options.cacheRoot ?? DEFAULT_CONTRACT_CACHE, options);
  const entries = new Map<string, Buffer>();
  const projections = new Map<string, Buffer>();
  const requirements = new Map(locked.requirements);
  for (const artifact of locked.packageArtifacts.values()) for (const contract of artifact.contracts.values()) requirements.set(contract.descriptorSha256, contract);
  for (const catalog of platforms.values()) for (const contract of catalog.contracts) {
    const requirement = requirements.get(contract.descriptorSha256);
    if (requirement) await add(entries, requirement, Buffer.from(contract.canonicalDescriptor));
  }
  const candidates: InspectedContractCandidate[] = [];
  const acquired: Array<Awaited<ReturnType<typeof acquireContractCandidate>>> = [];
  try {
  const accept = async (inspected: InspectedContractCandidate): Promise<void> => {
    const artifact = locked.packageArtifacts.get(String(inspected.inventory.packageId));
    if (!artifact) contractError("package-api.import-not-found", "Candidate must identify an existing locked package artifact.");
    if (inspected.publicInventorySha256 !== artifact.publicInventorySha256 || !Buffer.from(JSON.stringify(inspected.inventory)).equals(inventoryBytes(artifact))
      || inspected.modelProjectionBytes.length !== artifact.modelProjectionBytes || inspected.modelProjectionSha256 !== artifact.modelProjectionSha256)
      contractError("package-api.public-inventory-invalid", "Candidate does not reproduce the committed complete artifact inventory.");
    for (const contract of artifact.contracts.values()) {
      const required = requirements.get(contract.descriptorSha256)!;
      const bytes = inspected.descriptors.get(contract.descriptorSha256);
      if (!bytes) contractError("contract-cache.entry-missing", "Candidate omitted a locked public descriptor.");
      await add(entries, required, bytes);
    }
    projections.set(artifact.packageId, inspected.modelProjectionBytes); candidates.push(inspected);
  };
  for (const [id, candidate] of selectedCandidates) {
    if (!locked.packageArtifacts.has(id)) contractError("package-api.import-not-found", "Unselected candidate cannot hydrate this lock.");
    await accept(candidate);
  }
  if (options.candidate !== undefined && !selectedCandidates.size) await accept(await inspectContractCandidate(options.candidate, options.signal));
  else if (options.platformCatalog === undefined) {
    for (const artifact of locked.packageArtifacts.values()) {
      if (projections.has(artifact.packageId) || options.candidate !== undefined) continue;
      try {
        const cached = await readContractCache(options.cacheRoot ?? DEFAULT_CONTRACT_CACHE, new Map([...artifact.contracts.values()].map(contract => [contract.descriptorSha256, contract])));
        await readModelProjectionCache(options.cacheRoot ?? DEFAULT_CONTRACT_CACHE, new Map([[artifact.packageId, artifact]]), new Map([...cached].map(([hash, entry]) => [hash, entry.canonicalBytes])));
        continue;
      } catch (error) {
        if (!(error instanceof Error) || !/^contract-cache\.entry-(?:missing|corrupt):/u.test(error.message)) throw error;
      }
      if (artifact.registryOrigin === undefined) continue;
      const candidate = await acquireContractCandidate(artifact.packageId, artifact.selectedPackageVersion, artifact.registryOrigin, options);
      acquired.push(candidate); await accept(candidate);
    }
  }
  // Explicit offline inputs never trigger acquisition of other imports. They
  // still must leave a complete materializable selection, using verified cache
  // evidence for any descriptors not supplied by this invocation.
  const remaining = new Map([...requirements].filter(([hash]) => !entries.has(hash)));
  const allDescriptors = new Map(entries);
  try { for (const [hash, entry] of await readContractVerificationCache(options.cacheRoot ?? DEFAULT_CONTRACT_CACHE, remaining)) allDescriptors.set(hash, entry.canonicalBytes); }
  catch (error) {
    if (error instanceof Error && error.message.startsWith("contract-cache.entry-missing:"))
      throw new Error("contract-cache.entry-missing: Supply the exact local package candidate and platform catalog, or restore the remaining descriptor cache evidence; no Registry is inferred.", { cause: error });
    throw error;
  }
  await readModelProjectionCache(options.cacheRoot ?? DEFAULT_CONTRACT_CACHE, new Map([...locked.packageArtifacts].filter(([id]) => !projections.has(id))), allDescriptors);
  await validateInputs();
  for (const candidate of candidates) await candidate.revalidate();
  const orderedEntries=[...entries].sort(([a],[b])=>a<b?-1:a>b?1:0);
  for(let offset=0;offset<orderedEntries.length;offset+=128) await publishContractCache(options.cacheRoot ?? DEFAULT_CONTRACT_CACHE,new Map(orderedEntries.slice(offset,offset+128)),requirements,
    options.signal === undefined ? {} : { signal: options.signal });
  await publishModelProjectionCache(options.cacheRoot ?? DEFAULT_CONTRACT_CACHE, projections, locked.packageArtifacts, allDescriptors, options.signal);
  if (platforms.size) await publishPlatformCatalogs(options.cacheRoot ?? DEFAULT_CONTRACT_CACHE,
    new Map([...platforms].map(([hash, catalog]) => [hash, Buffer.from(catalog.getCanonicalUtf8())])), platformRequirements(locked),
    options.signal === undefined ? {} : { signal: options.signal });
  await validateInputs(); for (const candidate of candidates) await candidate.revalidate();
  return entries.size;
  } finally { for (const candidate of acquired) await candidate.dispose(); }
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
  signal.throwIfAborted();
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
