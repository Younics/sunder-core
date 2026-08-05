import { createHash, randomUUID } from "node:crypto";
import { createReadStream } from "node:fs";
import { lstat, readFile, rename, rm, writeFile } from "node:fs/promises";

export interface NodeCacheVerification {
  readonly schemaVersion: 1;
  readonly version: string;
  readonly file: string;
  readonly archiveSha256: string;
  readonly executable: string;
  readonly executableSha256: string;
}

export interface VerifyCachedNodeOptions {
  readonly verificationPath: string;
  readonly archivePath: string;
  readonly executablePath: string;
  readonly expectedVersion: string;
  readonly expectedFile: string;
  readonly expectedArchiveSha256: string;
  readonly expectedExecutable: string;
  readonly verifyVersion: () => Promise<void>;
}

export async function verifyCachedNode(options: VerifyCachedNodeOptions): Promise<boolean> {
  try {
    await requireRegularFile(options.verificationPath);
    await requireRegularFile(options.archivePath);
    await requireRegularFile(options.executablePath);
    const value = JSON.parse(await readFile(options.verificationPath, "utf8")) as Partial<NodeCacheVerification>;
    const allowed = new Set(["schemaVersion", "version", "file", "archiveSha256", "executable", "executableSha256"]);
    if (Object.keys(value).some((name) => !allowed.has(name))
      || value.schemaVersion !== 1
      || value.version !== options.expectedVersion
      || value.file !== options.expectedFile
      || value.archiveSha256 !== options.expectedArchiveSha256
      || value.executable !== options.expectedExecutable
      || typeof value.executableSha256 !== "string"
      || !/^[0-9a-f]{64}$/u.test(value.executableSha256)) {
      return false;
    }
    if (await hashFile(options.archivePath) !== options.expectedArchiveSha256) return false;
    if (await hashFile(options.executablePath) !== value.executableSha256) return false;
    await options.verifyVersion();
    return true;
  } catch {
    return false;
  }
}

export async function writeNodeCacheVerification(
  path: string,
  value: NodeCacheVerification,
): Promise<void> {
  const temporaryPath = `${path}.write-${randomUUID()}`;
  const backupPath = `${path}.backup-${randomUUID()}`;
  await writeFile(temporaryPath, `${JSON.stringify(value, null, 2)}\n`, { encoding: "utf8", flag: "wx" });
  let backedUp = false;
  try {
    try {
      await rename(path, backupPath);
      backedUp = true;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
    await rename(temporaryPath, path);
  } catch (error) {
    await rm(temporaryPath, { force: true });
    if (backedUp) await rename(backupPath, path).catch(() => undefined);
    throw error;
  }
  if (backedUp) await rm(backupPath, { force: true }).catch(() => undefined);
}

export async function hashFile(path: string): Promise<string> {
  const hash = createHash("sha256");
  for await (const chunk of createReadStream(path)) hash.update(chunk as Buffer);
  return hash.digest("hex");
}

async function requireRegularFile(path: string): Promise<void> {
  const value = await lstat(path);
  if (!value.isFile() || value.isSymbolicLink()) throw new Error(`Cached Node path '${path}' is not a regular file.`);
}
