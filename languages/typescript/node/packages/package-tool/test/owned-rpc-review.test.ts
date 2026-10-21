import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { canonicalRpcDescriptorBytes } from "@sunder/sdk";
import { loadProject, currentRid } from "../src/model";
import { contractsCommand } from "../src/rpc-contracts";
import { aggregateAndPack } from "../src/package";
import { parseBaseline } from "../src/rpc-baseline";
import { constructorSource, sharedSource } from "./owned-rpc-review-fixtures";
import { fixtureSource, generatedProviders, invocation, writeSourceFile as writeFile } from "./service-fixtures";

const exec = promisify(execFile);
const cli = resolve(__dirname, "../src/cli.js");
const emptyLock = `${JSON.stringify({ lockVersion: 1, packageArtifacts: {}, packageImports: {}, providerImports: {}, platformImports: {} }, null, 2)}\n`;
async function project(extension: string, source: string): Promise<string> {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-owned-review-")); await mkdir(resolve(root, "src"));
  await writeFile(resolve(root, `src/rpc.${extension}`), source);
  await writeFile(resolve(root, "sunder-contracts.lock.json"), emptyLock);
  await writeFile(resolve(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.review", name: "Review", version: "7.6.5", workerCapabilities: [], services: { module: `src/rpc.${extension}` } }));
  return root;
}
const run = (root: string, ...args: string[]) => exec(process.execPath, [cli, ...args, "--project", root]);

test("CLI rejects unreviewed source and retired conversion commands without changing baseline or lock", { timeout: 120_000 }, async () => {
  const root = await project("ts", constructorSource(true));
  try {
    await run(root, "contracts", "accept"); await run(root, "contracts", "freeze");
    const path = resolve(root, "sunder-contracts.owned.json"), before = await readFile(path);
    const legacy = JSON.parse(before.toString());
    for (const entry of Object.values(legacy.contracts) as Record<string, unknown>[]) delete entry.projectionReviews;
    await writeFile(path, JSON.stringify(legacy, null, 2) + "\n");
    const legacyBytes = await readFile(path);
    const mtime = (await stat(path)).mtimeMs, lock = await readFile(resolve(root, "sunder-contracts.lock.json"));
    await assert.rejects(loadProject(root), /source-projection-unreviewed/);
    await assert.rejects(run(root, "contracts", "freeze"), /source-projection-unreviewed/);
    await assert.rejects(run(root, "contracts", "accept"), /source-projection-unreviewed/);
    await assert.rejects(aggregateAndPack(root), /source-projection-unreviewed/);
    await assert.rejects(run(root, "contracts", "check"), /source-projection-unreviewed/);
    await assert.rejects(run(root, "contracts", "accept", "--adopt-initial-source-projection"), /Unknown contracts accept option/);
    await assert.rejects(run(root, "contracts", "migrate-private"), /contracts requires check, accept, or freeze/);
    await assert.rejects(run(root, "api", "migrate-consumer-roles"), /api requires add/);
    const help = await run(root, "--help");
    assert.doesNotMatch(help.stdout, /adopt-initial-source-projection|migrate-private|migrate-consumer-roles/);
    assert.deepEqual(await readFile(path), legacyBytes); assert.equal((await stat(path)).mtimeMs, mtime);
    assert.deepEqual(await readFile(resolve(root, "sunder-contracts.lock.json")), lock);
    await assert.rejects(stat(resolve(root, ".sunder/generated/rpc")), { code: "ENOENT" });
  } finally { await rm(root, { recursive: true, force: true }); }
});

for (const extension of ["ts", "js"]) test(`frozen ${extension} constructor error removal requires explicit Major acceptance`, { timeout: 120_000 }, async () => {
  const root = await project(extension, constructorSource(true));
  try {
    await run(root, "contracts", "accept"); await run(root, "contracts", "freeze");
    const path = resolve(root, "sunder-contracts.owned.json"); const frozen = await readFile(path);
    const before = parseBaseline(frozen).contracts.get("echo")!.current!;
    assert.equal(before.descriptor.version, "1.0.0"); assert.equal(before.state, "release-frozen"); assert.equal(Object.hasOwn(before.descriptor.errors, "constructor"), true);
    await writeFile(resolve(root, `src/rpc.${extension}`), constructorSource(false));
    await assert.rejects(run(root, "contracts", "accept"), /acknowledgement/);
    await assert.rejects(run(root, "contracts", "accept", "echo", "--current-version", "1.0.0", "--next-version", "1.1.0", "--acknowledge-breaking-change"), /Major|bump-required/);
    await assert.rejects(run(root, "contracts", "accept", "echo", "--current-version", "1.0.0", "--next-version", "2.0.0"), /acknowledgement/);
    assert.deepEqual(await readFile(path), frozen);
    await run(root, "contracts", "accept", "echo", "--current-version", "1.0.0", "--next-version", "2.0.0", "--acknowledge-breaking-change");
    const accepted = parseBaseline(await readFile(path)).contracts.get("echo")!;
    assert.equal(accepted.current!.descriptor.version, "2.0.0"); assert.equal(Object.hasOwn(accepted.current!.descriptor.errors, "constructor"), false);
    assert.deepEqual(canonicalRpcDescriptorBytes(accepted.history[0]!.descriptor), canonicalRpcDescriptorBytes(before.descriptor));
    await run(root, "contracts", "check");
  } finally { await rm(root, { recursive: true, force: true }); }
});

for (const extension of ["ts", "js"]) for (const reverse of [false, true]) test(`shared generated handler ${extension} ${reverse ? "object-first" : "wide-first"} rejects every incompatible binding`, { timeout: 120_000 }, async () => {
  const source = sharedSource(extension, reverse, false); const root = await project(extension, source);
  try {
    await assert.rejects(loadProject(root), /handler-incomplete/);
    await assert.rejects(contractsCommand("accept", root), /handler-incomplete/);
    await assert.rejects(stat(resolve(root, "sunder-contracts.owned.json")), { code: "ENOENT" });
    await assert.rejects(stat(resolve(root, ".sunder/generated/rpc")), { code: "ENOENT" });
    // Existing frozen state cannot bypass the same full source validation.
    await writeFile(resolve(root, `src/rpc.${extension}`), sharedSource(extension, reverse, true));
    await contractsCommand("accept", root); await contractsCommand("freeze", root); await run(root, "build");
    const packed = await aggregateAndPack(root); const archive = await readFile(packed.archivePath); const baseline = await readFile(resolve(root, "sunder-contracts.owned.json"));
    await writeFile(resolve(root, `src/rpc.${extension}`), source);
    for (const operation of ["check", "accept", "freeze"] as const) await assert.rejects(contractsCommand(operation, root), /handler-incomplete/);
    await assert.rejects(aggregateAndPack(root), /handler-incomplete/);
    assert.deepEqual(await readFile(packed.archivePath), archive); assert.deepEqual(await readFile(resolve(root, "sunder-contracts.owned.json")), baseline);
  } finally { await rm(root, { recursive: true, force: true }); }
});
for (const extension of ["ts", "js"]) for (const typed of [false, true]) test(`shared generated handler ${extension} valid ${typed ? "explicit types" : "contextual inference"} is independent of provider ordering`, { timeout: 120_000 }, async () => {
  const root = await project(extension, sharedSource(extension, false, true, typed));
  try {
    await contractsCommand("accept", root); await contractsCommand("freeze", root);
    const before = await readFile(resolve(root, "sunder-contracts.owned.json"));
    for (const reverse of [false, true]) {
      const source = sharedSource(extension, reverse, true, typed); await writeFile(resolve(root, `src/rpc.${extension}`), source);
      const loaded = await loadProject(root); assert.equal(loaded.ownedRpc!.source, fixtureSource(source, extension));
      assert.equal(loaded.contracts.length, 2); assert.equal(loaded.contracts.find((contract) => contract.contractKey === "z-object")!.descriptor.$defs.ZObjectCallResult!.type, "object");
      await contractsCommand("check", root); await contractsCommand("accept", root);
      await run(root, "build");
      const providers = await generatedProviders(root);
      assert.deepEqual(await Promise.all(providers.map(provider=>provider.handler.invokeUnary(invocation(),"main","call",{request:{value:"ok"}}))), [{ value: "OK" }, { value: "OK" }]);
      assert.deepEqual(await readFile(resolve(root, "sunder-contracts.owned.json")), before);
    }
  } finally { await rm(root, { recursive: true, force: true }); }
});
