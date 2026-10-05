import { readFile } from "node:fs/promises";
import { canonicalRpcDescriptorBytes, parseRpcContractDescriptor } from "@sunder/sdk";
import { parseContractLock } from "../src/rpc-contract-lock";
import { publishContractCache, readContractCache } from "../src/rpc-contract-cache";
import { collectContractCache } from "../src/rpc-contract-cache-gc";
import { platformRequirements, publishPlatformCatalogs, readPlatformCatalogs } from "../src/rpc-platform-catalogs";

async function main(): Promise<void> {
  const [mode, lockPath, root, descriptorPath, boundary] = process.argv.slice(2);
  if (!mode || !lockPath || !root || !descriptorPath) throw new Error("Expected mode, lock, cache and descriptor paths.");
  if (mode === "gc" || mode === "gc-crash") {
    process.stdout.write("collecting\n");
    await collectContractCache(root, [lockPath], { checkpoint: point => { if (mode === "gc-crash" && point.startsWith("gc-tombstoned:")) process.exit(86); } });
    return;
  }
  const locked = parseContractLock(await readFile(lockPath));
  if (mode.startsWith("catalog-")) {
    if (mode === "catalog-read") { await readPlatformCatalogs(locked, root); return; }
    const requirements = platformRequirements(locked); process.stdout.write("publishing\n");
    await publishPlatformCatalogs(root, new Map([[requirements[0]!.contentIdentity, await readFile(descriptorPath)]]), requirements,
      { checkpoint: point => { if (mode === "catalog-crash" && point === boundary) process.exit(86); } });
    return;
  }
  if (mode === "read") { process.stdout.write("reading\n"); await readContractCache(root, locked.requirements); return; }
  const descriptor = Buffer.from(canonicalRpcDescriptorBytes(parseRpcContractDescriptor(await readFile(descriptorPath))));
  const requirement = [...locked.requirements.values()][0]!;
  process.stdout.write("publishing\n");
  await publishContractCache(root, new Map([[requirement.descriptorSha256, descriptor]]), locked.requirements,
    { checkpoint: point => { if (mode === "crash" && point.startsWith("published:")) process.exit(86); } });
}
void main().catch(error => { process.stderr.write(String(error)); process.exitCode = 1; });
