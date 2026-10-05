import { randomUUID } from "node:crypto";
import { link, lstat, mkdir, open, realpath, rename, rmdir, unlink } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, join, parse, resolve, sep } from "node:path";
import { canonicalRpcDescriptorBytes, computeRpcAbiSha256, parseRpcContractDescriptor, type RpcContractDescriptor } from "@sunder/sdk";
import { readProjectDescriptorFile } from "./descriptor-files";
import { withOutputLeases } from "./output";
import { fields, outerBytes, parseOuter } from "./rpc-baseline-json";
import { contractError, hashBytes, parseContractWireJson, readRequirement, type CacheRequirement } from "./rpc-contract-lock";

export const CONTRACT_CACHE_MARKER = ".sunder-contract-cache";
export const CONTRACT_CACHE_MARKER_TEXT = "sunder.contract-cache.v1\n";
export const DEFAULT_CONTRACT_CACHE = process.env.SUNDER_CONTRACT_CACHE ?? join(homedir(), ".sunder", "contracts", "v1", "sha256");
const MAXIMUM_DESCRIPTOR = 4_194_304;
const RECEIPT_SUFFIX = ".receipt.json";
interface CacheEntry { readonly descriptorSha256: string; readonly previousDescriptorSha256: string; readonly previousReceiptSha256: string }
interface Intent { readonly schemaVersion: 1; readonly token: string; readonly entries: readonly CacheEntry[] }
export interface ResolvedCacheEntry { readonly requirement: CacheRequirement; readonly descriptor: RpcContractDescriptor; readonly canonicalBytes: Buffer }
interface PublicationOptions { readonly signal?: AbortSignal; readonly checkpoint?: (point: string) => void | Promise<void> }
function corrupt(message: string): never { return contractError("contract-cache.entry-corrupt", message); }
function missing(message: string): never { return contractError("contract-cache.entry-missing", `${message} Run sunder-package contracts restore.`); }
const isHash = (value: unknown): value is string => typeof value === "string" && /^[a-f0-9]{64}$/u.test(value);
const pathFor = (root: string, hash: string, receipt = false): string => {
  if (!isHash(hash)) return corrupt("Cache path requires a lowercase SHA-256 identity.");
  return join(root, hash + (receipt ? RECEIPT_SUFFIX : ".json"));
};
export function receiptBytes(value: CacheRequirement): Buffer {
  return outerBytes({ schemaVersion: 1, contractId: value.contractId, contractVersion: value.contractVersion,
    descriptorBytes: value.descriptorBytes, descriptorSha256: value.descriptorSha256, abiSha256: value.abiSha256 });
}
export async function cachePath(path: string): Promise<string> {
  path = resolve(path);
  if (process.platform === "darwin") for (const alias of ["/var", "/tmp", "/etc"]) if (path === alias || path.startsWith(alias + "/")) {
    if (await realpath(alias) !== `/private${alias}`) return corrupt("Unexpected OS path alias.");
    path = `/private${path}`; break;
  }
  await safePath(path); return path;
}
async function exists(path: string): Promise<boolean> {
  try { await lstat(path); return true; } catch (error) { if ((error as NodeJS.ErrnoException).code === "ENOENT") return false; throw error; }
}
async function safePath(path: string): Promise<void> {
  const root = parse(path).root; let current = root;
  const segments = path.slice(root.length).split(sep).filter(Boolean);
  for (const [index, segment] of segments.entries()) {
    current = join(current, segment);
    if (!await exists(current)) return;
    const stat = await lstat(current);
    if (stat.isSymbolicLink() || index + 1 < segments.length && !stat.isDirectory()) corrupt("Cache paths cannot contain symbolic links or non-directory ancestors.");
  }
}
async function regularDirectory(path: string): Promise<void> {
  await safePath(path); const stat = await lstat(path);
  if (!stat.isDirectory() || stat.isSymbolicLink()) corrupt("Cache directory is not a regular directory.");
}
async function read(path: string, maximum = MAXIMUM_DESCRIPTOR): Promise<Buffer> {
  await safePath(path); return readProjectDescriptorFile(dirname(path), path, "Contract cache file", maximum);
}
async function validateRoot(root: string, complete = false, allowCollectionRecovery = false): Promise<void> {
  await regularDirectory(root);
  const marker = join(root, CONTRACT_CACHE_MARKER);
  if (!await exists(marker) || (await read(marker, 128)).toString() !== CONTRACT_CACHE_MARKER_TEXT) corrupt("Cache ownership marker is missing or invalid.");
  if (complete && await exists(join(root, ".pending"))) corrupt("A cache publication is incomplete. Run explicit contracts restore to recover it.");
  if (!allowCollectionRecovery && await exists(join(root, ".gc-pending"))) corrupt("Cache collection is incomplete. Run explicit contracts gc with the original reviewed locks to recover it.");
}
async function initializeRoot(root: string): Promise<void> {
  await safePath(root); await mkdir(root, { recursive: true }); await regularDirectory(root);
  const marker = join(root, CONTRACT_CACHE_MARKER);
  if (!await exists(marker)) {
    if ((await boundedNames(root, 1)).length) corrupt("Cannot adopt an unmarked nonempty cache.");
    await writeNew(marker, Buffer.from(CONTRACT_CACHE_MARKER_TEXT));
  }
  await validateRoot(root);
}
export async function verifyCacheDescriptor(requirement: CacheRequirement, bytes: Buffer): Promise<RpcContractDescriptor> {
  const descriptor = parseRpcContractDescriptor(bytes);
  if (bytes.length !== requirement.descriptorBytes || hashBytes(bytes) !== requirement.descriptorSha256
    || descriptor.contractId !== requirement.contractId || descriptor.version !== requirement.contractVersion
    || await computeRpcAbiSha256(descriptor) !== requirement.abiSha256 || !Buffer.from(canonicalRpcDescriptorBytes(descriptor)).equals(bytes)) corrupt("Descriptor bytes do not reproduce their exact canonical requirement.");
  return descriptor;
}
async function verifyPair(root: string, hash: string): Promise<ResolvedCacheEntry> {
  const receipt = fields(parseOuter(await read(pathFor(root, hash, true), 16_384)), ["schemaVersion", "contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256"]);
  if (receipt.schemaVersion !== 1 || receipt.descriptorSha256 !== hash) corrupt("Cache receipt identity differs from its content address.");
  const requirement = readRequirement(receipt); const bytes = await read(pathFor(root, hash));
  return { requirement, descriptor: await verifyCacheDescriptor(requirement, bytes), canonicalBytes: bytes };
}
export async function readContractCache(root: string, requirements: ReadonlyMap<string, CacheRequirement>): Promise<ReadonlyMap<string, ResolvedCacheEntry>> {
  if (!requirements.size) return new Map();
  if (requirements.size > 128) corrupt("Cache read exceeds the materialization bound.");
  root = await cachePath(root);
  if (!await exists(root)) return missing("The contract cache is absent.");
  return withOutputLeases([root], async () => {
    await validateRoot(root, true); const result = new Map<string, ResolvedCacheEntry>();
    for (const [hash, requirement] of [...requirements].sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) {
      if (!await exists(pathFor(root, hash)) || !await exists(pathFor(root, hash, true))) missing(`Contract ${requirement.contractId} ${requirement.contractVersion} is missing.`);
      try {
        const entry = await verifyPair(root, hash);
        if (!receiptBytes(entry.requirement).equals(receiptBytes(requirement))) corrupt("Cache receipt differs from the lock.");
        result.set(hash, entry);
      } catch (error) { return corrupt(`Contract ${requirement.contractId} ${requirement.contractVersion} has invalid cache evidence.`); }
    }
    return result;
  }, 30_000);
}
export async function publishContractCache(root: string, entries: ReadonlyMap<string, Buffer>, requirements: ReadonlyMap<string, CacheRequirement>, options: PublicationOptions = {}): Promise<void> {
  if (entries.size < 1 || entries.size > 128) corrupt("Publication requires 1..128 entries.");
  for (const [hash, bytes] of entries) {
    const requirement = requirements.get(hash);
    if (!requirement || requirement.descriptorSha256 !== hash) corrupt("Publication has an unrequested entry.");
    await verifyCacheDescriptor(requirement, bytes);
  }
  options.signal?.throwIfAborted(); root = await cachePath(root);
  await withOutputLeases([root], async () => {
    options.signal?.throwIfAborted(); await initializeRoot(root); await recover(root, options);
    const selected: CacheEntry[] = [];
    for (const [hash, bytes] of [...entries].sort(([a], [b]) => a < b ? -1 : 1)) {
      const previousDescriptorSha256 = await existingHash(pathFor(root, hash));
      const previousReceiptSha256 = await existingHash(pathFor(root, hash, true));
      if (previousDescriptorSha256 !== hashBytes(bytes) || previousReceiptSha256 !== hashBytes(receiptBytes(requirements.get(hash)!)))
        selected.push({ descriptorSha256: hash, previousDescriptorSha256, previousReceiptSha256 });
    }
    if (!selected.length) return;
    const intent: Intent = { schemaVersion: 1, token: randomUUID(), entries: selected }; const stage = join(root, `.stage-${intent.token}`);
    await mkdir(stage);
    try {
      for (const entry of selected) {
        options.signal?.throwIfAborted(); const hash = entry.descriptorSha256;
        await writeNew(pathFor(stage, hash), entries.get(hash)!); await writeNew(pathFor(stage, hash, true), receiptBytes(requirements.get(hash)!));
        await verifyPair(stage, hash);
      }
      await writeNew(join(stage, "intent.json"), Buffer.from(JSON.stringify(intent)));
      options.signal?.throwIfAborted(); const pending = join(root, ".pending"); await safePath(pending);
      if (await exists(pending)) corrupt("A pending transaction appeared during publication.");
      await rename(stage, pending); await options.checkpoint?.("prepared"); await recover(root, options);
    } finally { if (await exists(stage)) await deleteFlat(stage); }
  }, 30_000);
}
async function recover(root: string, options: PublicationOptions): Promise<void> {
  const pending = join(root, ".pending"); if (!await exists(pending)) return;
  await regularDirectory(pending);
  const bytes = await read(join(pending, "intent.json"), 65_536);
  const intent = parseContractWireJson(bytes, 65_536) as Intent;
  if (intent?.schemaVersion !== 1 || typeof intent.token !== "string" || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(intent.token)
    || !Array.isArray(intent.entries) || intent.entries.length < 1 || intent.entries.length > 128
    || !bytes.equals(Buffer.from(JSON.stringify({ schemaVersion: intent.schemaVersion, token: intent.token, entries: intent.entries.map(entry => ({
      descriptorSha256: entry?.descriptorSha256, previousDescriptorSha256: entry?.previousDescriptorSha256, previousReceiptSha256: entry?.previousReceiptSha256 })) })))) corrupt("Cache intent is not canonical, closed or bounded.");
  const expected = new Set(["intent.json"]); let previous = "";
  for (const entry of intent.entries) {
    if (!isHash(entry.descriptorSha256) || entry.descriptorSha256 <= previous
      || entry.previousDescriptorSha256 !== "" && !isHash(entry.previousDescriptorSha256)
      || entry.previousReceiptSha256 !== "" && !isHash(entry.previousReceiptSha256)) corrupt("Cache intent entries have invalid hashes or order.");
    previous = entry.descriptorSha256; expected.add(previous + ".json"); expected.add(previous + RECEIPT_SUFFIX); await verifyPair(pending, previous);
  }
  const names = await boundedNames(pending, 257);
  if (names.length !== expected.size || names.some(name => !expected.has(name))) corrupt("Cache transaction contains unexpected files.");
  const quarantine = join(root, ".quarantine", intent.token);
  for (const entry of intent.entries) {
    await install(entry.descriptorSha256 + ".json", entry.previousDescriptorSha256);
    await install(entry.descriptorSha256 + RECEIPT_SUFFIX, entry.previousReceiptSha256);
  }
  await options.checkpoint?.("installed"); const completed = join(root, `.completed-${intent.token}`); await safePath(completed);
  if (await exists(completed)) corrupt("Cache completion tombstone already exists.");
  await rename(pending, completed); await options.checkpoint?.("committed"); await deleteFlat(completed);

  async function install(name: string, previousHash: string): Promise<void> {
    options.signal?.throwIfAborted(); const value = await read(join(pending, name)); const target = join(root, name); const current = await existingHash(target);
    if (current === hashBytes(value)) return;
    if (current !== "") {
      if (current !== previousHash) corrupt("Cache entry changed after planning; its bytes were preserved.");
      await safePath(quarantine); await mkdir(quarantine, { recursive: true }); const evidence = join(quarantine, "intent.json");
      if (!await exists(evidence)) await writeNew(evidence, bytes);
      else if (!(await read(evidence, 65_536)).equals(bytes)) corrupt("Quarantine evidence changed.");
      const preserved = join(quarantine, name); await safePath(preserved);
      if (await exists(preserved)) corrupt("Quarantine entry already exists.");
      await rename(target, preserved); await options.checkpoint?.(`quarantined:${name}`);
    }
    const temporary = join(root, `.write-${randomUUID()}`);
    try {
      await writeNew(temporary, value); await safePath(target);
      // Atomic no-replace publication, including Windows where rename may replace.
      await link(temporary, target); await options.checkpoint?.(`published:${name}`);
    } finally { if (await exists(temporary)) await unlink(temporary); }
  }
}
async function existingHash(path: string): Promise<string> { return await exists(path) ? hashBytes(await read(path)) : ""; }
async function writeNew(path: string, bytes: Buffer): Promise<void> {
  await safePath(path); const file = await open(path, "wx", 0o644);
  try { await file.writeFile(bytes); await file.sync(); } finally { await file.close(); }
}
async function boundedNames(path: string, maximum: number): Promise<string[]> {
  // opendir bounds enumeration before allocating an untrusted directory listing.
  const { opendir } = await import("node:fs/promises"); const directory = await opendir(path); const names: string[] = [];
  for await (const entry of directory) { names.push(entry.name); if (names.length > maximum) corrupt("Cache directory exceeds its entry bound."); }
  return names;
}
async function deleteFlat(path: string): Promise<void> {
  await regularDirectory(path); const names = await boundedNames(path, 257);
  for (const name of names) { const file = join(path, name); await safePath(file); if (!(await lstat(file)).isFile()) corrupt("Cache transaction cleanup encountered a non-file."); }
  for (const name of names) await unlink(join(path, name)); await rmdir(path);
}

// Internal cache maintenance uses the same path/marker/read primitives. Not exported by the package entry point.
export const contractCacheFiles = Object.freeze({ exists, safePath, regularDirectory, read, validateRoot, initializeRoot, verifyPair, writeNew, boundedNames });
