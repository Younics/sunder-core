import assert from "node:assert/strict";
import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
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

test("residue scan accepts the exact reopened Phase 0 cutover inventory", async () => {
  await withRepository(async (root) => {
    const directory = join(root, "eng", "compatibility");
    await mkdir(directory, { recursive: true });
    await writeFile(join(directory, "core-compatibility-candidate.json"), JSON.stringify({
      decisions: {
        pendingCutovers: [
          "implementation-first-csharp-and-node-rpc-authoring",
          "protocol-project-package-and-authored-descriptor-retirement",
          "source-derived-signed-platform-contract-catalog",
        ],
      },
    }));

    assert.deepEqual(await findV1Residue(root), []);
  });
});

test("residue scan rejects a changed reopened Phase 0 cutover inventory", async () => {
  await withRepository(async (root) => {
    const directory = join(root, "eng", "compatibility");
    await mkdir(directory, { recursive: true });
    await writeFile(join(directory, "core-compatibility-candidate.json"), JSON.stringify({
      decisions: {
        pendingCutovers: ["implementation-first-csharp-and-node-rpc-authoring"],
      },
    }));

    assert.deepEqual(await findV1Residue(root), [
      "eng/compatibility/core-compatibility-candidate.json: pending cutovers differ from the Phase 0 implementation-first inventory",
    ]);
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
