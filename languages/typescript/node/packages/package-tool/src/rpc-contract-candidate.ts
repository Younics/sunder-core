import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { constants, type BigIntStats } from "node:fs";
import { lstat, mkdtemp, open, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { cachePath, contractCacheFiles, verifyCacheDescriptor } from "./rpc-contract-cache";
import { contractError, digest, hashBytes, parseContractWireJson, readRequirement } from "./rpc-contract-lock";
import type { OuterObject } from "./rpc-baseline-json";

export interface InspectedContractCandidate {
  readonly archiveSha256: string;
  readonly archiveBytes: number;
  readonly inventory: Record<string, unknown>;
  readonly publicInventorySha256: string;
  readonly descriptors: ReadonlyMap<string, Buffer>;
  revalidate(): Promise<void>;
}

/** Uses the installed canonical Sunder.Package.Format inspector through the local CLI. */
export async function inspectContractCandidate(path: string, signal?: AbortSignal): Promise<InspectedContractCandidate> {
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
    if (JSON.stringify(Object.keys(receipt)) !== JSON.stringify(["schemaVersion", "archiveSha256", "manifestSha256", "contentIndexSha256", "inventory", "publicInventorySha256"])
      || receipt.schemaVersion !== 1 || digest(receipt.archiveSha256) !== original) invalid("Inspector receipt does not identify the exact candidate archive.");
    const inventory = object(receipt.inventory);
    const contentIdentity = hashBytes(Buffer.concat([Buffer.from(digest(receipt.manifestSha256), "hex"), Buffer.from(digest(receipt.contentIndexSha256), "hex")]));
    const inventoryHash = digest(receipt.publicInventorySha256);
    if (inventory.packageContentIdentity !== contentIdentity
      || createHash("sha256").update("sunder.package-public-api-inventory.v1\0").update(JSON.stringify(inventory)).digest("hex") !== inventoryHash
      || !Array.isArray(inventory.contracts) || inventory.contracts.length > 128 || inventory.publicContractCount !== inventory.contracts.length)
      invalid("Inspector inventory or raw manifest/content-index identity is inconsistent.");
    const descriptors = new Map<string, Buffer>();
    const expectedFiles = new Set(["inspection.json"]);
    for (const value of inventory.contracts) {
      const contract = object(value); const requirement = readRequirement(contract as OuterObject);
      const file = requirement.descriptorSha256 + ".json";
      if (expectedFiles.has(file)) invalid("Inspector exported duplicate descriptor identities.");
      expectedFiles.add(file);
      const descriptor = await contractCacheFiles.read(join(output, file));
      await verifyCacheDescriptor(requirement, descriptor);
      descriptors.set(requirement.descriptorSha256, descriptor);
    }
    const files = await contractCacheFiles.boundedNames(output, 129);
    if (files.length !== expectedFiles.size || files.some(file => !expectedFiles.has(file))) invalid("Inspector output contains extra or missing files.");
    await revalidate();
    return { archiveSha256: original, archiveBytes, inventory, publicInventorySha256: inventoryHash, descriptors, revalidate };
  } finally {
    // Only this invocation's random temporary directory is owned by this operation.
    await rm(temporary, { recursive: true, force: true });
  }
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
