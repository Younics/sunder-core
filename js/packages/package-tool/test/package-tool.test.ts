import assert from "node:assert/strict";
import { createHash, randomUUID } from "node:crypto";
import { mkdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import test from "node:test";
import { currentRid } from "../src/model";
import { aggregateAndPack, buildDevPackage, buildWebTarget, prepareProject, writeTargetLeaf } from "../src/package";

test("dev output uses explicit adjacent current-Node metadata outside the canonical tree", async () => {
  const root = await createFixture();
  try {
    await buildDevPackage({ projectPath: root, watch: false });
    const devRoot = resolve(root, "dist", "sunder-dev");
    const manifest = JSON.parse(await readFile(resolve(devRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    const target = assertSingle(manifest.targets);
    assert.equal(target.kind, "process");
    assert.equal(target.rid, currentRid());
    assert.equal(target.entryPoint, "worker.cjs");
    assert.equal(await exists(resolve(devRoot, "worker.cjs.sunder-node-dev.json")), false);
    const metadataPath = `${devRoot}.sunder-node-dev.json`;
    const metadata = JSON.parse(await readFile(metadataPath, "utf8")) as Record<string, unknown>;
    assert.equal(metadata.nodePath, process.execPath);
    assert.equal(metadata.nodeVersion, process.version);
    assert.equal(metadata.packageId, "test.node.package");
    assert.match(String(metadata.contentIdentity), /^[0-9a-f]{64}$/u);
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
    const leaf = await writeTargetLeaf({ project, rid: currentRid(), executablePath: executable });
    const manifest = JSON.parse(await readFile(resolve(leaf, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.equal(assertSingle(manifest.targets).kind, "process");
    assert.equal(manifest.contractBundles.length, 1);
    assert.equal(manifest.provides.length, 1);
    const first = await aggregateAndPack(root, [leaf]);
    const firstHash = sha256(await readFile(first.archivePath));
    const second = await aggregateAndPack(root, [leaf]);
    assert.equal(sha256(await readFile(second.archivePath)), firstHash);
    assert.ok(second.bytes <= 1024 * 1024);
    const entries = zipEntryNames(await readFile(second.archivePath));
    assert.ok(entries.includes("manifest/sunder-package.json"));
    assert.ok(entries.some((entry) => entry.startsWith("payload/") && entry.includes("/bin/test.node.package")));
    assert.ok(entries.includes("payload/shared/contracts/example.rpc.json"));
    assert.equal(entries.some((entry) => entry.endsWith("worker.cjs") || entry.endsWith(".map") || entry.includes("sunder-node-dev")), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Vite web leaf aggregates with a Node Runtime leaf and preserves declarative views", async () => {
  const root = await createFixture();
  try {
    await addWebApp(root);
    const project = await prepareProject(root);
    const executable = resolve(root, "fake-sea");
    await writeFile(executable, "self-contained-sea", "utf8");
    const runtimeLeaf = await writeTargetLeaf({ project, rid: currentRid(), executablePath: executable });
    const appLeaf = await buildWebTarget(root, currentRid());
    assert.notEqual(appLeaf, null);
    const appManifest = JSON.parse(await readFile(resolve(appLeaf!, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    const appTarget = assertSingle(appManifest.targets);
    assert.equal(appTarget.kind, "web");
    assert.equal(appTarget.views?.[0]?.viewId, "test.node.package.main");

    const result = await aggregateAndPack(root, [runtimeLeaf, appLeaf!]);
    const manifest = JSON.parse(await readFile(resolve(result.packageRoot, "manifest", "sunder-package.json"), "utf8")) as PackageManifest;
    assert.deepEqual(manifest.targets.map((target) => target.kind).sort(), ["process", "web"]);
    assert.equal(manifest.provides.length, 1);
    const entries = zipEntryNames(await readFile(result.archivePath));
    assert.ok(entries.includes(`payload/app/${currentRid()}/index.html`));
    assert.ok(entries.includes(`payload/app/${currentRid()}/icon.svg`));
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("SEA toolchain versions and all six Node distribution hashes remain exact pins", async () => {
  const packageRoot = resolve(__dirname, "..", "..");
  const metadata = JSON.parse(await readFile(resolve(packageRoot, "node-distributions.json"), "utf8")) as DistributionMetadata;
  assert.equal(metadata.nodeVersion, "24.18.1");
  assert.deepEqual(Object.keys(metadata.targets), ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]);
  for (const target of Object.values(metadata.targets)) assert.match(target.sha256, /^[0-9a-f]{64}$/u);
  const lock = JSON.parse(await readFile(resolve(packageRoot, "..", "..", "package-lock.json"), "utf8")) as PackageLock;
  assert.equal(lock.packages["node_modules/esbuild"]?.version, "0.28.1");
  assert.equal(lock.packages["node_modules/postject"]?.version, "1.0.0-alpha.6");
  assert.equal(lock.packages["node_modules/vite"]?.version, "8.0.10");
  assert.equal((await readFile(resolve(packageRoot, "..", "..", ".nvmrc"), "utf8")).trim(), "24.18.1");
});

test("aggregation rejects non-canonical external target leaf paths", async () => {
  const root = await createFixture();
  try {
    const project = await prepareProject(root);
    const executable = resolve(root, "fake-sea");
    await writeFile(executable, "self-contained-sea", "utf8");
    const leaf = await writeTargetLeaf({ project, rid: currentRid(), executablePath: executable });
    const indexPath = resolve(leaf, "manifest", "content-index.json");
    const index = JSON.parse(await readFile(indexPath, "utf8")) as { files: Array<{ path: string }> };
    const payload = index.files.find((item) => item.path.startsWith("payload/"));
    if (payload === undefined) throw new Error("Fixture payload index entry is missing.");
    payload.path = `payload/runtime/${currentRid()}/../escaped`;
    await writeFile(indexPath, `${JSON.stringify(index, null, 2)}\n`, "utf8");

    await assert.rejects(() => aggregateAndPack(root, [leaf]), /not canonical/u);
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
    void runWorker({ providers: [{
      providerId: "example.provider",
      contractId: "example.rpc",
      contractVersion: "1.0.0",
      contractSha256: "PLACEHOLDER",
      handler: {
        invokeUnary: () => ({ accepted: true }),
        async *invokeServerStream() { yield { accepted: true }; }
      }
    }] });
  `, "utf8");
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
  await mkdir(resolve(root, "app", "public"), { recursive: true });
  await writeFile(resolve(root, "app", "index.html"), "<!doctype html><html><body><main>Web fixture</main></body></html>\n", "utf8");
  await writeFile(resolve(root, "app", "public", "icon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>\n", "utf8");
  const path = resolve(root, "sunder.package.json");
  const config = JSON.parse(await readFile(path, "utf8")) as Record<string, unknown>;
  config.app = {
    root: "app",
    entryPoint: "index.html",
    views: [{
      viewId: "test.node.package.main",
      displayName: "Test Web",
      route: "/",
      icon: "icon.svg",
      defaultPlacement: "middle",
      showInHotbar: true,
    }],
  };
  await writeFile(path, `${JSON.stringify(config, null, 2)}\n`, "utf8");
}

function zipEntryNames(archive: Buffer): string[] {
  const output: string[] = [];
  let offset = 0;
  while (offset + 4 <= archive.length && archive.readUInt32LE(offset) === 0x04034b50) {
    const size = archive.readUInt32LE(offset + 18);
    const nameLength = archive.readUInt16LE(offset + 26);
    const extraLength = archive.readUInt16LE(offset + 28);
    output.push(archive.subarray(offset + 30, offset + 30 + nameLength).toString("utf8"));
    offset += 30 + nameLength + extraLength + size;
  }
  return output;
}

function assertSingle<T>(values: readonly T[]): T {
  assert.equal(values.length, 1);
  return values[0]!;
}

function sha256(value: Uint8Array): string {
  return createHash("sha256").update(value).digest("hex");
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
    readonly views?: readonly { readonly viewId: string }[];
  }>;
  readonly contractBundles: readonly unknown[];
  readonly provides: readonly unknown[];
}

interface DistributionMetadata {
  readonly nodeVersion: string;
  readonly targets: Readonly<Record<string, { readonly sha256: string }>>;
}

interface PackageLock {
  readonly packages: Readonly<Record<string, { readonly version?: string }>>;
}
