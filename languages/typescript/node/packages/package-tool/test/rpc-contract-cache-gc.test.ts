import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, readdir, rename, rm, stat, symlink, utimes, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";
import { randomUUID } from "node:crypto";
import test from "node:test";
import { canonicalRpcDescriptorBytes, computeRpcAbiSha256, parseRpcContractDescriptor } from "@sunder/sdk";
import { collectContractCache } from "../src/rpc-contract-cache-gc";
import { publishContractCache, readContractCache } from "../src/rpc-contract-cache";
import { hashBytes, parseContractLock, type CacheRequirement } from "../src/rpc-contract-lock";

const FIXTURES = resolve(__dirname, "../../../../../../../specs/rpc/v1");
const old = new Date(Date.now() - 10 * 86_400_000);
async function fixture() {
  const root = await mkdtemp(join(tmpdir(), "sunder-cache-gc-")); const cache = join(root, "cache");
  const path = join(root, "sunder-contracts.lock.json");
  const bytes = await readFile(join(FIXTURES, "authoring/conformance/valid/package-api-contract-lock.json"));
  await writeFile(path, bytes); const lock = parseContractLock(bytes);
  const descriptor = Buffer.from(canonicalRpcDescriptorBytes(parseRpcContractDescriptor(await readFile(join(FIXTURES, "conformance/valid/minimal.rpc.json")))));
  const requirement = [...lock.requirements.values()][0]!;
  await publishContractCache(cache, new Map([[requirement.descriptorSha256, descriptor]]), lock.requirements);
  const age = async (hash: string): Promise<void> => { for (const name of [hash + ".json", hash + ".receipt.json"]) await utimes(join(cache, name), old, old); };
  const add = async (id: string): Promise<CacheRequirement> => {
    const modified = parseRpcContractDescriptor(JSON.stringify({ ...JSON.parse(descriptor.toString()) as object, contractId: id }));
    const canonical = Buffer.from(canonicalRpcDescriptorBytes(modified));
    const value: CacheRequirement = { contractId: id, contractVersion: modified.version, descriptorSha256: hashBytes(canonical), abiSha256: await computeRpcAbiSha256(modified), descriptorBytes: canonical.length };
    await publishContractCache(cache, new Map([[value.descriptorSha256, canonical]]), new Map([[value.descriptorSha256, value]])); return value;
  };
  return { root, cache, path, bytes, lock, descriptor, requirement, age, add, dispose: () => rm(root, { recursive: true, force: true }) };
}

test("GC retains reviewed and young entries, deduplicates locks, and preserves source and cache mtimes", async () => {
  const c = await fixture();
  try {
    const unused = await c.add("example.unused"); const young = await c.add("example.young"); await c.age(unused.descriptorSha256); await c.age(c.requirement.descriptorSha256);
    const sourceTime = (await stat(c.path)).mtimeMs; const cacheTime = (await stat(join(c.cache, c.requirement.descriptorSha256 + ".json"))).mtimeMs;
    const result = await collectContractCache(c.cache, [c.path, c.path]);
    assert.equal(result.removedEntries, 1); assert.equal(result.removedArtifacts, 0);
    await assert.rejects(stat(join(c.cache, unused.descriptorSha256 + ".json")), { code: "ENOENT" });
    assert.ok(await stat(join(c.cache, young.descriptorSha256 + ".json")));
    assert.deepEqual(await readFile(c.path), c.bytes); assert.equal((await stat(c.path)).mtimeMs, sourceTime);
    assert.equal((await stat(join(c.cache, c.requirement.descriptorSha256 + ".json"))).mtimeMs, cacheTime);
    assert.equal((await readContractCache(c.cache, c.lock.requirements)).size, 1);
    assert.equal((await collectContractCache(c.cache, [c.path])).removedEntries, 0);
  } finally { await c.dispose(); }
});

