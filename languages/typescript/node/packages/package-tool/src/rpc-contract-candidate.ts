import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { constants, type BigIntStats } from "node:fs";
import { lstat, mkdtemp, open, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { inflateRawSync } from "node:zlib";
import { isPackageId, isSemanticVersion } from "@sunder/sdk";
import { cachePath, contractCacheFiles, verifyCacheDescriptor } from "./rpc-contract-cache";
import { contractError, digest, hashBytes, parseContractWireJson, readRequirement } from "./rpc-contract-lock";
import type { OuterObject } from "./rpc-baseline-json";
import { MODEL_PROJECTION_FILE, MAX_MODEL_PROJECTION_BYTES, parseModelProjectionDocument, validateProjectionDocument } from "./rpc-model-projection";

export interface InspectedContractCandidate {
  readonly archiveSha256: string;
  readonly archiveBytes: number;
  readonly inventory: Record<string, unknown>;
  readonly publicInventorySha256: string;
  readonly modelProjectionBytes: Buffer;
  readonly modelProjectionSha256: string;
  readonly descriptors: ReadonlyMap<string, Buffer>;
  readonly ownedContracts?: readonly InspectedOwnedContract[];
  revalidate(): Promise<void>;
}
export interface InspectedOwnedContract {
  readonly contractId: string; readonly version: string; readonly descriptorSha256: string; readonly abiSha256: string; readonly exposure: "public" | "package";
}

/** Uses the installed canonical Sunder.Package.Format inspector through the local CLI. */
export async function inspectContractCandidate(path: string, signal?: AbortSignal, includeOwnedContracts = false): Promise<InspectedContractCandidate> {
  path = await cachePath(path);
  if (!path.toLowerCase().endsWith(".sunderpkg")) contractError("package-api.public-inventory-invalid", "Candidate input must be a .sunderpkg archive.");
  const original = await archiveHash(path, signal);
  const archiveBytes = Number((await lstat(path)).size);
  const revalidate = async (): Promise<void> => {
    if (await archiveHash(path, signal) !== original) contractError("rpc-authoring.evidence-mismatch", "Candidate archive changed during the operation.");
  };
  const temporary = await cachePath(await mkdtemp(join(tmpdir(), "sunder-candidate-inspection-")));
  const output = join(temporary, "api");
  try {
    await invokeInspector(path, output, temporary, signal);
    const bytes = await contractCacheFiles.read(join(output, "inspection.json"), 2_097_152);
    const receipt = object(parseContractWireJson(bytes, 2_097_152));
    if (JSON.stringify(Object.keys(receipt)) !== JSON.stringify(["schemaVersion", "archiveSha256", "manifestSha256", "contentIndexSha256", "inventory", "publicInventorySha256", "modelProjectionBytes", "modelProjectionSha256"])
      || receipt.schemaVersion !== 1 || digest(receipt.archiveSha256) !== original) invalid("Inspector receipt does not identify the exact candidate archive.");
    const inventory = object(receipt.inventory);
    const contentIdentity = hashBytes(Buffer.concat([Buffer.from(digest(receipt.manifestSha256), "hex"), Buffer.from(digest(receipt.contentIndexSha256), "hex")]));
    const inventoryHash = digest(receipt.publicInventorySha256);
    if (typeof inventory.packageId !== "string" || !isPackageId(inventory.packageId) || typeof inventory.packageVersion !== "string" || !isSemanticVersion(inventory.packageVersion)
      || inventory.packageContentIdentity !== contentIdentity
      || createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(inventory)).digest("hex") !== inventoryHash
      || !Array.isArray(inventory.contracts) || inventory.contracts.length > 128 || inventory.publicContractCount !== inventory.contracts.length)
      invalid("Inspector inventory or raw manifest/content-index identity is inconsistent.");
    const descriptors = new Map<string, Buffer>();
    const expectedFiles = new Set(["inspection.json", MODEL_PROJECTION_FILE]);
    const projectionEvidence = [];
    for (const value of inventory.contracts) {
      const contract = object(value); const requirement = readRequirement(contract as OuterObject);
      const file = requirement.descriptorSha256 + ".json";
      if (expectedFiles.has(file)) invalid("Inspector exported duplicate descriptor identities.");
      expectedFiles.add(file);
      const descriptor = await contractCacheFiles.read(join(output, file));
      const parsed = await verifyCacheDescriptor(requirement, descriptor);
      if (typeof contract.authorityId !== "string" || !isPackageId(contract.authorityId) || contract.authorityKind !== "package" && contract.authorityKind !== "platform") invalid("Invalid public contract authority.");
      projectionEvidence.push({ ...requirement, descriptor: parsed, authorityKind: contract.authorityKind, authorityId: contract.authorityId });
      descriptors.set(requirement.descriptorSha256, descriptor);
    }
    const modelProjectionBytes = await contractCacheFiles.read(join(output, MODEL_PROJECTION_FILE), MAX_MODEL_PROJECTION_BYTES);
    const modelProjectionSha256 = digest(receipt.modelProjectionSha256);
    if (receipt.modelProjectionBytes !== modelProjectionBytes.length || hashBytes(modelProjectionBytes) !== modelProjectionSha256) invalid("Inspector model projection pins differ from its exact sidecar bytes.");
    validateProjectionDocument(parseModelProjectionDocument(modelProjectionBytes), inventory.packageId, inventory.packageVersion, projectionEvidence);
    const files = await contractCacheFiles.boundedNames(output, 130);
    if (files.length !== expectedFiles.size || files.some(file => !expectedFiles.has(file))) invalid("Inspector output contains extra or missing files.");
    const ownedContracts = includeOwnedContracts ? await inspectedOwnedContracts(path, archiveBytes, digest(receipt.manifestSha256), inventory, signal) : undefined;
    await revalidate();
    return { archiveSha256: original, archiveBytes, inventory, publicInventorySha256: inventoryHash, modelProjectionBytes, modelProjectionSha256, descriptors, ...(ownedContracts ? { ownedContracts } : {}), revalidate };
  } finally {
    // Only this invocation's random temporary directory is owned by this operation.
    await rm(temporary, { recursive: true, force: true });
  }
}

