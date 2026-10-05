import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { promisify } from "node:util";

/** The installed CLI restores a pinned Registry catalog without signing configuration. */
export async function exercisePackedPlatformCatalog(tool: string, nodeRoot: string): Promise<void> {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-packed-platform-catalog-"));
  try {
    const corpus = resolve(nodeRoot, "../../../specs/rpc/v1/authoring/conformance/valid");
    const locked = JSON.parse(await readFile(resolve(corpus, "mixed-contract-lock.json"), "utf8")); locked.packageArtifacts = {}; locked.packageImports = {};
    await writeFile(resolve(root, "sunder-contracts.lock.json"), JSON.stringify(locked, null, 2) + "\n");
    const catalog = resolve(root, "catalog.json");
    const bytes = Buffer.from(JSON.stringify(JSON.parse(await readFile(resolve(corpus, "platform-catalog.json"), "utf8"))));
    await writeFile(catalog, bytes);
    const args = [tool, "contracts", "restore", "--project", root, "--cache-root", resolve(root, "cache"), "--platform-catalog", catalog];
    await promisify(execFile)(process.execPath, args);
    assert.deepEqual(await readFile(resolve(root, "cache/platform-catalogs", locked.platformImports.stackContributor.selectedAuthorityContentIdentity + ".json")), bytes);
    await promisify(execFile)(process.execPath, args);
    assert.deepEqual(JSON.parse(await readFile(resolve(root, "sunder-contracts.lock.json"), "utf8")), locked);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}