for (const point of ["gc-prepared", "gc-tombstoned", "gc-moved", "gc-committed", "gc-deleted"]) test(`GC recovers the ${point} boundary`, async () => {
  const c = await fixture();
  try {
    const unused = await c.add("example.unused"); await c.age(unused.descriptorSha256);
    await assert.rejects(collectContractCache(c.cache, [c.path], { checkpoint: value => { if (value === point || value.startsWith(point + ":")) throw new Error("interrupted"); } }), /interrupted/u);
    if (!["gc-committed", "gc-deleted"].includes(point)) {
      await assert.rejects(readContractCache(c.cache, c.lock.requirements), /collection is incomplete/u);
      await assert.rejects(publishContractCache(c.cache, new Map([[c.requirement.descriptorSha256, c.descriptor]]), c.lock.requirements), /collection is incomplete/u);
    }
    await collectContractCache(c.cache, [c.path]);
    await assert.rejects(stat(join(c.cache, unused.descriptorSha256 + ".json")), { code: "ENOENT" });
    assert.equal((await readContractCache(c.cache, c.lock.requirements)).size, 1);
    assert.equal((await readdir(c.cache)).filter(name => name.startsWith(".gc-")).length, 0);
  } finally { await c.dispose(); }
});

test("GC recovery restores a newly referenced descriptor from its tombstone", async () => {
  const c = await fixture();
  try {
    const empty = await readFile(join(FIXTURES, "authoring/conformance/valid/minimal-contract-lock.json"));
    await writeFile(c.path, empty); await c.age(c.requirement.descriptorSha256);
    await assert.rejects(collectContractCache(c.cache, [c.path], { checkpoint: point => { if (point === "gc-moved") throw new Error("interrupted"); } }), /interrupted/u);
    await writeFile(c.path, c.bytes);
    assert.equal((await collectContractCache(c.cache, [c.path])).removedEntries, 0);
    assert.equal((await readContractCache(c.cache, c.lock.requirements)).size, 1);
    assert.deepEqual(await readFile(c.path), c.bytes);
  } finally { await c.dispose(); }
});

test("GC commit fence rejects a changed reviewed lock and restores current references on recovery", async () => {
  const c = await fixture();
  try {
    await writeFile(c.path, await readFile(join(FIXTURES, "authoring/conformance/valid/minimal-contract-lock.json")));
    await c.age(c.requirement.descriptorSha256);
    await assert.rejects(collectContractCache(c.cache, [c.path], { checkpoint: async point => { if (point === "gc-moved") await writeFile(c.path, c.bytes); } }), /Reviewed lock changed/u);
    assert.equal((await collectContractCache(c.cache, [c.path])).removedEntries, 0);
    assert.equal((await readContractCache(c.cache, c.lock.requirements)).size, 1);
  } finally { await c.dispose(); }
});

for (const completed of [false, true]) test(`GC collects a proven ${completed ? "completed publication" : "GC preparation stage"}`, async () => {
  const c = await fixture();
  try {
    let artifact: string;
    if (completed) {
      await rm(join(c.cache, c.requirement.descriptorSha256 + ".receipt.json"));
      await assert.rejects(publishContractCache(c.cache, new Map([[c.requirement.descriptorSha256, c.descriptor]]), c.lock.requirements,
        { checkpoint: point => { if (point === "committed") throw new Error("interrupted"); } }), /interrupted/u);
      artifact = join(c.cache, (await readdir(c.cache)).find(name => name.startsWith(".completed-"))!);
    } else {
      await writeFile(c.path, await readFile(join(FIXTURES, "authoring/conformance/valid/minimal-contract-lock.json"))); await c.age(c.requirement.descriptorSha256);
      await assert.rejects(collectContractCache(c.cache, [c.path], { checkpoint: point => { if (point === "gc-prepared") throw new Error("interrupted"); } }), /interrupted/u);
      const pending = join(c.cache, ".gc-pending"); const intent = JSON.parse(await readFile(join(pending, "intent.json"), "utf8"));
      artifact = join(c.cache, ".gc-stage-" + String(intent.token)); await rename(pending, artifact); await writeFile(c.path, c.bytes);
    }
    for (const name of await readdir(artifact)) await utimes(join(artifact, name), old, old); await utimes(artifact, old, old);
    const result = await collectContractCache(c.cache, [c.path]);
    assert.equal(result.removedEntries, 0); assert.equal(result.removedArtifacts, 1);
    await assert.rejects(stat(artifact), { code: "ENOENT" }); assert.equal((await readContractCache(c.cache, c.lock.requirements)).size, 1);
  } finally { await c.dispose(); }
});

