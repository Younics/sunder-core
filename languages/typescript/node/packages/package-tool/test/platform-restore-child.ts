import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { restoreContracts } from "../src/rpc-contract-restore";
import { parseContractLock } from "../src/rpc-contract-lock";
import { readPlatformCatalogs } from "../src/rpc-platform-catalogs";

async function main(): Promise<void> {
  const [project, cache, catalog, managedCache] = process.argv.slice(2);
  if (!project || !cache || !catalog || !managedCache) throw new Error("Expected project, catalog and cache inputs.");
  const bytes = await readFile(join(project, "sunder-contracts.lock.json")); const locked = parseContractLock(bytes);
  assert.equal((await readPlatformCatalogs(locked, managedCache)).size, 1);
  assert.equal(await restoreContracts(project, { cacheRoot: cache, platformCatalog: catalog, fetchImplementation: async () => { throw new Error("Unexpected HTTP"); } }), 1);
  assert.deepEqual(await readFile(join(project, "sunder-contracts.lock.json")), bytes);
  assert.equal((await readPlatformCatalogs(locked, cache)).size, 1);
}
void main().catch(error => { process.stderr.write(String(error)); process.exitCode = 1; });
