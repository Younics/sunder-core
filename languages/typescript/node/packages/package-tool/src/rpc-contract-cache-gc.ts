import { lstat, mkdir, rename, rmdir, unlink } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { dirname, isAbsolute, join, relative, sep } from "node:path";
import { withOutputLeases } from "./output";
import { cachePath, contractCacheFiles as fs } from "./rpc-contract-cache";
import { contractError, hashBytes, parseContractLock, parseContractWireJson, readRequirement } from "./rpc-contract-lock";
import { fields, outerBytes, parseOuter, type Outer } from "./rpc-baseline-json";
import { CATALOG_DIRECTORY, MAXIMUM_CATALOG_BYTES, catalogArtifactKey, catalogReceiptBytes, proveCatalogArtifact, readOwnedCatalogPair } from "./rpc-platform-cache";

export interface ContractCacheGcResult { readonly removedEntries: number; readonly removedArtifacts: number; readonly retainedUnprovenArtifacts: number; readonly moreCandidates: boolean }
export interface ContractCacheGcOptions { readonly graceDays?: number; readonly signal?: AbortSignal; readonly checkpoint?: (point: string) => void | Promise<void>; readonly observedNow?: number }
interface GcFile { readonly name: string; readonly sha256: string; readonly bytes: number }
interface GcUnit { readonly kind: "entry" | "catalog" | "artifact"; readonly key: string; readonly files: readonly GcFile[] }
interface GcIntent { readonly schemaVersion: 1; readonly token: string; readonly cutoffUnixMilliseconds: number; readonly reviewedLocks: readonly string[]; readonly units: readonly GcUnit[] }
const PENDING = ".gc-pending";
const RECEIPT = ".receipt.json";
const INTENT = "intent.json";
const MAXIMUM_INTENT = 262_144;
const MAXIMUM_FILE = 4_194_304;
// A complete 128-pair descriptor repair artifact must fit in one batch.
const MAXIMUM_COLLECTION_BYTES = 2_147_483_648;
const DAY = 86_400_000;
function corrupt(message: string): never { return contractError("contract-cache.entry-corrupt", message); }
const digest = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{64}$/u.test(value);
const token = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(value);
const tokenAfter = (value: string, prefix: string): boolean => value.startsWith(prefix) && token(value.slice(prefix.length));
const catalogKey = (hash: string): string => CATALOG_DIRECTORY + "/" + hash;
const isCatalogKey = (key: string): boolean => key.startsWith(CATALOG_DIRECTORY + "/") && digest(key.slice(CATALOG_DIRECTORY.length + 1));
const maximumFileBytes = (key: string): number => isCatalogKey(key) || catalogArtifactKey(key) ? MAXIMUM_CATALOG_BYTES : MAXIMUM_FILE;
const artifactKey = (key: string): boolean => tokenAfter(key, ".stage-") || tokenAfter(key, ".completed-") || tokenAfter(key, ".quarantine/") || tokenAfter(key, ".gc-stage-") || catalogArtifactKey(key);
const compare = (a: string, b: string): number => a < b ? -1 : a > b ? 1 : 0;
const pathKey = (path: string): string => process.platform === "win32" ? path.toLowerCase() : path;
const unitDirectory = (index: number): string => String(index).padStart(4, "0");
function artifactPath(root: string, key: string): string { if (!artifactKey(key)) corrupt("Invalid cache artifact path."); return join(root, ...key.split("/")); }
const sourceFile = (root: string, unit: GcUnit, name: string): string => join(unit.kind === "entry" ? root
  : unit.kind === "catalog" ? join(root, CATALOG_DIRECTORY) : artifactPath(root, unit.key), name);
const intentBytes = (intent: GcIntent): Buffer => outerBytes(intent as unknown as Outer);
function ordered(values: readonly string[]): void {
  if (values.some((value, index) => typeof value !== "string" || index > 0 && values[index - 1]! >= value)) corrupt("GC sets must be ascending and unique.");
}
async function modified(path: string): Promise<number> { await fs.safePath(path); return Math.floor((await lstat(path)).mtimeMs); }
async function regularFile(path: string): Promise<void> { await fs.safePath(path); if (!(await lstat(path)).isFile()) corrupt("GC requires regular files."); }
async function names(path: string, maximum: number): Promise<string[]> { await fs.regularDirectory(path); return fs.boundedNames(path, maximum); }

