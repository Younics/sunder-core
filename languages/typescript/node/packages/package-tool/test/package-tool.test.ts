import assert from "node:assert/strict";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { mkdir, readFile, readlink, readdir, realpath, rename, rm, stat, symlink, utimes, writeFile } from "node:fs/promises";
import { hostname, tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { inflateRawSync } from "node:zlib";
import { canonicalizeDescriptor, parseRpcContractDescriptor, type JsonValue } from "@sunder/sdk";
import { downloadBoundedFile, fetchBoundedText } from "../src/download";
import { currentRid, SUPPORTED_RIDS } from "../src/model";
import { hashFile, verifyCachedNode, writeNodeCacheVerification } from "../src/node-cache";
import { classifyBundlerVirtualPackage, resolveRolldownPackageJsonFromVite } from "../src/notices";
import { targetLeafDiscoveryKey, withOutputLocks } from "../src/output";
import {
  aggregateAndPack,
  buildDevPackage,
  buildWebTarget,
  prepareProject,
  writeTargetLeaf,
  writeWebTargetLeaf,
  type LeafDiscoverySource,
  type PreparedProject,
} from "../src/package";

test("dev output uses explicit adjacent current-Node metadata outside the canonical tree", async () => {
  const root = await createFixture();
  try {
    await addWorkerDependency(root);
    await buildDevPackage({ projectPath: root, watch: false });
    const devRoot = resolve(root, "dist", "sunder-dev");
    const manifest = JSON.parse(await readFile(resolve(devRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    const target = assertSingle(manifest.targets);
    assert.equal(target.kind, "worker");
    assert.equal(target.rid, currentRid());
    assert.equal(target.entryPoint, "worker.cjs");
    const sdkPackage = JSON.parse(await readFile(resolve(__dirname, "..", "..", "..", "sdk", "package.json"), "utf8")) as { readonly version: string };
    assert.equal(target.sdkVersion, sdkPackage.version);
    assert.ok(target.requiredHostCapabilities.includes("sdk-baseline-1-1.v1"));
    assert.equal(await exists(resolve(devRoot, "worker.cjs.sunder-node-dev.json")), false);
    const metadataPath = `${devRoot}.sunder-node-dev.json`;
    const metadata = JSON.parse(await readFile(metadataPath, "utf8")) as Record<string, unknown>;
    assert.equal(metadata.nodePath, process.execPath);
    assert.equal(metadata.nodeVersion, process.version);
    assert.equal(metadata.packageId, "test.node.package");
    assert.match(String(metadata.contentIdentity), /^[0-9a-f]{64}$/u);
    const contract = (await prepareProject(root)).contracts[0]!;
    const bundlePath = resolve(devRoot, "payload", "runtime", currentRid(), "worker.cjs");
    const bundle = await readFile(bundlePath, "utf8");
    assert.match(bundle, new RegExp(contract.sha256, "u"));
    assert.equal(await exists(resolve(devRoot, "payload", "runtime", currentRid(), "worker.cjs.map")), false);
    const workerNotice = await readFile(
      resolve(devRoot, "payload", "runtime", currentRid(), "THIRD-PARTY-NOTICES", "Bundled-Worker-Dependencies.txt"),
      "utf8",
    );
    assert.match(workerNotice, /@sunder\/sdk@1\.1\.0/u);
    assert.match(workerNotice, /fixture-worker-dependency@4\.5\.6/u);
    assert.match(workerNotice, /nested worker fixture notice/u);
    assert.doesNotMatch(workerNotice, /worker-build-only-dependency/u);
    assert.doesNotMatch(workerNotice, new RegExp(escapeRegExp(root), "u"));

    const descriptorPath = resolve(root, "contracts", "example.rpc.json");
    const descriptorSource = await readFile(descriptorPath, "utf8");
    await writeFile(descriptorPath, descriptorSource.replace('"maxLength": 128', '"maxLength": 129'), "utf8");
    const updatedContract = (await prepareProject(root)).contracts[0]!;
    assert.notEqual(updatedContract.sha256, contract.sha256);
    await buildDevPackage({ projectPath: root, watch: false });
    const updatedBundle = await readFile(bundlePath, "utf8");
    assert.match(updatedBundle, new RegExp(updatedContract.sha256, "u"));
    assert.doesNotMatch(updatedBundle, new RegExp(contract.sha256, "u"));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("target leaves are AggregateSunderPackageTask-compatible and production archives exclude dev code and metadata", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const executable = resolve(root, "fake-sea");
    await writeFile(executable, "self-contained-sea", "utf8");
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      executablePath: executable,
      nodeNoticePath: noticePath(root),
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const manifest = JSON.parse(await readFile(resolve(leaf, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(assertSingle(manifest.targets).kind, "worker");
    assert.equal(manifest.contractBundles.length, 1);
    assert.equal(manifest.provides.length, 1);
    const contractBytes = await readFile(resolve(leaf, "payload", "shared", "contracts", "example.rpc.json"));
    const contract = assertSingle(manifest.contractBundles);
    assert.equal(sha256(contractBytes), contract.sha256);
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
    assert.ok(entryNames.some((entry) => entry.startsWith("payload/") && entry.includes("/bin/test.node.package")));
    assert.ok(entryNames.includes("payload/shared/contracts/example.rpc.json"));
    assert.ok(entryNames.includes("payload/shared/THIRD-PARTY-NOTICES/Bundled-Worker-Dependencies.txt"));
    assert.ok(entryNames.includes("payload/shared/THIRD-PARTY-NOTICES/Node.js-LICENSE.txt"));
    assert.equal(entryNames.some((entry) => entry.endsWith("worker.cjs") || entry.endsWith(".map") || entry.includes("sunder-node-dev")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Vite web leaf aggregates with a Node Runtime leaf and preserves declarative views", async () => {
  const root = await createFixture();
  try {
    await addWebApp(root);
    await addReactDependencies(root);
    const project = await prepareProject(root);
    const executable = resolve(root, "fake-sea");
    await writeFile(executable, "self-contained-sea", "utf8");
    const runtimeLeaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      executablePath: executable,
      nodeNoticePath: noticePath(root),
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const appLeaf = await buildWebTarget(root, currentRid());
    assert.notEqual(appLeaf, null);
    const appManifest = JSON.parse(await readFile(resolve(appLeaf!, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    const appTarget = assertSingle(appManifest.targets);
    assert.equal(appTarget.kind, "web");
    assert.equal(appTarget.views?.[0]?.viewId, "test.node.package.main");

    const result = await aggregateAndPack(root, [leafSource(project, runtimeLeaf), leafSource(project, appLeaf!)]);
    const manifest = JSON.parse(await readFile(resolve(result.packageRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.deepEqual(manifest.targets.map((target) => target.kind).sort(), ["web", "worker"]);
    assert.equal(manifest.provides.length, 1);
    const entries = zipEntryNames(await readFile(result.archivePath));
    assert.ok(entries.includes("payload/app/shared/index.html"));
    assert.equal(entries.some((entry) => entry.endsWith(".svg") || entry.endsWith(".map")), false);
    assert.ok(entries.includes("payload/runtime/shared/THIRD-PARTY-NOTICES/Node.js-LICENSE.txt"));
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

    let settled = false;
    snapshotTask = writeWebTargetLeaf(project, rid, webRoot, targetDiscoveryRoot(project)).finally(() => { settled = true; });
    await delay(150);
    assert.equal(settled, false, "The web snapshot must wait for the source generation lock.");
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

test("SEA toolchain versions and all six Node distribution hashes remain exact pins", async () => {
  const packageRoot = resolve(__dirname, "..", "..");
  const metadata = JSON.parse(await readFile(resolve(packageRoot, "node-distributions.json"), "utf8")) as DistributionMetadata;
  assert.equal(metadata.nodeVersion, "24.18.1");
  assert.equal(metadata.notice.sha256, "148eacf7863ef4329224a29398623077200a27194aa075569faf4a0a85566ca5");
  assert.deepEqual(Object.keys(metadata.targets), ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]);
  for (const target of Object.values(metadata.targets)) assert.match(target.sha256, /^[0-9a-f]{64}$/u);
  const lock = JSON.parse(await readFile(resolve(packageRoot, "..", "..", "package-lock.json"), "utf8")) as PackageLock;
  assert.equal(lock.packages["node_modules/esbuild"]?.version, "0.28.1");
  assert.equal(lock.packages["node_modules/postject"]?.version, "1.0.0-alpha.6");
  assert.equal(lock.packages["node_modules/vite"]?.version, "8.2.0");
  assert.equal((await readFile(resolve(packageRoot, "..", "..", ".nvmrc"), "utf8")).trim(), "24.18.1");
  const toolPackage = JSON.parse(await readFile(resolve(packageRoot, "package.json"), "utf8")) as PackageJson;
  const createPackage = JSON.parse(await readFile(resolve(packageRoot, "..", "create-sunder-package", "package.json"), "utf8")) as PackageJson;
  assert.equal(toolPackage.dependencies?.["@sunder/sdk"], ">=1.1.0 <1.2.0");
  assert.equal(createPackage.dependencies?.["@sunder/sdk"], ">=1.1.0 <1.2.0");
  for (const template of ["template", "template-react-node"]) {
    const value = JSON.parse(await readFile(resolve(packageRoot, "..", "create-sunder-package", template, "package.json"), "utf8")) as PackageJson;
    assert.equal(value.dependencies?.["@sunder/sdk"], ">=1.1.0 <1.2.0");
    assert.equal(value.devDependencies?.["@sunder/package-tool"], ">=1.1.0 <1.2.0");
  }
});

test("aggregation rejects non-canonical external target leaf paths", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const executable = resolve(root, "fake-sea");
    await writeFile(executable, "self-contained-sea", "utf8");
    const leaf = await writeTargetLeaf({ project, rid: currentRid(), executablePath: executable, nodeNoticePath: noticePath(root), workerNoticePath: workerNoticePath(root), discoveryRoot: targetDiscoveryRoot(project) });
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

test("local descriptors use the strict parser and contract ranges use canonical precedence", async () => {
  const duplicateRoot = await createFixture();
  try {
    const path = resolve(duplicateRoot, "contracts", "example.rpc.json");
    const source = await readFile(path, "utf8");
    await writeFile(path, source.replace(
      '"contractId": "example.rpc",',
      '"contractId": "example.rpc", "contractId": "example.rpc",',
    ), "utf8");
    await assert.rejects(() => prepareProject(duplicateRoot), /Duplicate JSON property/u);
  } finally {
    await rm(duplicateRoot, { recursive: true, force: true });
  }

  const rangeRoot = await createFixture();
  try {
    const path = resolve(rangeRoot, "sunder.package.json");
    const config = JSON.parse(await readFile(path, "utf8")) as Record<string, unknown>;
    config.usesContracts = [{ contractId: "example.rpc", versionRange: ">=2.0.0 <3.0.0", required: true, actions: ["discover"] }];
    await writeFile(path, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(rangeRoot), /has no matching configured local contract descriptor/u);
  } finally {
    await rm(rangeRoot, { recursive: true, force: true });
  }
});

test("aggregation retains one deterministic universal package for all six exact RIDs", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const executable = resolve(root, "fake-sea");
    await writeFile(executable, "self-contained-sea", "utf8");
    const leaves: string[] = [];
    for (const rid of SUPPORTED_RIDS) {
      leaves.push(await writeTargetLeaf({ project, rid, executablePath: executable, nodeNoticePath: noticePath(root), workerNoticePath: workerNoticePath(root), discoveryRoot: targetDiscoveryRoot(project) }));
    }
    const result = await aggregateAndPack(root, leaves.map((leaf) => leafSource(project, leaf)));
    const manifest = JSON.parse(await readFile(resolve(result.packageRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.deepEqual(manifest.targets.map((target) => target.rid), [...SUPPORTED_RIDS]);
    const entries = zipEntryNames(await readFile(result.archivePath));
    assert.equal(entries.filter((entry) => entry.endsWith("THIRD-PARTY-NOTICES/Node.js-LICENSE.txt")).length, 1);
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
    const executable = resolve(root, "fake-sea");
    await writeFile(executable, "self-contained-sea", "utf8");
    const discoveryRoot = project.outputRoot;
    const leaves: string[] = [];
    for (const rid of SUPPORTED_RIDS.slice(0, 2)) {
      leaves.push(await writeTargetLeaf({ project, rid, executablePath: executable, nodeNoticePath: noticePath(root), workerNoticePath: workerNoticePath(root), discoveryRoot }));
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

    let settled = false;
    packageTask = aggregateAndPack(root, [{ path: targetsRoot, discoveryRoot }]).finally(() => { settled = true; });
    await delay(150);
    assert.equal(settled, false, "Aggregation must wait rather than enumerate a target-leaf swap gap.");
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
    const executable = resolve(root, "fake-sea");
    await writeFile(executable, "self-contained-sea", "utf8");
    const leaf = await writeTargetLeaf({ project, rid: currentRid(), executablePath: executable, nodeNoticePath: noticePath(root), workerNoticePath: workerNoticePath(root), discoveryRoot: targetDiscoveryRoot(project) });
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
    const firstExecutable = resolve(root, "fake-sea-first");
    const secondExecutable = resolve(root, "fake-sea-second");
    await writeFile(firstExecutable, "first-sea", "utf8");
    await writeFile(secondExecutable, "second-sea", "utf8");
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      executablePath: firstExecutable,
      nodeNoticePath: noticePath(root),
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const executableName = `test.node.package${currentRid().startsWith("win-") ? ".exe" : ""}`;
    const outputExecutable = resolve(leaf, "payload", "runtime", currentRid(), "bin", executableName);
    const originalManifest = await readFile(resolve(leaf, "manifest", "sunder-package.json"));
    await assert.rejects(
      writeTargetLeaf({
        project,
        rid: currentRid(),
        executablePath: resolve(root, "missing-sea"),
        nodeNoticePath: noticePath(root),
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(project),
      }),
      /ENOENT/u,
    );
    assert.equal(await readFile(outputExecutable, "utf8"), "first-sea");
    assert.deepEqual(await readFile(resolve(leaf, "manifest", "sunder-package.json")), originalManifest);

    await Promise.all([
      writeTargetLeaf({ project, rid: currentRid(), executablePath: firstExecutable, nodeNoticePath: noticePath(root), workerNoticePath: workerNoticePath(root), discoveryRoot: targetDiscoveryRoot(project) }),
      writeTargetLeaf({ project, rid: currentRid(), executablePath: secondExecutable, nodeNoticePath: noticePath(root), workerNoticePath: workerNoticePath(root), discoveryRoot: targetDiscoveryRoot(project) }),
    ]);
    assert.ok(["first-sea", "second-sea"].includes(await readFile(outputExecutable, "utf8")));
    const targetEntries = await readdir(resolve(root, "dist", "targets"));
    assert.equal(targetEntries.some((name) => /\.(?:stage|backup)-/u.test(name)), false);

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
    await delay(150);
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
    const executable = resolve(root, "fake-contention-sea");
    await writeFile(executable, "contention sea\n", "utf8");
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      executablePath: executable,
      nodeNoticePath: noticePath(root),
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
  const outputPath = resolve(root, "package");
  const markerPath = `${outputPath}.sunder-aggregate-generated-output`;
  const token = randomUUID();
  const stagedOutputPath = `${outputPath}.stage-${randomUUID().replaceAll("-", "")}`;
  const stagedMarkerPath = `${markerPath}.stage-${randomUUID().replaceAll("-", "")}`;
  const outputBackupPath = `${outputPath}.backup-${token}`;
  const markerBackupPath = `${markerPath}.backup-${token}`;
  const journalPath = `${outputPath}.sunder-output-transaction.json`;
  try {
    await mkdir(outputPath, { recursive: true });
    await writeFile(resolve(outputPath, "generation.txt"), "old\n", "utf8");
    await writeFile(markerPath, "old marker\n", "utf8");
    await mkdir(stagedOutputPath);
    await writeFile(resolve(stagedOutputPath, "generation.txt"), "new\n", "utf8");
    await writeFile(stagedMarkerPath, "new marker\n", "utf8");
    await writeFile(journalPath, `${JSON.stringify({
      schemaVersion: 1,
      token,
      coordinatorPath: outputPath,
      outputs: [
        { finalPath: outputPath, stagedPath: stagedOutputPath, backupPath: outputBackupPath, existed: true },
        { finalPath: markerPath, stagedPath: stagedMarkerPath, backupPath: markerBackupPath, existed: true },
      ],
    }, null, 2)}\n`, "utf8");
    await rename(outputPath, outputBackupPath);
    await rename(markerPath, markerBackupPath);
    await rename(stagedOutputPath, outputPath);

    await withOutputLocks([outputPath], async () => undefined);

    assert.equal(await readFile(resolve(outputPath, "generation.txt"), "utf8"), "old\n");
    assert.equal(await readFile(markerPath, "utf8"), "old marker\n");
    assert.equal(await exists(journalPath), false);
    assert.equal(await exists(stagedMarkerPath), false);
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

test("same-host owners without their OS-local process marker time out without reclamation", async () => {
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
    await rm(processMarkerPath, { force: true });
    await assert.rejects(() => withOutputLocks([outputPath], async () => undefined, 250), /timed out after 250 ms/iu);
    assert.equal(await exists(lockPath), true);
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
    const waiter = spawn(process.execPath, [helper, "hold", outputPath, waiterReady, waiterRelease], { stdio: ["pipe", "pipe", "pipe"] });
    children.push(waiter);
    await delay(300);
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
      const child = spawn(process.execPath, [helper, "hold", outputPath, ready, release], { stdio: ["pipe", "pipe", "pipe"] });
      children.push(child);
      return { child, ready, release };
    });
    await waitUntil(
      async () => await exists(reclaimerRuns[0]!.ready) || await exists(reclaimerRuns[1]!.ready),
      "first stale-lock reclaimer",
      () => "neither reclaimer acquired the stale lock",
    );
    await delay(250);
    const acquired = await Promise.all(reclaimerRuns.map((run) => exists(run.ready)));
    assert.equal(acquired.filter(Boolean).length, 1, "Exactly one stale-lock reclaimer may own the main lock.");
    const firstIndex = acquired[0] ? 0 : 1;
    const secondIndex = firstIndex === 0 ? 1 : 0;
    await writeFile(reclaimerRuns[firstIndex]!.release, "release\n", "utf8");
    assert.equal(await waitForExit(reclaimerRuns[firstIndex]!.child), 0);
    await waitUntil(
      () => exists(reclaimerRuns[secondIndex]!.ready),
      "second reclaimer after serialized release",
      () => "second reclaimer did not acquire after the first released",
    );
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
    const bundlePath = resolve(devRoot, "payload", "runtime", currentRid(), "worker.cjs");
    await waitUntil(async () => await exists(bundlePath), "initial watch output", () => output);

    await mkdir(resolve(root, "app-next"), { recursive: true });
    await writeFile(resolve(root, "app-next", "index.html"), "<!doctype html><html><body>APP_WATCH_V2</body></html>\n", "utf8");
    await writeFile(resolve(root, "contracts", "renamed.rpc.json"), await readFile(resolve(root, "contracts", "example.rpc.json")));
    await writeFile(resolve(root, "src", "worker-next.ts"), `
      import { runWorker } from "@sunder/sdk";
      import { contractIdentity } from "sunder:contracts";
      void runWorker({ providers: [{
        providerId: "example.provider",
        ...contractIdentity("contracts/renamed.rpc.json"),
        handler: {
          invokeUnary: () => ({ accepted: true, marker: "STRUCTURAL_WATCH_V2" }),
          async *invokeServerStream() { yield { accepted: true }; }
        }
      }] });
    `, "utf8");
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.entry = "src/worker-next.ts";
    config.contracts = [{ path: "contracts/renamed.rpc.json", descriptorPath: "contracts/renamed.rpc.json" }];
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

test("bounded downloads retain prior files and cached Node verification binds executable bytes to the pinned archive", async () => {
  const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
  try {
    await mkdir(root, { recursive: true });
    const outputPath = resolve(root, "download.bin");
    await writeFile(outputPath, "prior-valid", "utf8");
    const oversizedFetch = (async () => new Response(new Uint8Array(32))) as typeof fetch;
    await assert.rejects(() => fetchBoundedText("https://example.invalid/text", {
      label: "test text",
      maximumBytes: 8,
      timeoutMilliseconds: 1_000,
      fetchImplementation: oversizedFetch,
    }), /byte limit/u);
    await assert.rejects(() => downloadBoundedFile("https://example.invalid/file", outputPath, {
      label: "test file",
      maximumBytes: 8,
      timeoutMilliseconds: 1_000,
      fetchImplementation: oversizedFetch,
    }), /byte limit/u);
    assert.equal(await readFile(outputPath, "utf8"), "prior-valid");

    const timeoutFetch = ((_input: string | URL | Request, init?: RequestInit) => new Promise<Response>((_resolvePromise, reject) => {
      const signal = init?.signal;
      signal?.addEventListener("abort", () => reject(signal.reason), { once: true });
    })) as typeof fetch;
    await assert.rejects(() => fetchBoundedText("https://example.invalid/timeout", {
      label: "test timeout",
      maximumBytes: 8,
      timeoutMilliseconds: 20,
      fetchImplementation: timeoutFetch,
    }), /timed out/u);

    const archivePath = resolve(root, "node.tar.gz");
    const executablePath = resolve(root, "node");
    const verificationPath = resolve(root, "verified.json");
    await writeFile(archivePath, "verified archive", "utf8");
    await writeFile(executablePath, "verified executable", "utf8");
    const archiveSha256 = await hashFile(archivePath);
    const executableSha256 = await hashFile(executablePath);
    await writeNodeCacheVerification(verificationPath, {
      schemaVersion: 1,
      version: "24.18.1",
      file: "node.tar.gz",
      archiveSha256,
      executable: "node",
      executableSha256,
    });
    let versionChecks = 0;
    const verify = () => verifyCachedNode({
      verificationPath,
      archivePath,
      executablePath,
      expectedVersion: "24.18.1",
      expectedFile: "node.tar.gz",
      expectedArchiveSha256: archiveSha256,
      expectedExecutable: "node",
      verifyVersion: async () => { versionChecks += 1; },
    });
    assert.equal(await verify(), true);
    assert.equal(versionChecks, 1);
    await writeFile(executablePath, "tampered executable", "utf8");
    assert.equal(await verify(), false);
    assert.equal(versionChecks, 1);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

async function createFixture(): Promise<string> {
  const root = resolve(tmpdir(), "sunder-node-tool-tests", randomUUID());
  await mkdir(resolve(root, "src"), { recursive: true });
  await mkdir(resolve(root, "contracts"), { recursive: true });
  await writeFile(resolve(root, "src", "worker.ts"), `
    import { runWorker } from "@sunder/sdk";
    import { contractIdentity } from "sunder:contracts";
    void runWorker({ providers: [{
      providerId: "example.provider",
      ...contractIdentity("contracts/example.rpc.json"),
      handler: {
        invokeUnary: () => ({ accepted: true }),
        async *invokeServerStream() { yield { accepted: true }; }
      }
    }] });
  `, "utf8");
  await writeFile(noticePath(root), "Node.js test distribution notice\n", "utf8");
  await writeFile(workerNoticePath(root), "Sunder Bundled Worker Dependency Notices\n\n@sunder/sdk@1.1.0\n", "utf8");
  await writeFile(resolve(root, "contracts", "example.rpc.json"), `${JSON.stringify(descriptor, null, 2)}\n`, "utf8");
  await writeFile(resolve(root, "sunder.package.json"), `${JSON.stringify({
    schemaVersion: 1,
    id: "test.node.package",
    name: "Test Node Package",
    summary: "Node process package test fixture.",
    version: "1.0.0",
    entry: "src/worker.ts",
    contracts: [{ path: "contracts/example.rpc.json" }],
    providers: [{ providerId: "example.provider", contractId: "example.rpc", contractVersion: "1.0.0" }],
    usesContracts: [],
  }, null, 2)}\n`, "utf8");
  return root;
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
      "invokeUnary: () => ({ accepted: true })",
      "invokeUnary: () => ({ accepted: true, fixtureWorkerValue })",
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
  const processMarkerRoot = resolve(tmpdir(), "sunder-generated-output", "processes");
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
    const timer = setTimeout(() => reject(new Error("Watch process did not exit.")), 5_000);
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

function noticePath(root: string): string {
  return resolve(root, "Node.js-LICENSE.txt");
}

function workerNoticePath(root: string): string {
  return resolve(root, "Bundled-Worker-Dependencies.txt");
}

function assertSingle<T>(values: readonly T[]): T {
  assert.equal(values.length, 1);
  return values[0]!;
}

function sha256(value: Uint8Array): string {
  return createHash("sha256").update(value).digest("hex");
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
  contractId: "example.rpc",
  version: "1.0.0",
  services: [{
    serviceId: "messages",
    methods: [
      { methodId: "send", kind: "unary", requestSchema: { $ref: "#/$defs/Request" }, responseSchema: { $ref: "#/$defs/Response" } },
      { methodId: "watch", kind: "server-stream", requestSchema: { $ref: "#/$defs/Request" }, eventSchema: { $ref: "#/$defs/Response" } },
    ],
  }],
  $defs: {
    Request: { type: "object", properties: { message: { type: "string", minLength: 1, maxLength: 128 } }, required: ["message"], additionalProperties: false },
    Response: { type: "object", properties: { accepted: { type: "boolean" } }, required: ["accepted"], additionalProperties: false },
  },
};

interface PackageManifest {
  readonly targets: ReadonlyArray<{
    readonly kind: string;
    readonly rid: string;
    readonly entryPoint: string;
    readonly sdkVersion: string;
    readonly requiredHostCapabilities: readonly string[];
    readonly views?: readonly { readonly viewId: string }[];
  }>;
  readonly contractBundles: readonly { readonly sha256: string }[];
  readonly provides: readonly unknown[];
}

interface DistributionMetadata {
  readonly nodeVersion: string;
  readonly notice: { readonly sha256: string };
  readonly targets: Readonly<Record<string, { readonly sha256: string }>>;
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
  readonly dependencies?: Readonly<Record<string, string>>;
  readonly devDependencies?: Readonly<Record<string, string>>;
}
