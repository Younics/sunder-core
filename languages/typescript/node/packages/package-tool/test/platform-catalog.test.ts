import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";
import { verifyPlatformContractCatalog, validatePlatformCatalogCacheOwnership, type PlatformContractPins } from "../src/platform-catalog";

const ROOT = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring");
const GOLDEN = "5ef72ceacb24db5ca51a51f977c47853f787198e563a6fa8950fd62f636ea930";
type RecordValue = Record<string, unknown>;
interface Contract extends RecordValue {
  contractId: string; contractVersion: string; descriptorBytes: number;
  descriptorSha256: string; abiSha256: string; canonicalDescriptor: string;
}
interface Catalog extends RecordValue {
  schemaVersion: number; authorityKind: string; authorityId: string;
  releaseVersion: string; contractCount: number; contracts: Contract[];
}
const compact = (value: unknown): Buffer => Buffer.from(JSON.stringify(value));
const hash = (bytes: Uint8Array): string => createHash("sha256").update(bytes).digest("hex");
async function fixture(): Promise<Catalog> {
  return JSON.parse(await readFile(resolve(ROOT, "conformance/valid/platform-catalog.json"), "utf8")) as Catalog;
}

test("Registry catalog verifies without key material and reproduces the cross-language content identity", async () => {
  const original = await fixture(); const bytes = compact(original);
  const catalog = await verifyPlatformContractCatalog(bytes);
  assert.equal(catalog.releaseVersion, "1.1.0"); assert.equal(catalog.contentIdentity, GOLDEN);
  assert.equal(hash(bytes), GOLDEN); assert.equal(catalog.contracts.length, 1);
  assert.deepEqual(Buffer.from(catalog.getCanonicalUtf8()), bytes);
  const contract = catalog.contracts[0]!;
  assert.equal(contract.contractId, "sunder.stack.contributor"); assert.equal(contract.contractVersion, "2.0.0");
  const pins: PlatformContractPins = { releaseVersion: catalog.releaseVersion, contentIdentity: catalog.contentIdentity,
    contractId: contract.contractId, contractVersion: contract.contractVersion, descriptorSha256: contract.descriptorSha256, abiSha256: contract.abiSha256 };
  assert.equal(catalog.verifyPinned(pins), contract.descriptor);
  for (const field of Object.keys(pins)) assert.throws(() => catalog.verifyPinned({ ...pins, [field]: "wrong" }), /pin/iu);
  validatePlatformCatalogCacheOwnership(bytes, GOLDEN);
  assert.throws(() => validatePlatformCatalogCacheOwnership(bytes, "0".repeat(64)), /content address/u);
  original.releaseVersion = "1.1.1";
  const other = await verifyPlatformContractCatalog(compact(original));
  assert.notEqual(other.contentIdentity, GOLDEN);
  assert.throws(() => other.verifyPinned(pins), /pin/iu);
});

test("catalog evidence snapshots caller bytes and freezes every descriptor value", async () => {
  const bytes = compact(await fixture()); const expected = Buffer.from(bytes);
  const pending = verifyPlatformContractCatalog(bytes); bytes.fill(0);
  const catalog = await pending;
  const copy = catalog.getCanonicalUtf8(); copy.fill(0);
  assert.deepEqual(Buffer.from(catalog.getCanonicalUtf8()), expected);
  assert.equal(Reflect.set(catalog, "releaseVersion", "other"), false);
  assert.equal(Reflect.set(catalog.contracts, "0", {}), false);
  assert.equal(Reflect.set(catalog.contracts[0]!, "contractId", "other"), false);
  assert.equal(Reflect.set(catalog.contracts[0]!.descriptor, "version", "other"), false);
});

for (const shape of ["catalog", "contract"]) test(`catalog requires every ordered, non-null ${shape} member`, async () => {
  const original = await fixture();
  const select = (value: Catalog): RecordValue => shape === "catalog" ? value : value.contracts[0]!;
  for (const name of Object.keys(select(original))) {
    const absent = structuredClone(original); delete select(absent)[name];
    await assert.rejects(verifyPlatformContractCatalog(compact(absent)));
    const nil = structuredClone(original); select(nil)[name] = null;
    await assert.rejects(verifyPlatformContractCatalog(compact(nil)));
  }
  const unknown = structuredClone(original); select(unknown).signature = "obsolete";
  await assert.rejects(verifyPlatformContractCatalog(compact(unknown)), /schema/u);
  const reversed = Object.fromEntries(Object.entries(select(original)).reverse());
  if (shape === "catalog") await assert.rejects(verifyPlatformContractCatalog(compact(reversed)), /order/u);
  else { original.contracts[0] = reversed as Contract; await assert.rejects(verifyPlatformContractCatalog(compact(original)), /order/u); }
});

