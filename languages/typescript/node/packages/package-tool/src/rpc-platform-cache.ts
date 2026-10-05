import { randomUUID } from "node:crypto";
import { link, mkdir, rename, rmdir, unlink } from "node:fs/promises";
import { join } from "node:path";
import { validatePlatformCatalogCacheOwnership } from "./platform-catalog";
import { contractCacheFiles as fs } from "./rpc-contract-cache";
import { contractError, hashBytes, parseContractWireJson } from "./rpc-contract-lock";
import { outerBytes } from "./rpc-baseline-json";

export const CATALOG_DIRECTORY = "platform-catalogs";
export const MAXIMUM_CATALOG_BYTES = 67_108_864;
const RECEIPT = ".receipt.json";
const digest = (value: unknown): value is string => typeof value === "string" && /^[a-f0-9]{64}$/u.test(value);
const token = (value: unknown): value is string => typeof value === "string" && /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/u.test(value);
interface CatalogIntent { readonly schemaVersion: 1; readonly token: string; readonly catalogSha256: string; readonly previousCatalogSha256: string; readonly previousReceiptSha256: string }
function corrupt(message: string): never { return contractError("contract-cache.entry-corrupt", message); }

export function catalogReceiptBytes(hash: string, bytes: number): Buffer {
  return outerBytes({ schemaVersion: 1, catalogSha256: hash, catalogBytes: bytes });
}
export async function readOwnedCatalogPair(directory: string, hash: string): Promise<Buffer> {
  if (!digest(hash)) corrupt("Catalog content address is invalid.");
  const body = join(directory, hash + ".json"); const receipt = join(directory, hash + RECEIPT);
  if (!await fs.exists(body) || !await fs.exists(receipt)) contractError("contract-cache.entry-missing", "The catalog/ownership receipt pair is missing. Run explicit contracts restore.");
  const bytes = await fs.read(body, MAXIMUM_CATALOG_BYTES);
  try { validatePlatformCatalogCacheOwnership(bytes, hash); }
  catch (error) { throw new Error("contract-cache.entry-corrupt: Catalog bytes differ from their canonical content address. Run explicit contracts restore.", { cause: error }); }
  if (!(await fs.read(receipt, 16_384)).equals(catalogReceiptBytes(hash, bytes.length))) corrupt("Catalog ownership receipt differs from its exact bytes. Run explicit contracts restore.");
  return bytes;
}
export function catalogArtifactKey(key: string): boolean {
  return [".stage-", ".repair-", ".completed-"].some(prefix => key.startsWith(CATALOG_DIRECTORY + "/" + prefix) && token(key.slice(CATALOG_DIRECTORY.length + 1 + prefix.length)));
}
export async function proveCatalogArtifact(directory: string, key: string, names: readonly string[]): Promise<boolean> {
  if (!catalogArtifactKey(key) || !names.includes("intent.json")) return false;
  try {
    const bytes = await fs.read(join(directory, "intent.json"), 65_536);
    const intent = parseContractWireJson(bytes, 65_536) as CatalogIntent;
    if (intent?.schemaVersion !== 1 || !token(intent.token) || !key.endsWith(intent.token) || !digest(intent.catalogSha256)
      || intent.previousCatalogSha256 !== "" && !digest(intent.previousCatalogSha256)
      || intent.previousReceiptSha256 !== "" && !digest(intent.previousReceiptSha256)
      || !bytes.equals(Buffer.from(JSON.stringify({ schemaVersion: 1, token: intent.token, catalogSha256: intent.catalogSha256,
        previousCatalogSha256: intent.previousCatalogSha256, previousReceiptSha256: intent.previousReceiptSha256 })))) return false;
    const body = intent.catalogSha256 + ".json"; const receipt = intent.catalogSha256 + RECEIPT;
    if (names.some(name => !["intent.json", body, receipt].includes(name))) return false;
    const repair = key.startsWith(CATALOG_DIRECTORY + "/.repair-");
    for (const name of names.filter(name => name !== "intent.json")) {
      const value = await fs.read(join(directory, name), MAXIMUM_CATALOG_BYTES);
      if (repair) { if (hashBytes(value) !== (name === body ? intent.previousCatalogSha256 : intent.previousReceiptSha256)) return false; }
      else if (name === body) validatePlatformCatalogCacheOwnership(value, intent.catalogSha256);
      else {
        const record = parseContractWireJson(value, 16_384) as { catalogBytes: number };
        if (!Number.isSafeInteger(record.catalogBytes) || record.catalogBytes < 2 || record.catalogBytes > MAXIMUM_CATALOG_BYTES
          || !value.equals(catalogReceiptBytes(intent.catalogSha256, record.catalogBytes))) return false;
      }
    }
    if (key.startsWith(CATALOG_DIRECTORY + "/.stage-") && names.length !== 3) return false;
    if (!repair && names.includes(body) && names.includes(receipt))
      await readOwnedCatalogPair(directory, intent.catalogSha256);
    return true;
  } catch { return false; }
}