export async function collectContractCache(root: string, reviewedLocks: readonly string[], options: ContractCacheGcOptions = {}): Promise<ContractCacheGcResult> {
  const grace = options.graceDays === undefined ? 7 : options.graceDays;
  if (reviewedLocks.length < 1 || reviewedLocks.length > 128 || !Number.isInteger(grace) || grace < 1 || grace > 365) corrupt("GC requires 1..128 explicit reviewed locks and a grace period of 1..365 days.");
  root = await cachePath(root); const pathMap = new Map<string, string>();
  for (const input of reviewedLocks) {
    if (typeof input !== "string" || !input.trim()) corrupt("GC requires explicit reviewed lock paths.");
    const path = await cachePath(input); const traversal = relative(root, path);
    if (!traversal || traversal !== ".." && !traversal.startsWith(`..${sep}`) && !isAbsolute(traversal)) corrupt("Reviewed source locks must be outside the cache.");
    await regularFile(path); pathMap.set(pathKey(path), path);
  }
  const paths = [...pathMap.values()].sort(compare);
  options.signal?.throwIfAborted();
  return withOutputLeases(paths, async () => {
    const marks = new Set<string>(); const snapshots: Array<{ path: string; bytes: number; sha256: string }> = []; let total = 0;
    for (const path of paths) {
      options.signal?.throwIfAborted(); const bytes = await fs.read(path, Math.min(33_554_432, 67_108_864 - total)); total += bytes.length;
      const lock = parseContractLock(bytes, true); snapshots.push({ path, bytes: bytes.length, sha256: hashBytes(bytes) });
      for (const artifact of lock.packageArtifacts.values()) for (const contract of artifact.contracts.values()) {
        marks.add(contract.descriptorSha256);
        if (contract.authorityKind === "platform") marks.add(catalogKey(contract.selectedAuthorityContentIdentity));
      }
      for (const contract of lock.platformImports.values()) { marks.add(String(contract.descriptorSha256)); marks.add(catalogKey(String(contract.selectedAuthorityContentIdentity))); }
    }
    const verifyLocks = async (): Promise<void> => {
      for (const expected of snapshots) {
        try {
          const current = await fs.read(expected.path, expected.bytes);
          if (current.length === expected.bytes && hashBytes(current) === expected.sha256) continue;
        } catch { /* Missing, enlarged, linked or replaced locks also invalidate the plan. */ }
        corrupt("Reviewed lock changed during GC; retry against current inputs.");
      }
    };
    const now = options.observedNow ?? Date.now(); const cutoff = now - grace * DAY;
    if (!Number.isSafeInteger(now) || cutoff < 0) corrupt("GC requires a valid current clock.");
    if (!await fs.exists(root)) return result();
    return withOutputLeases([root], async () => {
      await fs.validateRoot(root, true, true); const children = await names(root, 16_384);
      for (const name of children) await fs.safePath(join(root, name));
      for (const name of children.filter(name => tokenAfter(name, ".gc-trash-"))) await cleanupTrash(join(root, name), options);
      if (await fs.exists(join(root, PENDING))) return { ...await resume(root, paths, marks, verifyLocks, now, options), moreCandidates: true };
      const units: GcUnit[] = []; let unproven = 0; let more = false; let count = 0; let selectedBytes = 0;
      const select = (unit: GcUnit): void => {
        const bytes = unit.files.reduce((sum, file) => sum + file.bytes, 0);
        if (units.length === 128 || count + unit.files.length > 1024 || selectedBytes + bytes > MAXIMUM_COLLECTION_BYTES) { more = true; return; }
        units.push(unit); count += unit.files.length; selectedBytes += bytes;
      };
      for (const name of children.sort(compare)) {
        options.signal?.throwIfAborted();
        if (name.length === 69 && name.endsWith(".json") && digest(name.slice(0, 64))) {
          const hash = name.slice(0, 64); if (marks.has(hash)) continue;
          const receipt = hash + RECEIPT;
          if (!await fs.exists(join(root, receipt))) { unproven++; continue; }
          const files = [name, receipt];
          if ((await Promise.all(files.map(file => modified(join(root, file))))).some(time => time > cutoff)) continue;
          try { await fs.verifyPair(root, hash); } catch { unproven++; continue; }
          select({ kind: "entry", key: hash, files: await describe(root, files) });
        } else if (tokenAfter(name, ".stage-") || tokenAfter(name, ".completed-") || tokenAfter(name, ".gc-stage-")) await selectArtifact(name);
        else if (name === ".quarantine") {
          for (const child of (await names(join(root, name), 4096)).sort(compare)) if (token(child)) await selectArtifact(name + "/" + child); else unproven++;
        } else if (name === CATALOG_DIRECTORY) {
          const directory = join(root, name);
          for (const child of (await names(directory, 16_384)).sort(compare)) {
            options.signal?.throwIfAborted(); await fs.safePath(join(directory, child));
            if (child.length === 69 && child.endsWith(".json") && digest(child.slice(0, 64))) {
              const hash = child.slice(0, 64); const key = catalogKey(hash); if (marks.has(key)) continue;
              const receipt = hash + RECEIPT;
              if (!await fs.exists(join(directory, receipt))) { unproven++; continue; }
              const files = [child, receipt];
              if ((await Promise.all(files.map(file => modified(join(directory, file))))).some(time => time > cutoff)) continue;
              let body: Buffer;
              try { body = await readOwnedCatalogPair(directory, hash); } catch { unproven++; continue; }
              const receiptBytes = catalogReceiptBytes(hash, body.length);
              select({ kind: "catalog", key, files: [{ name: child, sha256: hash, bytes: body.length }, { name: receipt, sha256: hashBytes(receiptBytes), bytes: receiptBytes.length }] });
            } else if (catalogArtifactKey(name + "/" + child)) await selectArtifact(name + "/" + child);
            else if (!child.endsWith(RECEIPT) || !digest(child.slice(0, -RECEIPT.length))
              || !await fs.exists(join(directory, child.slice(0, -RECEIPT.length) + ".json"))) unproven++;
          }
        } else if (name.startsWith(".write-") || name.startsWith(".gc-stage-")) unproven++;
      }
      if (!units.length) return result(0, 0, unproven, more);
      units.sort((a, b) => compare(a.key, b.key));
      const intent: GcIntent = { schemaVersion: 1, token: randomUUID(), cutoffUnixMilliseconds: cutoff, reviewedLocks: paths, units };
      const bytes = intentBytes(intent); if (bytes.length > MAXIMUM_INTENT) corrupt("GC intent exceeds its byte bound.");
      const stage = join(root, ".gc-stage-" + intent.token); await mkdir(stage);
      try {
        await fs.writeNew(join(stage, INTENT), bytes); await verifyLocks(); options.signal?.throwIfAborted();
        if (await fs.exists(join(root, PENDING))) corrupt("GC pending transaction appeared during planning.");
        await rename(stage, join(root, PENDING)); await options.checkpoint?.("gc-prepared");
        const completed = await resume(root, paths, marks, verifyLocks, now, options);
        return { ...completed, retainedUnprovenArtifacts: unproven, moreCandidates: more };
      } finally {
        if (await fs.exists(stage)) {
          const remaining = await names(stage, 1);
          if (remaining.length === 1 && remaining[0] === INTENT && (await fs.read(join(stage, INTENT), MAXIMUM_INTENT)).equals(bytes)) await unlink(join(stage, INTENT));
          if (!(await names(stage, 1)).length) await rmdir(stage);
        }
      }
      async function selectArtifact(key: string): Promise<void> {
        const path = artifactPath(root, key); const files = await names(path, 257);
        for (const file of files) await regularFile(join(path, file));
        if (await modified(path) > cutoff || (await Promise.all(files.map(file => modified(join(path, file))))).some(time => time > cutoff)) return;
        if (!await proveArtifact(path, key, files)) { unproven++; return; }
        select({ kind: "artifact", key, files: await describe(path, files, maximumFileBytes(key)) });
      }
    }, 30_000);
  }, 30_000);
}

