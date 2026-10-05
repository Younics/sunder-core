import assert from "node:assert/strict";
import { mkdtemp, readFile, readdir, rm, stat, symlink, utimes, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { parseContractLock } from "../src/rpc-contract-lock";
import { collectContractCache } from "../src/rpc-contract-cache-gc";
import { catalogReceiptBytes } from "../src/rpc-platform-cache";
import { catalogPath, platformRequirements, publishPlatformCatalogs, readPlatformCatalogs } from "../src/rpc-platform-catalogs";

const FIXTURES = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring/conformance/valid");
const compact = (value: unknown): Buffer => Buffer.from(JSON.stringify(value));
const outer = (value: unknown): Buffer => Buffer.from(JSON.stringify(value, null, 2) + "\n");
async function fixture() {
  const root = await mkdtemp(join(tmpdir(), "sunder-platform-gc-")); const cache = join(root, "cache"); const path = join(root, "sunder-contracts.lock.json");
  const value = JSON.parse(await readFile(join(FIXTURES, "mixed-contract-lock.json"), "utf8")); value.packageArtifacts = {}; value.packageImports = {};
  const bytes = outer(value); const locked = parseContractLock(bytes); await writeFile(path, bytes);
  const envelope = compact(JSON.parse(await readFile(join(FIXTURES, "platform-catalog.json"), "utf8")));
  const requirements = platformRequirements(locked);
  const hash = requirements[0]!.contentIdentity; const body = catalogPath(cache, hash); const receipt = body.replace(/\.json$/u, ".receipt.json");
  const directory = join(cache, "platform-catalogs");
  const publish = (checkpoint?: (point: string) => void | Promise<void>) => publishPlatformCatalogs(cache, new Map([[hash, envelope]]), requirements, { checkpoint });
  const clear = async () => writeFile(path, await readFile(join(FIXTURES, "minimal-contract-lock.json")));
  const metadata = async () => {
    const mixed = JSON.parse(await readFile(join(FIXTURES, "mixed-contract-lock.json"), "utf8")); mixed.platformImports = {};
    await writeFile(path, outer(mixed));
  };
  const age = async () => {
    const old = new Date(Date.now() - 10 * 86_400_000);
    for (const name of await readdir(cache, { recursive: true })) await utimes(join(cache, name), old, old);
  };
  const collect = () => collectContractCache(cache, [path]);
  await publish();
  return { root, cache, path, locked, bytes, envelope, hash, body, receipt, directory, publish, clear, metadata, age, collect,
    dispose: () => rm(root, { recursive: true, force: true }) };
}

for (const metadataOnly of [false, true]) test(`catalog GC marks ${metadataOnly ? "metadata-only" : "direct"} references and preserves young pairs`, async () => {
  const c = await fixture();
  try {
    await c.age(); if (metadataOnly) await c.metadata();
    const source = await readFile(c.path); const time = (await stat(c.body)).mtimeMs; const sourceTime = (await stat(c.path)).mtimeMs;
    assert.deepEqual(await collectContractCache(c.cache, [c.path, c.path]), { removedEntries: 0, removedArtifacts: 0, retainedUnprovenArtifacts: 0, moreCandidates: false });
    assert.deepEqual(await readFile(c.path), source); assert.equal((await stat(c.path)).mtimeMs, sourceTime); assert.equal((await stat(c.body)).mtimeMs, time);
    await c.clear(); await utimes(c.body, new Date(), new Date()); assert.equal((await c.collect()).removedEntries, 0);
    await c.age(); assert.deepEqual(await c.collect(), { removedEntries: 1, removedArtifacts: 0, retainedUnprovenArtifacts: 0, moreCandidates: false });
    assert.deepEqual(await readdir(c.directory), []);
  } finally { await c.dispose(); }
});

for (const boundary of ["gc-prepared", "gc-tombstoned", "gc-moved", "gc-committed", "gc-deleted"]) test(`catalog GC recovers ${boundary}`, async () => {
  const c = await fixture();
  try {
    await c.age(); await c.clear();
    await assert.rejects(collectContractCache(c.cache, [c.path], { checkpoint: point => { if (point === boundary || point.startsWith(boundary + ":")) throw new Error("interrupted"); } }), /interrupted/u);
    if (!["gc-committed", "gc-deleted"].includes(boundary)) {
      await assert.rejects(readPlatformCatalogs(c.locked, c.cache), /collection is incomplete/u); await assert.rejects(c.publish(), /collection is incomplete/u);
    }
    await c.collect(); assert.deepEqual(await readdir(c.directory), []); assert.equal((await readdir(c.cache)).filter(name => name.startsWith(".gc-")).length, 0);
  } finally { await c.dispose(); }
});

for (const editAtFence of [false, true]) test(`catalog GC restores new metadata references ${editAtFence ? "after the commit fence rejects a source edit" : "during recovery"}`, async () => {
  const c = await fixture();
  try {
    await c.age(); await c.clear();
    await assert.rejects(collectContractCache(c.cache, [c.path], { checkpoint: async point => {
      if (point !== "gc-moved") return;
      if (editAtFence) await c.metadata(); else throw new Error("interrupted");
    } }));
    await c.metadata(); const bytes = await readFile(c.path);
    assert.equal((await c.collect()).removedEntries, 0); assert.deepEqual(await readFile(c.path), bytes);
    assert.equal((await readPlatformCatalogs(parseContractLock(bytes), c.cache)).size, 1); assert.deepEqual(await readFile(c.body), c.envelope);
  } finally { await c.dispose(); }
});

for (const boundary of ["catalog-staged", "catalog-quarantined", "catalog-published", "catalog-committed"]) test(`catalog GC collects proven ${boundary} publication and large repair preimages`, async () => {
  const c = await fixture();
  try {
    await writeFile(c.body, Buffer.alloc(4_194_305)); await writeFile(c.receipt, "broken receipt");
    await assert.rejects(c.publish(point => { if (point === boundary) throw new Error("interrupted"); }), /interrupted/u);
    await c.publish(); const artifacts = (await readdir(c.directory, { withFileTypes: true })).filter(entry => entry.isDirectory()); assert.ok(artifacts.length);
    await c.age(); const result = await c.collect();
    assert.equal(result.removedEntries, 0); assert.equal(result.removedArtifacts, artifacts.length); assert.equal(result.retainedUnprovenArtifacts, 0);
    assert.equal((await readdir(c.directory)).length, 2); assert.equal((await readPlatformCatalogs(c.locked, c.cache)).size, 1);
  } finally { await c.dispose(); }
});

for (const failure of ["body", "receipt", "missing-receipt", "orphan-receipt", "legacy-corrupt", "stage-extra", "stage-intent", "stage-receipt", "repair-preimage"]) test(`catalog GC retains unproven ${failure}`, async () => {
  const c = await fixture();
  try {
    let retained = c.body;
    if (failure === "body") await writeFile(c.body, "changed");
    if (failure === "receipt") { retained = c.receipt; await writeFile(retained, "{}"); }
    if (failure === "missing-receipt") await rm(c.receipt);
    if (failure === "orphan-receipt") { retained = c.receipt; await rm(c.body); }
    if (failure === "legacy-corrupt") { retained = join(c.directory, ".corrupt-legacy"); await writeFile(retained, "unproven evidence"); }
    if (failure.startsWith("stage-") || failure === "repair-preimage") {
      await writeFile(c.body, "broken");
      await assert.rejects(c.publish(point => { if (point === "catalog-staged") throw new Error("interrupted"); }), /interrupted/u);
      await c.publish(); const stage = join(c.directory, (await readdir(c.directory)).find(name => name.startsWith(".stage-"))!);
      if (failure === "stage-extra") { retained = join(stage, "user.txt"); await writeFile(retained, "keep"); }
      if (failure === "stage-intent") { retained = join(stage, "intent.json"); await writeFile(retained, "{}"); }
      if (failure === "stage-receipt") { retained = join(stage, c.hash + ".receipt.json"); await writeFile(retained, catalogReceiptBytes(c.hash, c.envelope.length + 1)); }
      if (failure === "repair-preimage") {
        retained = join(c.directory, (await readdir(c.directory)).find(name => name.startsWith(".repair-"))!, c.hash + ".json"); await writeFile(retained, "unexpected user data");
      }
    }
    await c.age(); if (["body", "receipt", "missing-receipt", "orphan-receipt"].includes(failure)) await c.clear();
    const before = await readFile(retained); const result = await c.collect();
    assert.equal(result.removedEntries, 0); assert.equal(result.retainedUnprovenArtifacts, 1); assert.deepEqual(await readFile(retained), before);
  } finally { await c.dispose(); }
});

for (const failure of ["linked-body", "linked-repair", "changed-tombstone", "intent-traversal", "intent-size", "intent-batch-size"]) test(`catalog GC rejects ${failure} and preserves unknown bytes`, async () => {
  const c = await fixture();
  try {
    const outside = join(c.root, "outside"); await writeFile(outside, "must survive");
    if (failure === "linked-body") { await rm(c.body); await symlink(outside, c.body); }
    if (failure === "linked-repair") {
      await writeFile(c.body, "broken"); await c.publish();
      const target = join(c.directory, (await readdir(c.directory)).find(name => name.startsWith(".repair-"))!, c.hash + ".json");
      await rm(target); await symlink(outside, target);
    }
    if (!failure.startsWith("linked-")) {
      await c.age(); await c.clear();
      await assert.rejects(collectContractCache(c.cache, [c.path], { checkpoint: point => { if (point === "gc-moved") throw new Error("interrupted"); } }), /interrupted/u);
      if (failure === "changed-tombstone") await writeFile(join(c.cache, ".gc-pending/0000", c.hash + ".json"), "changed tombstone");
      else {
        const path = join(c.cache, ".gc-pending/intent.json"); const intent = JSON.parse(await readFile(path, "utf8"));
        if (failure === "intent-traversal") intent.units[0].key = "platform-catalogs/../outside";
        else if (failure === "intent-size") intent.units[0].files[0].bytes = 67_108_865;
        else intent.units = Array.from({ length: 33 }, (_, index) => {
          const hash = index.toString(16).padStart(64, "0"); const unit = structuredClone(intent.units[0]); unit.key = "platform-catalogs/" + hash;
          unit.files[0] = { name: hash + ".json", sha256: hash, bytes: 67_108_864 }; unit.files[1].name = hash + ".receipt.json"; return unit;
        });
        await writeFile(path, outer(intent));
      }
    }
    await assert.rejects(c.collect(), failure === "intent-batch-size" ? /GC file shape is invalid/u : /./u); assert.equal(await readFile(outside, "utf8"), "must survive");
    if (failure === "changed-tombstone") assert.equal(await readFile(join(c.cache, ".gc-pending/0000", c.hash + ".json"), "utf8"), "changed tombstone");
  } finally { await c.dispose(); }
});

test("catalog publication preserves a changed completion preimage for explicit investigation", async () => {
  const c = await fixture();
  try {
    await rm(c.receipt); let changed = "";
    await assert.rejects(c.publish(async point => {
      if (point !== "catalog-committed") return;
      const directory = join(c.directory, (await readdir(c.directory)).find(name => name.startsWith(".completed-"))!);
      changed = join(directory, c.hash + ".json"); await writeFile(changed, "user replacement");
    }), /cleanup preimage changed/u);
    await c.age(); assert.equal((await c.collect()).retainedUnprovenArtifacts, 1); assert.equal(await readFile(changed, "utf8"), "user replacement");
    assert.equal((await readPlatformCatalogs(c.locked, c.cache)).size, 1);
  } finally { await c.dispose(); }
});
