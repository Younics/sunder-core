import assert from "node:assert/strict";
import { mkdir, readFile, stat, writeFile } from "node:fs/promises";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { join, resolve } from "node:path";
import { restoreContracts } from "../src/rpc-contract-restore";
import { inventoryBytes, parseContractLock } from "../src/rpc-contract-lock";
import { readContractCache } from "../src/rpc-contract-cache";
import { packageApiCommand } from "../src/rpc-package-commands";
import { generateRpcBindingsForLoadedProject } from "../src/rpc-bindings";
import { loadProjectWithOwnedFrame } from "../src/model";

async function main(): Promise<void> {
  const [project, candidate, cache] = process.argv.slice(2);
  if (!project || !candidate || !cache) throw new Error("Expected project, archive and cache.");
  const lockPath = join(project, "sunder-contracts.lock.json"); const before = await readFile(lockPath);
  const restored = await restoreContracts(project, { candidate, cacheRoot: cache, fetchImplementation: async () => { throw new Error("Offline candidate restore attempted HTTP."); } });
  assert.equal(restored, 1); assert.deepEqual(await readFile(lockPath), before);
  const locked = parseContractLock(before);
  const cached = await readContractCache(cache, locked.requirements); assert.equal(cached.size, 1);
  const artifact = [...locked.packageArtifacts.values()][0]!;
  const contract = [...artifact.contracts.values()][0]!;
  if (artifact.registryOrigin === undefined) {
    const offline: typeof fetch = async () => { throw new Error("Local candidate authoring attempted HTTP."); };
    assert.equal(await restoreContracts(project, { cacheRoot: cache, fetchImplementation: offline }), 0);
    const root = join(project, "node-local-authoring"); await mkdir(join(root, "src"), { recursive: true });
    const fixtures = resolve(__dirname, "../../../../../../../specs/rpc/v1/authoring/conformance/valid");
    await writeFile(join(root, "sunder-contracts.lock.json"), await readFile(join(fixtures, "minimal-contract-lock.json")));
    await writeFile(join(root, "sunder.package.json"), JSON.stringify({ schemaVersion: 3, id: "example.owner", name: "Local Consumer", version: "1.0.0",
      entry: "src/worker.ts", workerCapabilities: [], rpc: { module: "src/rpc.ts", export: "packageRpc" } }));
    await writeFile(join(root, "src/rpc.ts"), 'import {rpc} from "@sunder/sdk"; export const packageRpc=rpc.router({contracts:{},providers:{}});');
    await writeFile(join(root, "src/worker.ts"), 'export {packageRpc} from "./rpc.js"; export {packageApis} from "#sunder/rpc";');
    const selection = await packageApiCommand(root, { operation: "add", packageId: artifact.packageId, versionRange: artifact.requestedVersionRange,
      candidate, cacheRoot: cache, fetchImplementation: offline });
    assert.equal(selection.changed, true); assert.equal(selection.lock.packageArtifacts.get(artifact.packageId)!.registryOrigin, undefined);
    await assert.rejects(stat(join(root, "sunder-contracts.owned.json")), { code: "ENOENT" });
    await assert.rejects(stat(join(root, ".sunder/generated/rpc")), { code: "ENOENT" });
    await promisify(execFile)(process.execPath, [resolve(__dirname, "../src/cli.js"), "contracts", "accept", "--project", root],
      { env: { ...process.env, SUNDER_CONTRACT_CACHE: cache } });
    await generateRpcBindingsForLoadedProject(await loadProjectWithOwnedFrame(root, { contractCacheRoot: cache }));
    assert.match(await readFile(join(root, ".sunder/generated/rpc/imports.ts"), "utf8"), /packageApis/u);
    assert.deepEqual(await readFile(lockPath), before);
    process.stdout.write("candidate-restored\n"); return;
  }
  const fetchImplementation: typeof fetch = async input => {
    const url = String(input); const headers: Record<string, string> = { "content-type": "application/json" }; let body: Uint8Array | string;
    if (url.endsWith("/public-api")) body = JSON.stringify({ inventory: JSON.parse(inventoryBytes(artifact).toString()) as unknown, publicInventorySha256: artifact.publicInventorySha256 });
    else if (url.endsWith("/descriptor")) {
      body = new Uint8Array(cached.get(contract.descriptorSha256)!.canonicalBytes); headers["content-type"] = "application/schema+json"; headers.etag = contract.etag;
    } else body = JSON.stringify({ contractId: contract.contractId, version: contract.contractVersion, descriptorSha256: contract.descriptorSha256,
      abiSha256: contract.abiSha256, size: contract.descriptorBytes, exposure: "public", firstPublisherPackageId: contract.authorityId, etag: contract.etag });
    const response = new Response(body, { headers }); Object.defineProperty(response, "url", { value: url }); return response;
  };
  assert.equal(await restoreContracts(project, { cacheRoot: cache + "-registry", fetchImplementation }), 1);
  for (const suffix of [".json", ".receipt.json"]) assert.deepEqual(await readFile(join(cache, contract.descriptorSha256 + suffix)), await readFile(join(cache + "-registry", contract.descriptorSha256 + suffix)));
  assert.deepEqual(await readFile(lockPath), before);
  process.stdout.write("candidate-restored\n");
}
void main().catch(error => { process.stderr.write(String(error)); process.exitCode = 1; });