/** Metadata retrieval only, AFTER the Format-owned inspector validated every
 * descriptor, payload and archive entry. Its exact manifest digest is the authority;
 * this bounded reader neither substitutes for Format nor extracts package files. */
async function inspectedOwnedContracts(path: string, length: number, manifestHash: string, inventory: Record<string, unknown>, signal?: AbortSignal): Promise<readonly InspectedOwnedContract[]> {
  const file = await open(path, process.platform === "win32" ? constants.O_RDONLY : constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    const read = async (offset: number, size: number): Promise<Buffer> => {
      signal?.throwIfAborted();
      if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(size) || size < 0 || size > 2_097_152 || offset + size > length) invalid("Inspected manifest ZIP metadata is out of bounds.");
      const bytes = Buffer.alloc(size); let position = 0;
      while (position < size) { signal?.throwIfAborted(); const next = await file.read(bytes, position, size - position, offset + position); if (!next.bytesRead) invalid("Inspected archive was truncated."); position += next.bytesRead; }
      return bytes;
    };
    const number64 = (bytes: Buffer, offset: number): number => {
      const value = bytes.readBigUInt64LE(offset); if (value > BigInt(Number.MAX_SAFE_INTEGER)) invalid("Inspected ZIP64 metadata exceeds the integer bound."); return Number(value);
    };
    const tailOffset = Math.max(0, length - 65_557); const tail = await read(tailOffset, length - tailOffset);
    let end = tail.length - 22;
    while (end >= 0 && (tail.readUInt32LE(end) !== 0x06054b50 || end + 22 + tail.readUInt16LE(end + 20) !== tail.length)) end--;
    if (end < 0) invalid("Inspected archive has no bounded ZIP directory.");
    let count = tail.readUInt16LE(end + 10); let offset = tail.readUInt32LE(end + 16); let directorySize = tail.readUInt32LE(end + 12);
    if (count === 0xffff || offset === 0xffffffff || directorySize === 0xffffffff) {
      const locator = await read(tailOffset + end - 20, 20); if (locator.readUInt32LE(0) !== 0x07064b50) invalid("Inspected ZIP64 locator is missing.");
      const directory = await read(number64(locator, 8), 56); if (directory.readUInt32LE(0) !== 0x06064b50) invalid("Inspected ZIP64 directory is missing.");
      count = number64(directory, 32); directorySize = number64(directory, 40); offset = number64(directory, 48);
    }
    const directoryEnd = offset + directorySize;
    if (directoryEnd > length || count > 65_535) invalid("Inspected ZIP directory exceeds its bound.");
    for (let index = 0; index < count; index++) {
      const entry = await read(offset, 46); if (entry.readUInt32LE(0) !== 0x02014b50) invalid("Inspected ZIP directory entry is missing.");
      const nameLength = entry.readUInt16LE(28); const extraLength = entry.readUInt16LE(30); const commentLength = entry.readUInt16LE(32);
      const variable = await read(offset + 46, nameLength + extraLength); offset += 46 + nameLength + extraLength + commentLength;
      if (offset > directoryEnd) invalid("Inspected ZIP entry exceeds its directory.");
      if (variable.subarray(0, nameLength).toString("utf8") !== "manifest/sunder-package.json") continue;
      let size = entry.readUInt32LE(24); let compressed = entry.readUInt32LE(20); let local = entry.readUInt32LE(42);
      if (size === 0xffffffff || compressed === 0xffffffff || local === 0xffffffff) {
        const extra = variable.subarray(nameLength); let position = 0; let found = false;
        while (position + 4 <= extra.length) {
          const tag = extra.readUInt16LE(position); const end = position + 4 + extra.readUInt16LE(position + 2);
          if (end > extra.length) invalid("Inspected ZIP64 extra data is truncated.");
          if (tag === 1) {
            let cursor = position + 4;
            const next = (): number => { if (cursor + 8 > end) invalid("Inspected ZIP64 entry is truncated."); const value = number64(extra, cursor); cursor += 8; return value; };
            if (size === 0xffffffff) size = next(); if (compressed === 0xffffffff) compressed = next(); if (local === 0xffffffff) local = next(); found = true; break;
          }
          position = end;
        }
        if (!found) invalid("Inspected ZIP64 entry metadata is missing.");
      }
      if (size < 2 || size > 1_048_576 || compressed > 2_097_152) invalid("Inspected manifest exceeds its metadata byte bound.");
      const header = await read(local, 30); if (header.readUInt32LE(0) !== 0x04034b50) invalid("Inspected manifest local header is missing.");
      const data = await read(local + 30 + header.readUInt16LE(26) + header.readUInt16LE(28), compressed);
      const method = entry.readUInt16LE(10);
      const bytes = method === 0 ? data : method === 8 ? inflateRawSync(data, { maxOutputLength: 1_048_576 }) : invalid("Inspected manifest compression is unsupported.");
      if (bytes.length !== size || hashBytes(bytes) !== manifestHash) invalid("Manifest differs from the canonical inspector's exact digest.");
      // Format already validated this exact, size-bounded JSON document. Do not
      // apply the narrower Registry response grammar to unrelated manifest data.
      const manifest = object(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)));
      if (manifest.id !== inventory.packageId || manifest.version !== inventory.packageVersion) invalid("Inspected manifest and public inventory name different packages.");
      const bundles = manifest.contractBundles ?? [];
      if (!Array.isArray(bundles) || bundles.length > 128) invalid("Inspected contract bundles exceed their bound.");
      const owned: InspectedOwnedContract[] = [];
      for (const item of bundles) {
        const bundle = object(item); if (bundle.source !== "owned") continue;
        if (typeof bundle.contractId !== "string" || !isPackageId(bundle.contractId) || typeof bundle.version !== "string" || !isSemanticVersion(bundle.version)
          || bundle.exposure !== "public" && bundle.exposure !== "package") invalid("Inspected owned contract identity is invalid.");
        owned.push({ contractId: bundle.contractId, version: bundle.version, descriptorSha256: digest(bundle.descriptorSha256), abiSha256: digest(bundle.abiSha256), exposure: bundle.exposure });
      }
      return owned;
    }
    return invalid("Canonical inspection did not supply its manifest entry.");
  } finally { await file.close(); }
}

