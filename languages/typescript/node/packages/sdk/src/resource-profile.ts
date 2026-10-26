import { createHash } from "node:crypto";
import type { WorkerResourceProfile } from "./types";

export const SUNDER_WORKER_STANDARD_RESOURCE_PROFILE: WorkerResourceProfile = Object.freeze({
  version: 1,
  rpcValueBytes: 32 * 1024 * 1024,
  valueDepth: 64,
  valueNodes: 4_194_304,
  objectMembers: 65_536,
  inlineDecodedBytes: 4 * 1024 * 1024,
  contentReferenceBytes: 256 * 1024 * 1024,
});

export function computeWorkerResourceProfileHash(profile: WorkerResourceProfile): string {
  const canonical = JSON.stringify({
    version: profile.version,
    rpcValueBytes: profile.rpcValueBytes,
    valueDepth: profile.valueDepth,
    valueNodes: profile.valueNodes,
    objectMembers: profile.objectMembers,
    inlineDecodedBytes: profile.inlineDecodedBytes,
    contentReferenceBytes: profile.contentReferenceBytes,
  });
  return createHash("sha256").update(canonical, "utf8").digest("hex");
}
