import { randomUUID } from "node:crypto";
import { link, mkdir, unlink } from "node:fs/promises";
import { join } from "node:path";
import { withOutputLeases } from "./output";
import { cachePath, contractCacheFiles as fs, verifyCacheDescriptor } from "./rpc-contract-cache";
import { contractError, hashBytes, type PackageArtifact } from "./rpc-contract-lock";
import { outerBytes } from "./rpc-baseline-json";
import { MAX_MODEL_PROJECTION_BYTES, parseModelProjectionDocument, validateProjectionDocument, type RpcModelProjectionDocument } from "./rpc-model-projection";

export const MODEL_PROJECTION_DIRECTORY = "model-projections/sha256";
export function modelProjectionReceiptBytes(hash: string, bytes: number): Buffer {
  return outerBytes({ schemaVersion: 1, modelProjectionSha256: hash, modelProjectionBytes: bytes });
}
export async function readOwnedModelProjectionPair(directory: string, hash: string): Promise<Buffer> {
  if (!/^[a-f0-9]{64}$/u.test(hash)) return contractError("contract-cache.entry-corrupt", "Invalid model projection content address.");
  const body = join(directory, hash + ".json"); const receipt = join(directory, hash + ".receipt.json");
  if (!await fs.exists(body) || !await fs.exists(receipt)) return contractError("contract-cache.entry-missing", "Model projection and ownership receipt are required. Run explicit contracts restore with the exact candidate.");
  const bytes = await fs.read(body, MAX_MODEL_PROJECTION_BYTES);
  parseModelProjectionDocument(bytes);
  if (hashBytes(bytes) !== hash || !(await fs.read(receipt, 16_384)).equals(modelProjectionReceiptBytes(hash, bytes.length)))
    return contractError("contract-cache.entry-corrupt", "Model projection ownership evidence differs from its content address.");
  return bytes;
}
export async function verifyModelProjectionArtifact(artifact: PackageArtifact, bytes: Buffer, descriptors: ReadonlyMap<string, Buffer>): Promise<RpcModelProjectionDocument> {
  if (bytes.length !== artifact.modelProjectionBytes || hashBytes(bytes) !== artifact.modelProjectionSha256)
    return contractError("contract-cache.entry-corrupt", "Model projection bytes differ from the selected artifact pins.");
  const document = parseModelProjectionDocument(bytes);
  const expected = [];
  for (const contract of artifact.contracts.values()) {
    const body = descriptors.get(contract.descriptorSha256);
    if (!body) return contractError("contract-cache.entry-missing", "Complete public descriptors are required to validate the selected model projection.");
    expected.push({ ...contract, descriptor: await verifyCacheDescriptor(contract, body) });
    const projection = document.contracts.find(entry => entry.contractId === contract.contractId)?.modelProjection;
    if (contract.authorityKind === "platform" && projection && (Object.keys(projection.models).length || Object.keys(projection.serviceReferences).length))
      return contractError("contract-cache.entry-corrupt", "Platform-only bundles must carry explicit empty projections.");
  }
  validateProjectionDocument(document, artifact.packageId, artifact.selectedPackageVersion, expected);
  return document;
}
export async function readModelProjectionCache(root: string, artifacts: ReadonlyMap<string, PackageArtifact>, descriptors: ReadonlyMap<string, Buffer>): Promise<ReadonlyMap<string, RpcModelProjectionDocument>> {
  if (!artifacts.size) return new Map();
  root = await cachePath(root);
  if (!await fs.exists(root)) return contractError("contract-cache.entry-missing", "Model projection cache is absent. Run explicit contracts restore.");
  return withOutputLeases([root], async () => {
    await fs.validateRoot(root, true); const result = new Map<string, RpcModelProjectionDocument>();
    for (const artifact of artifacts.values()) result.set(artifact.packageId, await verifyModelProjectionArtifact(artifact,
      await readOwnedModelProjectionPair(join(root, MODEL_PROJECTION_DIRECTORY), artifact.modelProjectionSha256), descriptors));
    return result;
  }, 30_000);
}
/** Exact-address, no-replace publication. Partial pairs never establish readiness. */
export async function publishModelProjectionCache(root: string, entries: ReadonlyMap<string, Buffer>, artifacts: ReadonlyMap<string, PackageArtifact>, descriptors: ReadonlyMap<string, Buffer>, signal?: AbortSignal): Promise<void> {
  if (entries.size > 128) return contractError("contract-cache.entry-corrupt", "Projection publication exceeds its entry bound.");
  for (const [id, bytes] of entries) {
    const artifact = artifacts.get(id); if (!artifact) return contractError("contract-cache.entry-corrupt", "Unselected projection publication.");
    await verifyModelProjectionArtifact(artifact, bytes, descriptors);
  }
  if (!entries.size) return;
  root = await cachePath(root);
  await withOutputLeases([root], async () => {
    signal?.throwIfAborted(); await fs.initializeRoot(root); await fs.validateRoot(root, true);
    const directory = join(root, MODEL_PROJECTION_DIRECTORY); await fs.safePath(directory); await mkdir(directory, { recursive: true });
    for (const [id, bytes] of entries) {
      const hash = artifacts.get(id)!.modelProjectionSha256;
      for (const [name, value] of [[hash + ".json", bytes], [hash + ".receipt.json", modelProjectionReceiptBytes(hash, bytes.length)]] as const) {
        signal?.throwIfAborted(); const path = join(directory, name);
        if (await fs.exists(path)) {
          if (!(await fs.read(path, MAX_MODEL_PROJECTION_BYTES)).equals(value)) return contractError("contract-cache.entry-corrupt", "Conflicting immutable projection cache bytes were preserved; remove the corrupt entry explicitly before restore.");
          continue;
        }
        const temporary = join(directory, ".write-" + randomUUID()); await fs.writeNew(temporary, value);
        try { signal?.throwIfAborted(); await fs.safePath(path); await link(temporary, path); }
        finally { await unlink(temporary); }
      }
      await readOwnedModelProjectionPair(directory, hash);
    }
  }, 30_000);
}