async function archiveHash(path: string, signal?: AbortSignal): Promise<string> {
  await cachePath(path);
  const beforePath = await lstat(path, { bigint: true });
  if (!beforePath.isFile() || beforePath.isSymbolicLink() || beforePath.size <= 0n || beforePath.size > 536_870_912n) invalid("Candidate must be a regular archive of at most 512 MiB.");
  const file = await open(path, process.platform === "win32" ? constants.O_RDONLY : constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    const before = await file.stat({ bigint: true }); requireSameFile(beforePath, before);
    const hash = createHash("sha256"); const buffer = Buffer.alloc(65_536); let offset = 0;
    while (offset < Number(before.size)) {
      signal?.throwIfAborted(); const { bytesRead } = await file.read(buffer, 0, Math.min(buffer.length, Number(before.size) - offset), offset);
      if (!bytesRead) invalid("Candidate archive changed during reading.");
      hash.update(buffer.subarray(0, bytesRead)); offset += bytesRead;
    }
    requireSameFile(before, await file.stat({ bigint: true })); await cachePath(path); requireSameFile(before, await lstat(path, { bigint: true }));
    return hash.digest("hex");
  } finally { await file.close(); }
}
function requireSameFile(left: BigIntStats, right: BigIntStats): void {
  if (!right.isFile() || right.isSymbolicLink() || left.dev !== right.dev || left.ino !== right.ino || left.mode !== right.mode
    || left.nlink !== right.nlink || left.size !== right.size || left.mtimeNs !== right.mtimeNs || left.ctimeNs !== right.ctimeNs) invalid("Candidate archive changed while its exact snapshot was captured.");
}