async function resume(root: string, paths: readonly string[], marks: ReadonlySet<string>, verifyLocks: () => Promise<void>, now: number, options: ContractCacheGcOptions): Promise<ContractCacheGcResult> {
  const pending = join(root, PENDING); const intent = await readIntent(pending); const pathSet = new Set(paths.map(pathKey));
  if (intent.cutoffUnixMilliseconds > now - DAY || intent.reviewedLocks.some(path => !pathSet.has(pathKey(path)))) corrupt("GC recovery requires the original reviewed lock set (additional locks are allowed) and a valid clock.");
  await validateChildren(pending, intent);
  const retained: boolean[] = [];
  for (const unit of intent.units) {
    let keep = unit.kind !== "artifact" && marks.has(unit.key);
    for (const file of unit.files) if (await fs.exists(sourceFile(root, unit, file.name)) && await modified(sourceFile(root, unit, file.name)) > intent.cutoffUnixMilliseconds) keep = true;
    retained.push(keep);
  }
  for (const [index, unit] of intent.units.entries()) for (const file of unit.files) {
    const source = sourceFile(root, unit, file.name); const tombstone = join(pending, unitDirectory(index), file.name);
    if (!await verifyOptional(source, file) && !await fs.exists(tombstone)) corrupt("GC source and tombstone are both missing.");
    await verifyOptional(tombstone, file);
  }
  await verifyLocks(); let entries = 0; let artifacts = 0;
  for (const [index, unit] of intent.units.entries()) {
    const tombstone = join(pending, unitDirectory(index));
    for (const file of unit.files) {
      options.signal?.throwIfAborted();
      const source = sourceFile(root, unit, file.name); const moved = join(tombstone, file.name);
      if (retained[index]) {
        if (!await verifyOptional(source, file)) {
          const parent = dirname(source); await fs.safePath(parent); await mkdir(parent, { recursive: true });
          await move(moved, source);
        } else if (await verifyOptional(moved, file)) await unlink(moved);
      } else if (!await verifyOptional(moved, file)) {
        await mkdir(tombstone, { recursive: true }); await move(source, moved); await options.checkpoint?.(`gc-tombstoned:${unit.key}/${file.name}`);
      } else if (await fs.exists(source)) corrupt("GC encountered duplicate source and tombstone ownership.");
    }
    if (retained[index]) { if (await fs.exists(tombstone)) await rmdir(tombstone); }
    else if (unit.kind !== "artifact") entries++;
    else { if (await fs.exists(artifactPath(root, unit.key))) await rmdir(artifactPath(root, unit.key)); artifacts++; }
  }
  await verifyLocks(); options.signal?.throwIfAborted(); await options.checkpoint?.("gc-moved"); await verifyLocks(); options.signal?.throwIfAborted();
  const trash = join(root, ".gc-trash-" + intent.token); await fs.safePath(trash);
  if (await fs.exists(trash)) corrupt("GC completion tombstone already exists.");
  await rename(pending, trash); await options.checkpoint?.("gc-committed"); await cleanupTrash(trash, options);
  return result(entries, artifacts);
}