test("GC collects proven aged quarantine and retains an unproven orphan stage", async () => {
  const c = await fixture();
  try {
    await writeFile(join(c.cache, c.requirement.descriptorSha256 + ".json"), "corrupt evidence");
    await publishContractCache(c.cache, new Map([[c.requirement.descriptorSha256, c.descriptor]]), c.lock.requirements);
    const [token] = await readdir(join(c.cache, ".quarantine")); const quarantine = join(c.cache, ".quarantine", token!);
    const stage = join(c.cache, ".stage-" + randomUUID()); await mkdir(stage); await writeFile(join(stage, "user.txt"), "keep me");
    for (const path of [quarantine, stage]) {
      for (const name of await readdir(path)) await utimes(join(path, name), old, old);
      await utimes(path, old, old);
    }
    const result = await collectContractCache(c.cache, [c.path]);
    assert.equal(result.removedArtifacts, 1); assert.equal(result.retainedUnprovenArtifacts, 1);
    await assert.rejects(stat(quarantine), { code: "ENOENT" }); assert.equal(await readFile(join(stage, "user.txt"), "utf8"), "keep me");
  } finally { await c.dispose(); }
});

for (const failure of ["missing-lock", "malformed-lock", "symlink", "wrong-recovery-locks", "tombstone-extra", "intent-traversal"]) test(`GC preserves bytes for ${failure}`, async () => {
  const c = await fixture();
  try {
    const unused = await c.add("example.unused"); await c.age(unused.descriptorSha256); let lockPath = c.path;
    if (failure === "missing-lock") lockPath = join(c.root, "missing.json");
    if (failure === "malformed-lock") await writeFile(c.path, "{}");
    if (failure === "symlink") {
      const original = join(c.cache, unused.descriptorSha256 + ".json"); const outside = join(c.root, "outside.json");
      await writeFile(outside, await readFile(original)); await rm(original); await symlink(outside, original);
    }
    if (["wrong-recovery-locks", "tombstone-extra", "intent-traversal"].includes(failure)) {
      await assert.rejects(collectContractCache(c.cache, [c.path], { checkpoint: point => { if (point === "gc-prepared") throw new Error("interrupted"); } }), /interrupted/u);
      if (failure === "wrong-recovery-locks") { lockPath = join(c.root, "other.json"); await writeFile(lockPath, c.bytes); }
      else if (failure === "tombstone-extra") await writeFile(join(c.cache, ".gc-pending", "user.txt"), "preserve");
      else {
        const path = join(c.cache, ".gc-pending", "intent.json"); const value = JSON.parse(await readFile(path, "utf8"));
        value.units[0].files[0].name = "../outside.json"; await writeFile(path, JSON.stringify(value, null, 2) + "\n");
      }
    }
    const descriptor = await readFile(join(c.cache, unused.descriptorSha256 + ".json"));
    await assert.rejects(collectContractCache(c.cache, [lockPath]));
    assert.deepEqual(await readFile(join(c.cache, unused.descriptorSha256 + ".json")), descriptor);
  } finally { await c.dispose(); }
});

test("GC CLI requires reviewed locks, validates grace and reports restore requirements", async () => {
  const c = await fixture();
  try {
    const unused = await c.add("example.unused"); await c.age(unused.descriptorSha256);
    const cli = resolve(__dirname, "../src/cli.js"); const run = promisify(execFile);
    for (const args of [[], ["--lock", ""], ["--lock", c.path, "--grace-days", "0"], ["--lock", c.path, "--grace-days", "07"], ["--lock", c.path, "--registry", "https://registry.example/"], ["--lock", c.path, "--development-registry"]])
      await assert.rejects(run(process.execPath, [cli, "contracts", "gc", "--cache-root", c.cache, ...args]));
    const { stdout } = await run(process.execPath, [cli, "contracts", "gc", "--cache-root", c.cache, "--lock", c.path]);
    assert.match(stdout, /Collected 1 contract entries/u); assert.match(stdout, /Pruned entries require explicit restore/u);
  } finally { await c.dispose(); }
});
