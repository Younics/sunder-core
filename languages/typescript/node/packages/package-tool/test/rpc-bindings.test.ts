import assert from "node:assert/strict";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { access, mkdir, open, readFile, readdir, rm, stat, symlink, utimes, writeFile, type FileHandle } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { generateRpcBindings } from "../src/index";
import { currentRid, loadProject } from "../src/model";
import { markerPath, withOutputLocks } from "../src/output";
import { buildDevPackage, buildWebTarget, bundleProduction, prepareProject } from "../src/package";
import {
  assertPendingAfterTurn,
  observeOutputLockAttempt,
  startOutputLockContender,
  waitForProcessOutputLockAttempt,
} from "./observable-barriers";

test("rpcBindings config validates bounded exact entries and portable collision-free TypeScript outputs", async () => {
  const root = await createProject();
  try {
    const config = await readConfig(root);
    config.rpcBindings = Array.from({ length: 128 }, (_value, index) => ({
      path: `contracts/input-${index}.rpc.json`,
      outputFile: `bindings/output-${index}.ts`,
    }));
    await writeConfig(root, config);
    assert.equal((await loadProject(root)).config.rpcBindings?.length, 128);

    const invalid: ReadonlyArray<{
      readonly name: string;
      readonly value: unknown;
      readonly expected: RegExp;
    }> = [
      {
        name: "more than 128 entries",
        value: Array.from({ length: 129 }, (_value, index) => ({ path: `contracts/${index}.json`, outputFile: `${index}.ts` })),
        expected: /at most 128 entries/u,
      },
      {
        name: "unknown entry property",
        value: [{ path: "contracts/v1.rpc.json", outputFile: "binding.ts", extra: true }],
        expected: /unknown property 'extra'/u,
      },
      {
        name: "absolute input",
        value: [{ path: "/contracts/v1.rpc.json", outputFile: "binding.ts" }],
        expected: /portable project-relative path/u,
      },
      {
        name: "input traversal",
        value: [{ path: "../v1.rpc.json", outputFile: "binding.ts" }],
        expected: /portable project-relative path/u,
      },
      {
        name: "absolute output",
        value: [{ path: "contracts/v1.rpc.json", outputFile: "/binding.ts" }],
        expected: /portable project-relative path/u,
      },
      {
        name: "output traversal",
        value: [{ path: "contracts/v1.rpc.json", outputFile: "../binding.ts" }],
        expected: /portable project-relative path/u,
      },
      {
        name: "non-TypeScript output",
        value: [{ path: "contracts/v1.rpc.json", outputFile: "binding.js" }],
        expected: /must identify a \.ts file/u,
      },
      {
        name: "declaration output",
        value: [{ path: "contracts/v1.rpc.json", outputFile: "binding.d.ts" }],
        expected: /must not be a \.d\.ts/u,
      },
      {
        name: "duplicate input",
        value: [
          { path: "contracts/v1.rpc.json", outputFile: "one.ts" },
          { path: "contracts/v1.rpc.json", outputFile: "two.ts" },
        ],
        expected: /RPC binding input path.*declared more than once/u,
      },
      {
        name: "duplicate output",
        value: [
          { path: "contracts/v1.rpc.json", outputFile: "same.ts" },
          { path: "contracts/v2.rpc.json", outputFile: "same.ts" },
        ],
        expected: /output paths.*collide on portable filesystems/u,
      },
      {
        name: "ASCII-case-colliding output",
        value: [
          { path: "contracts/v1.rpc.json", outputFile: "Bindings/Example.ts" },
          { path: "contracts/v2.rpc.json", outputFile: "bindings/example.ts" },
        ],
        expected: /output paths.*collide on portable filesystems/u,
      },
      {
        name: "file and directory output collision",
        value: [
          { path: "contracts/v1.rpc.json", outputFile: "nested.ts" },
          { path: "contracts/v2.rpc.json", outputFile: "nested.ts/child.ts" },
        ],
        expected: /output paths.*collide on portable filesystems/u,
      },
    ];

    for (const item of invalid) {
      const candidate = await readConfig(root);
      candidate.rpcBindings = item.value;
      await writeConfig(root, candidate);
      await assert.rejects(() => loadProject(root), item.expected, item.name);
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("outputDirectory cannot overlap the owned RPC binding tree on portable filesystems", async () => {
  const root = await createProject();
  try {
    for (const outputDirectory of [
      ".sunder",
      ".sunder/generated",
      ".sunder/generated/rpc",
      ".sunder/generated/rpc/nested",
      ".SUNDER/Generated/RPC",
      ".Sunder/Generated",
    ]) {
      const config = await readConfig(root);
      config.outputDirectory = outputDirectory;
      await writeConfig(root, config);
      await assert.rejects(
        () => loadProject(root),
        /must not equal, contain, or be contained by the owned RPC binding output/u,
        outputDirectory,
      );
    }

    const config = await readConfig(root);
    config.outputDirectory = ".sunder/generated/rpc-adjacent";
    await writeConfig(root, config);
    assert.equal((await loadProject(root)).config.outputDirectory, ".sunder/generated/rpc-adjacent");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("generateRpcBindings emits V1 and V2 bindings and preserves every unchanged mtime", async () => {
  const root = await createProject();
  try {
    await configureBindings(root, [
      { path: "contracts/v1.rpc.json", outputFile: "v1/example.ts" },
      { path: "contracts/v2.rpc.json", outputFile: "v2/example.ts" },
    ]);
    await generateRpcBindings(root);

    const outputRoot = generatedRoot(root);
    const v1Path = resolve(outputRoot, "v1", "example.ts");
    const v2Path = resolve(outputRoot, "v2", "example.ts");
    const v1Source = await readFile(v1Path, "utf8");
    const v2Source = await readFile(v2Path, "utf8");
    assert.match(v1Source, /export const CONTRACT_ID = "example\.rpc"/u);
    assert.match(v1Source, /export const CONTRACT_DESCRIPTOR_SHA256 = "[0-9a-f]{64}"/u);
    assert.match(v1Source, /export const CONTRACT_IDENTITY =/u);
    assert.doesNotMatch(v1Source, /RpcProviderRegistration/u);
    assert.match(v1Source, /export class ExampleClient/u);
    assert.match(v2Source, /export const CONTRACT_ID = "example\.rpc\.v2"/u);
    assert.match(v2Source, /export const CONTRACT_DESCRIPTOR_SHA256 = "[0-9a-f]{64}"/u);
    assert.match(v2Source, /export const CONTRACT_ABI_SHA256 = "[0-9a-f]{64}"/u);
    assert.match(v2Source, /export function createExampleRpcV2RpcProviderRegistration/u);
    assert.match(v2Source, /descriptorSha256: CONTRACT_DESCRIPTOR_SHA256/u);
    assert.match(v2Source, /abiSha256: CONTRACT_ABI_SHA256/u);
    assert.match(v2Source, /export class ExampleClient/u);

    const tracked = [outputRoot, v1Path, v2Path, markerPath(outputRoot)];
    const fixed = new Date("2020-01-02T03:04:05.000Z");
    for (const path of tracked.slice(1)) await utimes(path, fixed, fixed);
    await utimes(outputRoot, fixed, fixed);
    const before = await Promise.all(tracked.map(async (path) => (await stat(path)).mtimeMs));
    await generateRpcBindings(root);
    const after = await Promise.all(tracked.map(async (path) => (await stat(path)).mtimeMs));
    assert.deepEqual(after, before);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("binding generation replaces the complete set, removes stale paths, and materializes an empty owned set", async () => {
  const root = await createProject();
  const emptyRoot = await createProject();
  try {
    await configureBindings(root, [
      { path: "contracts/v1.rpc.json", outputFile: "old/first.ts" },
      { path: "contracts/v2.rpc.json", outputFile: "old/second.ts" },
    ]);
    await generateRpcBindings(root);
    await configureBindings(root, [{ path: "contracts/v2.rpc.json", outputFile: "current/generated.ts" }]);
    await generateRpcBindings(root);
    assert.equal(await exists(resolve(generatedRoot(root), "old", "first.ts")), false);
    assert.equal(await exists(resolve(generatedRoot(root), "old", "second.ts")), false);
    assert.equal(await exists(resolve(generatedRoot(root), "current", "generated.ts")), true);

    await configureBindings(root, []);
    await generateRpcBindings(root);
    assert.deepEqual(await readdir(generatedRoot(root)), []);
    assert.equal(await exists(markerPath(generatedRoot(root))), true);

    await configureBindings(emptyRoot, []);
    await generateRpcBindings(emptyRoot);
    assert.equal(await exists(generatedRoot(emptyRoot)), false);
    assert.equal(await exists(markerPath(generatedRoot(emptyRoot))), false);
    assert.equal(await exists(resolve(emptyRoot, ".sunder")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
    await rm(emptyRoot, { recursive: true, force: true });
  }
});

test("planning failure preserves the previous valid binding set without partial writes", async () => {
  const root = await createProject();
  try {
    await configureBindings(root, [
      { path: "contracts/v1.rpc.json", outputFile: "stable/one.ts" },
      { path: "contracts/v2.rpc.json", outputFile: "stable/two.ts" },
    ]);
    await generateRpcBindings(root);
    const outputRoot = generatedRoot(root);
    const before = await snapshotFiles(outputRoot);
    const beforeMarker = await readFile(markerPath(outputRoot));
    const beforeMtime = (await stat(outputRoot)).mtimeMs;

    await writeFile(resolve(root, "contracts", "v2.rpc.json"), "{ invalid descriptor", "utf8");
    await configureBindings(root, [
      { path: "contracts/v1.rpc.json", outputFile: "future/one.ts" },
      { path: "contracts/v2.rpc.json", outputFile: "future/two.ts" },
    ]);
    await assert.rejects(() => generateRpcBindings(root), /could not be generated/u);
    assert.deepEqual(await snapshotFiles(outputRoot), before);
    assert.deepEqual(await readFile(markerPath(outputRoot)), beforeMarker);
    assert.equal((await stat(outputRoot)).mtimeMs, beforeMtime);
    assert.equal(await exists(resolve(outputRoot, "future", "one.ts")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("generation refuses unmarked output directories and non-file descriptor inputs", async () => {
  const root = await createProject();
  try {
    await configureBindings(root, [{ path: "contracts/v1.rpc.json", outputFile: "example.ts" }]);
    const outputRoot = generatedRoot(root);
    await mkdir(outputRoot, { recursive: true });
    const sentinel = resolve(outputRoot, "sentinel.txt");
    await writeFile(sentinel, "user-owned\n", "utf8");
    await assert.rejects(() => generateRpcBindings(root), /unmarked generated output/u);
    assert.equal(await readFile(sentinel, "utf8"), "user-owned\n");

    await rm(outputRoot, { recursive: true, force: true });
    await configureBindings(root, [{ path: "contracts", outputFile: "example.ts" }]);
    await assert.rejects(() => generateRpcBindings(root), /must resolve to a regular file/u);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("generation refuses descriptor paths that pass through symbolic links or reparse points", async (context) => {
  const root = await createProject();
  try {
    const realDirectory = resolve(root, "linked-source");
    await mkdir(realDirectory);
    await writeFile(resolve(realDirectory, "descriptor.rpc.json"), descriptorText(v1Descriptor()), "utf8");
    try {
      await symlink(realDirectory, resolve(root, "contracts-link"), process.platform === "win32" ? "junction" : "dir");
    } catch (error) {
      if (["EPERM", "EACCES"].includes((error as NodeJS.ErrnoException).code ?? "")) {
        context.skip("The test runner cannot create a symbolic link on this platform.");
        return;
      }
      throw error;
    }
    await configureBindings(root, [{ path: "contracts-link/descriptor.rpc.json", outputFile: "example.ts" }]);
    await assert.rejects(() => generateRpcBindings(root), /symbolic links or reparse points/u);
    assert.equal(await exists(generatedRoot(root)), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("descriptor reads fail if the opened regular file mutates during the immutable snapshot read", async () => {
  const root = await createProject();
  const descriptorPath = resolve(root, "contracts", "v2.rpc.json");
  let prototype: { readFile: FileHandle["readFile"] } | undefined;
  let originalReadFile: FileHandle["readFile"] | undefined;
  try {
    try {
      await configureBindings(root, [{ path: "contracts/v2.rpc.json", outputFile: "example.ts" }]);
      const probe = await open(descriptorPath, "r");
      prototype = Object.getPrototypeOf(probe) as { readFile: FileHandle["readFile"] };
      originalReadFile = prototype.readFile;
      await probe.close();

      let mutated = false;
      const original = originalReadFile;
      prototype.readFile = (async function(this: FileHandle): Promise<Buffer> {
        const bytes = await (original as (this: FileHandle) => Promise<Buffer>).call(this);
        if (!mutated) {
          mutated = true;
          await writeFile(descriptorPath, descriptorText(v2Descriptor("mutated.during.snapshot.read", "2.0.1")), "utf8");
        }
        return bytes;
      }) as FileHandle["readFile"];

      await assert.rejects(() => generateRpcBindings(root), /changed while it was being read.*mixed descriptor snapshot/u);
      assert.equal(mutated, true);
      assert.equal(await exists(generatedRoot(root)), false);
    } finally {
      if (prototype !== undefined && originalReadFile !== undefined) prototype.readFile = originalReadFile;
    }

    await generateRpcBindings(root);
    assert.match(await readFile(resolve(generatedRoot(root), "example.ts"), "utf8"), /mutated\.during\.snapshot\.read/u);
  } finally {
    if (prototype !== undefined && originalReadFile !== undefined) prototype.readFile = originalReadFile;
    await rm(root, { recursive: true, force: true });
  }
});

test("concurrent API calls serialize one complete binding set and leave unchanged output untouched", async () => {
  const root = await createProject();
  try {
    await configureBindings(root, [
      { path: "contracts/v1.rpc.json", outputFile: "one.ts" },
      { path: "contracts/v2.rpc.json", outputFile: "nested/two.ts" },
    ]);
    await Promise.all(Array.from({ length: 8 }, async () => generateRpcBindings(root)));
    assert.deepEqual((await readdir(generatedRoot(root))).sort(), ["nested", "one.ts"]);
    assert.equal(await exists(resolve(generatedRoot(root), "nested", "two.ts")), true);

    const outputRoot = generatedRoot(root);
    const generatedFile = resolve(outputRoot, "one.ts");
    const fixed = new Date("2021-02-03T04:05:06.000Z");
    await utimes(generatedFile, fixed, fixed);
    await utimes(outputRoot, fixed, fixed);
    const before = [(await stat(outputRoot)).mtimeMs, (await stat(generatedFile)).mtimeMs];
    await Promise.all(Array.from({ length: 8 }, async () => generateRpcBindings(root)));
    assert.deepEqual([(await stat(outputRoot)).mtimeMs, (await stat(generatedFile)).mtimeMs], before);
    assert.deepEqual((await readdir(resolve(root, ".sunder", "generated"))).sort(), [
      "rpc",
      "rpc.sunder-generated-output",
    ]);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("a stale generator cannot overwrite a newer plan after waiting for the publish lock", {
  skip: process.platform === "win32" ? "Windows has no SIGSTOP/SIGCONT process suspension." : false,
}, async () => {
  const root = await createProject();
  let staleProcess: ChildProcessWithoutNullStreams | undefined;
  let releaseLock: (() => void) | undefined;
  let lockTask: Promise<void> | undefined;
  try {
    await writeFile(resolve(root, "contracts", "race.rpc.json"), descriptorText(v2Descriptor("race.old", "1.0.0")), "utf8");
    await configureBindings(root, [{ path: "contracts/race.rpc.json", outputFile: "old.ts" }]);
    let signalLocked!: () => void;
    const locked = new Promise<void>((resolvePromise) => { signalLocked = resolvePromise; });
    const lockRelease = new Promise<void>((resolvePromise) => { releaseLock = resolvePromise; });
    lockTask = withOutputLocks([generatedRoot(root)], async () => {
      signalLocked();
      await lockRelease;
    });
    await locked;

    const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
    staleProcess = spawn(process.execPath, [cli, "generate", "--project", root], { stdio: ["pipe", "pipe", "pipe"] });
    await waitForProcessOutputLockAttempt(staleProcess.pid, () => `exitCode=${staleProcess?.exitCode ?? "running"}`);
    assert.equal(staleProcess.exitCode, null);
    await suspendOutsideTransitionGuard(staleProcess, generatedRoot(root));

    await writeFile(resolve(root, "contracts", "race.rpc.json"), descriptorText(v2Descriptor("race.new", "2.0.0")), "utf8");
    await configureBindings(root, [{ path: "contracts/race.rpc.json", outputFile: "new.ts" }]);
    if (releaseLock === undefined) throw new Error("Publish lock release was not initialized.");
    releaseLock();
    releaseLock = undefined;
    await lockTask;
    lockTask = undefined;

    await generateRpcBindings(root);
    assert.match(await readFile(resolve(generatedRoot(root), "new.ts"), "utf8"), /race\.new/u);
    assert.equal(staleProcess.kill("SIGCONT"), true);
    const staleResult = await waitForExit(staleProcess);
    assert.equal(staleResult.code, 0, staleResult.output);
    staleProcess = undefined;

    assert.deepEqual(await readdir(generatedRoot(root)), ["new.ts"]);
    assert.match(await readFile(resolve(generatedRoot(root), "new.ts"), "utf8"), /race\.new/u);
  } finally {
    releaseLock?.();
    await lockTask?.catch(() => undefined);
    if (staleProcess !== undefined) {
      staleProcess.kill("SIGCONT");
      staleProcess.kill("SIGKILL");
      await waitForExit(staleProcess).catch(() => undefined);
    }
    await rm(root, { recursive: true, force: true });
  }
});

test("generate CLI supports standalone V1 bindings without admitting V1 descriptors to Manifest V3 packages", async () => {
  const root = await createProject();
  try {
    const config = await readConfig(root);
    config.contracts = [{ path: "contracts/v1.rpc.json", exposure: "package" }];
    config.rpcBindings = [{ path: "contracts/v1.rpc.json", outputFile: "v1.ts" }];
    await writeConfig(root, config);
    await assert.rejects(() => prepareProject(root), /Package Manifest V3 requires RPC descriptorVersion 2/u);

    const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
    const result = await runProcess(process.execPath, [cli, "generate", "--project", root]);
    assert.equal(result.code, 0, result.output);
    assert.match(await readFile(resolve(generatedRoot(root), "v1.ts"), "utf8"), /example\.rpc/u);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("dev, production worker, and Vite builds consume bindings generated before bundling", async () => {
  const root = await createProject();
  try {
    const generatedId = "generated.binding.only";
    await writeFile(resolve(root, "contracts", "binding.rpc.json"), descriptorText(v2Descriptor(generatedId, "7.8.9")), "utf8");
    const config = await readConfig(root);
    config.contracts = [{ path: "contracts/v2.rpc.json", exposure: "package" }];
    config.providers = [{ providerId: "example.provider", role: "runtime", contractId: "example.rpc.v2", contractVersion: "2.0.0", audience: "owner" }];
    config.rpcBindings = [
      { path: "contracts/v2.rpc.json", outputFile: "package.ts" },
      { path: "contracts/binding.rpc.json", outputFile: "binding.ts" },
    ];
    config.app = {
      root: "app",
      entryPoint: "index.html",
      views: [{
        viewId: "test.rpc.bindings.main",
        displayName: "RPC Bindings",
        route: "/",
        defaultPlacement: "middle",
        showInHotbar: true,
      }],
    };
    await writeConfig(root, config);
    await mkdir(resolve(root, "app"));
    await writeFile(resolve(root, "src", "worker.ts"), workerSource("../.sunder/generated/rpc/binding.ts"), "utf8");
    await writeFile(resolve(root, "app", "index.html"), "<!doctype html><script type=\"module\" src=\"./main.ts\"></script>\n", "utf8");
    await writeFile(resolve(root, "app", "main.ts"), `
      import { CONTRACT_ID } from "../.sunder/generated/rpc/binding.ts";
      document.body.textContent = CONTRACT_ID;
    `, "utf8");

    await buildDevPackage({ projectPath: root, watch: false });
    const devRoot = resolve(root, "dist", "sunder-dev", "payload");
    assert.match(await readFile(resolve(devRoot, "runtime", currentRid(), "worker.cjs"), "utf8"), new RegExp(generatedId, "u"));
    assert.match((await readJavaScriptFiles(resolve(devRoot, "app", currentRid()))).join("\n"), new RegExp(generatedId, "u"));

    await removeGeneratedOutput(root);
    const productionBundle = resolve(root, "production", "worker.cjs");
    await bundleProduction(await prepareProject(root), currentRid(), productionBundle);
    assert.equal(await exists(resolve(generatedRoot(root), "binding.ts")), true);
    assert.match(await readFile(productionBundle, "utf8"), new RegExp(generatedId, "u"));

    await removeGeneratedOutput(root);
    assert.notEqual(await buildWebTarget(root, currentRid()), null);
    assert.equal(await exists(resolve(generatedRoot(root), "binding.ts")), true);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("generated identity, Vite build, and web leaf emission share one immutable contract snapshot", async () => {
  const root = await createProject();
  const startedPath = resolve(root, "vite-build-started");
  const releasePath = resolve(root, "vite-build-release");
  let buildTask: Promise<string | null> | undefined;
  let generationTask: Promise<void> | undefined;
  try {
    const config = await readConfig(root);
    config.contracts = [{ path: "contracts/v2.rpc.json", exposure: "package" }];
    config.rpcBindings = [{ path: "contracts/v2.rpc.json", outputFile: "shared.ts" }];
    config.app = {
      root: "app",
      entryPoint: "index.html",
      views: [{
        viewId: "test.rpc.bindings.snapshot",
        displayName: "Snapshot",
        route: "/",
        defaultPlacement: "middle",
        showInHotbar: true,
      }],
    };
    await writeConfig(root, config);
    await mkdir(resolve(root, "app"));
    await writeFile(resolve(root, "app", "index.html"), "<!doctype html><script type=\"module\" src=\"./main.ts\"></script>\n", "utf8");
    await writeFile(resolve(root, "app", "main.ts"), `
      import { CONTRACT_VERSION } from "../.sunder/generated/rpc/shared.ts";
      document.body.textContent = CONTRACT_VERSION;
    `, "utf8");
    await writeFile(resolve(root, "app", "vite.config.mjs"), `
      import { access, writeFile } from "node:fs/promises";
      const delay = (milliseconds) => new Promise((resolvePromise) => setTimeout(resolvePromise, milliseconds));
      export default { plugins: [{
        name: "sunder-test-build-barrier",
        async buildStart() {
          await writeFile(${JSON.stringify(startedPath)}, "started\\n", "utf8");
          while (true) {
            try { await access(${JSON.stringify(releasePath)}); return; }
            catch { await delay(20); }
          }
        },
      }] };
    `, "utf8");

    buildTask = buildWebTarget(root, currentRid());
    await waitUntil(() => exists(startedPath), "Vite build barrier", () => "Vite did not enter buildStart.");
    const generatedDuringBuild = await readFile(resolve(generatedRoot(root), "shared.ts"), "utf8");
    assert.match(generatedDuringBuild, /export const CONTRACT_VERSION = "2\.0\.0"/u);
    assert.match(generatedDuringBuild, /export const CONTRACT_DESCRIPTOR_SHA256 = "[0-9a-f]{64}"/u);
    assert.match(generatedDuringBuild, /export const CONTRACT_ABI_SHA256 = "[0-9a-f]{64}"/u);
    assert.match(generatedDuringBuild, /export const CONTRACT_IDENTITY = \{/u);

    await writeFile(resolve(root, "contracts", "v2.rpc.json"), descriptorText({
      ...(v2Descriptor() as Record<string, unknown>),
      version: "2.0.1",
    }), "utf8");
    const generationContender = startOutputLockContender(
      generatedRoot(root),
      () => generateRpcBindings(root),
      () => "External generation did not attempt the generated-output lock.",
    );
    generationTask = generationContender.completion;
    await generationContender.attempting;
    await assertPendingAfterTurn(
      generationTask,
      () => "External generation did not wait for Vite and web leaf emission.",
    );
    assert.equal(await readFile(resolve(generatedRoot(root), "shared.ts"), "utf8"), generatedDuringBuild);

    await writeFile(releasePath, "release\n", "utf8");
    const leaf = await buildTask;
    buildTask = undefined;
    assert.notEqual(leaf, null);
    await generationTask;
    generationTask = undefined;

    const manifest = JSON.parse(await readFile(resolve(leaf!, "manifest", "sunder-package.json"), "utf8")) as {
      readonly contractBundles: ReadonlyArray<{
        readonly version: string;
        readonly descriptorSha256: string;
        readonly abiSha256: string;
      }>;
    };
    const packagedBytes = await readFile(resolve(leaf!, "payload", "shared", "contracts", "v2.rpc.json"));
    assert.equal(manifest.contractBundles[0]?.version, "2.0.0");
    assert.equal(manifest.contractBundles[0]?.descriptorSha256, sha256(packagedBytes));
    assert.ok(generatedDuringBuild.includes(manifest.contractBundles[0]!.descriptorSha256));
    assert.ok(generatedDuringBuild.includes(manifest.contractBundles[0]!.abiSha256));
    assert.match((await readJavaScriptFiles(resolve(leaf!, "payload", "app", currentRid()))).join("\n"), /2\.0\.0/u);
    assert.match(await readFile(resolve(generatedRoot(root), "shared.ts"), "utf8"), /export const CONTRACT_VERSION = "2\.0\.1"/u);
  } finally {
    await writeFile(releasePath, "release\n", "utf8").catch(() => undefined);
    await buildTask?.catch(() => undefined);
    await generationTask?.catch(() => undefined);
    await rm(root, { recursive: true, force: true });
  }
});

test("watch treats RPC binding descriptors as structural inputs before recreating its context", async () => {
  const root = await createProject();
  let child: ChildProcessWithoutNullStreams | undefined;
  let output = "";
  try {
    const descriptorPath = resolve(root, "contracts", "watch.rpc.json");
    await writeFile(descriptorPath, descriptorText(v2Descriptor("watch.binding", "7.0.0")), "utf8");
    const config = await readConfig(root);
    config.contracts = [{ path: "contracts/v2.rpc.json", exposure: "package" }];
    config.providers = [{ providerId: "example.provider", role: "runtime", contractId: "example.rpc.v2", contractVersion: "2.0.0", audience: "owner" }];
    config.rpcBindings = [
      { path: "contracts/v2.rpc.json", outputFile: "package.ts" },
      { path: "contracts/watch.rpc.json", outputFile: "watch.ts" },
    ];
    await writeConfig(root, config);
    await writeFile(resolve(root, "src", "worker.ts"), workerSource("../.sunder/generated/rpc/watch.ts", true), "utf8");

    const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
    child = spawn(process.execPath, [cli, "dev", "--watch", "--project", root], { stdio: ["pipe", "pipe", "pipe"] });
    const watchProcess = child;
    watchProcess.stdout.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
    watchProcess.stderr.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
    const bundlePath = resolve(root, "dist", "sunder-dev", "payload", "runtime", currentRid(), "worker.cjs");
    await waitUntil(async () => (await readTextIfPresent(bundlePath)).includes("7.0.0"), "initial generated watch binding", () => output);

    await withOutputLocks([generatedRoot(root)], async () => {
      await observeOutputLockAttempt(
        generatedRoot(root),
        () => writeFile(descriptorPath, descriptorText(v2Descriptor("watch.binding", "7.0.1")), "utf8"),
        () => output,
      );
      assert.match(await readFile(bundlePath, "utf8"), /7\.0\.0/u);
      assert.match(await readFile(resolve(generatedRoot(root), "watch.ts"), "utf8"), /7\.0\.0/u);
    });
    await waitUntil(async () => (await readTextIfPresent(bundlePath)).includes("7.0.1")
      && (await readTextIfPresent(resolve(generatedRoot(root), "watch.ts"))).includes("7.0.1"), "regenerated watch binding", () => output);
    assert.equal(watchProcess.exitCode, null, output);
    watchProcess.kill("SIGINT");
    assert.equal((await waitForExit(watchProcess)).code, 0, output);
    child = undefined;
  } finally {
    child?.kill("SIGKILL");
    await rm(root, { recursive: true, force: true });
  }
});

async function createProject(): Promise<string> {
  const root = resolve(tmpdir(), "sunder-node-rpc-binding-tests", randomUUID());
  await mkdir(resolve(root, "src"), { recursive: true });
  await mkdir(resolve(root, "contracts"), { recursive: true });
  await writeFile(resolve(root, "src", "worker.ts"), "export {};\n", "utf8");
  await writeFile(resolve(root, "contracts", "v1.rpc.json"), descriptorText(v1Descriptor()), "utf8");
  await writeFile(resolve(root, "contracts", "v2.rpc.json"), descriptorText(v2Descriptor()), "utf8");
  await writeConfig(root, {
    schemaVersion: 2,
    id: "test.rpc.bindings",
    name: "RPC Binding Test",
    version: "1.0.0",
    entry: "src/worker.ts",
    workerCapabilities: [],
    contracts: [],
    providers: [],
    usesContracts: [],
  });
  return root;
}

async function configureBindings(root: string, bindings: readonly { readonly path: string; readonly outputFile: string }[]): Promise<void> {
  const config = await readConfig(root);
  config.rpcBindings = bindings;
  await writeConfig(root, config);
}

async function readConfig(root: string): Promise<Record<string, unknown>> {
  return JSON.parse(await readFile(resolve(root, "sunder.package.json"), "utf8")) as Record<string, unknown>;
}

async function writeConfig(root: string, config: Readonly<Record<string, unknown>>): Promise<void> {
  await writeFile(resolve(root, "sunder.package.json"), `${JSON.stringify(config, null, 2)}\n`, "utf8");
}

function generatedRoot(root: string): string {
  return resolve(root, ".sunder", "generated", "rpc");
}

async function removeGeneratedOutput(root: string): Promise<void> {
  await rm(generatedRoot(root), { recursive: true, force: true });
  await rm(markerPath(generatedRoot(root)), { force: true });
}

async function snapshotFiles(root: string): Promise<Readonly<Record<string, string>>> {
  const files: Record<string, string> = {};
  const visit = async (directory: string, prefix: string): Promise<void> => {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const path = resolve(directory, entry.name);
      const logicalPath = prefix.length === 0 ? entry.name : `${prefix}/${entry.name}`;
      if (entry.isDirectory()) await visit(path, logicalPath);
      else files[logicalPath] = (await readFile(path)).toString("base64");
    }
  };
  await visit(root, "");
  return files;
}

async function readJavaScriptFiles(root: string): Promise<string[]> {
  const output: string[] = [];
  const visit = async (directory: string): Promise<void> => {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const path = resolve(directory, entry.name);
      if (entry.isDirectory()) await visit(path);
      else if (entry.name.endsWith(".js")) output.push(await readFile(path, "utf8"));
    }
  };
  await visit(root);
  return output;
}

function workerSource(bindingImport: string, useVersion = false): string {
  return `
    import { CONTRACT_IDENTITY as PACKAGE_CONTRACT_IDENTITY } from "../.sunder/generated/rpc/package.ts";
    import { ${useVersion ? "CONTRACT_VERSION" : "CONTRACT_ID"} as GENERATED_VALUE } from ${JSON.stringify(bindingImport)};
    export const GENERATED_BINDING_FIXTURE = { PACKAGE_CONTRACT_IDENTITY, GENERATED_VALUE };
  `;
}

function v1Descriptor(): unknown {
  return {
    descriptorVersion: 1,
    contractId: "example.rpc",
    version: "1.0.0",
    services: [{
      serviceId: "example",
      methods: [{
        methodId: "invoke",
        kind: "unary",
        requestSchema: "#/$defs/Empty",
        responseSchema: "#/$defs/Result",
      }],
    }],
    $defs: definitions(),
  };
}

function v2Descriptor(contractId = "example.rpc.v2", version = "2.0.0"): unknown {
  return {
    descriptorVersion: 2,
    schemaProfile: "sunder.rpc.values.v1",
    contractId,
    version,
    errors: {},
    services: {
      example: {
        methods: {
          invoke: {
            kind: "unary",
            idempotency: "none",
            errors: [],
            requestSchema: { $ref: "#/$defs/Empty" },
            responseSchema: { $ref: "#/$defs/Result" },
          },
        },
      },
    },
    $defs: definitions(),
  };
}

function definitions(): unknown {
  return {
    Empty: { type: "object", properties: {}, required: [], additionalProperties: false },
    Result: {
      type: "object",
      properties: { accepted: { type: "boolean" } },
      required: ["accepted"],
      additionalProperties: false,
    },
  };
}

function descriptorText(descriptor: unknown): string {
  return `${JSON.stringify(descriptor, null, 2)}\n`;
}

function sha256(bytes: Uint8Array): string {
  return createHash("sha256").update(bytes).digest("hex");
}

async function exists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

async function readTextIfPresent(path: string): Promise<string> {
  try {
    return await readFile(path, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return "";
    throw error;
  }
}

async function runProcess(file: string, argumentsList: readonly string[]): Promise<{ readonly code: number | null; readonly output: string }> {
  const child = spawn(file, [...argumentsList], { stdio: ["ignore", "pipe", "pipe"] });
  let output = "";
  child.stdout.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
  child.stderr.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
  return await new Promise((resolvePromise, reject) => {
    child.once("error", reject);
    child.once("exit", (code) => resolvePromise({ code, output }));
  });
}

async function waitForExit(child: ChildProcessWithoutNullStreams): Promise<{ readonly code: number | null; readonly output: string }> {
  if (child.exitCode !== null) return { code: child.exitCode, output: "" };
  return await new Promise((resolvePromise, reject) => {
    let output = "";
    child.stdout.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
    child.stderr.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
    child.once("error", reject);
    child.once("exit", (code) => resolvePromise({ code, output }));
  });
}

async function waitUntil(condition: () => Promise<boolean>, label: string, diagnostics: () => string): Promise<void> {
  const deadline = Date.now() + 20_000;
  while (!await condition()) {
    if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${label}.\n${diagnostics()}`);
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 25));
  }
}

async function delay(milliseconds: number): Promise<void> {
  await new Promise((resolvePromise) => setTimeout(resolvePromise, milliseconds));
}

async function suspendOutsideTransitionGuard(child: ChildProcessWithoutNullStreams, outputRoot: string): Promise<void> {
  const ownerPath = `${outputRoot}.sunder-output.lock.transition/owner.json`;
  for (let attempt = 0; attempt < 10; attempt++) {
    if (!child.kill("SIGSTOP")) throw new Error("Could not suspend the stale generator process.");
    await delay(50);
    if (!await exists(ownerPath)) return;
    if (!child.kill("SIGCONT")) throw new Error("Could not resume the stale generator process.");
    await delay(100);
  }
  throw new Error("Could not suspend the stale generator outside its transition guard.");
}