// Caller holds the shared root lease and has independently verified signature/trust.
export async function publishCatalogPair(directory: string, hash: string, body: Buffer, options: {
  readonly signal?: AbortSignal; readonly checkpoint?: (point: string) => void | Promise<void>;
}): Promise<void> {
  options.signal?.throwIfAborted();
  const entries = [{ name: hash + ".json", bytes: body }, { name: hash + RECEIPT, bytes: catalogReceiptBytes(hash, body.length) }];
  const existing = async (path: string): Promise<Buffer | undefined> => await fs.exists(path) ? fs.read(path, MAXIMUM_CATALOG_BYTES) : undefined;
  const previous = new Map<string, Buffer | undefined>();
  for (const entry of entries) previous.set(entry.name, await existing(join(directory, entry.name)));
  if (entries.every(entry => previous.get(entry.name)?.equals(entry.bytes))) return;
  const id = randomUUID(); const stage = join(directory, ".stage-" + id); const repair = join(directory, ".repair-" + id);
  const intent: CatalogIntent = { schemaVersion: 1, token: id, catalogSha256: hash,
    previousCatalogSha256: previous.get(entries[0]!.name) ? hashBytes(previous.get(entries[0]!.name)!) : "",
    previousReceiptSha256: previous.get(entries[1]!.name) ? hashBytes(previous.get(entries[1]!.name)!) : "" };
  const intentBytes = Buffer.from(JSON.stringify(intent));
  await fs.safePath(stage); await mkdir(stage);
  for (const entry of entries) await fs.writeNew(join(stage, entry.name), entry.bytes);
  await fs.writeNew(join(stage, "intent.json"), intentBytes); await options.checkpoint?.("catalog-staged");
  for (const entry of entries) {
    options.signal?.throwIfAborted(); const source = join(stage, entry.name); const target = join(directory, entry.name);
    if (!(await fs.read(source, MAXIMUM_CATALOG_BYTES)).equals(entry.bytes)) corrupt("Catalog stage changed.");
    const current = await existing(target); if (current?.equals(entry.bytes)) continue;
    const old = previous.get(entry.name);
    if (old === undefined ? current !== undefined : current === undefined || !current.equals(old)) corrupt("Catalog changed during publication.");
    if (current !== undefined) {
      await fs.safePath(repair); await mkdir(repair, { recursive: true }); const evidence = join(repair, "intent.json");
      if (!await fs.exists(evidence)) await fs.writeNew(evidence, intentBytes);
      else if (!(await fs.read(evidence, 65_536)).equals(intentBytes)) corrupt("Catalog repair evidence changed.");
      const destination = join(repair, entry.name); await fs.safePath(destination);
      if (await fs.exists(destination)) corrupt("Catalog repair would overwrite retained evidence.");
      await rename(target, destination); await options.checkpoint?.("catalog-quarantined");
    }
    const temporary = join(directory, ".write-" + randomUUID()); await fs.writeNew(temporary, entry.bytes);
    await fs.safePath(target); await link(temporary, target); await unlink(temporary); await options.checkpoint?.("catalog-published");
  }
  await readOwnedCatalogPair(directory, hash);
  const completed = join(directory, ".completed-" + id); await fs.safePath(completed);
  if (await fs.exists(completed)) corrupt("Catalog completion path already exists.");
  await rename(stage, completed); await options.checkpoint?.("catalog-committed");
  for (const entry of [...entries, { name: "intent.json", bytes: intentBytes }]) {
    const path = join(completed, entry.name);
    if (!(await fs.read(path, MAXIMUM_CATALOG_BYTES)).equals(entry.bytes)) corrupt("Catalog cleanup preimage changed; it was preserved.");
    await unlink(path);
  }
  await rmdir(completed);
}