async function cleanupTrash(path: string, options: ContractCacheGcOptions): Promise<void> {
  if (!(await names(path, 129)).length) { await rmdir(path); return; }
  const intent = await readIntent(path); if (!path.endsWith(sep + ".gc-trash-" + intent.token)) corrupt("GC trash identity mismatch.");
  await validateChildren(path, intent);
  for (const [index, unit] of intent.units.entries()) for (const file of unit.files) await verifyOptional(join(path, unitDirectory(index), file.name), file);
  for (const [index, unit] of intent.units.entries()) {
    const directory = join(path, unitDirectory(index)); if (!await fs.exists(directory)) continue;
    for (const file of unit.files) {
      options.signal?.throwIfAborted(); const target = join(directory, file.name);
      if (await verifyOptional(target, file)) { await unlink(target); await options.checkpoint?.("gc-deleted:" + file.name); }
    }
    await rmdir(directory);
  }
  await unlink(join(path, INTENT)); await rmdir(path);
}

async function readIntent(directory: string): Promise<GcIntent> {
  await fs.regularDirectory(directory); const bytes = await fs.read(join(directory, INTENT), MAXIMUM_INTENT);
  const value = fields(parseOuter(bytes), ["schemaVersion", "token", "cutoffUnixMilliseconds", "reviewedLocks", "units"]);
  const intent = value as unknown as GcIntent;
  if (intent.schemaVersion !== 1 || !token(intent.token) || !Number.isSafeInteger(intent.cutoffUnixMilliseconds) || intent.cutoffUnixMilliseconds < 0
    || !Array.isArray(intent.reviewedLocks) || intent.reviewedLocks.length < 1 || intent.reviewedLocks.length > 128
    || !Array.isArray(intent.units) || intent.units.length < 1 || intent.units.length > 128) corrupt("GC intent is not canonical and bounded.");
  ordered(intent.reviewedLocks); if (intent.reviewedLocks.some(path => !path.trim() || !isAbsolute(path))) corrupt("GC reviewed lock paths are invalid.");
  ordered(intent.units.map(unit => unit.key)); let count = 0; let totalBytes = 0;
  for (const unit of intent.units) {
    if (JSON.stringify(Object.keys(unit)) !== JSON.stringify(["kind", "key", "files"]) || !["entry", "catalog", "artifact"].includes(unit.kind)
      || unit.kind === "entry" && !digest(unit.key) || unit.kind === "catalog" && !isCatalogKey(unit.key) || unit.kind === "artifact" && !artifactKey(unit.key)
      || !Array.isArray(unit.files) || unit.files.length < 1 || unit.files.length > 257 || (count += unit.files.length) > 1024) corrupt("GC unit shape is invalid.");
    ordered(unit.files.map((file: GcFile) => file.name));
    for (const file of unit.files) {
      if (JSON.stringify(Object.keys(file)) !== JSON.stringify(["name", "sha256", "bytes"])
        || file.name !== INTENT && !(file.name.endsWith(".json") && digest(file.name.endsWith(RECEIPT) ? file.name.slice(0, -RECEIPT.length) : file.name.slice(0, -5)))
        || !digest(file.sha256) || !Number.isInteger(file.bytes) || file.bytes < 0 || file.bytes > maximumFileBytes(unit.key)
        || (totalBytes += file.bytes) > MAXIMUM_COLLECTION_BYTES) corrupt("GC file shape is invalid.");
    }
    const hash = unit.kind === "catalog" ? unit.key.slice(CATALOG_DIRECTORY.length + 1) : unit.key;
    if (unit.kind !== "artifact" && JSON.stringify(unit.files.map((file: GcFile) => file.name)) !== JSON.stringify([hash + ".json", hash + RECEIPT])) corrupt("GC entry is not one complete content/receipt pair.");
    if (unit.kind !== "artifact" && unit.files[0]!.sha256 !== hash) corrupt("GC entry preimage differs from its content address.");
    if (unit.kind === "artifact" && !unit.files.some((file: GcFile) => file.name === INTENT)) corrupt("GC artifact lacks ownership evidence.");
  }
  return intent;
}

