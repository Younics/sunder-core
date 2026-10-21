import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { findV1Residue } from "../compatibility/check-v1-residue.mjs";

test("active Core surfaces contain no supported pre-V1 runtime architecture residue", async () => {
  assert.deepEqual(await findV1Residue(), []);
});

test("residue scan covers active repository documentation", async () => {
  await withRepository(async (root) => {
    await mkdir(join(root, "docs"), { recursive: true });
    await writeFile(join(root, "docs", "active.md"), "Requires worker-protocol.v3.\n");

    assert.deepEqual(await findV1Residue(root), [
      "docs/active.md:1: contains forbidden worker-capability-v3 residue",
    ]);
  });
});

test("official runtime cutover rejects retired production tooling paths", async () => {
  await withRepository(async (root) => {
    const relative = ".github/workflows/sunder-execution-runtime-native-release.yml";
    await mkdir(join(root, ".github/workflows"), { recursive: true });
    await writeFile(join(root, relative), "name: stale runtime production\n");
    assert.deepEqual(await findV1Residue(root), [`${relative}: obsolete path still exists`]);
  });
});

test("residue scan covers active root documentation", async () => {
  await withRepository(async (root) => {
    await writeFile(join(root, "README.md"), "Requires rpc.v2.\n");

    assert.deepEqual(await findV1Residue(root), [
      "README.md:1: contains forbidden rpc-capability-v2 residue",
    ]);
  });
});

test("residue scan rejects stale package apphost signing guidance", async () => {
  await withRepository(async (root) => {
    const directory = join(root, "docs", "package-development");
    await mkdir(directory, { recursive: true });
    await writeFile(
      join(directory, "SECURITY.md"),
      "A C# build may platform-sign its apphost and native closure through the signing hook.\n",
    );

    assert.deepEqual(await findV1Residue(root), [
      "docs/package-development/SECURITY.md:1: contains forbidden package-apphost-signing residue",
    ]);
  });
});

test("residue scan retains explicit negative rejection fixtures", async () => {
  await withRepository(async (root) => {
    const relative = "languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests/RuntimePackageManifestValidatorTests.cs";
    const path = join(root, ...relative.split("/"));
    await mkdir(join(path, ".."), { recursive: true });
    await writeFile(path, "Reject(\"worker-protocol.v3\");\n");

    assert.deepEqual(await findV1Residue(root), []);
  });
});

test("residue scan rejects an additional allowlisted category occurrence", async () => {
  await withRepository(async (root) => {
    const relative = "languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests/RuntimePackageManifestValidatorTests.cs";
    const path = join(root, ...relative.split("/"));
    await mkdir(join(path, ".."), { recursive: true });
    await writeFile(path, "Reject(\"worker-protocol.v3\");\nRejectAgain(\"worker-protocol.v3\");\n");

    assert.deepEqual(await findV1Residue(root), [
      `${relative}:2: contains forbidden worker-capability-v3 residue`,
    ]);
  });
});

test("residue scan covers additional authored text formats and special filenames", async () => {
  await withRepository(async (root) => {
    await mkdir(join(root, "languages", "sample"), { recursive: true });
    await writeFile(join(root, "languages", "sample", "view.razor"), "Requires rpc.v2.\n");
    await writeFile(join(root, "languages", "sample", "Dockerfile"), "# Requires worker-protocol.v3.\n");

    assert.deepEqual(await findV1Residue(root), [
      "languages/sample/Dockerfile:1: contains forbidden worker-capability-v3 residue",
      "languages/sample/view.razor:1: contains forbidden rpc-capability-v2 residue",
    ]);
  });
});

test("current residue checks are independent of historical pending-cutover records", async () => {
  await withRepository(async (root) => {
    const directory = join(root, "eng", "compatibility");
    await mkdir(directory, { recursive: true });
    for (const decisions of [{ pendingCutovers: [] }, { pendingCutovers: ["historical-only"] }, {}]) {
      const path = join(directory, "core-compatibility-candidate.json");
      const bytes = JSON.stringify({ decisions });
      await writeFile(path, bytes);
      assert.deepEqual(await findV1Residue(root), []);
      assert.equal(await readFile(path, "utf8"), bytes);
    }
  });
});

test("Node profile scan distinguishes pinned vendor development engines from Sunder runtime policy", async () => {
  const current = JSON.parse(await readFile(new URL("../../languages/typescript/node/package-lock.json", import.meta.url), "utf8"));
  const packages = Object.fromEntries(["@exodus/bytes", "html-encoding-sniffer", "jsdom"]
    .map((name) => [`node_modules/${name}`, current.packages[`node_modules/${name}`]]));
  await withRepository(async (root) => {
    const directory = join(root, "languages", "typescript", "node");
    const path = join(directory, "package-lock.json");
    await mkdir(directory, { recursive: true });
    const write = async (value) => writeFile(path, `${JSON.stringify({ lockfileVersion: 3, packages: value }, null, 2)}\n`);
    await write(packages);
    assert.deepEqual(await findV1Residue(root), []);

    const rejected = async (value) => {
      await write(value);
      const failures = await findV1Residue(root);
      assert.equal(failures.length, 1);
      assert.match(failures[0], /package-lock\.json: contains/u);
    };
    await rejected({ ...packages, "": { engines: { node: ">=24.0.0 <25.0.0" } } });
    await rejected({ ...packages, "packages/sdk": { engines: { node: ">=24.0.0" } } });
    await rejected({ ...packages, "node_modules/unreviewed": { engines: { node: ">=24.0.0" } } });
    await rejected({ ...packages, "node_modules/jsdom": { ...packages["node_modules/jsdom"], version: "27.4.1" } });
    await rejected({ ...packages, "node_modules/jsdom": { ...packages["node_modules/jsdom"], dev: false } });
    await rejected({ ...packages, "node_modules/jsdom": {
      ...packages["node_modules/jsdom"], description: "Requires >=24.0.0",
    } });

    await write(packages);
    const text = await readFile(path, "utf8");
    const duplicate = `    "node_modules/jsdom": ${JSON.stringify(packages["node_modules/jsdom"], null, 2).replaceAll("\n", "\n    ")},\n`;
    await writeFile(path, text.replace('  "packages": {\n', `  "packages": {\n${duplicate}`));
    assert.equal((await findV1Residue(root)).length, 1);
  });
});

async function withRepository(action) {
  const root = await mkdtemp(join(tmpdir(), "sunder-v1-residue-"));
  try {
    await action(root);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}
