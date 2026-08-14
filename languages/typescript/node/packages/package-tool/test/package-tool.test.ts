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
import { currentRid, SUPPORTED_RIDS, SUPPORTED_WORKER_CAPABILITIES } from "../src/model";
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
    assert.equal(manifest.manifestVersion, 3);
    const target = assertSingle(manifest.targets);
    assert.equal(target.kind, "worker");
    assert.equal(target.rid, currentRid());
    assert.equal(target.entryPoint, "worker.cjs");
    const sdkPackage = JSON.parse(await readFile(resolve(__dirname, "..", "..", "..", "sdk", "package.json"), "utf8")) as { readonly version: string };
    assert.equal(target.sdkVersion, sdkPackage.version);
    assert.ok(target.requiredHostCapabilities.includes("sdk-baseline-1-1.v1"));
    assert.ok(target.requiredHostCapabilities.includes("rpc.v2"));
    assert.ok(target.requiredHostCapabilities.includes("worker-protocol.v3"));
    assert.ok(!target.requiredHostCapabilities.includes("rpc.v1"));
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
    assert.match(bundle, new RegExp(contract.descriptorSha256, "u"));
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
    assert.notEqual(updatedContract.descriptorSha256, contract.descriptorSha256);
    assert.notEqual(updatedContract.abiSha256, contract.abiSha256);
    await buildDevPackage({ projectPath: root, watch: false });
    const updatedBundle = await readFile(bundlePath, "utf8");
    assert.match(updatedBundle, new RegExp(updatedContract.descriptorSha256, "u"));
    assert.doesNotMatch(updatedBundle, new RegExp(contract.descriptorSha256, "u"));
  } finally {
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
      "logging.v1",
      "role-local-workspace.v1",
      "rpc.v2",
      "sdk-baseline-1-1.v1",
      "secrets.v1",
      "settings.schema.v1",
      "settings.v1",
      "storage.key-migration.v1",
      "storage.v1",
      "worker-protocol.v3",
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

test("aggregation accepts only Manifest V3 worker Runtime leaves with exact Worker V3 capabilities", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.workerCapabilities = ["logging.v1"];
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    const project = await prepareProject(root);
    const executable = resolve(root, "fake-sea");
    await writeFile(executable, "self-contained-sea", "utf8");
    const writeLeaf = () => writeTargetLeaf({
      project,
      rid: currentRid(),
      executablePath: executable,
      nodeNoticePath: noticePath(root),
      workerNoticePath: workerNoticePath(root),
      discoveryRoot: targetDiscoveryRoot(project),
    });
    const cases: ReadonlyArray<{
      readonly name: string;
      readonly update: (manifest: PackageManifest) => void;
      readonly expected: RegExp;
    }> = [
      ...[1, 2].map((manifestVersion) => ({
        name: `manifestVersion ${manifestVersion}`,
        update: (manifest: PackageManifest) => {
          (manifest as unknown as { manifestVersion: number }).manifestVersion = manifestVersion;
        },
        expected: /must declare manifestVersion 3/u,
      })),
      ...["dotnet", "process"].map((kind) => ({
        name: `legacy Runtime kind ${kind}`,
        update: (manifest: PackageManifest) => {
          (assertSingle(manifest.targets) as unknown as { kind: string }).kind = kind;
        },
        expected: new RegExp(`Runtime target kind '${kind}'.*must use 'worker'`, "u"),
      })),
      ...["rpc.v1", "worker-protocol.v1"].map((capability) => ({
        name: `legacy capability ${capability}`,
        update: (manifest: PackageManifest) => {
          const target = assertSingle(manifest.targets) as unknown as { requiredHostCapabilities: string[] };
          target.requiredHostCapabilities = [...target.requiredHostCapabilities, capability].sort();
        },
        expected: /requiredHostCapabilities must exactly match/u,
      })),
      ...[
        { name: "missing rpc.v2", remove: "rpc.v2" },
        { name: "missing worker-protocol.v3", remove: "worker-protocol.v3" },
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
    assert.equal(manifest.manifestVersion, 3);
    assert.deepEqual(assertSingle(manifest.targets).requiredHostCapabilities, [
      "logging.v1",
      "rpc.v2",
      "sdk-baseline-1-1.v1",
      "worker-protocol.v3",
    ]);
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
    assert.equal(manifest.archiveFormatVersion, 1);
    assert.equal(manifest.manifestVersion, 3);
    const target = assertSingle(manifest.targets);
    assert.equal(target.kind, "worker");
    assert.deepEqual(target.requiredHostCapabilities, ["rpc.v2", "sdk-baseline-1-1.v1", "worker-protocol.v3"]);
    assert.equal(manifest.contractBundles.length, 1);
    assert.equal(manifest.provides.length, 1);
    const contractBytes = await readFile(resolve(leaf, "payload", "shared", "contracts", "example.rpc.json"));
    const contract = assertSingle(manifest.contractBundles);
    assert.deepEqual(Object.keys(contract).sort(), [
      "abiSha256",
      "contractId",
      "descriptorPath",
      "descriptorSha256",
      "exposure",
      "version",
    ]);
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
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.providers = [
      { providerId: "example.provider", role: "runtime", contractId: "example.rpc", contractVersion: "1.0.0", audience: "owner" },
    ];
    config.usesContracts = [
      { role: "runtime", contractId: "example.rpc", contractVersion: "1.0.0", required: true, actions: ["invoke"] },
      { role: "app", contractId: "example.rpc", contractVersion: "1.0.0", required: false, actions: ["discover", "subscribe"] },
    ];
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
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
    const runtimeManifest = JSON.parse(await readFile(resolve(runtimeLeaf, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(runtimeManifest.manifestVersion, 3);
    assert.deepEqual(runtimeManifest.provides.map((provider) => provider.role), ["runtime"]);
    assert.deepEqual(runtimeManifest.usesContracts.map((use) => use.role), ["runtime"]);
    const appLeaf = await buildWebTarget(root, currentRid());
    assert.notEqual(appLeaf, null);
    const appManifest = JSON.parse(await readFile(resolve(appLeaf!, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(appManifest.manifestVersion, 3);
    const appTarget = assertSingle(appManifest.targets);
    assert.equal(appTarget.kind, "web");
    assert.equal(appTarget.views?.[0]?.viewId, "test.node.package.main");
    assert.deepEqual(appTarget.requiredHostCapabilities, ["rpc.v2", "sdk-baseline-1-1.v1", "views.v1"]);
    assert.deepEqual(appManifest.provides, []);
    assert.deepEqual(appManifest.usesContracts.map((use) => use.role), ["app"]);

    const result = await aggregateAndPack(root, [leafSource(project, runtimeLeaf), leafSource(project, appLeaf!)]);
    const manifest = JSON.parse(await readFile(resolve(result.packageRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(manifest.manifestVersion, 3);
    assert.deepEqual(manifest.targets.map((target) => target.kind).sort(), ["web", "worker"]);
    assert.deepEqual(manifest.provides.map((provider) => `${provider.role}/${provider.providerId}`), ["runtime/example.provider"]);
    assert.deepEqual(manifest.usesContracts.map((use) => `${use.role}/${use.contractId}`), [
      "app/example.rpc",
      "runtime/example.rpc",
    ]);
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
    const config = JSON.parse(await readFile(resolve(packageRoot, "..", "create-sunder-package", template, "sunder.package.json"), "utf8")) as {
      readonly workerCapabilities: readonly string[];
    };
    assert.deepEqual(config.workerCapabilities, []);
  }
  const seaFixture = JSON.parse(await readFile(resolve(packageRoot, "..", "..", "fixtures", "sea-smoke", "sunder.package.json"), "utf8")) as {
    readonly summary: string;
    readonly workerCapabilities: readonly string[];
  };
  assert.match(seaFixture.summary, /Worker V3/u);
  assert.deepEqual(seaFixture.workerCapabilities, []);
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

test("aggregation rejects unknown external target and view properties", async () => {
  const targetRoot = await createFixture();
  try {
    const project = await prepareProject(targetRoot);
    const executable = resolve(targetRoot, "fake-sea");
    await writeFile(executable, "self-contained-sea", "utf8");
    const leaf = await writeTargetLeaf({
      project,
      rid: currentRid(),
      executablePath: executable,
      nodeNoticePath: noticePath(targetRoot),
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
    config.usesContracts = [{ role: "runtime", contractId: "example.rpc", contractVersion: "2.0.0", required: true, actions: ["discover"] }];
    await writeFile(path, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(rangeRoot), /has no exact configured local contract descriptor/u);
  } finally {
    await rm(rangeRoot, { recursive: true, force: true });
  }
});

test("Manifest V3 package builds reject valid RPC descriptor V1", async () => {
  const root = await createFixture();
  try {
    const source = `${JSON.stringify(v1Descriptor, null, 2)}\n`;
    assert.equal(parseRpcContractDescriptor(source).descriptorVersion, 1);
    await writeFile(resolve(root, "contracts", "example.rpc.json"), source, "utf8");

    await assert.rejects(
      () => buildDevPackage({ projectPath: root, watch: false }),
      /Contract descriptor 'contracts\/example\.rpc\.json' uses descriptorVersion 1; Package Manifest V3 requires RPC descriptorVersion 2\./u,
    );
    assert.equal(await exists(resolve(root, "dist", "sunder-dev", "manifest", "sunder-package.json")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Manifest V3 emits exact derived RPC fields and preserves complete SemVer build metadata", async () => {
  const root = await createFixture();
  try {
    const contractVersion = "2.3.4-rc.1+build.7";
    const descriptorPath = resolve(root, "contracts", "example.rpc.json");
    const descriptorValue = JSON.parse(await readFile(descriptorPath, "utf8")) as Record<string, unknown>;
    descriptorValue.version = contractVersion;
    await writeFile(descriptorPath, `${JSON.stringify(descriptorValue, null, 2)}\n`, "utf8");
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.providers = [{
      providerId: "example.provider",
      role: "runtime",
      contractId: "example.rpc",
      contractVersion,
      audience: "owner",
    }];
    config.usesContracts = [{
      role: "runtime",
      contractId: "example.rpc",
      contractVersion,
      required: true,
      actions: ["discover", "invoke", "subscribe"],
    }];
    config.dependencies = [{ packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0" }];
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");

    const project = await prepareProject(root);
    const loaded = assertSingle(project.contracts);
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

    assert.equal(manifest.archiveFormatVersion, 1);
    assert.equal(manifest.manifestVersion, 3);
    assert.deepEqual(manifest.dependsOn, [{ packageId: "example.dependency", versionRange: ">=1.0.0 <2.0.0" }]);
    assert.deepEqual(manifest.contractBundles, [{
      contractId: "example.rpc",
      version: contractVersion,
      descriptorPath: "contracts/example.rpc.json",
      descriptorSha256: loaded.descriptorSha256,
      abiSha256: loaded.abiSha256,
      exposure: "package",
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

    config.usesContracts = [{
      role: "runtime",
      contractId: "example.rpc",
      contractVersion: "2.3.4-rc.1",
      required: true,
      actions: ["invoke"],
    }];
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(root), /version '2\.3\.4-rc\.1' has no exact configured local contract descriptor/u);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("V2 descriptor annotations change only descriptor identity while structural changes alter both hashes", async () => {
  const root = await createFixture();
  try {
    const descriptorPath = resolve(root, "contracts", "example.rpc.json");
    const original = await prepareProject(root);
    const initial = assertSingle(original.contracts);
    assert.equal(initial.descriptor.descriptorVersion, 2);
    assert.equal(initial.exposure, "package");
    assert.equal(initial.canonicalBytes.toString("utf8"), initial.canonical);
    assert.equal(sha256(initial.canonicalBytes), initial.descriptorSha256);

    const annotated = JSON.parse(await readFile(descriptorPath, "utf8")) as {
      $defs: Record<string, Record<string, unknown>>;
    };
    annotated.$defs.Request!.description = "Annotation-only documentation.";
    await writeFile(descriptorPath, `${JSON.stringify(annotated, null, 2)}\n`, "utf8");
    const annotationIdentity = assertSingle((await prepareProject(root)).contracts);
    assert.notEqual(annotationIdentity.descriptorSha256, initial.descriptorSha256);
    assert.equal(annotationIdentity.abiSha256, initial.abiSha256);

    const request = annotated.$defs.Request!;
    const properties = request.properties as Record<string, Record<string, unknown>>;
    properties.message!.maxLength = 129;
    await writeFile(descriptorPath, `${JSON.stringify(annotated, null, 2)}\n`, "utf8");
    const structuralIdentity = assertSingle((await prepareProject(root)).contracts);
    assert.notEqual(structuralIdentity.descriptorSha256, annotationIdentity.descriptorSha256);
    assert.notEqual(structuralIdentity.abiSha256, annotationIdentity.abiSha256);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("schema V2 rejects schema V1 and closed-shape V1 aliases with migration diagnostics", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const baseline = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    await writeFile(configPath, `${JSON.stringify({ ...baseline, schemaVersion: 1 }, null, 2)}\n`, "utf8");
    await assert.rejects(
      () => prepareProject(root),
      /schemaVersion 1 is no longer supported; migrate RPC package authoring to schemaVersion 2/u,
    );

    const invalid: ReadonlyArray<{ readonly value: Partial<Record<string, unknown>>; readonly expected: RegExp }> = [
      {
        value: { contracts: [{ path: "contracts/example.rpc.json" }] },
        expected: /contracts\[0\]\.exposure must be one of/u,
      },
      {
        value: { contracts: [{ path: "contracts/example.rpc.json", exposure: "package", sha256: "0".repeat(64) }] },
        expected: /unknown property 'sha256'/u,
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
        expected: /unknown property 'contractSha256'/u,
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
        expected: /unknown property 'versionRange'/u,
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

test("contract exposure and provider audience enforce Manifest V3 owner/public visibility policy", async () => {
  const root = await createFixture();
  try {
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    for (const [exposure, audience] of [
      ["package", "owner"],
      ["public", "owner"],
      ["public", "public"],
    ] as const) {
      config.contracts = [{ path: "contracts/example.rpc.json", exposure }];
      config.providers = [{
        providerId: "example.provider",
        role: "runtime",
        contractId: "example.rpc",
        contractVersion: "1.0.0",
        audience,
      }];
      await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
      const project = await prepareProject(root);
      assert.equal(assertSingle(project.contracts).exposure, exposure);
    }

    config.contracts = [{ path: "contracts/example.rpc.json", exposure: "package" }];
    config.providers = [{
      providerId: "example.provider",
      role: "runtime",
      contractId: "example.rpc",
      contractVersion: "1.0.0",
      audience: "public",
    }];
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
    await assert.rejects(() => prepareProject(root), /audience 'public'.*exposed only to its package owner/u);

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
      /App-role providers are not supported because App provider registration and serving are not available/u,
    );

    config.providers = [{
      providerId: "example.provider",
      role: "runtime",
      contractId: "example.rpc",
      contractVersion: "1.0.0",
      audience: "owner",
    }];
    config.usesContracts = [{
      role: "app",
      contractId: "example.rpc",
      contractVersion: "1.0.0",
      required: false,
      actions: ["discover"],
    }];
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

test("aggregation rejects conflicting same-role RID uses and providers", async () => {
  for (const kind of ["use", "provider"] as const) {
    const root = await createFixture();
    try {
      const configPath = resolve(root, "sunder.package.json");
      const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
      config.contracts = [{ path: "contracts/example.rpc.json", exposure: "public" }];
      if (kind === "use") {
        config.usesContracts = [{
          role: "runtime",
          contractId: "example.rpc",
          contractVersion: "1.0.0",
          required: true,
          actions: ["invoke"],
        }];
      }
      await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
      const firstProject = await prepareProject(root);
      const executable = resolve(root, "fake-sea");
      await writeFile(executable, "self-contained-sea", "utf8");
      const firstLeaf = await writeTargetLeaf({
        project: firstProject,
        rid: SUPPORTED_RIDS[0],
        executablePath: executable,
        nodeNoticePath: noticePath(root),
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(firstProject),
      });

      if (kind === "use") {
        config.usesContracts = [{
          role: "runtime",
          contractId: "example.rpc",
          contractVersion: "1.0.0",
          required: true,
          actions: ["discover"],
        }];
      } else {
        config.providers = [{
          providerId: "example.provider",
          role: "runtime",
          contractId: "example.rpc",
          contractVersion: "1.0.0",
          audience: "public",
        }];
      }
      await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
      const secondProject = await prepareProject(root);
      const secondLeaf = await writeTargetLeaf({
        project: secondProject,
        rid: SUPPORTED_RIDS[1],
        executablePath: executable,
        nodeNoticePath: noticePath(root),
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(secondProject),
      });

      await assert.rejects(
        () => aggregateAndPack(root, [leafSource(secondProject, firstLeaf), leafSource(secondProject, secondLeaf)]),
        new RegExp(`conflicting ${kind === "use" ? "contract use" : "provider"} metadata`, "u"),
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
      if (kind === "use") {
        config.usesContracts = [{
          role: "runtime",
          contractId: "example.rpc",
          contractVersion: "1.0.0",
          required: true,
          actions: ["invoke"],
        }];
      }
      const configuredDeclarations = kind === "use" ? config.usesContracts : config.providers;
      await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
      const firstProject = await prepareProject(root);
      const executable = resolve(root, "fake-sea");
      await writeFile(executable, "self-contained-sea", "utf8");
      const firstLeaf = await writeTargetLeaf({
        project: firstProject,
        rid: SUPPORTED_RIDS[0],
        executablePath: executable,
        nodeNoticePath: noticePath(root),
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(firstProject),
      });

      if (kind === "use") config.usesContracts = [];
      else config.providers = [];
      await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
      const secondProject = await prepareProject(root);
      const secondLeaf = await writeTargetLeaf({
        project: secondProject,
        rid: SUPPORTED_RIDS[1],
        executablePath: executable,
        nodeNoticePath: noticePath(root),
        workerNoticePath: workerNoticePath(root),
        discoveryRoot: targetDiscoveryRoot(secondProject),
      });

      if (kind === "use") config.usesContracts = configuredDeclarations;
      else config.providers = configuredDeclarations;
      await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
      await assert.rejects(
        () => aggregateAndPack(root, [leafSource(firstProject, firstLeaf), leafSource(firstProject, secondLeaf)]),
        new RegExp(`identical ${kind === "use" ? "contract use" : "provider"} sets for role 'runtime'`, "u"),
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

    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.usesContracts = [{
      role: "runtime",
      contractId: "example.rpc",
      contractVersion: "1.0.0",
      required: true,
      actions: ["invoke"],
    }];
    await writeFile(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");

    await assert.rejects(
      () => aggregateAndPack(root, [leafSource(project, leaf)]),
      /do not exactly match the configured role-scoped RPC uses and providers/u,
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
      import { CONTRACT_IDENTITY } from "../.sunder/generated/rpc/example.rpc";
      void runWorker(() => ({ providers: [{
        providerId: "example.provider",
        contractId: CONTRACT_IDENTITY.contractId,
        contractVersion: CONTRACT_IDENTITY.contractVersion,
        descriptorSha256: CONTRACT_IDENTITY.descriptorSha256,
        abiSha256: CONTRACT_IDENTITY.abiSha256,
        declaredErrors: {},
        handler: {
          invokeUnary: () => ({ accepted: true, marker: "STRUCTURAL_WATCH_V2" }),
          async *invokeServerStream() { yield { accepted: true }; }
        }
      }] }));
    `, "utf8");
    const configPath = resolve(root, "sunder.package.json");
    const config = JSON.parse(await readFile(configPath, "utf8")) as Record<string, unknown>;
    config.entry = "src/worker-next.ts";
    config.contracts = [{ path: "contracts/renamed.rpc.json", descriptorPath: "contracts/renamed.rpc.json", exposure: "package" }];
    config.rpcBindings = [{ path: "contracts/renamed.rpc.json", outputFile: "example.rpc.ts" }];
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
    import { CONTRACT_IDENTITY } from "../.sunder/generated/rpc/example.rpc";
    void runWorker(() => ({ providers: [{
      providerId: "example.provider",
      contractId: CONTRACT_IDENTITY.contractId,
      contractVersion: CONTRACT_IDENTITY.contractVersion,
      descriptorSha256: CONTRACT_IDENTITY.descriptorSha256,
      abiSha256: CONTRACT_IDENTITY.abiSha256,
      declaredErrors: {},
      handler: {
        invokeUnary: () => ({ accepted: true }),
        async *invokeServerStream() { yield { accepted: true }; }
      }
    }] }));
  `, "utf8");
  await writeFile(noticePath(root), "Node.js test distribution notice\n", "utf8");
  await writeFile(workerNoticePath(root), "Sunder Bundled Worker Dependency Notices\n\n@sunder/sdk@1.1.0\n", "utf8");
  await writeFile(resolve(root, "contracts", "example.rpc.json"), `${JSON.stringify(descriptor, null, 2)}\n`, "utf8");
  await writeFile(resolve(root, "sunder.package.json"), `${JSON.stringify({
    schemaVersion: 2,
    id: "test.node.package",
    name: "Test Node Package",
    summary: "Node Worker V3 package test fixture.",
    version: "1.0.0",
    entry: "src/worker.ts",
    workerCapabilities: [],
    contracts: [{ path: "contracts/example.rpc.json", exposure: "package" }],
    rpcBindings: [{ path: "contracts/example.rpc.json", outputFile: "example.rpc.ts" }],
    providers: [{ providerId: "example.provider", role: "runtime", contractId: "example.rpc", contractVersion: "1.0.0", audience: "owner" }],
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
  descriptorVersion: 2,
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

const v1Descriptor = {
  descriptorVersion: 1,
  contractId: "example.rpc",
  version: "1.0.0",
  services: [{
    serviceId: "example",
    methods: [{
      methodId: "invoke",
      kind: "unary",
      requestSchema: { $ref: "#/$defs/Empty" },
      responseSchema: { $ref: "#/$defs/Empty" },
    }],
  }],
  $defs: {
    Empty: { type: "object", properties: {}, required: [], additionalProperties: false },
  },
};

interface PackageManifest {
  readonly archiveFormatVersion: number;
  readonly manifestVersion: number;
  readonly dependsOn: readonly { readonly packageId: string; readonly versionRange: string }[];
  readonly targets: ReadonlyArray<{
    readonly role: string;
    readonly kind: string;
    readonly rid: string;
    readonly entryPoint: string;
    readonly sdkVersion: string;
    readonly requiredHostCapabilities: readonly string[];
    readonly views?: readonly { readonly viewId: string }[];
  }>;
  readonly contractBundles: readonly {
    readonly contractId: string;
    readonly version: string;
    readonly descriptorPath: string;
    readonly descriptorSha256: string;
    readonly abiSha256: string;
    readonly exposure: string;
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
