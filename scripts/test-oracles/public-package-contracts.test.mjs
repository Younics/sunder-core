import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { access, readFile, readdir, rm, mkdir, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, relative, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const inventoryPath = resolve(repositoryRoot, "eng/test-oracles/core-public-packages.json");
const nodeWorkspaceRoot = resolve(repositoryRoot, "languages/typescript/node");
const staleSentinelName = ".sunder-stale-artifact-oracle";
const commonEntries = ["package.json", "README.md", "LICENSE", "NOTICE"];

test("every public npm package has its inventoried role and deterministic artifact", async () => {
  const inventory = JSON.parse(await readFile(inventoryPath, "utf8"));
  assert.equal(inventory.schemaVersion, 1);
  const publicWorkspaces = await discoverPublicWorkspaces();
  assertPublicWorkspaceCoverage(inventory.npm, publicWorkspaces);
  await rebuildPublicWorkspacesFromCleanOutput(publicWorkspaces);
  const root = resolve(tmpdir(), `sunder-npm-package-contract-${randomUUID()}`);
  try {
    await mkdir(root, { recursive: true });
    for (const contract of inventory.npm) {
      const project = resolve(repositoryRoot, contract.project);
      const manifest = JSON.parse(await readFile(resolve(project, "package.json"), "utf8"));
      assert.equal(manifest.name, contract.name);
      assert.equal(manifest.version, inventory.coordinatedVersion);
      assert.equal(manifest.engines?.node, ">=24.18.1 <25.0.0");
      verifyRole(contract.role, manifest);

      const safeName = contract.name.replaceAll("/", "-").replaceAll("@", "");
      const firstDirectory = resolve(root, safeName, "first");
      const secondDirectory = resolve(root, safeName, "second");
      await mkdir(firstDirectory, { recursive: true });
      await mkdir(secondDirectory, { recursive: true });
      const first = pack(project, firstDirectory);
      const second = pack(project, secondDirectory);
      assert.equal(first.name, contract.name);
      assert.equal(first.version, inventory.coordinatedVersion);
      assert.ok(first.size > 0 && first.size <= contract.maximumBytes, `${contract.name} exceeded ${contract.maximumBytes} bytes`);
      const entries = first.files.map((file) => file.path);
      verifyEntries(contract, entries);
      verifyExportTargets(manifest, entries);
      assert.equal(entries.includes(`dist/${staleSentinelName}`), false, `${contract.name} packed stale ignored output`);
      assert.deepEqual(first.files, second.files, `${contract.name} packed file inventory changed between runs`);
      const firstBytes = await readFile(resolve(firstDirectory, first.filename));
      const secondBytes = await readFile(resolve(secondDirectory, second.filename));
      assert.equal(sha256(firstBytes), sha256(secondBytes), `${contract.name} tarball was not deterministic`);
    }
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("public npm artifact oracle rejects a seeded missing entry", async () => {
  const inventory = JSON.parse(await readFile(inventoryPath, "utf8"));
  const contract = inventory.npm[0];
  const entries = [...commonEntries, ...contract.requiredEntries.slice(1)];
  assert.throws(() => verifyEntries(contract, entries), /tarball entry closure drifted/u);
});

test("public npm artifact oracle rejects a seeded unexpected entry", async () => {
  const inventory = JSON.parse(await readFile(inventoryPath, "utf8"));
  const contract = inventory.npm[0];
  const entries = [...commonEntries, ...contract.requiredEntries, "dist/unexpected.js"];
  assert.throws(() => verifyEntries(contract, entries), /tarball entry closure drifted/u);
});

test("public npm inventory rejects a seeded unlisted public workspace", async () => {
  const inventory = JSON.parse(await readFile(inventoryPath, "utf8"));
  const publicWorkspaces = await discoverPublicWorkspaces();
  assert.throws(
    () => assertPublicWorkspaceCoverage(inventory.npm, [
      ...publicWorkspaces,
      { name: "seeded-unlisted-package", project: "languages/typescript/node/packages/seeded-unlisted-package" },
    ]),
    /seeded-unlisted-package/u,
  );
});

async function discoverPublicWorkspaces() {
  const rootManifest = JSON.parse(await readFile(resolve(nodeWorkspaceRoot, "package.json"), "utf8"));
  const projects = [];
  for (const pattern of rootManifest.workspaces ?? []) {
    if (!pattern.endsWith("/*") || pattern.slice(0, -2).includes("*")) {
      throw new Error(`Unsupported npm workspace pattern '${pattern}'.`);
    }
    const parent = resolve(nodeWorkspaceRoot, pattern.slice(0, -2));
    for (const entry of await readdir(parent, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const project = resolve(parent, entry.name);
      let manifest;
      try {
        manifest = JSON.parse(await readFile(resolve(project, "package.json"), "utf8"));
      } catch (error) {
        if (error?.code === "ENOENT") continue;
        throw error;
      }
      if (manifest.private === true) continue;
      assert.equal(typeof manifest.name, "string", `Public workspace '${project}' has no package name.`);
      projects.push({
        name: manifest.name,
        project: relative(repositoryRoot, project).replaceAll("\\", "/"),
      });
    }
  }
  return projects.sort((left, right) => ordinal(left.project, right.project));
}

function assertPublicWorkspaceCoverage(contracts, publicWorkspaces) {
  const declared = contracts
    .map(({ name, project }) => ({ name, project }))
    .sort((left, right) => ordinal(left.project, right.project));
  const discovered = publicWorkspaces
    .map(({ name, project }) => ({ name, project }))
    .sort((left, right) => ordinal(left.project, right.project));
  assert.deepEqual(
    declared,
    discovered,
    `Public npm workspace inventory drift: declared=${JSON.stringify(declared)} discovered=${JSON.stringify(discovered)}`,
  );
}

async function rebuildPublicWorkspacesFromCleanOutput(publicWorkspaces) {
  for (const workspace of publicWorkspaces) {
    const project = resolve(repositoryRoot, workspace.project);
    const dist = resolve(project, "dist");
    await mkdir(dist, { recursive: true });
    await writeFile(resolve(dist, staleSentinelName), "stale ignored output\n", "utf8");
    await rm(dist, { recursive: true, force: true });
    await rm(resolve(project, "dist-test"), { recursive: true, force: true });
  }
  runRequired(process.platform === "win32" ? "npm.cmd" : "npm", ["run", "build"], nodeWorkspaceRoot, "clean public workspace build");
  for (const workspace of publicWorkspaces) {
    await assert.rejects(access(resolve(repositoryRoot, workspace.project, "dist", staleSentinelName)), { code: "ENOENT" });
  }
}

function pack(project, outputDirectory) {
  const command = process.platform === "win32" ? "npm.cmd" : "npm";
  const result = spawnSync(command, [
    "pack",
    "--ignore-scripts",
    "--json",
    "--pack-destination",
    outputDirectory,
  ], {
    cwd: project,
    encoding: "utf8",
    env: { ...process.env, NO_COLOR: "1" },
  });
  assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
  const report = JSON.parse(result.stdout);
  assert.equal(report.length, 1, result.stdout);
  return report[0];
}

function runRequired(command, arguments_, cwd, label) {
  const result = spawnSync(command, arguments_, {
    cwd,
    encoding: "utf8",
    env: { ...process.env, NO_COLOR: "1" },
  });
  assert.equal(result.error, undefined, `${label} could not start: ${result.error?.message ?? "unknown error"}`);
  assert.equal(result.status, 0, `${label} failed.\n${result.stdout ?? ""}\n${result.stderr ?? ""}`);
}

function verifyRole(role, manifest) {
  switch (role) {
    case "contract-and-worker-library":
      assert.equal(manifest.main, "dist/index.js");
      assert.equal(manifest.types, "dist/index.d.ts");
      assert.deepEqual(manifest.exports, {
        ".": { types: "./dist/index.d.ts", require: "./dist/index.js", default: "./dist/index.js" },
        "./browser": { types: "./dist/browser.d.ts", require: "./dist/browser.js", default: "./dist/browser.js" },
        "./core": { types: "./dist/core.d.ts", require: "./dist/core.js", default: "./dist/core.js" },
        "./react": { types: "./dist/react.d.ts", require: "./dist/react.js", default: "./dist/react.js" },
      });
      assert.deepEqual(manifest.dependencies ?? {}, {}, "The main SDK remains runtime-dependency-free.");
      assert.deepEqual(manifest.peerDependencies, { react: ">=19.2.0 <20.0.0" });
      assert.deepEqual(manifest.peerDependenciesMeta, { react: { optional: true } });
      assert.equal(manifest.bin["sunder-rpc-gen"], "dist/generator-cli.js");
      break;
    case "node-package-tool":
      assert.equal(manifest.bin["sunder-package"], "dist/cli.js");
      assert.equal(manifest.types, "dist/index.d.ts");
      break;
    case "node-template-tool":
      assert.equal(manifest.bin["create-sunder-package"], "dist/cli.js");
      assert.ok(manifest.files.includes("template"));
      assert.ok(manifest.files.includes("!template/.sunder"));
      assert.ok(manifest.files.includes("template-react-node"));
      assert.ok(manifest.files.includes("!template-react-node/.sunder"));
      break;
    default:
      assert.fail(`Unknown public npm package role '${role}'.`);
  }
}

function verifyExportTargets(manifest, entries) {
  const published = new Set(entries);
  const check = (path) => {
    assert.equal(typeof path, "string", `${manifest.name} entry point must be a concrete path`);
    assert.ok(published.has(path.replace(/^\.\//u, "")), `${manifest.name} entry point '${path}' is missing from the tarball`);
  };
  for (const path of [manifest.main, manifest.types, ...Object.values(manifest.bin ?? {})]) if (path !== undefined) check(path);
  for (const conditions of Object.values(manifest.exports ?? {})) for (const path of Object.values(conditions)) check(path);
}

function verifyEntries(contract, entries) {
  const expected = [...commonEntries, ...contract.requiredEntries];
  assert.equal(new Set(expected).size, expected.length, `${contract.name} expected entry inventory contains duplicates`);
  assert.equal(new Set(entries).size, entries.length, `${contract.name} tarball contains duplicate entries`);
  assert.deepEqual(
    entries.toSorted(ordinal),
    expected.toSorted(ordinal),
    `${contract.name} tarball entry closure drifted`,
  );
}

function sha256(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

function ordinal(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}
