import assert from "node:assert/strict";
import { spawn, spawnSync, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { createReadStream } from "node:fs";
import { chmod, copyFile, mkdir, open, readFile, readlink, readdir, realpath, rename, rm, stat, symlink, utimes, writeFile } from "node:fs/promises";
import { homedir, hostname, tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { inflateRawSync } from "node:zlib";
import { canonicalizeDescriptor, parseRpcContractDescriptor, type JsonValue } from "@sunder/sdk";
import { currentRid, SUPPORTED_RIDS, SUPPORTED_WORKER_CAPABILITIES } from "../src/model";
import { contractsCommand } from "../src/rpc-contracts";
import { buildModuleTarget, smokeModuleTarget } from "../src/module";
import { classifyBundlerVirtualPackage, resolveRolldownPackageJsonFromVite } from "../src/notices";
import { targetLeafDiscoveryKey, withOutputLocks } from "../src/output";
import {
  assertPendingAfterTurn,
  startOutputLockContender,
  waitForProcessOutputLockAttempt,
} from "./observable-barriers";
import {
  aggregateAndPack,
  buildDevPackage,
  buildWebTarget,
  bundleProduction,
  prepareProject,
  writeTargetLeaf,
  writeWebTargetLeaf,
  type LeafDiscoverySource,
  type PreparedProject,
} from "../src/package";

test("dev output uses the canonical runtime-backed Node module shape without launch metadata", async () => {
  const root = await createFixture();
  try {
    await addWorkerDependency(root);
    await buildDevPackage({ projectPath: root, watch: false });
    const devRoot = resolve(root, "dist", "sunder-dev");
    const manifest = JSON.parse(await readFile(resolve(devRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(manifest.manifestVersion, 1);
    const contractBundle = assertSingle(manifest.contractBundles);
    assert.equal(contractBundle.source, "owned");
    assert.equal(contractBundle.authorityKind, "package");
    assert.equal(contractBundle.authorityId, manifest.id);
    const target = assertSingle(manifest.targets);
    assert.equal(target.kind, "worker");
    assert.equal(target.rid, currentRid());
    assert.equal(target.entryPoint, "bin/worker.cjs");
    assert.deepEqual(target.executionRuntime, {
      id: "sunder.runtime.node",
      versionRange: ">=24.18.1 <25.0.0",
      moduleAbi: "sunder.node-worker-module.v1",
    });
    const sdkPackage = JSON.parse(await readFile(resolve(__dirname, "..", "..", "..", "sdk", "package.json"), "utf8")) as { readonly version: string };
    assert.equal(target.sdkVersion, sdkPackage.version);
    assert.ok(target.requiredHostCapabilities.includes("sdk-baseline-1-1.v1"));
    assert.ok(target.requiredHostCapabilities.includes("core.v1"));
    assert.ok(target.requiredHostCapabilities.includes("execution-runtime.v1"));
    assert.ok(target.requiredHostCapabilities.includes("rpc.v1"));
    assert.ok(target.requiredHostCapabilities.includes("worker-protocol.v1"));
    const metadataPath = `${devRoot}.sunder-node-dev.json`;
    assert.equal(await exists(metadataPath), false);
    const contract = (await prepareProject(root)).contracts[0]!;
    const bundlePath = resolve(devRoot, "payload", "runtime", "shared", "bin", "worker.cjs");
    const bundle = await readFile(bundlePath, "utf8");
    assert.match(bundle, new RegExp(contract.descriptorSha256, "u"));
    assert.equal(await exists(resolve(devRoot, "payload", "runtime", "shared", "bin", "worker.cjs.map")), false);
    const workerNotice = await readFile(
      resolve(devRoot, "payload", "runtime", "shared", "THIRD-PARTY-NOTICES", "Bundled-Worker-Dependencies.txt"),
      "utf8",
    );
    assert.match(workerNotice, /@sunder\/sdk@1\.1\.0/u);
    assert.match(workerNotice, /fixture-worker-dependency@4\.5\.6/u);
    assert.match(workerNotice, /nested worker fixture notice/u);
    assert.doesNotMatch(workerNotice, /worker-build-only-dependency/u);
    assert.doesNotMatch(workerNotice, new RegExp(escapeRegExp(root), "u"));

    const descriptorPath = resolve(root, "src", "worker.ts");
    const descriptorSource = await readFile(descriptorPath, "utf8");
    await writeFile(descriptorPath, descriptorSource.replace('maxLength:128', 'maxLength:129'), "utf8");
    const updatedContract = (await prepareProject(root)).contracts[0]!;
    assert.notEqual(updatedContract.descriptorSha256, contract.descriptorSha256);
    assert.notEqual(updatedContract.abiSha256, contract.abiSha256);
    await writeFile(metadataPath, "unowned adjacent file", "utf8");
    await buildDevPackage({ projectPath: root, watch: false });
    assert.equal(await readFile(metadataPath, "utf8"), "unowned adjacent file");
    const updatedBundle = await readFile(bundlePath, "utf8");
    assert.match(updatedBundle, new RegExp(updatedContract.descriptorSha256, "u"));
    assert.doesNotMatch(updatedBundle, new RegExp(contract.descriptorSha256, "u"));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("smoke builds the canonical dev package and delegates to the managed Sunder CLI", async () => {
  const root = await createFixture();
  const marker = resolve(root, "smoke-handoff.json");
  const previousCli = process.env.SUNDER_CLI;
  const previousMarker = process.env.SUNDER_SMOKE_HANDOFF_MARKER;
  try {
    await writeFile(resolve(root, "dev"), [
      "const { existsSync, writeFileSync } = require('node:fs');",
      "const { resolve } = require('node:path');",
      "const args = process.argv.slice(2);",
      "if (args[0] !== 'package' || args[1] !== 'smoke' || !existsSync(resolve(args[2], 'manifest', 'sunder-package.json'))) process.exit(2);",
      "writeFileSync(process.env.SUNDER_SMOKE_HANDOFF_MARKER, JSON.stringify(args));",
    ].join("\n"), "utf8");
    process.env.SUNDER_CLI = process.execPath;
    process.env.SUNDER_SMOKE_HANDOFF_MARKER = marker;

    await smokeModuleTarget(root);

    assert.deepEqual(JSON.parse(await readFile(marker, "utf8")), [
      "package",
      "smoke",
      resolve(root, "dist", "sunder-dev"),
    ]);
  } finally {
    if (previousCli === undefined) delete process.env.SUNDER_CLI;
    else process.env.SUNDER_CLI = previousCli;
    if (previousMarker === undefined) delete process.env.SUNDER_SMOKE_HANDOFF_MARKER;
    else process.env.SUNDER_SMOKE_HANDOFF_MARKER = previousMarker;
    await rm(root, { recursive: true, force: true });
  }
});

test("worker capabilities are explicit, closed, canonical, and stamped on every worker target", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const baseline = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    const authored = [...SUPPORTED_WORKER_CAPABILITIES].reverse();
    await writeFile(configPath, `${JSON.stringify({ ...baseline, workerCapabilities: authored }, null, 2)}\n`, "utf8");
    const project = await prepareProject(root);
    assert.deepEqual(project.config.workerCapabilities, SUPPORTED_WORKER_CAPABILITIES);
    await buildDevPackage({ projectPath: root, watch: false });
    const manifest = JSON.parse(await readFile(
      resolve(root, "dist", "sunder-dev", "manifest", "sunder-package.json"),
      "utf8",
    )) as PackageManifest;
    assert.deepEqual(assertSingle(manifest.targets).requiredHostCapabilities, [
      "auth.v1",
      "callbacks.v1",
      "core.v1",
      "execution-runtime.v1",
      "logging.v1",
      "role-local-workspace.v1",
      "rpc.call-ownership.v1",
      "rpc.extended-deadline.v1",
      "rpc.v1",
      "sdk-baseline-1-1.v1",
      "secrets.v1",
      "settings.schema.v1",
      "settings.v1",
      "storage.key-migration.v1",
      "storage.v1",
      "worker-protocol.v1",
    ]);

    await writeFile(configPath, `${JSON.stringify({ ...baseline, workerCapabilities: ["auth.v1"] }, null, 2)}\n`, "utf8");
    assert.deepEqual((await prepareProject(root)).config.workerCapabilities, ["auth.v1", "callbacks.v1"]);

    const missing = { ...baseline };
    delete missing.workerCapabilities;
    await writeFile(configPath, `${JSON.stringify(missing, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(root), /workerCapabilities must be an array/u);
    await writeFile(configPath, `${JSON.stringify({ ...baseline, workerCapabilities: ["storage.key-migration.v1"] }, null, 2)}\n`, "utf8");
    assert.deepEqual(
      (await prepareProject(root)).config.workerCapabilities,
      ["storage.key-migration.v1", "storage.v1"],
    );
    await writeFile(configPath, `${JSON.stringify({ ...baseline, workerCapabilities: ["logging.v1", "logging.v1"] }, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(root), /Worker capability 'logging\.v1' is declared more than once/u);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("aggregation accepts only Manifest V1 runtime-backed Worker leaves with exact Worker V1 capabilities", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.workerCapabilities = ["logging.v1"];
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const writeLeaf = () => writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const cases: ReadonlyArray<{
      readonly name: string;
      readonly update: (manifest: PackageManifest) => void;
      readonly expected: RegExp;
    }> = [
      ...[2, 3].map((manifestVersion) => ({
        name: `manifestVersion ${manifestVersion}`,
        update: (manifest: PackageManifest) => {
          (manifest as unknown as { manifestVersion: number }).manifestVersion = manifestVersion;
        },
        expected: /must declare manifestVersion 1/u,
      })),
      ...["dotnet", "process"].map((kind) => ({
        name: `legacy Runtime kind ${kind}`,
        update: (manifest: PackageManifest) => {
          (assertSingle(manifest.targets) as unknown as { kind: string }).kind = kind;
        },
        expected: new RegExp(`Runtime target kind '${kind}'.*must use 'worker'`, "u"),
      })),
      ...["rpc.v2", "worker-protocol.v3"].map((capability) => ({
        name: `unsupported capability ${capability}`,
        update: (manifest: PackageManifest) => {
          const target = assertSingle(manifest.targets) as unknown as { requiredHostCapabilities: string[] };
          target.requiredHostCapabilities = [...target.requiredHostCapabilities, capability].sort();
        },
        expected: /requiredHostCapabilities must exactly match/u,
      })),
      ...[
        { name: "missing core.v1", remove: "core.v1" },
        { name: "missing execution-runtime.v1", remove: "execution-runtime.v1" },
        { name: "missing rpc.v1", remove: "rpc.v1" },
        { name: "missing worker-protocol.v1", remove: "worker-protocol.v1" },
        { name: "missing truthful feature capability", remove: "logging.v1" },
      ].map(({ name, remove }) => ({
        name,
        update: (manifest: PackageManifest) => {
          const target = assertSingle(manifest.targets) as unknown as { requiredHostCapabilities: string[] };
          target.requiredHostCapabilities = target.requiredHostCapabilities.filter((capability) => capability !== remove);
        },
        expected: /requiredHostCapabilities must exactly match/u,
      })),
      {
        name: "SDK baseline mismatch",
        update: (manifest) => {
          const target = assertSingle(manifest.targets) as unknown as { requiredHostCapabilities: string[] };
          target.requiredHostCapabilities = target.requiredHostCapabilities.map((capability) => (
            capability === "sdk-baseline-1-1.v1" ? "sdk-baseline-1-0.v1" : capability
          ));
        },
        expected: /requiredHostCapabilities must exactly match/u,
      },
      {
        name: "undeclared feature capability",
        update: (manifest) => {
          const target = assertSingle(manifest.targets) as unknown as { requiredHostCapabilities: string[] };
          target.requiredHostCapabilities = [...target.requiredHostCapabilities, "storage.v1"].sort();
        },
        expected: /requiredHostCapabilities must exactly match/u,
      },
    ];

    for (const item of cases) {
      const leaf = await writeLeaf();
      await rewriteLeafManifest(leaf, item.update);
      await assert.rejects(
        () => aggregateAndPack(root, [leafSource(project, leaf)]),
        item.expected,
        item.name,
      );
    }

    const validLeaf = await writeLeaf();
    const result = await aggregateAndPack(root, [leafSource(project, validLeaf)]);
    const manifest = JSON.parse(await readFile(resolve(result.packageRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(manifest.manifestVersion, 1);
    assert.deepEqual(assertSingle(manifest.targets).requiredHostCapabilities, [
      "core.v1",
      "execution-runtime.v1",
      "logging.v1",
      "rpc.v1",
      "sdk-baseline-1-1.v1",
      "worker-protocol.v1",
    ]);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("target leaves are AggregateSunderPackageTask-compatible thin runtime-backed modules", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const manifest = JSON.parse(await readFile(resolve(leaf, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(manifest.archiveFormatVersion, 1);
    assert.equal(manifest.manifestVersion, 1);
    const target = assertSingle(manifest.targets);
    assert.equal(target.kind, "worker");
    assert.deepEqual(target.requiredHostCapabilities, [
      "core.v1",
      "execution-runtime.v1",
      "rpc.v1",
      "sdk-baseline-1-1.v1",
      "worker-protocol.v1",
    ]);
    assert.equal(target.entryPoint, "bin/worker.cjs");
    assert.deepEqual(target.executionRuntime, {
      id: "sunder.runtime.node",
      versionRange: ">=24.18.1 <25.0.0",
      moduleAbi: "sunder.node-worker-module.v1",
    });
    assert.equal(manifest.contractBundles.length, 1);
    assert.equal(manifest.provides.length, 1);
    const contractBytes = await readFile(resolve(leaf, "payload", "shared", "contracts", "example.rpc.json"));
    const contract = assertSingle(manifest.contractBundles);
    assert.deepEqual(Object.keys(contract).sort(), [
      "abiSha256",
      "authorityId",
      "authorityKind",
      "contractId",
      "descriptorPath",
      "descriptorSha256",
      "exposure",
      "source",
      "version",
    ]);
    assert.equal(contract.source, "owned");
    assert.equal(contract.authorityKind, "package");
    assert.equal(contract.authorityId, project.config.id);
    assert.equal(sha256(contractBytes), contract.descriptorSha256);
    const provider = assertSingle(manifest.provides);
    assert.deepEqual(Object.keys(provider).sort(), [
      "abiSha256",
      "audience",
      "contractId",
      "contractVersion",
      "descriptorSha256",
      "providerId",
      "role",
    ]);
    assert.equal(provider.descriptorSha256, contract.descriptorSha256);
    assert.equal(provider.abiSha256, contract.abiSha256);
    assert.equal(provider.audience, "owner");
    assert.equal(
      contractBytes.toString("utf8"),
      canonicalizeDescriptor(parseRpcContractDescriptor(contractBytes) as unknown as JsonValue),
    );
    const first = await aggregateAndPack(root, [leafSource(project, leaf)]);
    const firstHash = sha256(await readFile(first.archivePath));
    const second = await aggregateAndPack(root, [leafSource(project, leaf)]);
    assert.equal(sha256(await readFile(second.archivePath)), firstHash);
    assert.ok(second.bytes <= 1024 * 1024);
    const archive = await readFile(second.archivePath);
    const entries = zipEntries(archive);
    assert.ok(entries.every((entry) => entry.method === 8));
    assert.equal(entries.find((entry) => entry.name === "payload/shared/contracts/example.rpc.json")?.data.toString("utf8"), contractBytes.toString("utf8"));
    const entryNames = entries.map((entry) => entry.name);
    assert.ok(entryNames.includes("manifest/sunder-package.json"));
    assert.ok(entryNames.includes("payload/runtime/shared/bin/worker.cjs"));
    assert.ok(entryNames.includes("payload/shared/contracts/example.rpc.json"));
    assert.ok(entryNames.includes("payload/runtime/shared/THIRD-PARTY-NOTICES/Bundled-Worker-Dependencies.txt"));
    assert.equal(entryNames.some((entry) => entry.endsWith(".exe") || entry.endsWith(".map") || entry.includes("sunder-node-dev") || entry.includes("Node.js-LICENSE")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Vite web leaf aggregates with a Node Runtime leaf and preserves declarative views", async () => {
  const root = await createFixture();
  try {
    await addWebApp(root);
    await addReactDependencies(root);
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    await writeFile(resolve(root,"src/worker.ts"),packageServiceSource('exposure:"package",providerId:"example.provider",runtimeUse:{required:true,actions:["invoke"]},appUse:{actions:["discover","subscribe"]}'));
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const runtimeLeaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const runtimeManifest = JSON.parse(await readFile(resolve(runtimeLeaf, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(runtimeManifest.manifestVersion, 1);
    assert.deepEqual(runtimeManifest.provides.map((provider) => provider.role), ["runtime"]);
    assert.deepEqual(runtimeManifest.usesContracts.map((use) => use.role), ["runtime"]);
    const appLeaf = await buildWebTarget(root, currentRid());
    assert.notEqual(appLeaf, null);
    const appManifest = JSON.parse(await readFile(resolve(appLeaf!, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(appManifest.manifestVersion, 1);
    const appTarget = assertSingle(appManifest.targets);
    assert.equal(appTarget.kind, "web");
    assert.equal(appTarget.views?.[0]?.viewId, "test.node.package.main");
    assert.deepEqual(appTarget.requiredHostCapabilities, ["core.v1", "rpc.v1", "sdk-baseline-1-1.v1", "views.v1"]);
    assert.equal(appTarget.executionRuntime, undefined);
    assert.deepEqual(appManifest.contractBundles, runtimeManifest.contractBundles);
    assert.deepEqual(appManifest.provides, []);
    assert.deepEqual(appManifest.usesContracts.map((use) => use.role), ["app"]);

    const result = await aggregateAndPack(root, [leafSource(project, runtimeLeaf), leafSource(project, appLeaf!)]);
    const manifest = JSON.parse(await readFile(resolve(result.packageRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(manifest.manifestVersion, 1);
    assert.deepEqual(manifest.targets.map((target) => target.kind).sort(), ["web", "worker"]);
    assert.deepEqual(manifest.provides.map((provider) => `${provider.role}/${provider.providerId}`), ["runtime/example.provider"]);
    assert.deepEqual(manifest.usesContracts.map((use) => `${use.role}/${use.contractId}`), [
      "app/example.rpc",
      "runtime/example.rpc",
    ]);
    const entries = zipEntryNames(await readFile(result.archivePath));
    assert.ok(entries.includes("payload/app/shared/index.html"));
    assert.equal(entries.some((entry) => entry.endsWith(".svg") || entry.endsWith(".map")), false);
    assert.ok(entries.includes("payload/runtime/shared/bin/worker.cjs"));
    assert.ok(entries.includes("payload/runtime/shared/THIRD-PARTY-NOTICES/Bundled-Worker-Dependencies.txt"));
    const webNoticeEntry = entries.find((entry) => entry === "payload/app/shared/THIRD-PARTY-NOTICES/Bundled-Web-Dependencies.txt");
    assert.notEqual(webNoticeEntry, undefined);
    const webNotice = zipEntries(await readFile(result.archivePath)).find((entry) => entry.name === webNoticeEntry)?.data.toString("utf8") ?? "";
    assert.match(webNotice, /react@19\.2\.8/u);
    assert.match(webNotice, /react-dom@19\.2\.8/u);
    assert.match(webNotice, /scheduler@0\.27\.0/u);
    assert.match(webNotice, /vite@8\.2\.0/u);
    assert.match(webNotice, /rolldown@1\.2\.1/u);
    assert.match(webNotice, /fixture-bundled-dependency@2\.3\.4/u);
    assert.match(webNotice, /nested fixture notice/u);
    assert.doesNotMatch(webNotice, /build-only-dependency/u);
    assert.doesNotMatch(webNotice, new RegExp(escapeRegExp(root), "u"));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("web target snapshot waits for a concurrent web-work replacement", async () => {
  const root = await createFixture();
  let releaseGap: (() => void) | undefined;
  let replacementTask: Promise<void> | undefined;
  let snapshotTask: Promise<string> | undefined;
  try {
    await addWebApp(root);
    const rid = currentRid();
    assert.notEqual(await buildWebTarget(root, rid), null);
    const project = await prepareProject(root);
    const webRoot = resolve(root, "dist", ".sunder-web-work", rid);
    const stagedRoot = `${webRoot}.manual-stage-${randomUUID()}`;
    const backupRoot = `${webRoot}.manual-backup-${randomUUID()}`;
    let signalGap!: () => void;
    const gapReady = new Promise<void>((resolvePromise) => { signalGap = resolvePromise; });
    const gapRelease = new Promise<void>((resolvePromise) => { releaseGap = resolvePromise; });
    replacementTask = withOutputLocks([webRoot], async () => {
      await mkdir(stagedRoot);
      await writeFile(resolve(stagedRoot, "index.html"), "<!doctype html><main>replacement generation</main>\n", "utf8");
      await rename(webRoot, backupRoot);
      signalGap();
      await gapRelease;
      await rename(stagedRoot, webRoot);
      await rm(backupRoot, { recursive: true });
    });
    await gapReady;

    const snapshotContender = startOutputLockContender(
      webRoot,
      () => writeWebTargetLeaf(project, rid, webRoot, targetDiscoveryRoot(project)),
      () => "The web snapshot did not attempt the source generation lock.",
    );
    snapshotTask = snapshotContender.completion;
    await snapshotContender.attempting;
    await assertPendingAfterTurn(snapshotTask, () => "The web snapshot did not wait for the source generation lock.");
    if (releaseGap === undefined) throw new Error("Web replacement release was not initialized.");
    releaseGap();
    releaseGap = undefined;
    await replacementTask;
    replacementTask = undefined;
    const leaf = await snapshotTask;
    snapshotTask = undefined;
    assert.match(
      await readFile(resolve(leaf, "payload", "app", rid, "index.html"), "utf8"),
      /replacement generation/u,
    );
  } finally {
    releaseGap?.();
    await replacementTask?.catch(() => undefined);
    await snapshotTask?.catch(() => undefined);
    await rm(root, { recursive: true, force: true });
  }
});

test("Vite attribution failure preserves the prior valid web output", async () => {
  const root = await createFixture();
  try {
    await addWebApp(root);
    const firstLeaf = await buildWebTarget(root, currentRid());
    assert.notEqual(firstLeaf, null);
    const priorIndex = await readFile(resolve(root, "dist", ".sunder-web-work", currentRid(), "index.html"));

    const dependencyRoot = resolve(root, "node_modules", "missing-attribution");
    await mkdir(dependencyRoot, { recursive: true });
    await writeFile(resolve(dependencyRoot, "package.json"), `${JSON.stringify({
      name: "missing-attribution",
      version: "1.0.0",
      type: "module",
      exports: "./index.js",
    }, null, 2)}\n`, "utf8");
    await writeFile(resolve(dependencyRoot, "index.js"), "export const missing = 'missing';\n", "utf8");
    await writeFile(resolve(root, "app", "main.js"), "import { missing } from 'missing-attribution'; document.body.textContent = missing;\n", "utf8");
    await writeFile(resolve(root, "app", "index.html"), "<!doctype html><html><body><script type=\"module\" src=\"./main.js\"></script></body></html>\n", "utf8");

    await assert.rejects(() => buildWebTarget(root, currentRid()), /missing-attribution@1\.0\.0.*no LICENSE/iu);
    assert.deepEqual(await readFile(resolve(root, "dist", ".sunder-web-work", currentRid(), "index.html")), priorIndex);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("rendered unknown virtual runtime modules fail attribution closed", async () => {
  const root = await createFixture();
  try {
    await addWebApp(root);
    const firstLeaf = await buildWebTarget(root, currentRid());
    assert.notEqual(firstLeaf, null);
    const outputPath = resolve(root, "dist", ".sunder-web-work", currentRid(), "index.html");
    const priorIndex = await readFile(outputPath);
    await writeFile(resolve(root, "app", "main.js"), "import { value } from 'virtual:unattributed-runtime'; document.body.textContent = value;\n", "utf8");
    await writeFile(resolve(root, "app", "index.html"), "<!doctype html><html><body><script type=\"module\" src=\"./main.js\"></script></body></html>\n", "utf8");
    await writeFile(resolve(root, "app", "vite.config.mjs"), `
      export default {
        plugins: [{
          name: "unattributed-runtime-fixture",
          resolveId(id) { return id === "virtual:unattributed-runtime" ? "plugin:unattributed-runtime" : null; },
          load(id) { return id === "plugin:unattributed-runtime" ? "export const value = 'virtual';" : null; },
        }],
      };
    `, "utf8");

    await assert.rejects(
      () => buildWebTarget(root, currentRid()),
      /rendered bundled module 'plugin:unattributed-runtime'.*no attributable/iu,
    );
    assert.deepEqual(await readFile(outputPath), priorIndex);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("all pinned Vite and Rolldown runtime virtual ID forms resolve to their exact bundler", () => {
  for (const id of [
    "vite:modulepreload-polyfill",
    "\0vite/modulepreload-polyfill.js",
    "\0vite/preload-helper.js",
    "\0vite/wasm-helper.js",
    "\0vite/dynamic-import-helper.js",
    "\0vite:custom-runtime",
  ]) assert.equal(classifyBundlerVirtualPackage(id), "vite", id);
  for (const id of ["rolldown:runtime", "\0rolldown/runtime.js", "\0rolldown:runtime"]) {
    assert.equal(classifyBundlerVirtualPackage(id), "rolldown", id);
  }
  for (const id of ["\0unknown/runtime.js", "virtual:unknown", "<unknown>"]) {
    assert.equal(classifyBundlerVirtualPackage(id), undefined, id);
  }
});

test("Rolldown attribution resolves from Vite's nested dependency context", async () => {
  const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
  try {
    const vitePackageJson = resolve(root, "node_modules", "vite", "package.json");
    const nestedRolldown = resolve(root, "node_modules", "vite", "node_modules", "rolldown", "package.json");
    const ambientRolldown = resolve(root, "node_modules", "rolldown", "package.json");
    await mkdir(dirname(nestedRolldown), { recursive: true });
    await mkdir(dirname(ambientRolldown), { recursive: true });
    await writeFile(vitePackageJson, '{"name":"vite","version":"8.2.0"}\n', "utf8");
    await writeFile(nestedRolldown, '{"name":"rolldown","version":"1.2.1"}\n', "utf8");
    await writeFile(ambientRolldown, '{"name":"rolldown","version":"9.9.9"}\n', "utf8");

    assert.equal(resolveRolldownPackageJsonFromVite(vitePackageJson), await realpath(nestedRolldown));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("public package tooling contains no embedded Node or SEA production surface", async () => {
  const packageRoot = resolve(__dirname, "..", "..");
  const lock = JSON.parse(await readFile(resolve(packageRoot, "..", "..", "package-lock.json"), "utf8")) as PackageLock;
  assert.equal(lock.packages["node_modules/esbuild"]?.version, "0.28.1");
  assert.equal(lock.packages["node_modules/postject"], undefined);
  assert.equal(lock.packages["node_modules/vite"]?.version, "8.2.0");
  assert.equal((await readFile(resolve(packageRoot, "..", "..", ".nvmrc"), "utf8")).trim(), "24.22.1");
  const toolPackage = JSON.parse(await readFile(resolve(packageRoot, "package.json"), "utf8")) as PackageJson;
  assert.equal(toolPackage.dependencies?.postject, undefined);
  assert.equal(toolPackage.files?.includes("node-distributions.json"), false);
  assert.equal(await exists(resolve(packageRoot, "node-distributions.json")), false);
  assert.equal(await exists(resolve(packageRoot, "src", "sea.ts")), false);
  assert.equal(await exists(resolve(packageRoot, "src", "download.ts")), false);
  assert.equal(await exists(resolve(packageRoot, "src", "node-cache.ts")), false);
  for (const moduleName of ["sea", "download", "node-cache"]) {
    assert.equal(await exists(resolve(packageRoot, "dist", `${moduleName}.js`)), false);
    assert.equal(await exists(resolve(packageRoot, "dist", `${moduleName}.d.ts`)), false);
    assert.equal(await exists(resolve(packageRoot, "dist", `${moduleName}.js.map`)), false);
  }
  const createPackage = JSON.parse(await readFile(resolve(packageRoot, "..", "create-sunder-package", "package.json"), "utf8")) as PackageJson;
  assert.equal(toolPackage.dependencies?.["@sunder/sdk"], ">=1.1.0 <1.2.0");
  assert.equal(createPackage.dependencies?.["@sunder/sdk"], ">=1.1.0 <1.2.0");
  for (const template of ["template", "template-react-node"]) {
    const value = JSON.parse(await readFile(resolve(packageRoot, "..", "create-sunder-package", template, "package.json"), "utf8")) as PackageJson;
    assert.equal(value.dependencies?.["@sunder/sdk"], ">=1.1.0 <1.2.0");
    assert.equal(value.devDependencies?.["@sunder/package-tool"], ">=1.1.0 <1.2.0");
    const config = JSON.parse(await readFile(resolve(packageRoot, "..", "create-sunder-package", template, "sunder.package.json"), "utf8")) as {
      readonly workerCapabilities: readonly string[];
    };
    assert.deepEqual(config.workerCapabilities, []);
  }
  const moduleFixture = JSON.parse(await readFile(resolve(packageRoot, "..", "..", "fixtures", "runtime-backed-worker", "sunder.package.json"), "utf8")) as {
    readonly summary: string;
    readonly workerCapabilities: readonly string[];
  };
  assert.match(moduleFixture.summary, /runtime-backed Node Worker V1/iu);
  assert.deepEqual(moduleFixture.workerCapabilities, []);
});

test("aggregation rejects non-canonical external target leaf paths", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const leaf = await writeTargetLeaf({ project, rid: currentRid(), modulePath: module, workerNoticePath: workerNoticePath(root), discoveryRoot: targetDiscoveryRoot(project) });
    const indexPath = resolve(leaf, "manifest", "content-index.json");
    const index = JSON.parse(await readFile(indexPath, "utf8")) as { files: Array<{ path: string }> };
    const payload = index.files.find((item) => item.path.startsWith("payload/"));
    if (payload === undefined) throw new Error("Fixture payload index entry is missing.");
    payload.path = `payload/runtime/${currentRid()}/../escaped`;
    await writeFile(indexPath, `${JSON.stringify(index, null, 2)}\n`, "utf8");

    await assert.rejects(() => aggregateAndPack(root, [leafSource(project, leaf)]), /canonical/u);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("aggregation rejects unknown external target and view properties", async () => {
  const targetRoot = await createFixture();
  try {
    const project = await prepareProject(targetRoot);
    const module = resolve(targetRoot, "worker.cjs");
    await writeValidModule(module);
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(targetRoot),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    await rewriteLeafManifest(leaf, (manifest) => {
      const target = assertSingle(manifest.targets) as unknown as Record<string, unknown>;
      target.futureField = true;
    });

    await assert.rejects(
      () => aggregateAndPack(targetRoot, [leafSource(project, leaf)]),
      /target contains unknown property 'futureField'/u,
    );
  } finally {
    await rm(targetRoot, { recursive: true, force: true });
  }

  const viewRoot = await createFixture();
  try {
    await addWebApp(viewRoot);
    const project = await prepareProject(viewRoot);
    const leaf = await buildWebTarget(viewRoot, currentRid());
    assert.notEqual(leaf, null);
    await rewriteLeafManifest(leaf!, (manifest) => {
      const target = assertSingle(manifest.targets);
      const view = assertSingle(target.views ?? []) as unknown as Record<string, unknown>;
      view.futureField = true;
    });

    await assert.rejects(
      () => aggregateAndPack(viewRoot, [leafSource(project, leaf!)]),
      /views\[0\] contains unknown property 'futureField'/u,
    );
  } finally {
    await rm(viewRoot, { recursive: true, force: true });
  }
});

test("local descriptors use the strict parser and contract uses require exact versions", async () => {
  const duplicateRoot = await createFixture();
  try {
    const path = resolve(duplicateRoot, "src", "worker.ts");
    const source = await readFile(path, "utf8");
    await writeFile(path, source.replace(
      'contractId:"example.rpc",',
      'contractId:"example.rpc", contractId:"example.rpc",',
    ), "utf8");
    await assert.rejects(() => prepareProject(duplicateRoot), /Duplicate semantic option/u);
  } finally {
    await rm(duplicateRoot, { recursive: true, force: true });
  }

  const rangeRoot = await createFixture();
  try {
    const path = resolve(rangeRoot, "sunder.package.json");
    const config = JSON.parse(await readFile(path, "utf8")) as Record<string, unknown>;
    config.usesContracts = [{ role: "runtime", contractId: "example.rpc", contractVersion: "2.0.0", required: true, actions: ["discover"] }];
    await writeFile(path, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(rangeRoot), /unknown property 'usesContracts'/u);
  } finally {
    await rm(rangeRoot, { recursive: true, force: true });
  }
});

test("Manifest V1 package builds reject non-V1 RPC descriptors", async () => {
  const root = await createFixture();
  try {
    await writeFile(resolve(root, "src", "worker.ts"), packageServiceSource('descriptorVersion:2'), "utf8");

    await assert.rejects(
      () => buildDevPackage({ projectPath: root, watch: false }),
      /Unknown semantic option 'descriptorVersion'/u,
    );
    assert.equal(await exists(resolve(root, "dist", "sunder-dev", "manifest", "sunder-package.json")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Manifest V1 emits exact derived RPC fields and preserves complete SemVer build metadata", async () => {
  const root = await createFixture();
  try {
    const contractVersion = "2.3.4-rc.1+build.7";
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.version = contractVersion;
    await writeFile(resolve(root,"src/worker.ts"),packageServiceSource('exposure:"package",providerId:"example.provider",runtimeUse:{required:true}'));
    config.dependencies = [{ packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0" }];
    const configSource = `${JSON.stringify(config, null, 2)}\n`;
    await writeFile(configPath, configSource, "utf8");

    const project = await prepareProject(root);
    assert.deepEqual(project.config.dependencies, config.dependencies);
    const loaded = assertSingle(project.contracts);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const manifest = JSON.parse(await readFile(resolve(leaf, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;

    assert.equal(manifest.archiveFormatVersion, 1);
    assert.equal(manifest.manifestVersion, 1);
    assert.deepEqual(manifest.dependsOn, [{ packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0", presence: "required" }]);
    for (const field of ["packageArtifactImports", "packageApiImports", "providerImports", "platformImports"] as const) {
      assert.deepEqual(manifest[field], [], field);
    }
    assert.deepEqual(manifest.contractBundles, [{
      contractId: "example.rpc",
      version: contractVersion,
      descriptorPath: "contracts/example.rpc.json",
      descriptorSha256: loaded.descriptorSha256,
      abiSha256: loaded.abiSha256,
      exposure: "package",
      source: "owned",
      authorityKind: "package",
      authorityId: project.config.id,
    }]);
    assert.deepEqual(manifest.usesContracts, [{
      role: "runtime",
      contractId: "example.rpc",
      contractVersion,
      abiSha256: loaded.abiSha256,
      required: true,
      actions: ["discover", "invoke", "subscribe"],
    }]);
    assert.deepEqual(manifest.provides, [{
      providerId: "example.provider",
      role: "runtime",
      contractId: "example.rpc",
      contractVersion,
      descriptorSha256: loaded.descriptorSha256,
      abiSha256: loaded.abiSha256,
      audience: "owner",
    }]);
    assert.deepEqual(Object.keys(assertSingle(manifest.usesContracts)).sort(), [
      "abiSha256",
      "actions",
      "contractId",
      "contractVersion",
      "required",
      "role",
    ]);

    await buildDevPackage({ projectPath: root, watch: false });
    const packed = await aggregateAndPack(root, [leafSource(project, leaf)]);
    for (const outputRoot of [leaf, resolve(root, "dist", "sunder-dev"), packed.packageRoot]) {
      const manifestBytes = await readFile(resolve(outputRoot, "manifest", "sunder-package.json"));
      assert.deepEqual(JSON.parse(manifestBytes.toString("utf8")), manifest);
      const index = JSON.parse(await readFile(resolve(outputRoot, "manifest", "content-index.json"), "utf8")) as {
        readonly files: readonly { readonly path: string; readonly sha256: string; readonly size: number }[];
      };
      assert.deepEqual(index.files.find((entry) => entry.path === "manifest/sunder-package.json"), {
        path: "manifest/sunder-package.json",
        sha256: sha256(manifestBytes),
        size: manifestBytes.byteLength,
      });
    }
    assert.equal(await readFile(configPath, "utf8"), configSource);

    await rewriteLeafManifest(leaf, value => { (value.usesContracts[0] as {contractVersion:string}).contractVersion="2.3.4-rc.1"; });
    await assert.rejects(() => aggregateAndPack(root,[leafSource(project,leaf)]), /metadata|exact|source/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("dependency range policy applies before builds and during target leaf admission without changing authored ranges", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const configSource = await readFile(configPath, "utf8");
    const config = JSON.parse(configSource) as Record<string, unknown>;
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const writeLeaf = (configured: PreparedProject) => writeTargetLeaf({
      project: configured,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(configured),
    });
    const boundedCore = "1".repeat(250) + ".0.0";
    const maximum = "9".repeat(252) + ".9.9";
    const adjacent: ReadonlyArray<readonly [string, string]> = [
      ["1.0.0", "1.0.1-0"],
      ["1.0.0-alpha", "1.0.0-alpha.0"],
      ["1.0.0+" + "b".repeat(250), "1.0.1-0"],
      ["1.0.0-" + "a".repeat(250), "1.0.0-" + "a".repeat(249) + "b"],
      [`${boundedCore}-9`, `${boundedCore}--`],
      [`${boundedCore}--`, `${boundedCore}-A`],
      ["1.0." + "9".repeat(252), "1.1.0-0"],
      ["1." + "9".repeat(252) + ".9", "2.0.0-0"],
    ];
    const cases: ReadonlyArray<{ readonly range: string; readonly expected?: RegExp }> = [
      ...["1.2.3+reviewed", ">=1.0.0 <=1.0.0", "=1.2.3+first =1.2.3+second",
        ">=1.0.0+z <=1.0.0+a", ">1.0.0 <1.0.1", ">1.0.0-alpha.9 <1.0.0-alpha.10",
        "<=0.0.0-0", `>=${maximum}`].map((range) => ({ range })),
      ...[">=2.0.0 <1.0.0", ">1.0.0 <=1.0.0", "=1.0.0 =2.0.0",
        ">1.0.0+first <=1.0.0+second", ">=1.0.0-alpha.10 <=1.0.0-alpha.9",
        "<0.0.0-0", `>${maximum}`].map((range) => ({ range, expected: /permits no semantic version/u })),
      ...["", "^1.0.0", "1.0.0 <2.0.0", ">=1.0.0  <2.0.0", ">=1.0.0\t<2.0.0",
        " >=1.0.0 <2.0.0", ">=1.0.0 <2.0.0 "].map((range) => ({
        range,
        expected: /unsupported or noncanonical|must be a non-empty bounded string/u,
      })),
      ...adjacent.flatMap(([lower, successor]) => [
        { range: `>${lower} <${successor}`, expected: /permits no semantic version/u },
        { range: `>${lower} <=${successor}` },
      ]),
    ];
    for (const { range, expected } of cases) {
      const dependency = { packageId: "example.dependency", versionRange: range };
      await writeFile(configPath, `${JSON.stringify({ ...config, dependencies: [dependency] }, null, 2)}\n`, "utf8");
      if (expected !== undefined) {
        await assert.rejects(() => buildDevPackage({ projectPath: root, watch: false }), expected, range);
        assert.equal(await exists(resolve(project.outputRoot, "sunder-dev")), false, range);
        await writeFile(configPath, configSource, "utf8");
        const leaf = await writeLeaf(project);
        await rewriteLeafManifest(leaf, (manifest) => {
          (manifest as unknown as Record<string, unknown>).dependsOn = [{ ...dependency, presence: "required" }];
        });
        await assert.rejects(() => aggregateAndPack(root, [leafSource(project, leaf)]), expected, range);
      } else {
        const configured = await prepareProject(root);
        assert.deepEqual(configured.config.dependencies, [dependency], range);
        const leaf = await writeLeaf(configured);
        const packed = await aggregateAndPack(root, [leafSource(configured, leaf)]);
        const manifest = JSON.parse(await readFile(resolve(packed.packageRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
        assert.deepEqual(manifest.dependsOn, [{ ...dependency, presence: "required" }], range);
      }
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Manifest V1 aggregation requires explicit owned bundle provenance and rejects imported bundles", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const writeLeaf = () => writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const cases: ReadonlyArray<{
      readonly name: string;
      readonly update: (bundle: Record<string, unknown>) => void;
      readonly expected: RegExp;
    }> = [
      ...[
        { field: "source", canonical: "owned", unknown: "borrowed" },
        { field: "authorityKind", canonical: "package", unknown: "organization" },
        { field: "authorityId", canonical: project.config.id, unknown: "invalid authority" },
      ].flatMap(({ field, canonical, unknown }) => [
        {
          name: `missing ${field}`,
          update: (bundle: Record<string, unknown>) => { delete bundle[field]; },
          expected: /contractBundles\[0\] must contain exactly/u,
        },
        ...[null, "", unknown, canonical.toUpperCase(), ` ${canonical}`, `${canonical} `, false, {}, []].map((value) => ({
          name: `${field} = ${JSON.stringify(value)}`,
          update: (bundle: Record<string, unknown>) => { bundle[field] = value; },
          expected: /must declare canonical source, authorityKind, and authorityId provenance/u,
        })),
        {
          name: `case-variant ${field} property`,
          update: (bundle: Record<string, unknown>) => {
            bundle[field.toUpperCase()] = bundle[field];
            delete bundle[field];
          },
          expected: new RegExp(`contains unknown property '${field.toUpperCase()}'`, "u"),
        },
      ]),
      {
        name: "unknown provenance property",
        update: (bundle) => { bundle.futureProvenance = "owned"; },
        expected: /contains unknown property 'futureProvenance'/u,
      },
      {
        name: "contract identity is not package ownership",
        update: (bundle) => { bundle.authorityId = bundle.contractId; },
        expected: /owned bundles require authorityKind 'package' and authorityId equal to the manifest package id/u,
      },
      {
        name: "owned platform authority",
        update: (bundle) => { bundle.authorityKind = "platform"; bundle.authorityId = "sunder.core"; },
        expected: /owned bundles require authorityKind 'package' and authorityId equal to the manifest package id/u,
      },
      ...[
        { field: "selectedAuthorityRelease", canonical: "1.2.3+release.4" },
        { field: "selectedAuthorityContentIdentity", canonical: "a".repeat(64) },
      ].flatMap(({ field, canonical }) => [
        ...[canonical, null].map((value) => ({
          name: `owned bundle forbids ${field} = ${JSON.stringify(value)}`,
          update: (bundle: Record<string, unknown>) => { bundle[field] = value; },
          expected: new RegExp(`contains unknown property '${field}'`, "u"),
        })),
        ...[undefined, null, "invalid"].map((value) => ({
          name: `imported bundle requires ${field} = ${JSON.stringify(value)}`,
          update: (bundle: Record<string, unknown>) => {
            bundle.source = "imported";
            bundle.selectedAuthorityRelease = "1.2.3+release.4";
            bundle.selectedAuthorityContentIdentity = "a".repeat(64);
            if (value === undefined) delete bundle[field];
            else bundle[field] = value;
          },
          expected: value === undefined
            ? /contractBundles\[0\] must contain exactly/u
            : /imported bundles require a canonical selectedAuthorityRelease and selectedAuthorityContentIdentity/u,
        })),
      ]),
      ...["package", "platform"].map((authorityKind) => ({
        name: `unsupported imported ${authorityKind} bundle`,
        update: (bundle: Record<string, unknown>) => {
          bundle.source = "imported";
          bundle.exposure = "public";
          bundle.authorityKind = authorityKind;
          bundle.authorityId = authorityKind === "package" ? "example.owner" : "sunder.core";
          bundle.selectedAuthorityRelease = "1.2.3+release.4";
          bundle.selectedAuthorityContentIdentity = "a".repeat(64);
        },
        expected: /metadata differs from the complete fresh source/u,
      })),
      ...["catalogSigningKeyId", "catalogTrustGeneration", "catalogTrustFingerprint"].flatMap(field =>
        ["package", "platform"].map(authorityKind => ({
          name: `${authorityKind} bundle rejects retired ${field}`,
          update: (bundle: Record<string, unknown>) => {
            Object.assign(bundle, {
              source: "imported",
              authorityKind,
              authorityId: authorityKind === "package" ? "example.owner" : "sunder.core",
              selectedAuthorityRelease: "1.2.3+release.4",
              selectedAuthorityContentIdentity: "a".repeat(64),
              [field]: "obsolete",
            });
          },
          expected: new RegExp(`contains unknown property '${field}'`, "u"),
        }))),
    ];
    const validLeaf = await writeLeaf();
    const packed = await aggregateAndPack(root, [leafSource(project, validLeaf)]);
    const preservedPaths = [
      packed.archivePath,
      packed.evidencePath,
      resolve(packed.packageRoot, "manifest", "sunder-package.json"),
      resolve(packed.packageRoot, "manifest", "content-index.json"),
    ];
    const preservedBytes = await Promise.all(preservedPaths.map((path) => readFile(path)));
    for (const candidate of cases) {
      const leaf = await writeLeaf();
      await rewriteLeafManifest(leaf, (manifest) => {
        candidate.update(assertSingle(manifest.contractBundles) as unknown as Record<string, unknown>);
      });
      await assert.rejects(() => aggregateAndPack(root, [leafSource(project, leaf)]), candidate.expected, candidate.name);
      assert.deepEqual(await Promise.all(preservedPaths.map((path) => readFile(path))), preservedBytes, candidate.name);
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Manifest V1 aggregation accepts empty import groups but rejects nonempty or malformed groups and invalid dependencies", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.dependencies = [{ packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0" }];
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const writeLeaf = () => writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const dependency = { packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0", presence: "required" };
    const cases: ReadonlyArray<{
      readonly name: string;
      readonly update: (manifest: Record<string, unknown>) => void;
      readonly expected: RegExp;
    }> = [
      ...[undefined, null, "", "unknown", "Required", "OPTIONAL", " required", "required ", false, {}, []].map((presence) => ({
        name: `dependency presence = ${JSON.stringify(presence)}`,
        update: (manifest: Record<string, unknown>) => { manifest.dependsOn = [{ ...dependency, presence }]; },
        expected: presence === undefined
          ? /dependsOn\[0\] must contain exactly packageId, versionRange, presence/u
          : /presence 'required' or 'optional'/u,
      })),
      {
        name: "optional dependency",
        update: (manifest) => { manifest.dependsOn = [{ ...dependency, presence: "optional" }]; },
        expected: /metadata differs from the complete fresh source/u,
      },
      {
        name: "case-variant dependency presence property",
        update: (manifest) => {
          manifest.dependsOn = [{ packageId: dependency.packageId, versionRange: dependency.versionRange, Presence: "required" }];
        },
        expected: /dependsOn\[0\] contains unknown property 'Presence'/u,
      },
      {
        name: "unknown dependency property",
        update: (manifest) => { manifest.dependsOn = [{ ...dependency, futureField: true }]; },
        expected: /dependsOn\[0\] contains unknown property 'futureField'/u,
      },
      ...[
        { packageId: "Example.Dependency" },
        { packageId: null },
        { versionRange: "invalid" },
        { versionRange: null },
      ].map((value) => ({
        name: `non-canonical dependency ${JSON.stringify(value)}`,
        update: (manifest: Record<string, unknown>) => { manifest.dependsOn = [{ ...dependency, ...value }]; },
        expected: value.versionRange === "invalid"
          ? /versionRange 'invalid' is unsupported or noncanonical/u
          : /must declare a canonical package id, version range, and presence/u,
      })),
      {
        name: "duplicate dependency",
        update: (manifest) => { manifest.dependsOn = [dependency, dependency]; },
        expected: /declares dependency package id 'example.dependency' more than once/u,
      },
      {
        name: "null dependency",
        update: (manifest) => { manifest.dependsOn = [null]; },
        expected: /dependsOn\[0\] must be an object/u,
      },
      ...[null, {}, Array.from({ length: 129 }, () => dependency)].map((dependsOn) => ({
        name: `invalid dependency array (${Array.isArray(dependsOn) ? dependsOn.length : JSON.stringify(dependsOn)})`,
        update: (manifest: Record<string, unknown>) => { manifest.dependsOn = dependsOn; },
        expected: /dependsOn must be an array with at most 128 entries/u,
      })),
      ...["packageArtifactImports", "packageApiImports", "providerImports", "platformImports"].flatMap((field) => [
        ...[[{}], [null], null, {}, false, 0, "[]"].map((value) => ({
          name: `${field} = ${JSON.stringify(value)}`,
          update: (manifest: Record<string, unknown>) => { manifest[field] = value; },
          expected: Array.isArray(value) ? /metadata differs from the complete fresh source/u : new RegExp(`${field} must be an array when present`, "u"),
        })),
        {
          name: `case-variant ${field} property`,
          update: (manifest: Record<string, unknown>) => { manifest[field.toUpperCase()] = []; },
          expected: new RegExp(`contains unknown property '${field.toUpperCase()}'`, "u"),
        },
        {
          name: `missing canonical ${field} still differs in the metadata fingerprint`,
          update: (manifest: Record<string, unknown>) => { delete manifest[field]; },
          expected: /do not match the configured package identity and metadata/u,
        },
      ]),
      {
        name: "unknown manifest property",
        update: (manifest) => { manifest.futureField = true; },
        expected: /contains unknown property 'futureField'/u,
      },
    ];
    for (const candidate of cases) {
      const leaf = await writeLeaf();
      await rewriteLeafManifest(leaf, (manifest) => candidate.update(manifest as unknown as Record<string, unknown>));
      await assert.rejects(() => aggregateAndPack(root, [leafSource(project, leaf)]), candidate.expected, candidate.name);
      assert.equal(await exists(resolve(project.outputRoot, "package")), false, candidate.name);
      assert.equal(await exists(resolve(project.outputRoot, `${project.config.id}.${project.config.version}.sunderpkg`)), false, candidate.name);
      assert.equal(await exists(resolve(project.outputRoot, `${project.config.id}.${project.config.version}.package-evidence.json`)), false, candidate.name);
    }
    const validLeaf = await writeLeaf();
    const packed = await aggregateAndPack(root, [leafSource(project, validLeaf)]);
    const leafManifest = JSON.parse(await readFile(resolve(validLeaf, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    const packedManifest = JSON.parse(await readFile(resolve(packed.packageRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.deepEqual(packedManifest, leafManifest);
    for (const field of ["packageArtifactImports", "packageApiImports", "providerImports", "platformImports"] as const) {
      assert.deepEqual(packedManifest[field], [], field);
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("V1 descriptor annotations change only descriptor identity while structural changes alter both hashes", async () => {
  const root = await createFixture();
  try {
    const descriptorPath = resolve(root, "src", "worker.ts");
    const original = await prepareProject(root);
    const initial = assertSingle(original.contracts);
    assert.equal(initial.descriptor.descriptorVersion, 1);
    assert.equal(initial.exposure, "package");
    assert.equal(initial.canonicalBytes.toString("utf8"), initial.canonical);
    assert.equal(sha256(initial.canonicalBytes), initial.descriptorSha256);

    await writeFile(descriptorPath, packageServiceSource(undefined,128,"Annotation-only documentation."), "utf8");
    const annotationIdentity = assertSingle((await prepareProject(root)).contracts);
    assert.notEqual(annotationIdentity.descriptorSha256, initial.descriptorSha256);
    assert.equal(annotationIdentity.abiSha256, initial.abiSha256);

    await writeFile(descriptorPath, packageServiceSource(undefined,129,"Annotation-only documentation."), "utf8");
    const structuralIdentity = assertSingle((await prepareProject(root)).contracts);
    assert.notEqual(structuralIdentity.descriptorSha256, annotationIdentity.descriptorSha256);
    assert.notEqual(structuralIdentity.abiSha256, annotationIdentity.abiSha256);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("service authoring rejects retired schemas and manual wire/provenance aliases", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const baseline = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    await writeFile(configPath, `${JSON.stringify({ ...baseline, schemaVersion: 1 }, null, 2)}\n`, "utf8");
    await assert.rejects(
      () => prepareProject(root),
      /schemaVersion must be 3|unknown property 'services'/u,
    );
    await writeFile(configPath, `${JSON.stringify({ ...baseline, schemaVersion: 4 }, null, 2)}\n`, "utf8");
    await assert.rejects(
      () => prepareProject(root),
      /schemaVersion must be 3|unknown property 'services'/u,
    );

    const invalid: ReadonlyArray<{ readonly value: Partial<Record<string, unknown>>; readonly expected: RegExp }> = [
      ...["source", "authorityKind", "authorityId", "selectedAuthorityRelease", "selectedAuthorityContentIdentity",
        "catalogSigningKeyId", "catalogTrustGeneration", "catalogTrustFingerprint"].map((field) => ({
        value: { contracts: [{ path: "contracts/example.rpc.json", exposure: "package", [field]: "unsupported" }] },
        expected: /unknown property 'contracts'/u,
      })),
      ...["required", "optional"].map((presence) => ({
        value: { dependencies: [{ packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0", presence }] },
        expected: /unknown property 'presence'/u,
      })),
      ...["packageArtifactImports", "packageApiImports", "providerImports", "platformImports"].map((field) => ({
        value: { [field]: [] },
        expected: new RegExp(`unknown property '${field}'`, "u"),
      })),
      {
        value: { contracts: [{ path: "contracts/example.rpc.json" }] },
        expected: /unknown property 'contracts'/u,
      },
      {
        value: { contracts: [{ path: "contracts/example.rpc.json", exposure: "package", sha256: "0".repeat(64) }] },
        expected: /unknown property 'contracts'/u,
      },
      {
        value: {
          providers: [{
            providerId: "example.provider",
            role: "runtime",
            contractId: "example.rpc",
            contractVersion: "1.0.0",
            audience: "owner",
            contractSha256: "0".repeat(64),
          }],
        },
        expected: /unknown property 'providers'/u,
      },
      {
        value: {
          usesContracts: [{
            role: "runtime",
            contractId: "example.rpc",
            versionRange: "1.0.0",
            required: true,
            actions: ["invoke"],
          }],
        },
        expected: /unknown property 'usesContracts'/u,
      },
    ];
    for (const candidate of invalid) {
      await writeFile(configPath, `${JSON.stringify({ ...baseline, ...candidate.value }, null, 2)}\n`, "utf8");
      await assert.rejects(() => prepareProject(root), candidate.expected);
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("contract exposure and provider audience enforce Manifest V1 owner/public visibility policy", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    for (const [exposure, audience] of [
      ["package", "owner"],
      ["public", "owner"],
      ["public", "public"],
    ] as const) {
      await writeFile(resolve(root,"src/worker.ts"),packageServiceSource(`exposure:"${exposure}",audience:"${audience}",providerId:"example.provider"`));
      if(exposure==="public")await contractsCommand("accept",root);
      const project = await prepareProject(root);
      assert.equal(assertSingle(project.contracts).exposure, exposure);
    }

    await writeFile(resolve(root,"src/worker.ts"),packageServiceSource('exposure:"package",audience:"public"'));
    await assert.rejects(() => prepareProject(root), /audience is incompatible/u);

  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("authoring rejects App-role providers while App-role uses remain valid", async () => {
  const root = await createFixture();
  try {
    await addWebApp(root);
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.providers = [{
      providerId: "example.provider",
      role: "app",
      contractId: "example.rpc",
      contractVersion: "1.0.0",
      audience: "owner",
    }];
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    await assert.rejects(
      () => prepareProject(root),
      /unknown property 'providers'/u,
    );

    delete config.providers;
    await writeFile(resolve(root,"src/worker.ts"),packageServiceSource('appUse:{actions:["discover"]}'));
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    const project = await prepareProject(root);
    assert.equal(assertSingle(project.config.usesContracts ?? []).role, "app");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("strict target leaf validation rejects App-role providers", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const manifestPath = resolve(leaf, "manifest", "sunder-package.json");
    const manifest = JSON.parse(await readFile(manifestPath, "utf8")) as {
      provides: Array<{ role: string }>;
    };
    assertSingle(manifest.provides).role = "app";
    const manifestBytes = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`);
    await writeFile(manifestPath, manifestBytes);
    const indexPath = resolve(leaf, "manifest", "content-index.json");
    const index = JSON.parse(await readFile(indexPath, "utf8")) as {
      files: Array<{ path: string; sha256: string; size: number }>;
    };
    const manifestEntry = index.files.find((entry) => entry.path === "manifest/sunder-package.json");
    if (manifestEntry === undefined) throw new Error("Fixture manifest index entry is missing.");
    manifestEntry.sha256 = sha256(manifestBytes);
    manifestEntry.size = manifestBytes.byteLength;
    await writeFile(indexPath, `${JSON.stringify(index, null, 2)}\n`, "utf8");

    await assert.rejects(
      () => aggregateAndPack(root, [leafSource(project, leaf)]),
      /App-role providers are not supported because App provider registration and serving are not available/u,
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("aggregation retains one deterministic universal package for all six exact RIDs", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const leaves: string[] = [];
    for (const rid of SUPPORTED_RIDS) {
      leaves.push(await writeTargetLeaf({ project, rid, modulePath: module, workerNoticePath: workerNoticePath(root), discoveryRoot: targetDiscoveryRoot(project) }));
    }
    const result = await aggregateAndPack(root, leaves.map((leaf) => leafSource(project, leaf)));
    const manifest = JSON.parse(await readFile(resolve(result.packageRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.deepEqual(manifest.targets.map((target) => target.rid), [...SUPPORTED_RIDS]);
    const entries = zipEntryNames(await readFile(result.archivePath));
    assert.equal(entries.filter((entry) => entry === "payload/runtime/shared/bin/worker.cjs").length, 1);
    assert.equal(entries.some((entry) => entry.endsWith(".exe") || entry.includes("Node.js-LICENSE")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("package CLI builds all six exact-RID leaves before aggregation", async () => {
  const root = await createFixture();
  try {
    const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
    const result = spawnSync(process.execPath, [cli, "package", "--project", root], {
      encoding: "utf8",
      timeout: 300_000,
    });
    assert.equal(result.status, 0, `${result.stdout}${result.stderr}`);
    const manifest = JSON.parse(await readFile(
      resolve(root, "dist", "package", "manifest", "sunder-package.json"),
      "utf8",
    )) as PackageManifest;
    assert.deepEqual(manifest.targets.map((target) => target.rid), [...SUPPORTED_RIDS]);
    for (const rid of SUPPORTED_RIDS) {
      assert.equal(await exists(resolve(root, "dist", "targets", rid)), true);
    }
    assert.equal(await exists(resolve(root, "dist", "test.node.package.1.0.0.sunderpkg")), true);
    assert.equal(await exists(resolve(root, "dist", "test.node.package.1.0.0.package-evidence.json")), true);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("configured package-owned native assets remain in their exact RID layers", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.nativeAssets = Object.fromEntries(await Promise.all(SUPPORTED_RIDS.map(async (rid) => {
      const sourcePath = `native/${rid}/sqlite.node`;
      await mkdir(resolve(root, "native", rid), { recursive: true });
      await writeFile(resolve(root, ...sourcePath.split("/")), `native-${rid}\n`, "utf8");
      return [rid, [{ sourcePath, targetPath: "native/sqlite.node" }]];
    })));
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const leaves = [];
    for (const rid of SUPPORTED_RIDS) {
      const leaf = await writeTargetLeaf({
        project,
        rid,
        modulePath: module,
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(project),
      });
      assert.equal(
        await readFile(resolve(leaf, "payload", "runtime", rid, "native", "sqlite.node"), "utf8"),
        `native-${rid}\n`,
      );
      leaves.push(leaf);
    }

    const first = await aggregateAndPack(root, leaves.map((leaf) => leafSource(project, leaf)));
    const firstHash = await hashFile(first.archivePath);
    const firstEvidence = await readFile(first.evidencePath);
    const entries = zipEntries(await readFile(first.archivePath));
    assert.equal(entries.some((entry) => entry.name === "payload/runtime/shared/native/sqlite.node"), false);
    for (const rid of SUPPORTED_RIDS) {
      assert.equal(
        entries.find((entry) => entry.name === `payload/runtime/${rid}/native/sqlite.node`)?.data.toString("utf8"),
        `native-${rid}\n`,
      );
    }
    const evidence = JSON.parse(firstEvidence.toString("utf8")) as PackageOwnershipEvidence;
    assert.equal(evidence.schemaVersion, 1);
    assert.equal(evidence.archiveSha256, firstHash);
    assert.equal(
      evidence.metadataBytes,
      (await stat(resolve(first.packageRoot, "manifest", "sunder-package.json"))).size
        + (await stat(resolve(first.packageRoot, "manifest", "content-index.json"))).size,
    );
    assert.ok(evidence.payloadBytes > 0);
    assert.equal(evidence.runtimeClosureFileCount, 0);
    assert.equal(evidence.runtimeClosureBytes, 0);
    assert.deepEqual(evidence.targets.map((target) => target.rid), [...SUPPORTED_RIDS]);
    for (const target of evidence.targets) {
      assert.equal(target.runtimeClosureBytes, 0);
      assert.equal(target.nativeAssets.length, 1);
      assert.equal(target.nativeAssets[0]?.path, "native/sqlite.node");
      assert.ok(target.ownership.exactRidBytes > 0);
      assert.ok(target.ownership.roleSharedBytes > 0);
    }
    const second = await aggregateAndPack(root, leaves.map((leaf) => leafSource(project, leaf)));
    assert.equal(await hashFile(second.archivePath), firstHash);
    assert.deepEqual(await readFile(second.evidencePath), firstEvidence);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("renamed exact and format-mutated active Node executables are rejected independent of executable mode", async () => {
  const root = await createFixture();
  try {
    const nativeRoot = resolve(root, "native", currentRid());
    await mkdir(nativeRoot, { recursive: true });
    const disguisedNode = resolve(nativeRoot, "sqlite.node");
    await copyFile(process.execPath, disguisedNode);
    if (process.platform !== "win32") await chmod(disguisedNode, 0o600);
    await configureNativeAsset(root, currentRid(), `native/${currentRid()}/sqlite.node`);
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const input = {
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    } as const;

    await assert.rejects(
      () => writeTargetLeaf(input),
      /forbidden execution-runtime closure.*executable image/u,
    );

    const handle = await open(disguisedNode, "r+");
    try {
      const magic = Buffer.alloc(1);
      assert.equal((await handle.read(magic, 0, 1, 0)).bytesRead, 1);
      magic[0] = magic[0]! ^ 0xff;
      await handle.write(magic, 0, 1, 0);
    } finally {
      await handle.close();
    }
    await assert.rejects(
      () => writeTargetLeaf(input),
      /active Node executable's header-independent content identity/u,
    );
    assert.equal(await exists(resolve(root, "dist", "targets", currentRid())), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("aggregate admission rejects a forged renamed Node executable before zero-closure evidence is emitted", async () => {
  const root = await createFixture();
  try {
    const rid = currentRid();
    const sourcePath = `native/${rid}/sqlite.node`;
    await mkdir(resolve(root, "native", rid), { recursive: true });
    await writeFile(resolve(root, ...sourcePath.split("/")), "package-owned native fixture\n", "utf8");
    await configureNativeAsset(root, rid, sourcePath);
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const leaf = await writeTargetLeaf({
      project,
      rid,
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const forgedPath = `payload/runtime/${rid}/native/sqlite.node`;
    await copyFile(process.execPath, resolve(leaf, ...forgedPath.split("/")));
    await rewriteLeafFileIndex(leaf, forgedPath);

    await assert.rejects(
      () => aggregateAndPack(root, [leafSource(project, leaf)]),
      /forbidden execution-runtime closure.*executable image/u,
    );
    assert.equal(await exists(resolve(root, "dist", "test.node.package.1.0.0.package-evidence.json")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("binary metadata admits target-compatible shared libraries for every supported RID", async () => {
  const root = await createFixture();
  try {
    await configureNativeAssetMatrix(root, (rid) => nativeBinaryFixture(rid, "shared-library"));
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const leaves = [];
    for (const rid of SUPPORTED_RIDS) {
      leaves.push(await writeTargetLeaf({
        project,
        rid,
        modulePath: module,
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(project),
      }));
    }

    const result = await aggregateAndPack(root, leaves.map((leaf) => leafSource(project, leaf)));
    const evidence = JSON.parse(await readFile(result.evidencePath, "utf8")) as PackageOwnershipEvidence;
    assert.equal(evidence.runtimeClosureFileCount, 0);
    assert.equal(evidence.runtimeClosureBytes, 0);
    assert.deepEqual(evidence.targets.map((target) => target.rid), [...SUPPORTED_RIDS]);
    assert.ok(evidence.targets.every((target) => target.runtimeClosureFileCount === 0
      && target.runtimeClosureBytes === 0
      && target.nativeAssets[0]?.path === "native/sqlite.node"));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("binary metadata rejects renamed executables for every supported RID", async () => {
  const root = await createFixture();
  try {
    await configureNativeAssetMatrix(root, (rid) => nativeBinaryFixture(rid, "executable"));
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    for (const rid of SUPPORTED_RIDS) {
      await assert.rejects(
        () => writeTargetLeaf({
          project,
          rid,
          modulePath: module,
          workerNoticePath: workerNoticePath(root),
          discoveryRoot: targetDiscoveryRoot(project),
        }),
        /forbidden execution-runtime closure.*executable image/u,
      );
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("binary ownership rejects managed BCL, object toolchain, runtime fingerprints, and foreign formats when renamed", async () => {
  const root = await createFixture();
  try {
    const cases: ReadonlyArray<{
      readonly rid: typeof SUPPORTED_RIDS[number];
      readonly bytes: Buffer;
      readonly expected: RegExp;
    }> = [
      { rid: "win-x64", bytes: peBinaryFixture("x64", "managed-assembly"), expected: /managed PE\/CLR assembly/u },
      { rid: "linux-x64", bytes: elfBinaryFixture("x64", "toolchain"), expected: /ELF relocatable object/u },
      {
        rid: "linux-arm64",
        bytes: Buffer.concat([
          elfBinaryFixture("arm64", "shared-library"),
          Buffer.from("NODE_OPTIONS NODE_EXTRA_CA_CERTS node::Start process.binding", "ascii"),
        ]),
        expected: /Node runtime content in a native asset/u,
      },
      { rid: "osx-arm64", bytes: peBinaryFixture("arm64", "shared-library"), expected: /foreign to osx-arm64/u },
      { rid: "win-arm64", bytes: peBinaryFixture("x64", "shared-library"), expected: /does not contain the arm64 architecture/u },
      { rid: "osx-x64", bytes: machBinaryFixture("x64", "mutated-executable"), expected: /Mach-O executable image/u },
    ];
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    for (const [index, item] of cases.entries()) {
      const sourcePath = `native/case-${index}/sqlite.node`;
      await mkdir(resolve(root, "native", `case-${index}`), { recursive: true });
      await writeFile(resolve(root, ...sourcePath.split("/")), item.bytes);
      await configureNativeAsset(root, item.rid, sourcePath);
      const project = await prepareProject(root);
      await assert.rejects(
        () => writeTargetLeaf({
          project,
          rid: item.rid,
          modulePath: module,
          workerNoticePath: workerNoticePath(root),
          discoveryRoot: targetDiscoveryRoot(project),
        }),
        item.expected,
      );
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("native asset authoring rejects RID ambiguity, payload collisions, and execution-runtime files", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const baseline = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    await mkdir(resolve(root, "native"), { recursive: true });
    await writeFile(resolve(root, "native", "addon.node"), "native-addon\n", "utf8");

    await writeFile(configPath, `${JSON.stringify({
      ...baseline,
      nativeAssets: { "freebsd-x64": [{ sourcePath: "native/addon.node", targetPath: "native/addon.node" }] },
    }, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(root), /unknown property 'freebsd-x64'/u);

    await writeFile(configPath, `${JSON.stringify({
      ...baseline,
      nativeAssets: { [currentRid()]: [{ sourcePath: "native/addon.node", targetPath: "bin/worker.cjs" }] },
    }, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(root), /collides with generated Worker entry point/u);

    await writeFile(resolve(root, "native", "node"), "renamed runtime fixture\n", "utf8");
    await writeFile(configPath, `${JSON.stringify({
      ...baseline,
      nativeAssets: { [currentRid()]: [{ sourcePath: "native/node", targetPath: "native/runtime.bin" }] },
    }, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(root), /must not include a Node or \.NET execution runtime or toolchain asset/u);

    await writeFile(resolve(root, "native", "hostpolicy.dll"), "renamed .NET runtime fixture\n", "utf8");
    await writeFile(configPath, `${JSON.stringify({
      ...baseline,
      nativeAssets: { [currentRid()]: [{ sourcePath: "native/hostpolicy.dll", targetPath: "native/runtime.bin" }] },
    }, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(root), /must not include a Node or \.NET execution runtime or toolchain asset/u);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("module builds are deterministic and RID-neutral across all six exact target records", async () => {
  const root = await createFixture();
  try {
    const results = [];
    for (const rid of SUPPORTED_RIDS) results.push(await buildModuleTarget({ projectPath: root, rid }));
    const moduleHashes = await Promise.all(results.map(async (result) => hashFile(result.modulePath)));
    assert.equal(new Set(moduleHashes).size, 1);
    for (const [index, result] of results.entries()) {
      assert.equal(
        await readFile(resolve(result.leafPath, "payload", "runtime", SUPPORTED_RIDS[index]!, "bin", "worker.cjs"), "utf8"),
        await readFile(results[0]!.modulePath, "utf8"),
      );
    }

    const project = await prepareProject(root);
    const aggregate = await aggregateAndPack(root, results.map((result) => leafSource(project, result.leafPath)));
    const entries = zipEntryNames(await readFile(aggregate.archivePath));
    assert.equal(entries.filter((entry) => entry === "payload/runtime/shared/bin/worker.cjs").length, 1);
    assert.equal(entries.some((entry) => entry.endsWith(".exe") || entry.includes("sea-prep") || entry.includes("NODE_SEA")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("aggregation rejects conflicting same-role RID uses and providers", async () => {
  for (const kind of ["use", "provider"] as const) {
    const root = await createFixture();
    try {
      const configPath = resolve(root, "sunder.package.json");
      const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
      const originalSource=packageServiceSource(`exposure:"public",providerId:"example.provider",audience:"owner"${kind==="use"?',runtimeUse:{required:true,actions:["invoke"]}':''}`);
      await writeFile(resolve(root,"src/worker.ts"),originalSource);await contractsCommand("accept",root);await contractsCommand("freeze",root);
      const firstProject = await prepareProject(root);
      const module = resolve(root, "worker.cjs");
      await writeValidModule(module);
      const firstLeaf = await writeTargetLeaf({
        project: firstProject,
        rid: SUPPORTED_RIDS[0],
        modulePath: module,
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(firstProject),
      });

      await writeFile(resolve(root,"src/worker.ts"),kind==="use"?originalSource.replace('actions:["invoke"]','actions:["discover"]'):originalSource.replace('audience:"owner"','audience:"public"'));
      const secondProject = await prepareProject(root);
      const secondLeaf = await writeTargetLeaf({
        project: secondProject,
        rid: SUPPORTED_RIDS[1],
        modulePath: module,
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(secondProject),
      });

      await assert.rejects(
        () => aggregateAndPack(root, [leafSource(secondProject, firstLeaf), leafSource(secondProject, secondLeaf)]),
        /metadata differs from the complete fresh source/u,
      );
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  }
});

test("aggregation rejects same-role RID declaration omissions", async () => {
  for (const kind of ["use", "provider"] as const) {
    const root = await createFixture();
    try {
      const configPath = resolve(root, "sunder.package.json");
      const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
      if(kind==="use")await writeFile(resolve(root,"src/worker.ts"),packageServiceSource('exposure:"package",providerId:"example.provider",runtimeUse:{required:true,actions:["invoke"]}'));
      const firstProject = await prepareProject(root);
      const module = resolve(root, "worker.cjs");
      await writeValidModule(module);
      const firstLeaf = await writeTargetLeaf({
        project: firstProject,
        rid: SUPPORTED_RIDS[0],
        modulePath: module,
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(firstProject),
      });

      const secondProject = await prepareProject(root);
      const secondLeaf = await writeTargetLeaf({
        project: secondProject,
        rid: SUPPORTED_RIDS[1],
        modulePath: module,
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(secondProject),
      });

      await rewriteLeafManifest(secondLeaf,manifest=>{if(kind==="use")(manifest as unknown as {usesContracts:unknown[]}).usesContracts=[];else (manifest as unknown as {provides:unknown[]}).provides=[];});
      await assert.rejects(
        () => aggregateAndPack(root, [leafSource(firstProject, firstLeaf), leafSource(firstProject, secondLeaf)]),
        /metadata differs from the complete fresh source/u,
      );
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  }
});

test("aggregation rejects a role-scoped RPC declaration missing from all target leaves", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });

    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    await writeFile(resolve(root,"src/worker.ts"),packageServiceSource('exposure:"package",providerId:"example.provider",runtimeUse:{required:true,actions:["invoke"]}'));

    await assert.rejects(
      () => aggregateAndPack(root, [leafSource(project, leaf)]),
      /metadata differs from the complete fresh source/u,
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("recursive target discovery holds its configured root across a nested atomic-swap gap", async () => {
  const root = await createFixture();
  let releaseGap: (() => void) | undefined;
  let gapTask: Promise<void> | undefined;
  let packageTask: Promise<Awaited<ReturnType<typeof aggregateAndPack>>> | undefined;
  try {
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const discoveryRoot = project.outputRoot;
    const leaves: string[] = [];
    for (const rid of SUPPORTED_RIDS.slice(0, 2)) {
      leaves.push(await writeTargetLeaf({ project, rid, modulePath: module, workerNoticePath: workerNoticePath(root), discoveryRoot }));
    }
    const targetsRoot = resolve(root, "dist", "targets");
    const hiddenLeaf = leaves[0]!;
    const backupLeaf = `${hiddenLeaf}.manual-backup-${randomUUID()}`;
    let signalGap!: () => void;
    const gapReady = new Promise<void>((resolvePromise) => { signalGap = resolvePromise; });
    const gapRelease = new Promise<void>((resolvePromise) => { releaseGap = resolvePromise; });
    gapTask = withOutputLocks([targetLeafDiscoveryKey(discoveryRoot)], async () => {
      await rename(hiddenLeaf, backupLeaf);
      signalGap();
      await gapRelease;
      await rename(backupLeaf, hiddenLeaf);
    });
    await gapReady;

    const packageContender = startOutputLockContender(
      targetLeafDiscoveryKey(discoveryRoot),
      () => aggregateAndPack(root, [{ path: targetsRoot, discoveryRoot }]),
      () => "Aggregation did not attempt the target-leaf discovery lock.",
    );
    packageTask = packageContender.completion;
    await packageContender.attempting;
    await assertPendingAfterTurn(packageTask, () => "Aggregation did not wait for the target-leaf swap gap.");
    if (releaseGap === undefined) throw new Error("Discovery-gap release was not initialized.");
    releaseGap();
    releaseGap = undefined;
    await gapTask;
    gapTask = undefined;
    const result = await packageTask;
    packageTask = undefined;
    const manifest = JSON.parse(await readFile(resolve(result.packageRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(manifest.targets.length, 2);
  } finally {
    releaseGap?.();
    await gapTask?.catch(() => undefined);
    await packageTask?.catch(() => undefined);
    await rm(root, { recursive: true, force: true });
  }
});

test("streaming package writes enforce the configured compressed artifact limit", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const module = resolve(root, "worker.cjs");
    await writeValidModule(module);
    const leaf = await writeTargetLeaf({ project, rid: currentRid(), modulePath: module, workerNoticePath: workerNoticePath(root), discoveryRoot: targetDiscoveryRoot(project) });
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.maximumPackageBytes = 536870913;
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(root), /must not exceed 536870912/u);
    config.maximumPackageBytes = 256;
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    await assert.rejects(() => aggregateAndPack(root, [leafSource(project, leaf)]), /maximumPackageBytes 256/u);
    assert.equal(await exists(resolve(root, "dist", "test.node.package.1.0.0.sunderpkg")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("generated directories and archives preserve prior valid outputs across failures and serialize concurrent writers", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const firstModule = resolve(root, "worker-first.cjs");
    const secondModule = resolve(root, "worker-second.cjs");
    await writeFile(firstModule, "first-module", "utf8");
    await writeFile(secondModule, "second-module", "utf8");
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: firstModule,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const outputModule = resolve(leaf, "payload", "runtime", currentRid(), "bin", "worker.cjs");
    const originalManifest = await readFile(resolve(leaf, "manifest", "sunder-package.json"));
    await assert.rejects(
      writeTargetLeaf({
        project,
        rid: currentRid(),
        modulePath: resolve(root, "missing-module"),
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(project),
      }),
      /ENOENT/u,
    );
    assert.equal(await readFile(outputModule, "utf8"), "first-module");
    assert.deepEqual(await readFile(resolve(leaf, "manifest", "sunder-package.json")), originalManifest);

    await Promise.all([
      writeTargetLeaf({ project, rid: currentRid(), modulePath: firstModule, workerNoticePath: workerNoticePath(root), discoveryRoot: targetDiscoveryRoot(project) }),
      writeTargetLeaf({ project, rid: currentRid(), modulePath: secondModule, workerNoticePath: workerNoticePath(root), discoveryRoot: targetDiscoveryRoot(project) }),
    ]);
    assert.ok(["first-module", "second-module"].includes(await readFile(outputModule, "utf8")));
    const targetEntries = await readdir(resolve(root, "dist", "targets"));
    assert.equal(targetEntries.some((name) => /\.(?:stage|backup)-/u.test(name)), false);

    // Staging races deliberately used distinct arbitrary bytes. Release admission
    // requires the source-built Worker; establish that valid preimage first.
    await writeValidModule(firstModule);
    await writeTargetLeaf({project,rid:currentRid(),modulePath:firstModule,workerNoticePath:workerNoticePath(root),discoveryRoot:targetDiscoveryRoot(project)});
    const firstPackage = await aggregateAndPack(root, [leafSource(project, leaf)]);
    const archiveHash = await hashFile(firstPackage.archivePath);
    const packageManifest = await readFile(resolve(firstPackage.packageRoot, "manifest", "sunder-package.json"));
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.maximumPackageBytes = 256;
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    await assert.rejects(() => aggregateAndPack(root, [leafSource(project, leaf)]), /maximumPackageBytes 256/u);
    assert.equal(await hashFile(firstPackage.archivePath), archiveHash);
    assert.deepEqual(await readFile(resolve(firstPackage.packageRoot, "manifest", "sunder-package.json")), packageManifest);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("generated output rejects an unexpected marker directory without deleting it", async () => {
  const root = await createFixture();
  try {
    const markerDirectory = resolve(root, "dist", ".sunder-dev-work.sunder-generated-output");
    const sentinel = resolve(markerDirectory, "sentinel.txt");
    await mkdir(markerDirectory, { recursive: true });
    await writeFile(sentinel, "preserve\n", "utf8");

    await assert.rejects(
      () => buildDevPackage({ projectPath: root, watch: false }),
      /generated output marker.*must be a regular file/iu,
    );
    assert.equal(await readFile(sentinel, "utf8"), "preserve\n");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("separate CLI processes contend safely and recover a killed owner during an atomic swap", async () => {
  const root = await createFixture();
  const helper = resolve(__dirname, "output-child.js");
  const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
  const devRoot = resolve(root, "dist", "sunder-dev");
  let child: ChildProcessWithoutNullStreams | undefined;
  try {
    await addWebApp(root);
    const ready = resolve(root, "hold.ready");
    const release = resolve(root, "hold.release");
    child = spawn(process.execPath, [helper, "hold", devRoot, ready, release], { stdio: ["pipe", "pipe", "pipe"] });
    const holder = child;
    await waitUntil(() => exists(ready), "child output lock", () => "holder did not acquire the lock");

    const firstCli = spawn(process.execPath, [cli, "dev", "--project", root], { stdio: ["pipe", "pipe", "pipe"] });
    let firstOutput = "";
    firstCli.stdout.on("data", (chunk: Buffer) => { firstOutput += chunk.toString("utf8"); });
    firstCli.stderr.on("data", (chunk: Buffer) => { firstOutput += chunk.toString("utf8"); });
    await waitForProcessOutputLockAttempt(firstCli.pid, () => firstOutput);
    assert.equal(firstCli.exitCode, null);
    assert.equal(await exists(devRoot), false);
    await writeFile(release, "release\n", "utf8");
    assert.equal(await waitForExit(holder), 0);
    child = undefined;
    assert.equal(await waitForExit(firstCli), 0, firstOutput);

    const concurrent = [0, 1].map(() => {
      const processChild = spawn(process.execPath, [cli, "dev", "--project", root], { stdio: ["pipe", "pipe", "pipe"] });
      let output = "";
      processChild.stdout.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
      processChild.stderr.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
      return { processChild, diagnostics: () => output };
    });
    for (const processRun of concurrent) {
      assert.equal(await waitForExit(processRun.processChild), 0, processRun.diagnostics());
    }

    const project = await prepareProject(root);
    const module = resolve(root, "contention-worker.cjs");
    await writeValidModule(module);
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      modulePath: module,
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const packageRuns = [0, 1].map(() => {
      const processChild = spawn(
        process.execPath,
        [cli, "package", "--project", root, "--leaf", leaf, "--discovery-root", targetDiscoveryRoot(project)],
        { stdio: ["pipe", "pipe", "pipe"] },
      );
      let output = "";
      processChild.stdout.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
      processChild.stderr.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
      return { processChild, diagnostics: () => output };
    });
    for (const processRun of packageRuns) {
      assert.equal(await waitForExit(processRun.processChild), 0, processRun.diagnostics());
    }
    assert.ok(zipEntryNames(await readFile(resolve(root, "dist", "test.node.package.1.0.0.sunderpkg"))).includes("manifest/sunder-package.json"));

    const priorManifest = await readFile(resolve(devRoot, "manifest", "sunder-package.json"));
    const crashReady = resolve(root, "crash.ready");
    child = spawn(process.execPath, [helper, "crash-swap", devRoot, crashReady], { stdio: ["pipe", "pipe", "pipe"] });
    const crashing = child;
    await waitUntil(() => exists(crashReady), "crashing output transaction", () => "child did not reach the backed-up phase");
    assert.equal(await exists(devRoot), false);
    assert.equal(crashing.kill("SIGKILL"), true);
    await waitForExit(crashing);
    child = undefined;

    const recovery = spawn(process.execPath, [helper, "recover", devRoot], { stdio: ["pipe", "pipe", "pipe"] });
    let recoveryOutput = "";
    recovery.stdout.on("data", (chunk: Buffer) => { recoveryOutput += chunk.toString("utf8"); });
    recovery.stderr.on("data", (chunk: Buffer) => { recoveryOutput += chunk.toString("utf8"); });
    assert.equal(await waitForExit(recovery), 0, recoveryOutput);
    assert.deepEqual(await readFile(resolve(devRoot, "manifest", "sunder-package.json")), priorManifest);
    assert.equal(await exists(resolve(devRoot, "replacement.txt")), false);
    const outputEntries = await readdir(resolve(root, "dist"));
    assert.equal(outputEntries.some((name) => name.includes(".stage-") || name.includes(".backup-") || name.endsWith(".sunder-output-transaction.json")), false);
  } finally {
    child?.kill("SIGKILL");
    await rm(root, { recursive: true, force: true });
  }
});

test("Node recovery rolls back an interrupted C# aggregate transaction", async () => {
  const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
  const outputPath = resolve(root, "sunder-dev");
  const markerPath = `${outputPath}.sunder-aggregate-generated-output`;
  const importsPath = `${outputPath}.imports.v1.json`;
  const token = randomUUID();
  const stagedOutputPath = `${outputPath}.stage-${randomUUID().replaceAll("-", "")}`;
  const stagedMarkerPath = `${markerPath}.stage-${randomUUID().replaceAll("-", "")}`;
  const stagedImportsPath = `${importsPath}.stage-${randomUUID().replaceAll("-", "")}`;
  const outputBackupPath = `${outputPath}.backup-${token}`;
  const markerBackupPath = `${markerPath}.backup-${token}`;
  const importsBackupPath = `${importsPath}.backup-${token}`;
  const journalPath = `${outputPath}.sunder-output-transaction.json`;
  try {
    await mkdir(outputPath, { recursive: true });
    await writeFile(resolve(outputPath, "generation.txt"), "old\n", "utf8");
    await writeFile(markerPath, "old marker\n", "utf8");
    await writeFile(importsPath, "old local selection\n", "utf8");
    await mkdir(stagedOutputPath);
    await writeFile(resolve(stagedOutputPath, "generation.txt"), "new\n", "utf8");
    await writeFile(stagedMarkerPath, "new marker\n", "utf8");
    await writeFile(stagedImportsPath, "new local selection\n", "utf8");
    await writeFile(journalPath, `${JSON.stringify({
      schemaVersion: 1,
      token,
      coordinatorPath: outputPath,
      outputs: [
        { finalPath: outputPath, stagedPath: stagedOutputPath, backupPath: outputBackupPath, existed: true },
        { finalPath: markerPath, stagedPath: stagedMarkerPath, backupPath: markerBackupPath, existed: true },
        { finalPath: importsPath, stagedPath: stagedImportsPath, backupPath: importsBackupPath, existed: true },
      ],
    }, null, 2)}\n`, "utf8");
    await rename(outputPath, outputBackupPath);
    await rename(markerPath, markerBackupPath);
    await rename(importsPath, importsBackupPath);
    await rename(stagedOutputPath, outputPath);
    await rename(stagedImportsPath, importsPath);

    await withOutputLocks([outputPath], async () => undefined);

    assert.equal(await readFile(resolve(outputPath, "generation.txt"), "utf8"), "old\n");
    assert.equal(await readFile(markerPath, "utf8"), "old marker\n");
    assert.equal(await readFile(importsPath, "utf8"), "old local selection\n");
    assert.equal(await exists(journalPath), false);
    assert.equal(await exists(stagedMarkerPath), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("generated output documents match the cross-language property corpus", async () => {
  const corpusPath = resolve(__dirname, "../../../../../../../specs/generated-output/v1/conformance/documents.json");
  const corpus = JSON.parse(await readFile(corpusPath, "utf8")) as OutputDocumentCorpus;
  assert.equal(corpus.formatVersion, 1);
  const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
  const outputPath = resolve(root, "shared-output");
  try {
    await mkdir(root, { recursive: true });
    await withOutputLocks([outputPath], async () => {
      const owner = JSON.parse(await readFile(`${outputPath}.sunder-output.lock/owner.json`, "utf8")) as Record<string, unknown>;
      assertDocumentProperties(owner, corpus.documents.owner);
      const marker = JSON.parse(await readFile(String(owner.processMarkerPath), "utf8")) as Record<string, unknown>;
      assertDocumentProperties(marker, corpus.documents.processMarker);
    });

    const token = randomUUID();
    const stagedPath = `${outputPath}.stage-${randomUUID().replaceAll("-", "")}`;
    const backupPath = `${outputPath}.backup-${token}`;
    const journalPath = `${outputPath}.sunder-output-transaction.json`;
    await mkdir(outputPath);
    await writeFile(resolve(outputPath, "generation.txt"), "old", "utf8");
    await mkdir(stagedPath);
    await writeFile(resolve(stagedPath, "generation.txt"), "new", "utf8");
    const transaction = {
      schemaVersion: 1,
      token,
      coordinatorPath: outputPath,
      outputs: [{ finalPath: outputPath, stagedPath, backupPath, existed: true }],
    };
    assertDocumentProperties(transaction, corpus.documents.transaction);
    assertDocumentProperties(transaction.outputs[0]!, corpus.documents.transactionOutput);
    await writeFile(journalPath, `${JSON.stringify(transaction)}\n`, "utf8");
    await rename(outputPath, backupPath);
    await rename(stagedPath, outputPath);

    await withOutputLocks([outputPath], async () => undefined);

    assert.equal(await readFile(resolve(outputPath, "generation.txt"), "utf8"), "old");
    assert.equal(await exists(journalPath), false);
    assert.throws(
      () => assertDocumentProperties({ ...transaction, seeded: true }, corpus.documents.transaction),
      /property set/u,
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("same-host PID reuse is identified by process start time", async () => {
  const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
  const outputPath = resolve(root, "shared-output");
  const lockPath = `${outputPath}.sunder-output.lock`;
  let processMarkerPath: string | undefined;
  try {
    processMarkerPath = await writeLockOwner(lockPath, {
      pid: process.pid,
      ownerHostname: hostname(),
      processStartedAt: "2000-01-01T00:00:00.000Z",
    });
    let acquired = false;
    await withOutputLocks([outputPath], async () => { acquired = true; }, 2_000);
    assert.equal(acquired, true);
    assert.equal(await exists(lockPath), false);
  } finally {
    await rm(root, { recursive: true, force: true });
    if (processMarkerPath !== undefined) await rm(processMarkerPath, { force: true });
  }
});

for (const evidence of ["missing", "temp-root"] as const) test(`same-host owners with ${evidence} process evidence time out without reclamation`, async () => {
  const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
  const outputPath = resolve(root, "shared-output");
  const lockPath = `${outputPath}.sunder-output.lock`;
  let processMarkerPath: string | undefined;
  try {
    processMarkerPath = await writeLockOwner(lockPath, {
      pid: 2_147_483_647,
      ownerHostname: hostname(),
      processStartedAt: "2000-01-01T00:00:00.000Z",
    });
    if (evidence === "missing") await rm(processMarkerPath, { force: true });
    else {
      const markerRoot = resolve(tmpdir(), "sunder-generated-output", "processes");
      await mkdir(markerRoot, { recursive: true });
      const ownerPath = resolve(lockPath, "owner.json");
      const owner = JSON.parse(await readFile(ownerPath, "utf8")) as Record<string, unknown>;
      const moved = resolve(markerRoot, `${String(owner.processMarkerToken)}.json`);
      await rename(processMarkerPath, moved);
      processMarkerPath = moved;
      owner.processMarkerPath = moved;
      // Keep the token/path relationship valid: the root, not malformed evidence,
      // must be the reason this obsolete marker is not recovery authority.
      await writeFile(ownerPath, JSON.stringify(owner), "utf8");
    }
    const before = await readFile(resolve(lockPath, "owner.json"));
    await assert.rejects(() => withOutputLocks([outputPath], async () => undefined, 250), /timed out after 250 ms/iu);
    assert.equal(await exists(lockPath), true);
    assert.deepEqual(await readFile(resolve(lockPath, "owner.json")), before);
  } finally {
    await rm(root, { recursive: true, force: true });
    if (processMarkerPath !== undefined) await rm(processMarkerPath, { force: true });
  }
});

test("Linux boot and PID namespace mismatches time out without reclamation", { skip: process.platform !== "linux" }, async () => {
  for (const mismatch of ["boot", "namespace"] as const) {
    const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
    const outputPath = resolve(root, "shared-output");
    const lockPath = `${outputPath}.sunder-output.lock`;
    let processMarkerPath: string | undefined;
    try {
      processMarkerPath = await writeLockOwner(lockPath, {
        pid: 2_147_483_647,
        ownerHostname: hostname(),
        processStartedAt: "2000-01-01T00:00:00.000Z",
        ...(mismatch === "boot" ? { linuxBootId: randomUUID() } : { linuxPidNamespace: "pid:[0]" }),
      });
      await assert.rejects(() => withOutputLocks([outputPath], async () => undefined, 250), /timed out after 250 ms/iu);
      assert.equal(await exists(lockPath), true);
    } finally {
      await rm(root, { recursive: true, force: true });
      if (processMarkerPath !== undefined) await rm(processMarkerPath, { force: true });
    }
  }
});

test("unknown-host stable owners and transition guards time out without reclamation", async () => {
  const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
  const outputPath = resolve(root, "shared-output");
  const lockPath = `${outputPath}.sunder-output.lock`;
  const remoteHostname = `${hostname()}.unknown-host`;
  const processMarkerPaths: string[] = [];
  try {
    processMarkerPaths.push(await writeLockOwner(lockPath, {
      pid: process.pid,
      ownerHostname: remoteHostname,
      processStartedAt: "2000-01-01T00:00:00.000Z",
    }));
    const oldTime = new Date(Date.now() - 24 * 60 * 60_000);
    await utimes(resolve(lockPath, "owner.json"), oldTime, oldTime);
    await assert.rejects(() => withOutputLocks([outputPath], async () => undefined, 250), /timed out after 250 ms/iu);
    assert.equal(await exists(lockPath), true);

    await rm(lockPath, { recursive: true });
    const guardPath = `${lockPath}.transition`;
    processMarkerPaths.push(await writeLockOwner(guardPath, {
      pid: process.pid,
      ownerHostname: remoteHostname,
      processStartedAt: "2000-01-01T00:00:00.000Z",
    }));
    await utimes(resolve(guardPath, "owner.json"), oldTime, oldTime);
    await assert.rejects(() => withOutputLocks([outputPath], async () => undefined, 250), /timed out after 250 ms/iu);
    assert.equal(await exists(guardPath), true);
  } finally {
    await rm(root, { recursive: true, force: true });
    await Promise.all(processMarkerPaths.map((path) => rm(path, { force: true })));
  }
});

test("live suspended lock owners are retained and two dead-owner reclaimers serialize", async () => {
  const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
  const outputPath = resolve(root, "shared-output");
  const lockPath = `${outputPath}.sunder-output.lock`;
  const helper = resolve(__dirname, "output-child.js");
  const children: ChildProcessWithoutNullStreams[] = [];
  const crashedProcessMarkers: string[] = [];
  try {
    await mkdir(root, { recursive: true });
    const ownerReady = resolve(root, "owner.ready");
    const ownerRelease = resolve(root, "owner.release");
    const owner = spawn(process.execPath, [helper, "hold", outputPath, ownerReady, ownerRelease], { stdio: ["pipe", "pipe", "pipe"] });
    children.push(owner);
    await waitUntil(() => exists(ownerReady), "live lock owner", () => "owner did not acquire the lock");
    const liveOwner = JSON.parse(await readFile(resolve(lockPath, "owner.json"), "utf8")) as { readonly processMarkerPath: string };
    if (process.platform !== "win32") assert.equal(owner.kill("SIGSTOP"), true);
    const oldHeartbeat = new Date(Date.now() - 10 * 60_000);
    await utimes(resolve(lockPath, "owner.json"), oldHeartbeat, oldHeartbeat);

    const waiterReady = resolve(root, "waiter.ready");
    const waiterRelease = resolve(root, "waiter.release");
    const waiterAttempting = resolve(root, "waiter.attempting");
    const waiter = spawn(process.execPath, [helper, "hold", outputPath, waiterReady, waiterRelease, waiterAttempting], { stdio: ["pipe", "pipe", "pipe"] });
    children.push(waiter);
    await waitUntil(() => exists(waiterAttempting), "waiter output-lock attempt", () => "waiter did not announce lock acquisition");
    assert.equal(await exists(waiterReady), false, "A stale heartbeat must not override a live same-host PID.");
    if (process.platform !== "win32") assert.equal(owner.kill("SIGCONT"), true);
    await writeFile(ownerRelease, "release\n", "utf8");
    assert.equal(await waitForExit(owner), 0);
    await waitUntil(async () => !await exists(liveOwner.processMarkerPath), "normal process marker cleanup", () => "released owner marker was retained");
    await waitUntil(() => exists(waiterReady), "waiter after live owner release", () => "waiter did not acquire the released lock");
    await writeFile(waiterRelease, "release\n", "utf8");
    assert.equal(await waitForExit(waiter), 0);

    const deadReady = resolve(root, "dead.ready");
    const deadRelease = resolve(root, "dead.release");
    const deadOwner = spawn(process.execPath, [helper, "hold", outputPath, deadReady, deadRelease], { stdio: ["pipe", "pipe", "pipe"] });
    children.push(deadOwner);
    await waitUntil(() => exists(deadReady), "owner to kill", () => "dead-owner fixture did not acquire the lock");
    const deadOwnerMetadata = JSON.parse(await readFile(resolve(lockPath, "owner.json"), "utf8")) as { readonly processMarkerPath: string };
    crashedProcessMarkers.push(deadOwnerMetadata.processMarkerPath);
    assert.equal(deadOwner.kill("SIGKILL"), true);
    await waitForExit(deadOwner);
    assert.equal(await exists(deadOwnerMetadata.processMarkerPath), true, "A crashed owner must retain its locality proof.");
    crashedProcessMarkers.push(await writeLockOwner(`${lockPath}.transition`, {
      pid: 2_147_483_647,
      ownerHostname: hostname(),
      processStartedAt: "2000-01-01T00:00:00.000Z",
    }));

    const reclaimerRuns = ["first", "second"].map((name) => {
      const ready = resolve(root, `${name}.ready`);
      const release = resolve(root, `${name}.release`);
      const attempting = resolve(root, `${name}.attempting`);
      const child = spawn(process.execPath, [helper, "hold", outputPath, ready, release, attempting], { stdio: ["pipe", "pipe", "pipe"] });
      children.push(child);
      return { child, ready, release, attempting };
    });
    await Promise.all(reclaimerRuns.map((run) => waitUntil(
      () => exists(run.attempting),
      "stale-lock reclaimer attempt",
      () => `pid=${run.child.pid ?? "missing"}`,
    )));
    await waitUntil(
      async () => await exists(reclaimerRuns[0]!.ready) || await exists(reclaimerRuns[1]!.ready),
      "first stale-lock reclaimer",
      () => "neither reclaimer acquired the stale lock",
    );
    const acquired = await Promise.all(reclaimerRuns.map((run) => exists(run.ready)));
    const firstIndex = acquired[0] ? 0 : 1;
    const secondIndex = firstIndex === 0 ? 1 : 0;
    const secondReady = waitUntil(
      () => exists(reclaimerRuns[secondIndex]!.ready),
      "second reclaimer after serialized release",
      () => "second reclaimer did not acquire after the first released",
    );
    await assertPendingAfterTurn(secondReady, () => "Both stale-lock reclaimers acquired before the first release.");
    assert.equal(acquired.filter(Boolean).length, 1, "Exactly one stale-lock reclaimer may own the main lock.");
    await writeFile(reclaimerRuns[firstIndex]!.release, "release\n", "utf8");
    assert.equal(await waitForExit(reclaimerRuns[firstIndex]!.child), 0);
    await secondReady;
    await writeFile(reclaimerRuns[secondIndex]!.release, "release\n", "utf8");
    assert.equal(await waitForExit(reclaimerRuns[secondIndex]!.child), 0);
    assert.equal(await exists(lockPath), false);
    assert.equal(await exists(`${lockPath}.transition`), false);
    assert.equal(
      (await readdir(root)).filter((name) => name.startsWith("shared-output.sunder-output.lock.transition.stale-")).length,
      1,
    );
  } finally {
    for (const child of children) {
      if (child.exitCode === null) {
        if (process.platform !== "win32") child.kill("SIGCONT");
        child.kill("SIGKILL");
      }
    }
    await rm(root, { recursive: true, force: true });
    await Promise.all(crashedProcessMarkers.map((path) => rm(path, { force: true })));
  }
});

test("watch recreates immutable build context and App watcher after structural config changes", async () => {
  const root = await createFixture();
  let child: ChildProcessWithoutNullStreams | undefined;
  let output = "";
  try {
    await addWebApp(root);
    const cli = resolve(__dirname, "..", "..", "dist", "cli.js");
    const watchProcess = spawn(process.execPath, [cli, "dev", "--watch", "--project", root], { stdio: ["pipe", "pipe", "pipe"] });
    child = watchProcess;
    watchProcess.stdout.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
    watchProcess.stderr.on("data", (chunk: Buffer) => { output += chunk.toString("utf8"); });
    const devRoot = resolve(root, "dist", "sunder-dev");
    const bundlePath = resolve(devRoot, "payload", "runtime", "shared", "bin", "worker.cjs");
    await waitUntil(async () => await exists(bundlePath), "initial watch output", () => output);

    await mkdir(resolve(root, "app-next"), { recursive: true });
    await writeFile(resolve(root, "app-next", "index.html"), "<!doctype html><html><body>APP_WATCH_V2</body></html>\n", "utf8");
    await writeFile(resolve(root, "contracts", "renamed.rpc.json"), await readFile(resolve(root, "contracts", "example.rpc.json")));
    await writeFile(resolve(root, "src", "worker-next.ts"), packageServiceSource().replace('const example=', 'const renamed=').replace('return {accepted:true};','if(request.message==="STRUCTURAL_WATCH_V2")return {accepted:false};return {accepted:true};'), "utf8");
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.services = {module:"src/worker-next.ts"};
    config.app = {
      root: "app-next",
      entryPoint: "index.html",
      views: [{
        viewId: "test.node.package.main",
        displayName: "Test Web",
        route: "/",
        defaultPlacement: "middle",
        showInHotbar: true,
      }],
    };
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    const appOutput = resolve(devRoot, "payload", "app", currentRid(), "index.html");
    await waitUntil(async () => (await readTextIfPresent(bundlePath)).includes("STRUCTURAL_WATCH_V2")
      && (await readTextIfPresent(appOutput)).includes("APP_WATCH_V2"), "structural watch rebuild", () => output);
    const index = JSON.parse(await readFile(resolve(devRoot, "manifest", "content-index.json"), "utf8")) as { readonly files: readonly { readonly path: string }[] };
    assert.ok(index.files.some((item) => item.path.endsWith("contracts/renamed.rpc.json")));
    assert.equal(index.files.some((item) => item.path.endsWith("contracts/example.rpc.json")), false);

    await writeFile(resolve(root, "app-next", "index.html"), "<!doctype html><html><body>APP_WATCH_V3</body></html>\n", "utf8");
    await waitUntil(async () => (await readTextIfPresent(appOutput)).includes("APP_WATCH_V3"), "recreated App watcher", () => output);
    watchProcess.kill("SIGINT");
    assert.equal(await waitForExit(watchProcess), 0, output);
    child = undefined;
  } finally {
    child?.kill("SIGKILL");
    await rm(root, { recursive: true, force: true });
  }
});

async function configureNativeAsset(
  root: string,
  rid: (typeof SUPPORTED_RIDS)[number],
  sourcePath: string,
): Promise<void> {
  const configPath = resolve(root, "sunder.package.json");
  const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
  config.nativeAssets = { [rid]: [{ sourcePath, targetPath: "native/sqlite.node" }] };
  await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
}

async function configureNativeAssetMatrix(
  root: string,
  createBytes: (rid: (typeof SUPPORTED_RIDS)[number]) => Buffer,
): Promise<void> {
  const configPath = resolve(root, "sunder.package.json");
  const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
  config.nativeAssets = Object.fromEntries(await Promise.all(SUPPORTED_RIDS.map(async (rid) => {
    const sourcePath = `native/${rid}/sqlite.node`;
    await mkdir(resolve(root, "native", rid), { recursive: true });
    await writeFile(resolve(root, ...sourcePath.split("/")), createBytes(rid));
    return [rid, [{ sourcePath, targetPath: "native/sqlite.node" }]];
  })));
  await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
}

function nativeBinaryFixture(
  rid: (typeof SUPPORTED_RIDS)[number],
  kind: "shared-library" | "executable",
): Buffer {
  const architecture = rid.endsWith("-x64") ? "x64" : "arm64";
  if (rid.startsWith("win-")) return peBinaryFixture(architecture, kind);
  if (rid.startsWith("linux-")) return elfBinaryFixture(architecture, kind);
  if (kind === "shared-library") return fatMachSharedLibraryFixture();
  return machBinaryFixture(architecture, kind);
}

function peBinaryFixture(
  architecture: "x64" | "arm64",
  kind: "shared-library" | "executable" | "managed-assembly",
): Buffer {
  const peOffset = 0x80;
  const optionalHeaderBytes = 0xf0;
  const bytes = Buffer.alloc(peOffset + 24 + optionalHeaderBytes + 40);
  bytes[0] = 0x4d;
  bytes[1] = 0x5a;
  bytes.writeUInt32LE(peOffset, 0x3c);
  bytes.write("PE\0\0", peOffset, "binary");
  bytes.writeUInt16LE(architecture === "x64" ? 0x8664 : 0xaa64, peOffset + 4);
  bytes.writeUInt16LE(1, peOffset + 6);
  bytes.writeUInt16LE(optionalHeaderBytes, peOffset + 20);
  bytes.writeUInt16LE(kind === "executable" ? 0x0022 : 0x2022, peOffset + 22);
  const optionalOffset = peOffset + 24;
  bytes.writeUInt16LE(0x20b, optionalOffset);
  bytes.writeUInt32LE(kind === "executable" ? 0x1000 : 0, optionalOffset + 16);
  bytes.writeUInt32LE(16, optionalOffset + 108);
  if (kind === "managed-assembly") {
    bytes.writeUInt32LE(0x2000, optionalOffset + 112 + 14 * 8);
    bytes.writeUInt32LE(0x48, optionalOffset + 112 + 14 * 8 + 4);
  }
  return bytes;
}

function elfBinaryFixture(
  architecture: "x64" | "arm64",
  kind: "shared-library" | "executable" | "toolchain",
): Buffer {
  const bytes = Buffer.alloc(kind === "toolchain" ? 64 : 120);
  bytes.set([0x7f, 0x45, 0x4c, 0x46, 2, 1, 1], 0);
  bytes.writeUInt16LE(kind === "toolchain" ? 1 : 3, 16);
  bytes.writeUInt16LE(architecture === "x64" ? 62 : 183, 18);
  bytes.writeUInt32LE(1, 20);
  bytes.writeBigUInt64LE(BigInt(kind === "executable" ? 0x1000 : 0), 24);
  bytes.writeBigUInt64LE(64n, 32);
  bytes.writeUInt16LE(64, 52);
  bytes.writeUInt16LE(56, 54);
  bytes.writeUInt16LE(kind === "toolchain" ? 0 : 1, 56);
  if (kind !== "toolchain") bytes.writeUInt32LE(kind === "executable" ? 3 : 1, 64);
  return bytes;
}

function machBinaryFixture(
  architecture: "x64" | "arm64",
  kind: "shared-library" | "executable" | "mutated-executable",
): Buffer {
  const hasMainCommand = kind === "mutated-executable";
  const bytes = Buffer.alloc(hasMainCommand ? 56 : 32);
  bytes.set([0xcf, 0xfa, 0xed, 0xfe], 0);
  bytes.writeUInt32LE(architecture === "x64" ? 0x01000007 : 0x0100000c, 4);
  bytes.writeUInt32LE(kind === "executable" ? 2 : 8, 12);
  bytes.writeUInt32LE(hasMainCommand ? 1 : 0, 16);
  bytes.writeUInt32LE(hasMainCommand ? 24 : 0, 20);
  if (hasMainCommand) {
    bytes.writeUInt32LE(0x80000028, 32);
    bytes.writeUInt32LE(24, 36);
  }
  return bytes;
}

function fatMachSharedLibraryFixture(): Buffer {
  const slices = [
    { cpu: 0x01000007, bytes: machBinaryFixture("x64", "shared-library") },
    { cpu: 0x0100000c, bytes: machBinaryFixture("arm64", "shared-library") },
  ];
  const headerBytes = 8 + slices.length * 20;
  const bytes = Buffer.alloc(headerBytes + slices.reduce((total, slice) => total + slice.bytes.length, 0));
  bytes.set([0xca, 0xfe, 0xba, 0xbe], 0);
  bytes.writeUInt32BE(slices.length, 4);
  let sliceOffset = headerBytes;
  for (const [index, slice] of slices.entries()) {
    const recordOffset = 8 + index * 20;
    bytes.writeUInt32BE(slice.cpu, recordOffset);
    bytes.writeUInt32BE(sliceOffset, recordOffset + 8);
    bytes.writeUInt32BE(slice.bytes.length, recordOffset + 12);
    slice.bytes.copy(bytes, sliceOffset);
    sliceOffset += slice.bytes.length;
  }
  return bytes;
}

async function createFixture(): Promise<string> {
  const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
  await mkdir(resolve(root, "src"), { recursive: true });
  await mkdir(resolve(root, "contracts"), { recursive: true });
  await writeFile(resolve(root, "src", "worker.ts"), packageServiceSource(), "utf8");
  await writeFile(workerNoticePath(root), "Sunder Bundled Worker Dependency Notices\n\n@sunder/sdk@1.1.0\n", "utf8");
  await writeFile(resolve(root, "contracts", "example.rpc.json"), `${JSON.stringify(descriptor, null, 2)}\n`, "utf8");
  await writeFile(resolve(root, "sunder.package.json"), `${JSON.stringify({
    schemaVersion: 3,
    id: "test.node.package",
    name: "Test Node Package",
    summary: "Runtime-backed Node Worker V1 package test fixture.",
    version: "1.0.0",
    services: {module:"src/worker.ts"},
    workerCapabilities: [],
  }, null, 2)}\n`, "utf8");
  await writeFile(resolve(root,"sunder-contracts.lock.json"),JSON.stringify({lockVersion:1,packageArtifacts:{},packageImports:{},providerImports:{},platformImports:{}},null,2)+"\n");
  return root;
}

function packageServiceSource(options = 'exposure:"package",providerId:"example.provider"', maximum = 128, title?: string): string {
  return `import {defineService} from "@sunder/sdk";
type Request={message:string}; type Response={accepted:boolean};
export const example=defineService({send(request:Request):Response{return {accepted:true};},async *watch(request:Request):AsyncIterable<Response>{yield {accepted:true};}},
{contractId:"example.rpc",serviceId:"messages",${options},constraints:{"Request.message":{minLength:1,maxLength:${maximum}${title === undefined ? "" : `,title:${JSON.stringify(title)}`}}}});`;
}

async function addWebApp(root: string): Promise<void> {
  await mkdir(resolve(root, "app"), { recursive: true });
  await writeFile(resolve(root, "app", "index.html"), "<!doctype html><html><body><main>Web fixture</main></body></html>\n", "utf8");
  const path = resolve(root, "sunder.package.json");
  const config = JSON.parse(await readFile(path, "utf8")) as Record<string, unknown>;
  config.app = {
    root: "app",
    entryPoint: "index.html",
    views: [{
      viewId: "test.node.package.main",
      displayName: "Test Web",
      route: "/",
      defaultPlacement: "middle",
      showInHotbar: true,
    }],
  };
  await writeFile(path, `${JSON.stringify(config, null, 2)}\n`, "utf8");
}

async function addWorkerDependency(root: string): Promise<void> {
  const workerPath = resolve(root, "src", "worker.ts");
  const worker = await readFile(workerPath, "utf8");
  await writeFile(
    workerPath,
    `import { fixtureWorkerValue } from "fixture-worker-dependency";\n${worker.replace(
      "return {accepted:true};",
      "if(!fixtureWorkerValue)throw new Error('dependency');return {accepted:true};",
    )}`,
    "utf8",
  );
  await writeFile(resolve(root, "package.json"), `${JSON.stringify({
    private: true,
    dependencies: { "fixture-worker-dependency": "4.5.6" },
    devDependencies: { "worker-build-only-dependency": "9.9.9" },
  }, null, 2)}\n`, "utf8");
  const dependencyRoot = resolve(root, "node_modules", "fixture-worker-dependency");
  await mkdir(resolve(dependencyRoot, "legal"), { recursive: true });
  await writeFile(resolve(dependencyRoot, "package.json"), `${JSON.stringify({
    name: "fixture-worker-dependency",
    version: "4.5.6",
    license: "MIT",
    type: "module",
    exports: "./index.js",
  }, null, 2)}\n`, "utf8");
  await writeFile(resolve(dependencyRoot, "index.js"), "export const fixtureWorkerValue = 'worker fixture';\n", "utf8");
  await writeFile(resolve(dependencyRoot, "index.d.ts"), "export declare const fixtureWorkerValue: string;\n", "utf8");
  await writeFile(resolve(dependencyRoot, "LICENSE.md"), "Worker fixture license\n", "utf8");
  await writeFile(resolve(dependencyRoot, "legal", "NOTICE"), "nested worker fixture notice\n", "utf8");
}

async function addReactDependencies(root: string): Promise<void> {
  await writeFile(resolve(root, "app", "index.html"), "<!doctype html><html><body><main id=\"root\"></main><script type=\"module\" src=\"./main.ts\"></script></body></html>\n", "utf8");
  await writeFile(resolve(root, "app", "main.ts"), `
    import React from "react";
    import { createRoot } from "react-dom/client";
    import { fixtureValue } from "fixture-bundled-dependency";
    createRoot(document.getElementById("root")!).render(React.createElement("span", null, fixtureValue));
  `, "utf8");
  await writeFile(resolve(root, "package.json"), `${JSON.stringify({
    private: true,
    dependencies: {
      react: "19.2.8",
      "react-dom": "19.2.8",
      "fixture-bundled-dependency": "2.3.4",
    },
    devDependencies: {
      "build-only-dependency": "9.9.9",
    },
  }, null, 2)}\n`, "utf8");
  const modules = resolve(root, "node_modules");
  await mkdir(modules, { recursive: true });
  for (const packageName of ["react", "react-dom", "scheduler"]) {
    const source = resolve(require.resolve(`${packageName}/package.json`), "..");
    await symlink(source, resolve(modules, packageName), "junction");
  }
  const fixtureRoot = resolve(modules, "fixture-bundled-dependency");
  await mkdir(resolve(fixtureRoot, "legal"), { recursive: true });
  await writeFile(resolve(fixtureRoot, "package.json"), `${JSON.stringify({
    name: "fixture-bundled-dependency",
    version: "2.3.4",
    license: "MIT",
    type: "module",
    exports: "./index.js",
  }, null, 2)}\n`, "utf8");
  await writeFile(resolve(fixtureRoot, "index.js"), "export const fixtureValue = 'bundled fixture';\n", "utf8");
  await writeFile(resolve(fixtureRoot, "LICENSE"), "Fixture dependency license\n", "utf8");
  await writeFile(resolve(fixtureRoot, "legal", "NOTICE.txt"), "nested fixture notice\n", "utf8");
}

async function waitUntil(
  condition: () => Promise<boolean>,
  label: string,
  diagnostics: () => string,
): Promise<void> {
  const deadline = Date.now() + 15_000;
  while (!await condition()) {
    if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${label}.\n${diagnostics()}`);
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 25));
  }
}

async function delay(milliseconds: number): Promise<void> {
  await new Promise((resolvePromise) => setTimeout(resolvePromise, milliseconds));
}

function targetDiscoveryRoot(project: PreparedProject): string {
  return resolve(project.outputRoot, "targets");
}

function leafSource(project: PreparedProject, path: string): LeafDiscoverySource {
  return { path, discoveryRoot: targetDiscoveryRoot(project) };
}

async function readTextIfPresent(path: string): Promise<string> {
  try {
    return await readFile(path, "utf8");
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return "";
    throw error;
  }
}

async function writeLockOwner(
  directory: string,
  owner: {
    readonly pid: number;
    readonly ownerHostname: string;
    readonly processStartedAt: string;
    readonly linuxBootId?: string;
    readonly linuxPidNamespace?: string;
  },
): Promise<string> {
  if (process.platform !== "linux" && process.platform !== "win32" && process.platform !== "darwin") {
    throw new Error(`Unsupported lock-test platform '${process.platform}'.`);
  }
  let linuxBootId: string | null = null;
  let linuxPidNamespace: string | null = null;
  if (process.platform === "linux") {
    linuxBootId = owner.linuxBootId ?? (await readFile("/proc/sys/kernel/random/boot_id", "utf8")).trim().toLowerCase();
    linuxPidNamespace = owner.linuxPidNamespace ?? await readlink("/proc/self/ns/pid");
  }
  const processMarkerToken = randomUUID();
  const processMarkerRoot = resolve(homedir(), ".sunder", "generated-output", "processes");
  const processMarkerPath = resolve(processMarkerRoot, `${processMarkerToken}.json`);
  await mkdir(processMarkerRoot, { recursive: true, mode: 0o700 });
  await writeFile(processMarkerPath, `${JSON.stringify({
    schemaVersion: 1,
    token: processMarkerToken,
    pid: owner.pid,
    hostname: owner.ownerHostname,
    processStartedAt: owner.processStartedAt,
    platform: process.platform,
    linuxBootId,
    linuxPidNamespace,
    createdAt: new Date().toISOString(),
  }, null, 2)}\n`, { encoding: "utf8", flag: "wx", mode: 0o600 });
  await mkdir(directory, { recursive: true });
  await writeFile(resolve(directory, "owner.json"), `${JSON.stringify({
    schemaVersion: 2,
    token: randomUUID(),
    pid: owner.pid,
    hostname: owner.ownerHostname,
    processStartedAt: owner.processStartedAt,
    platform: process.platform,
    processMarkerPath,
    processMarkerToken,
    linuxBootId,
    linuxPidNamespace,
    acquiredAt: new Date().toISOString(),
  }, null, 2)}\n`, "utf8");
  return processMarkerPath;
}

async function waitForExit(child: ChildProcessWithoutNullStreams): Promise<number | null> {
  if (child.exitCode !== null) return child.exitCode;
  return await new Promise<number | null>((resolvePromise, reject) => {
    const timer = setTimeout(() => reject(new Error("Watch process did not exit.")), 120_000);
    child.once("exit", (code) => {
      clearTimeout(timer);
      resolvePromise(code);
    });
  });
}

function zipEntryNames(archive: Buffer): string[] {
  return zipEntries(archive).map((entry) => entry.name);
}

function zipEntries(archive: Buffer): ZipEntry[] {
  const endOffset = archive.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  assert.ok(endOffset >= 0, "ZIP end record is missing");
  const count = archive.readUInt16LE(endOffset + 10);
  let offset = archive.readUInt32LE(endOffset + 16);
  const output: ZipEntry[] = [];
  for (let index = 0; index < count; index++) {
    assert.equal(archive.readUInt32LE(offset), 0x02014b50);
    const method = archive.readUInt16LE(offset + 10);
    const compressedBytes = archive.readUInt32LE(offset + 20);
    const uncompressedBytes = archive.readUInt32LE(offset + 24);
    const nameLength = archive.readUInt16LE(offset + 28);
    const extraLength = archive.readUInt16LE(offset + 30);
    const commentLength = archive.readUInt16LE(offset + 32);
    const localOffset = archive.readUInt32LE(offset + 42);
    const name = archive.subarray(offset + 46, offset + 46 + nameLength).toString("utf8");
    assert.equal(archive.readUInt32LE(localOffset), 0x04034b50);
    const localNameLength = archive.readUInt16LE(localOffset + 26);
    const localExtraLength = archive.readUInt16LE(localOffset + 28);
    const dataOffset = localOffset + 30 + localNameLength + localExtraLength;
    const compressed = archive.subarray(dataOffset, dataOffset + compressedBytes);
    const data = method === 8 ? inflateRawSync(compressed) : compressed;
    assert.equal(data.byteLength, uncompressedBytes);
    output.push({ name, method, data });
    offset += 46 + nameLength + extraLength + commentLength;
  }
  return output;
}

function workerNoticePath(root: string): string {
  return resolve(root, "Bundled-Worker-Dependencies.txt");
}
async function writeValidModule(path: string): Promise<void> {
  // Artifact admission tests need authentic current source bytes now that every
  // service package is source-verified. Do not bypass that fence with dummy JS.
  await bundleProduction(await prepareProject(dirname(path)), currentRid(), path);
}

function assertSingle<T>(values: readonly T[]): T {
  assert.equal(values.length, 1);
  return values[0]!;
}

function assertDocumentProperties(document: Record<string, unknown>, specification: OutputDocumentSpecification): void {
  if (specification.schemaVersion !== undefined) assert.equal(document.schemaVersion, specification.schemaVersion);
  assert.deepEqual(
    Object.keys(document).sort(),
    [...specification.properties].sort(),
    "generated output document property set changed",
  );
}

function sha256(value: Uint8Array): string {
  return createHash("sha256").update(value).digest("hex");
}

async function hashFile(path: string): Promise<string> {
  const hash = createHash("sha256");
  for await (const chunk of createReadStream(path)) hash.update(chunk as Buffer);
  return hash.digest("hex");
}

async function rewriteLeafFileIndex(leaf: string, archivePath: string): Promise<void> {
  const fullPath = resolve(leaf, ...archivePath.split("/"));
  const indexPath = resolve(leaf, "manifest", "content-index.json");
  const index = JSON.parse(await readFile(indexPath, "utf8")) as {
    files: Array<{ path: string; sha256: string; size: number }>;
  };
  const entry = index.files.find((item) => item.path === archivePath);
  if (entry === undefined) throw new Error(`Fixture content index entry '${archivePath}' is missing.`);
  entry.sha256 = await hashFile(fullPath);
  entry.size = (await stat(fullPath)).size;
  await writeFile(indexPath, `${JSON.stringify(index, null, 2)}\n`, "utf8");
}

async function rewriteLeafManifest(leaf: string, update: (manifest: PackageManifest) => void): Promise<void> {
  const manifestPath = resolve(leaf, "manifest", "sunder-package.json");
  const manifest = JSON.parse(await readFile(manifestPath, "utf8")) as PackageManifest;
  update(manifest);
  const manifestBytes = Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`);
  await writeFile(manifestPath, manifestBytes);
  const indexPath = resolve(leaf, "manifest", "content-index.json");
  const index = JSON.parse(await readFile(indexPath, "utf8")) as {
    files: Array<{ path: string; sha256: string; size: number }>;
  };
  const manifestEntry = index.files.find((entry) => entry.path === "manifest/sunder-package.json");
  if (manifestEntry === undefined) throw new Error("Fixture manifest index entry is missing.");
  manifestEntry.sha256 = sha256(manifestBytes);
  manifestEntry.size = manifestBytes.byteLength;
  await writeFile(indexPath, `${JSON.stringify(index, null, 2)}\n`, "utf8");
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&");
}

async function exists(path: string): Promise<boolean> {
  try {
    await stat(path);
    return true;
  } catch {
    return false;
  }
}

const descriptor = {
  descriptorVersion: 1,
  schemaProfile: "sunder.rpc.values.v1",
  contractId: "example.rpc",
  version: "1.0.0",
  errors: {},
  services: {
    messages: {
      methods: {
        send: {
          kind: "unary",
          idempotency: "none",
          errors: [],
          requestSchema: { $ref: "#/$defs/Request" },
          responseSchema: { $ref: "#/$defs/Response" },
        },
        watch: {
          kind: "server-stream",
          idempotency: "none",
          errors: [],
          requestSchema: { $ref: "#/$defs/Request" },
          eventSchema: { $ref: "#/$defs/Response" },
        },
      },
    },
  },
  $defs: {
    Request: { type: "object", properties: { message: { type: "string", minLength: 1, maxLength: 128 } }, required: ["message"], additionalProperties: false },
    Response: { type: "object", properties: { accepted: { type: "boolean" } }, required: ["accepted"], additionalProperties: false },
  },
};

interface PackageManifest {
  readonly archiveFormatVersion: number;
  readonly manifestVersion: number;
  readonly id: string;
  readonly dependsOn: readonly { readonly packageId: string; readonly versionRange: string; readonly presence: string }[];
  readonly targets: ReadonlyArray<{
    readonly role: string;
    readonly kind: string;
    readonly rid: string;
    readonly entryPoint: string;
    readonly sdkVersion: string;
    readonly requiredHostCapabilities: readonly string[];
    readonly executionRuntime?: {
      readonly id: string;
      readonly versionRange: string;
      readonly moduleAbi: string;
    };
    readonly views?: readonly { readonly viewId: string }[];
  }>;
  readonly contractBundles: readonly {
    readonly contractId: string;
    readonly version: string;
    readonly descriptorPath: string;
    readonly descriptorSha256: string;
    readonly abiSha256: string;
    readonly exposure: string;
    readonly source: string;
    readonly authorityKind: string;
    readonly authorityId: string;
  }[];
  readonly usesContracts: readonly {
    readonly role: string;
    readonly contractId: string;
    readonly contractVersion: string;
    readonly abiSha256: string;
    readonly required: boolean;
    readonly actions: readonly string[];
  }[];
  readonly provides: readonly {
    readonly providerId: string;
    readonly role: string;
    readonly contractId: string;
    readonly contractVersion: string;
    readonly descriptorSha256: string;
    readonly abiSha256: string;
    readonly audience: string;
  }[];
  readonly packageArtifactImports: readonly unknown[];
  readonly packageApiImports: readonly unknown[];
  readonly providerImports: readonly unknown[];
  readonly platformImports: readonly unknown[];
}

interface OutputDocumentSpecification {
  readonly schemaVersion?: number;
  readonly properties: readonly string[];
}

interface OutputDocumentCorpus {
  readonly formatVersion: number;
  readonly documents: {
    readonly owner: OutputDocumentSpecification;
    readonly processMarker: OutputDocumentSpecification;
    readonly transaction: OutputDocumentSpecification;
    readonly transactionOutput: OutputDocumentSpecification;
  };
}

interface ZipEntry {
  readonly name: string;
  readonly method: number;
  readonly data: Buffer;
}

interface PackageLock {
  readonly packages: Readonly<Record<string, { readonly version?: string }>>;
}

interface PackageJson {
  readonly files?: readonly string[];
  readonly dependencies?: Readonly<Record<string, string>>;
  readonly devDependencies?: Readonly<Record<string, string>>;
}

interface PackageOwnershipEvidence {
  readonly schemaVersion: number;
  readonly archiveSha256: string;
  readonly metadataBytes: number;
  readonly payloadBytes: number;
  readonly runtimeClosureFileCount: number;
  readonly runtimeClosureBytes: number;
  readonly targets: readonly {
    readonly rid: string;
    readonly runtimeClosureFileCount: number;
    readonly runtimeClosureBytes: number;
    readonly ownership: {
      readonly packageSharedBytes: number;
      readonly roleSharedBytes: number;
      readonly exactRidBytes: number;
    };
    readonly nativeAssets: readonly { readonly path: string }[];
  }[];
}
