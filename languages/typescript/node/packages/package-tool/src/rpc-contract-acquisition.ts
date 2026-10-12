import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { cachePath, contractCacheFiles } from "./rpc-contract-cache";
import { inspectContractCandidate, type InspectedContractCandidate } from "./rpc-contract-candidate";
import { readResponse, type ContractRestoreOptions } from "./rpc-contract-restore";

/** Explicit authoring acquisition only; installation keeps shared/current-RID transport. */
export async function acquireContractCandidate(packageId: string, version: string, origin: string, options: ContractRestoreOptions): Promise<InspectedContractCandidate & { dispose(): Promise<void> }> {
  const temporary = await cachePath(await mkdtemp(join(tmpdir(), "sunder-authoring-candidate-")));
  const dispose = (): Promise<void> => rm(temporary, { recursive: true, force: true });
  try {
    const url = new URL(`api/v1/packages/${encodeURIComponent(packageId)}/versions/${encodeURIComponent(version)}/canonical`, origin);
    const bytes = await readResponse(url, "application/octet-stream", 536_870_912, undefined, options.fetchImplementation ?? fetch, options.signal,
      undefined, "package-api.package-not-found", options.allowDevelopmentRegistryOrigin);
    const path = join(temporary, "candidate.sunderpkg"); await contractCacheFiles.writeNew(path, bytes);
    return { ...await inspectContractCandidate(path, options.signal), dispose };
  } catch (error) { await dispose(); throw error; }
}