async function proveArtifact(path: string, key: string, names: readonly string[]): Promise<boolean> {
  if (!names.includes(INTENT)) return false;
  try {
    if (catalogArtifactKey(key)) return proveCatalogArtifact(path, key, names);
    if (key.startsWith(".gc-stage-")) return names.length === 1 && names[0] === INTENT && key === ".gc-stage-" + (await readIntent(path)).token;
    const bytes = await fs.read(join(path, INTENT), 65_536);
    const intent = parseContractWireJson(bytes, 65_536) as { schemaVersion: number; token: string; entries: Array<{ descriptorSha256: string; previousDescriptorSha256: string; previousReceiptSha256: string }> };
    if (intent.schemaVersion !== 1 || !token(intent.token) || !key.endsWith(intent.token) || !Array.isArray(intent.entries) || intent.entries.length < 1 || intent.entries.length > 128
      || !bytes.equals(Buffer.from(JSON.stringify({ schemaVersion: intent.schemaVersion, token: intent.token, entries: intent.entries.map(entry => ({ descriptorSha256: entry.descriptorSha256,
        previousDescriptorSha256: entry.previousDescriptorSha256, previousReceiptSha256: entry.previousReceiptSha256 })) })))) return false;
    ordered(intent.entries.map(entry => entry.descriptorSha256)); const quarantine = key.startsWith(".quarantine/");
    const allowed = new Map([[INTENT, hashBytes(bytes)]]);
    for (const entry of intent.entries) {
      if (!digest(entry.descriptorSha256) || entry.previousDescriptorSha256 !== "" && !digest(entry.previousDescriptorSha256) || entry.previousReceiptSha256 !== "" && !digest(entry.previousReceiptSha256)) return false;
      allowed.set(entry.descriptorSha256 + ".json", quarantine ? entry.previousDescriptorSha256 : entry.descriptorSha256);
      allowed.set(entry.descriptorSha256 + RECEIPT, quarantine ? entry.previousReceiptSha256 : "receipt");
      if (key.startsWith(".stage-")) await fs.verifyPair(path, entry.descriptorSha256);
    }
    for (const name of names) {
      const expected = allowed.get(name); if (expected === undefined) return false;
      if (expected === "receipt") {
        const receipt = fields(parseOuter(await fs.read(join(path, name), 16_384)), ["schemaVersion", "contractId", "contractVersion", "descriptorBytes", "descriptorSha256", "abiSha256"]);
        if (receipt.schemaVersion !== 1 || readRequirement(receipt).descriptorSha256 + RECEIPT !== name) return false;
      } else if (hashBytes(await fs.read(join(path, name))) !== expected) return false;
    }
    return !key.startsWith(".stage-") || names.length === allowed.size;
  } catch { return false; }
}
async function describe(root: string, names: readonly string[], maximum = MAXIMUM_FILE): Promise<GcFile[]> {
  const files: GcFile[] = []; for (const name of [...names].sort(compare)) { const bytes = await fs.read(join(root, name), maximum); files.push({ name, sha256: hashBytes(bytes), bytes: bytes.length }); } return files;
}
async function verifyOptional(path: string, file: GcFile): Promise<boolean> {
  if (!await fs.exists(path)) return false; const bytes = await fs.read(path, file.bytes);
  if (bytes.length !== file.bytes || hashBytes(bytes) !== file.sha256) corrupt("GC file changed; unexpected bytes were preserved."); return true;
}
async function validateChildren(path: string, intent: GcIntent): Promise<void> {
  const allowed = new Set(intent.units.map((_, index) => unitDirectory(index)));
  for (const name of await names(path, 129)) {
    if (name === INTENT) continue; if (!allowed.has(name)) corrupt("Unexpected GC transaction child.");
    const files = new Set(intent.units[Number(name)]!.files.map(file => file.name)); const directory = join(path, name);
    for (const file of await names(directory, 257)) { if (!files.has(file)) corrupt("Unexpected GC tombstone file."); await regularFile(join(directory, file)); }
  }
}
async function move(source: string, destination: string): Promise<void> {
  await fs.safePath(source); await fs.safePath(destination); if (await fs.exists(destination)) corrupt("GC move would overwrite an existing file."); await rename(source, destination);
}
function result(removedEntries = 0, removedArtifacts = 0, retainedUnprovenArtifacts = 0, moreCandidates = false): ContractCacheGcResult {
  return { removedEntries, removedArtifacts, retainedUnprovenArtifacts, moreCandidates };
}