test("catalog rejects duplicate, escaped, reordered, nonscalar and noncanonical JSON bytes", async () => {
  const bytes = compact(await fixture()); const text = bytes.toString();
  for (const value of [" " + text, text + "\n", text.replace('"schemaVersion":1', '"schemaVersion":1.0'),
    text.replace('"schemaVersion":1', '"schemaVersion":1e0'), text.replace('"contractCount":1', '"contractCount":-0'),
    text.replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1'),
    text.replace('"schemaVersion":1', '"SchemaVersion":1,"schemaVersion":1'),
    text.replace("sunder.core", "\\u0073under.core"), text.replace("sunder.core", "\\ud800"), text.replace("sunder.core", "\\udc00"),
    "null", "[]", "{}", text.slice(0, -1) + ",}", "/*comment*/" + text])
    await assert.rejects(verifyPlatformContractCatalog(Buffer.from(value)));
  await assert.rejects(verifyPlatformContractCatalog(Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), bytes])), /BOM/u);
  await assert.rejects(verifyPlatformContractCatalog(Buffer.concat([bytes, Buffer.from([0xff])])));
});

test("catalog preflight rejects excessive bytes, nesting, values and inventory counts", async () => {
  await assert.rejects(verifyPlatformContractCatalog(Buffer.alloc(64 * 1024 * 1024 + 1)), /byte bound/u);
  await assert.rejects(verifyPlatformContractCatalog(Buffer.from("[".repeat(17) + "0" + "]".repeat(17))), /depth/u);
  await assert.rejects(verifyPlatformContractCatalog(Buffer.from("[" + "0,".repeat(262_144) + "0]")), /budget/u);
  const catalog = await fixture(); catalog.contracts = Array.from({ length: 129 }, () => structuredClone(catalog.contracts[0]!)); catalog.contractCount = 129;
  await assert.rejects(verifyPlatformContractCatalog(compact(catalog)), /128/u);
});

const mutations: ReadonlyArray<readonly [string, (value: Catalog) => void]> = [
  ["schema", value => { value.schemaVersion = 2; }],
  ["authority kind", value => { value.authorityKind = "package"; }],
  ["authority identity", value => { value.authorityId = "example.other"; }],
  ["prerelease", value => { value.releaseVersion = "1.1.0-preview.1"; }],
  ["release grammar", value => { value.releaseVersion = "01.1.0"; }],
  ["count", value => { value.contractCount++; }],
  ["empty inventory", value => { value.contractCount = 0; value.contracts = []; }],
  ["duplicate identity", value => { value.contracts.push(structuredClone(value.contracts[0]!)); value.contractCount++; }],
  ["missing mandatory contract", value => { value.contracts[0]!.contractId = "example.other"; }],
  ["descriptor version", value => { value.contracts[0]!.contractVersion = "3.0.0"; }],
  ["descriptor bytes", value => { value.contracts[0]!.descriptorBytes++; }],
  ["descriptor limit", value => { value.contracts[0]!.descriptorBytes = 4_194_305; }],
  ["descriptor hash", value => { value.contracts[0]!.descriptorSha256 = "a".repeat(64); }],
  ["ABI hash", value => { value.contracts[0]!.abiSha256 = "a".repeat(64); }],
  ["hash casing", value => { value.contracts[0]!.descriptorSha256 = value.contracts[0]!.descriptorSha256.toUpperCase(); }],
  ["descriptor grammar", value => { value.contracts[0]!.canonicalDescriptor = "{}"; value.contracts[0]!.descriptorBytes = 2; }],
  ["descriptor canonical form", value => { value.contracts[0]!.canonicalDescriptor += "\n"; value.contracts[0]!.descriptorBytes++; }],
];
for (const [name, mutate] of mutations) test(`catalog rejects mismatched ${name}`, async () => {
  const catalog = await fixture(); mutate(catalog);
  await assert.rejects(verifyPlatformContractCatalog(compact(catalog)));
});