async function invokeInspector(candidate: string, output: string, cwd: string, signal?: AbortSignal): Promise<void> {
  signal?.throwIfAborted();
  const executable = process.env.SUNDER_CLI ?? "sunder";
  if (!executable.trim()) invalid("SUNDER_CLI must identify the installed Sunder CLI executable.");
  await new Promise<void>((resolve, reject) => {
    const child = spawn(executable, ["dev", "package", "validate", candidate, "--api-output", output, "--json"],
      { cwd, shell: false, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    let bytes = 0; let failure: Error | undefined;
    const stop = (message: string): void => {
      failure ??= new Error("package-api.public-inventory-invalid: " + message); child.kill("SIGKILL");
    };
    const abort = (): void => stop("Candidate inspection was cancelled.");
    const timeout = setTimeout(() => stop("Canonical candidate inspection exceeded its five-minute deadline."), 300_000);
    signal?.addEventListener("abort", abort, { once: true });
    for (const stream of [child.stdout, child.stderr]) stream.on("data", (chunk: Buffer) => {
      bytes += chunk.length; if (bytes > 4_194_304) stop("Canonical inspector output exceeded its diagnostic limit.");
    });
    child.once("error", () => { failure ??= new Error("package-api.public-inventory-invalid: Install a matching Sunder CLI or set SUNDER_CLI to its executable path for canonical candidate inspection."); });
    child.once("close", code => {
      clearTimeout(timeout); signal?.removeEventListener("abort", abort);
      if (failure) reject(failure);
      else if (code !== 0) reject(new Error("package-api.public-inventory-invalid: The canonical Sunder CLI inspector rejected the archive. A matching CLI with --api-output support is required."));
      else resolve();
    });
  });
}
function object(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return invalid("Inspector metadata must be an object.");
  return value as Record<string, unknown>;
}
function invalid(message: string): never { return contractError("package-api.public-inventory-invalid", message); }
