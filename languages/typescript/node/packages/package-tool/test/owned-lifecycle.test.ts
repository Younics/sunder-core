import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, stat, readdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { promisify } from "node:util";
import test from "node:test";
import { contractsCommand } from "../src/rpc-contracts";
import { parseBaseline } from "../src/rpc-baseline";
import { sha256 } from "../src/rpc-baseline";
import { captureBaseline, withBaselineLease } from "../src/rpc-baseline-store";
import { aggregateAndPack, prepareProject } from "../src/package";
import { loadProject, currentRid } from "../src/model";
import { withOutputLeases } from "../src/output";
import { generatedProviders, invocation, fixtureSource, writeSourceFile as writeFile } from "./service-fixtures";

const exec = promisify(execFile);
const cli = resolve(__dirname, "../../dist/cli.js");
const lock = `${JSON.stringify({ lockVersion: 1, packageArtifacts: {}, packageImports: {}, providerImports: {}, platformImports: {} }, null, 2)}\n`;
function source(limit = 10, title = "Input", exposure = "public", idle = true, reversed = false): string {
  const echo = `export const echo=defineService({
/** @param {string} request @param {import("@sunder/sdk").ServiceContext} context @returns {string} */
call(request/*: string*/,context/*: import("@sunder/sdk").ServiceContext*/)/*: string*/{context.signal.throwIfAborted(); return "ok";}
},{exposure:"${exposure}", serviceId:"main", constraints:{"call.request":{maxLength:${limit},title:${JSON.stringify(title)}},"call.result":{maxLength:10}}});`;
  const declaration = '/** @type {import("@sunder/sdk").ServiceDefinition<{run():Promise<void>}>} */\nexport const idle=defineService/*<{run():Promise<void>}>*/(null,{exposure:"public",declarationOnly:true,serviceId:"ping"});';
  return `import {defineService} from "@sunder/sdk";\n${idle ? reversed ? declaration + "\n" + echo : echo + "\n" + declaration : echo}`;
}
async function project(extension = "ts", text = source()): Promise<string> {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-owned-lifecycle-")); await mkdir(resolve(root, "src"));
  await writeFile(resolve(root, `src/rpc.${extension}`), text);
  await writeFile(resolve(root, `src/worker.${extension}`), 'export {};');
  await writeFile(resolve(root, `src/rpc.${extension}`), `import "./worker.js";\n${text}`);
  await writeFile(resolve(root, "sunder-contracts.lock.json"), lock);
  await writeFile(resolve(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.lifecycle", name: "Lifecycle", version: "7.6.5", workerCapabilities: [], services: { module: `src/rpc.${extension}` } }));
  return root;
}
const run = (root: string, ...args: string[]) => exec(process.execPath, [cli, ...args, "--project", root], { maxBuffer: 4 * 1024 * 1024 });
const baseline = async (root: string) => parseBaseline(await readFile(resolve(root, "sunder-contracts.owned.json")));
const contractVersion = async (root: string) => (await baseline(root)).contracts.get("echo")!.current!.descriptor.version;
async function workerIdentity(root: string): Promise<{ contractId: string; contractVersion: string; descriptorSha256: string; abiSha256: string; value: string }> {
  const provider = (await generatedProviders(root))[0]!;
  return { ...provider, value: await provider.handler.invokeUnary(invocation(), "main", "call", {request:"hello"}) as string };
}
for (const extension of ["ts", "js"]) test(`first ${extension} selected Accept permits stable next-only and binds 2.0.0 before Freeze`, { timeout: 120_000 }, async () => {
  const text = source(10, "Input", "public", false).replaceAll("echo", "greeting"); const root = await project(extension, text);
  try {
    const config = await readFile(resolve(root, "sunder.package.json"));
    await writeFile(resolve(root, "sunder-contracts.lock.json"), "{}\n");
    await assert.rejects(run(root, "contracts", "accept", "greeting", "--next-version", "2.0.0"), /rpc-authoring\./);
    await writeFile(resolve(root, "sunder-contracts.lock.json"), lock);
    await run(root, "contracts", "accept", "greeting", "--next-version", "2.0.0");
    const accepted = await baseline(root); const snapshot = accepted.contracts.get("greeting")!.current!;
    assert.equal(snapshot.state, "draft"); assert.equal(snapshot.descriptor.version, "2.0.0"); assert.equal(snapshot.descriptor.contractId, "example.lifecycle.greeting");
    assert.deepEqual(accepted.contracts.get("greeting")!.history, []); assert.equal(Object.hasOwn(snapshot.json, "packageRelease"), false);
    await assert.rejects(stat(resolve(root, ".sunder/generated/rpc")), { code: "ENOENT" });
    const loaded = await loadProject(root);
    assert.equal(loaded.config.providers[0]!.contractVersion, "2.0.0"); assert.equal(loaded.config.providers[0]!.contractId, "example.lifecycle.greeting");
    assert.match(loaded.ownedRpc!.files.get(`owned/greeting.${extension}`)!, /CONTRACT_VERSION\s*=\s*"2\.0\.0"/);
    await run(root, "contracts", "check"); await run(root, "generate"); await run(root, "build");
    assert.match(await readFile(resolve(root, `.sunder/generated/rpc/owned/greeting.${extension}`), "utf8"), /CONTRACT_VERSION\s*=\s*"2\.0\.0"/);
    const provider = await workerIdentity(root);
    assert.equal(provider.contractVersion, "2.0.0"); assert.equal(provider.contractId, "example.lifecycle.greeting"); assert.equal(provider.value, "ok");
    assert.equal(provider.descriptorSha256, snapshot.json.descriptorSha256); assert.equal(provider.abiSha256, snapshot.json.abiSha256);
    assert.deepEqual((await baseline(root)).bytes, accepted.bytes);
    await assert.rejects(run(root, "package", "--discovery-root", resolve(root, "dist/targets")), /release-frozen or published/);
    await run(root, "contracts", "freeze");
    const frozen = await baseline(root); const current = frozen.contracts.get("greeting")!.current!;
    assert.equal(current.state, "release-frozen"); assert.equal(current.descriptor.version, "2.0.0");
    assert.deepEqual(JSON.parse(JSON.stringify(current.json.packageRelease)), { packageId: "example.lifecycle", packageVersion: "7.6.5" });
    await run(root, "package", "--discovery-root", resolve(root, "dist/targets"));
    await assert.rejects(run(root, "contracts", "accept", "greeting", "--next-version", "2.1.0"), /current\/next version pair/);
    assert.deepEqual((await baseline(root)).bytes, frozen.bytes);
    assert.deepEqual(await readFile(resolve(root, "sunder.package.json")), config);
    assert.equal(await readFile(resolve(root, `src/rpc.${extension}`), "utf8"), fixtureSource(`import "./worker.js";\n${text}`, extension)); assert.equal(await readFile(resolve(root, "sunder-contracts.lock.json"), "utf8"), lock);
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("contractsCommand rejects unknown JavaScript operations before filesystem access or draft freeze", async () => {
  const root = await project();
  try {
    const missing = resolve(root, "missing");
    const operations: readonly unknown[] = ["publish", "accept ", "Freeze", "", "constructor", "__proto__", undefined, null, false, 0, {}, { toString() { throw new Error("Must not coerce an operation"); } }];
    for (const operation of operations) await assert.rejects(contractsCommand(operation as never, missing), /Unknown contracts operation/);
    await contractsCommand("accept", root); const before = await baseline(root); const beforeStat = await stat(resolve(root, "sunder-contracts.owned.json"));
    for (const operation of operations) await assert.rejects(contractsCommand(operation as never, root), /Unknown contracts operation/);
    assert.deepEqual((await baseline(root)).bytes, before.bytes); assert.equal((await stat(resolve(root, "sunder-contracts.owned.json"))).mtimeMs, beforeStat.mtimeMs);
    assert.ok([...(await baseline(root)).contracts.values()].every((entry) => entry.current!.state === "draft"));
    await assert.rejects(stat(missing), { code: "ENOENT" }); await assert.rejects(stat(resolve(root, ".sunder/generated/rpc")), { code: "ENOENT" });
  } finally { await rm(root, { recursive: true, force: true }); }
});
for (const extension of ["ts", "js"]) test(`actual ${extension.toUpperCase()} CLI Accept -> Check -> Freeze -> Generate -> Build -> Pack and prebuilt`, { timeout: 600_000 }, async () => {
  const root = await project(extension);
  try {
    await run(root, "contracts", "accept"); assert.equal(await contractVersion(root), "1.0.0");
    await assert.rejects(stat(resolve(root, ".sunder/generated/rpc")), { code: "ENOENT" });
    await run(root, "contracts", "check"); await run(root, "contracts", "freeze", "echo");
    await assert.rejects(run(root, "package"), /declaration-only|every current/);
    await run(root, "contracts", "freeze"); const frozen = await baseline(root);
    await run(root, "generate"); await run(root, "build");
    const identity = await workerIdentity(root); const snapshot = frozen.contracts.get("echo")!.current!.json;
    assert.equal(identity.contractVersion, snapshot.contractVersion); assert.equal(identity.descriptorSha256, snapshot.descriptorSha256); assert.equal(identity.abiSha256, snapshot.abiSha256); assert.equal(identity.value, "ok");
    await run(root, "package", "--discovery-root", resolve(root, "dist/targets"));
    const archive = resolve(root, "dist/example.lifecycle.7.6.5.sunderpkg"); assert.ok((await stat(archive)).size > 0);
    await run(root, "package");
    const manifest = JSON.parse(await readFile(resolve(root, "dist/package/manifest/sunder-package.json"), "utf8"));
    assert.equal(manifest.targets.length, 6); assert.equal(manifest.contractBundles.length, 2); assert.equal(manifest.provides.length, 1);
    for (const key of ["usesContracts", "packageArtifactImports", "packageApiImports", "providerImports", "platformImports"]) assert.deepEqual(manifest[key], []);
    const originalArchive = await readFile(archive); const before = await stat(resolve(root, "sunder-contracts.owned.json"));
    for (const operation of ["accept", "check", "freeze"]) await run(root, "contracts", operation);
    assert.deepEqual((await baseline(root)).bytes, frozen.bytes); assert.equal((await stat(resolve(root, "sunder-contracts.owned.json"))).mtimeMs, before.mtimeMs);
    await run(root, "package", "--discovery-root", resolve(root, "dist/targets")); assert.deepEqual(await readFile(archive), originalArchive);
    // Metadata-only change allocates Patch, recompiles bindings at that identity,
    // and does not leave an endless version-only Patch on the next invocation.
    await writeFile(resolve(root, `src/rpc.${extension}`), source(10, "Reviewed λ"));
    await assert.rejects(run(root, "contracts", "check"), /baseline-drift/);
    const oldReceipt = await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"));
    await run(root, "contracts", "accept"); assert.equal(await contractVersion(root), "1.0.1");
    assert.deepEqual(await readFile(resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json")), oldReceipt);
    await run(root, "contracts", "accept"); assert.equal(await contractVersion(root), "1.0.1");
    await run(root, "contracts", "freeze"); await run(root, "build"); assert.equal((await workerIdentity(root)).contractVersion, "1.0.1");
    // Declaration enumeration is joined by contractKey, not the array position.
    await writeFile(resolve(root, `src/rpc.${extension}`), source(10, "Reviewed λ", "public", true, true));
    await run(root, "contracts", "check");
    await writeFile(resolve(root, `src/rpc.${extension}`), source(20, "Reviewed λ"));
    await run(root, "contracts", "accept"); assert.equal(await contractVersion(root), "1.1.0");
    await run(root, "contracts", "freeze");
    await writeFile(resolve(root, `src/rpc.${extension}`), source(20, "Patch after minor"));
    await run(root, "contracts", "accept"); assert.equal(await contractVersion(root), "1.1.1");
    await run(root, "contracts", "freeze");
    await writeFile(resolve(root, `src/rpc.${extension}`), source(5, "Patch after minor"));
    await assert.rejects(run(root, "contracts", "accept"), /acknowledgement/);
    await run(root, "contracts", "accept", "echo", "--current-version", "1.1.1", "--next-version", "2.0.0", "--acknowledge-breaking-change");
    assert.equal(await contractVersion(root), "2.0.0"); await run(root, "contracts", "check"); await run(root, "contracts", "freeze");
    await run(root, "build"); assert.equal((await workerIdentity(root)).contractVersion, "2.0.0");
    // Package versions are independent. Reuse keeps original evidence and tuples.
    const configPath = resolve(root, "sunder.package.json"); const config = JSON.parse(await readFile(configPath, "utf8"));
    await writeFile(configPath, JSON.stringify({ ...config, version: "7.6.6" })); const beforeReuse = (await baseline(root)).bytes;
    await run(root, "contracts", "freeze"); assert.deepEqual((await baseline(root)).bytes, beforeReuse);
    await run(root, "package"); const nextManifest = JSON.parse(await readFile(resolve(root, "dist/package/manifest/sunder-package.json"), "utf8"));
    assert.equal(nextManifest.version, "7.6.6"); assert.equal(nextManifest.provides[0].contractVersion, "2.0.0");
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("closed contracts CLI rejects flags, extra keys, booleans with values, duplicates and publication commands", async () => {
  const root = await project();
  try {
    for (const args of [["publish"], ["check", "echo"], ["check", "--next-version", "2.0.0"], ["freeze", "--current-version", "1.0.0"], ["freeze", "--acknowledge-breaking-change"],
      ["accept", "--next-version", "2.0.0"], ["accept", "--current-version", "1.0.0", "--next-version", "2.0.0"],
      ["accept", "echo", "extra"], ["accept", "echo", "--acknowledge-breaking-change", "true"], ["accept", "--rid", "linux-x64"], ["accept", "--project", root, "--project", root]]) {
      await assert.rejects(run(root, "contracts", ...args));
    }
    await assert.rejects(stat(resolve(root, "sunder-contracts.owned.json")), { code: "ENOENT" });
    await assert.rejects(run(root, "generate"), /baseline/);
    await run(root, "contracts", "accept"); await run(root, "generate");
    await rm(resolve(root, "sunder-contracts.owned.json"));
    for (const operation of ["check", "accept", "freeze"]) await assert.rejects(run(root, "contracts", operation), /baseline/);
    const receiptPath = resolve(root, ".sunder/generated/rpc/authoring-receipt.v1.json"); const receipt = await readFile(receiptPath);
    await assert.rejects(run(root, "contracts", "accept", "echo", "--next-version", "2.0.0"), /baseline/);
    assert.deepEqual(await readFile(receiptPath), receipt); await assert.rejects(stat(resolve(root, "sunder-contracts.owned.json")), { code: "ENOENT" });
    await assert.rejects(run(root, "package"), /baseline/);
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("deleted baseline, V3->V2 and non-RPC laundering cannot bypass current source/lock/receipt evidence", async () => {
  const root = await project();
  try {
    await run(root, "contracts", "accept"); await run(root, "generate"); const saved = (await baseline(root)).bytes;
    await rm(resolve(root, "sunder-contracts.owned.json"));
    for (const command of ["generate", "build", "package"]) await assert.rejects(run(root, command), /baseline/);
    await writeFile(resolve(root, "sunder-contracts.owned.json"), saved);
    const configPath = resolve(root, "sunder.package.json"); const config = JSON.parse(await readFile(configPath, "utf8")); delete config.services; config.entry="src/worker.ts";
    for (const schemaVersion of [2, 3]) { await writeFile(configPath, JSON.stringify({ ...config, schemaVersion, ...(schemaVersion === 2 ? { contracts: [], providers: [] } : {}) })); await assert.rejects(loadProject(root), /fallback|retired/); }
    await rm(resolve(root, ".sunder"), { recursive: true, force: true }); await rm(resolve(root, "sunder-contracts.owned.json"));
    await assert.rejects(loadProject(root), /fallback/); // canonical lock is itself source evidence
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("draft removal retains only immutable history and stale prebuilt/incomplete unions cannot release", { timeout: 120_000 }, async () => {
  const root = await project();
  try {
    await run(root, "contracts", "accept"); await run(root, "contracts", "freeze", "echo"); await run(root, "build");
    await writeFile(resolve(root, "src/rpc.ts"), source(10, "Input", "public", false));
    await run(root, "contracts", "accept", "idle", "--current-version", "1.0.0", "--next-version", "2.0.0", "--acknowledge-breaking-change");
    assert.equal((await baseline(root)).contracts.has("idle"), false);
    await assert.rejects(run(root, "package", "--discovery-root", resolve(root, "dist/targets")), /metadata/);
    await run(root, "build"); await run(root, "package", "--discovery-root", resolve(root, "dist/targets"));
    const before = await readFile(resolve(root, "dist/example.lifecycle.7.6.5.sunderpkg"));
    // A valid but changed ordinary implementation must not be laundered by an
    // old descriptor-equivalent prebuilt module.
    await writeFile(resolve(root, "src/rpc.ts"), source(10, "Input", "public", false).replace('return "ok"', 'return "other"'));
    await assert.rejects(run(root, "package", "--discovery-root", resolve(root, "dist/targets")), /fresh full ordinary source/);
    await writeFile(resolve(root, "src/rpc.ts"), source(10, "Input", "public", false).replace('return "ok"', 'return 42'));
    await assert.rejects(run(root, "package", "--discovery-root", resolve(root, "dist/targets")), /handler-incomplete/);
    assert.deepEqual(await readFile(resolve(root, "dist/example.lifecycle.7.6.5.sunderpkg")), before);
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("targeted exposure acceptance cannot omit other source contracts or drift their tuples", async () => {
  const root = await project();
  try {
    await run(root, "contracts", "accept"); await run(root, "contracts", "freeze");
    const before = (await baseline(root)).bytes;
    await writeFile(resolve(root, "src/rpc.ts"), source(10, "Input", "package", false));
    await assert.rejects(run(root, "contracts", "accept", "echo", "--current-version", "1.0.0", "--next-version", "2.0.0", "--acknowledge-breaking-change"), /outside the selected/);
    assert.deepEqual((await baseline(root)).bytes, before);
    await writeFile(resolve(root, "src/rpc.ts"), source(10, "Input", "package"));
    await assert.rejects(run(root, "contracts", "accept"), /acknowledgement/);
    await run(root, "contracts", "accept", "echo", "--current-version", "1.0.0", "--next-version", "7.6.5", "--acknowledge-breaking-change");
    assert.equal((await baseline(root)).contracts.get("echo")!.current!.exposure, "package");
  } finally { await rm(root, { recursive: true, force: true }); }
});
for (const extension of ["ts", "js"]) test(`Accept recompiles ${extension} contextual generated identity constraints before writing the baseline`, { timeout: 90_000 }, async () => {
  const root = await project(extension);
  try {
    await run(root, "contracts", "accept"); await run(root, "contracts", "freeze"); const before = (await baseline(root)).bytes;
    const constraint = extension === "ts" ? 'const exact: "1.0.0" = CONTRACT_VERSION;' : '/** @type {"1.0.0"} */ const exact = CONTRACT_VERSION;';
    await writeFile(resolve(root, `src/helper.${extension}`), `import {CONTRACT_VERSION} from "../.sunder/generated/rpc/owned/echo.js"; ${constraint} export {exact};`);
    await writeFile(resolve(root, `src/worker.${extension}`), 'import {exact} from "./helper.js"; if(exact!=="1.0.0") throw new Error("version");');
    await run(root, "contracts", "check");
    await writeFile(resolve(root, `src/rpc.${extension}`), `import "./worker.js";\n${source(10, "Patch")}`);
    await assert.rejects(run(root, "contracts", "accept"), /handler-incomplete.*|not assignable/s);
    assert.deepEqual((await baseline(root)).bytes, before);
    await assert.rejects(stat(resolve(root, ".sunder/generated/rpc")), { code: "ENOENT" });
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("actual CLI arbitrary-digit version boundary automatically selects a fitting higher bump", { timeout: 90_000 }, async () => {
  const root = await project();
  try {
    await run(root, "contracts", "accept"); await run(root, "contracts", "freeze");
    const huge = `1.0.${"9".repeat(252)}`;
    await run(root, "contracts", "accept", "echo", "--current-version", "1.0.0", "--next-version", huge);
    assert.equal(await contractVersion(root), huge); await run(root, "contracts", "freeze");
    await writeFile(resolve(root, "src/rpc.ts"), source(10, "Boundary patch"));
    await run(root, "contracts", "accept"); assert.equal(await contractVersion(root), "1.1.0");
    const before = (await baseline(root)).bytes; await run(root, "contracts", "accept"); assert.deepEqual((await baseline(root)).bytes, before);
  } finally { await rm(root, { recursive: true, force: true }); }
});
// Observe the real lease call rather than queued fs.watch notifications from the
// holder's own acquisition. The original cross-process lease still blocks it.
async function heldBaseline<T>(path: string, operation: () => Promise<T>, mutate: () => Promise<void>): Promise<T> {
  const leases = require("../src/output") as { withOutputLeases: typeof withOutputLeases };
  const original = leases.withOutputLeases;
  let completion!: Promise<T>;
  await original([path], async () => {
    let signal!: () => void; const attempting = new Promise<void>((done) => { signal = done; });
    leases.withOutputLeases = async (paths, operation, wait) => { if (paths.includes(path)) signal(); return original(paths, operation, wait); };
    try {
      completion = operation(); void completion.catch(() => undefined);
      await Promise.race([attempting, completion.then(() => { throw new Error("Operation unexpectedly finished without baseline lease"); })]);
      await mutate();
    } finally { leases.withOutputLeases = original; }
  });
  return completion;
}
for (const mutation of ["source", "config", "lock", "baseline", "file-set"]) test(`Accept revalidates ${mutation} after waiting for the baseline lease`, { timeout: 90_000 }, async () => {
  const root = await project();
  try {
    if (mutation === "file-set") {
      await writeFile(resolve(root, "src/helper.js"), 'export function helper(){return "ok";}');
      await writeFile(resolve(root, "src/worker.ts"), 'import {helper} from "./helper.js"; if(helper()!=="ok") throw new Error("helper");');
    }
    await contractsCommand("accept", root); const input = await captureBaseline(root); const saved = (await baseline(root)).bytes;
    await assert.rejects(heldBaseline(input.path, () => contractsCommand("accept", root), async () => {
      if (mutation === "source") await writeFile(resolve(root, "src/rpc.ts"), source(20));
      if (mutation === "config") { const path = resolve(root, "sunder.package.json"); const config = JSON.parse(await readFile(path, "utf8")); await writeFile(path, JSON.stringify({ ...config, version: "7.6.6" })); }
      if (mutation === "lock") await writeFile(resolve(root, "sunder-contracts.lock.json"), "{}\n");
      if (mutation === "baseline") await writeFile(input.path, "{}\n");
      if (mutation === "file-set") await writeFile(resolve(root, "src/helper.ts"), 'export function helper(){return "ok";}');
    }), /changed|input|baseline/i);
    if (mutation !== "baseline") assert.deepEqual(await readFile(input.path), saved);
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("Pack revalidates source/config/lock/baseline/runtime/file-set after archive creation under the baseline lease", { timeout: 240_000 }, async () => {
  const root = await project();
  try {
    await writeFile(resolve(root, "src/helper.js"), 'export function helper(){return "ok";}');
    await writeFile(resolve(root, "src/helper.d.ts"), 'export declare function helper(): string;');
    await writeFile(resolve(root, "src/worker.ts"), 'import {helper} from "./helper.js"; if(helper()!=="ok") throw new Error("helper");');
    await run(root, "contracts", "accept"); await run(root, "contracts", "freeze"); await run(root, "build");
    const project = await prepareProject(root); const packed = await aggregateAndPack(root); const before = await readFile(packed.archivePath); const evidence = await readFile(packed.evidencePath);
    for (const name of ["src/rpc.ts", "sunder.package.json", "sunder-contracts.lock.json", "sunder-contracts.owned.json", "src/helper.js", "src/helper.ts"]) {
      const path = resolve(root, name); const saved = await readFile(path).catch((error: NodeJS.ErrnoException) => { if (error.code === "ENOENT") return undefined; throw error; });
      try {
        await assert.rejects(heldBaseline(project.ownedRpc!.baselineInput.path, () => aggregateAndPack(root), async () => {
          const stages = (await readdir(resolve(root, "dist"))).filter((name) => name.startsWith("example.lifecycle.7.6.5.sunderpkg.stage-"));
          assert.equal(stages.length, 1); assert.ok((await stat(resolve(root, "dist", stages[0]!))).size > 0);
          await writeFile(path, name.endsWith(".json") ? "{}\n" : name.endsWith("rpc.ts") ? source(20) : 'export function helper(){return "changed";}');
        }), /changed/);
        assert.deepEqual(await readFile(packed.archivePath), before); assert.deepEqual(await readFile(packed.evidencePath), evidence);
      } finally { if (saved) await writeFile(path, saved); else await rm(path, { force: true }); }
    }
  } finally { await rm(root, { recursive: true, force: true }); }
});
test("source-verified release rejects incomplete provider/bundle unions and every imported/authority group", { timeout: 180_000 }, async () => {
  const root = await project();
  try {
    await run(root, "contracts", "accept"); await run(root, "contracts", "freeze"); await run(root, "build");
    const leaf = resolve(root, "dist/targets", currentRid()); const manifestPath = resolve(leaf, "manifest/sunder-package.json"); const indexPath = resolve(leaf, "manifest/content-index.json");
    const manifest = JSON.parse(await readFile(manifestPath, "utf8")); const index = JSON.parse(await readFile(indexPath, "utf8"));
    const variants = [{ ...manifest, provides: [] }, { ...manifest, contractBundles: manifest.contractBundles.slice(0, 1) },
      ...["packageArtifactImports", "packageApiImports", "providerImports", "platformImports"].map((group) => ({ ...manifest, [group]: [{ forged: true }] })),
      { ...manifest, contractBundles: manifest.contractBundles.map((bundle: object) => ({ ...bundle, selectedAuthorityContentIdentity: "f".repeat(64) })) }];
    for (const value of variants) {
      const text = Buffer.from(`${JSON.stringify(value, null, 2)}\n`); await writeFile(manifestPath, text);
      const nextIndex = structuredClone(index); const item = nextIndex.files.find((item: { path: string }) => item.path === "manifest/sunder-package.json"); item.sha256 = sha256(text); item.size = text.length;
      await writeFile(indexPath, `${JSON.stringify(nextIndex, null, 2)}\n`);
      await assert.rejects(aggregateAndPack(root), /RPC uses and providers|metadata|not supported|unsupported|unknown/);
    }
  } finally { await rm(root, { recursive: true, force: true }); }
});
