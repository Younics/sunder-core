import { constants, type BigIntStats } from "node:fs";
import { lstat, open, realpath, type FileHandle } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";

export async function readProjectDescriptorFile(root: string, path: string, label: string, maximumBytes = 4_194_304): Promise<Buffer> {
  const beforePath = await inspectRegularProjectFile(root, path, label);
  let handle: FileHandle;
  try {
    // Windows does not support O_NOFOLLOW. The fallback is fenced by lstat,
    // realpath, and opened-handle identity checks before and after the read.
    const flags = process.platform === "win32"
      ? constants.O_RDONLY
      : constants.O_RDONLY | constants.O_NOFOLLOW;
    handle = await open(path, flags);
  } catch (error) {
    if (["ELOOP", "EMLINK"].includes((error as NodeJS.ErrnoException).code ?? "")) {
      throw regularFileError(label, error);
    }
    throw error;
  }

  try {
    const beforeHandle = await handle.stat({ bigint: true });
    if (!beforeHandle.isFile() || !sameStableFile(beforePath, beforeHandle)) {
      throw changedDuringReadError(label);
    }
    if (beforeHandle.size > BigInt(maximumBytes)) throw new Error(`${label}: rpc-authoring.source-limit-exceeded: input exceeds ${maximumBytes} bytes.`);
    // Never let a concurrently growing input turn a bounded read into readFile's
    // unbounded allocation. Read at most the admitted size in bounded chunks.
    const bytes = Buffer.alloc(Number(beforeHandle.size));
    let offset = 0;
    while (offset < bytes.length) {
      const { bytesRead } = await handle.read(bytes, offset, Math.min(65_536, bytes.length - offset), offset);
      if (!bytesRead) throw changedDuringReadError(label);
      offset += bytesRead;
    }
    const afterHandle = await handle.stat({ bigint: true });
    if (!sameStableFile(beforeHandle, afterHandle)) throw changedDuringReadError(label);
    const afterPath = await inspectRegularProjectFile(root, path, label);
    if (!sameStableFile(afterHandle, afterPath)) throw changedDuringReadError(label);
    return bytes;
  } finally {
    await handle.close();
  }
}

async function inspectRegularProjectFile(root: string, path: string, label: string): Promise<BigIntStats> {
  const normalizedRoot = resolve(root);
  const normalizedPath = resolve(path);
  const traversal = relative(normalizedRoot, normalizedPath);
  if (traversal.length === 0 || traversal === ".." || traversal.startsWith(`..${sep}`) || isAbsolute(traversal)) {
    throw regularFileError(label);
  }

  let current = normalizedRoot;
  const segments = traversal.split(sep);
  let leaf: BigIntStats | undefined;
  for (const [index, segment] of segments.entries()) {
    current = resolve(current, segment);
    let entry: BigIntStats;
    try {
      entry = await lstat(current, { bigint: true });
    } catch (error) {
      throw regularFileError(label, error);
    }
    const isLeaf = index === segments.length - 1;
    if (entry.isSymbolicLink() || (isLeaf ? !entry.isFile() : !entry.isDirectory())) {
      throw regularFileError(label);
    }
    if (isLeaf) leaf = entry;
  }

  try {
    const [realRoot, realFile] = await Promise.all([realpath(normalizedRoot), realpath(normalizedPath)]);
    if (!isInside(realRoot, realFile)) throw regularFileError(label);
  } catch (error) {
    if (error instanceof Error && error.message.startsWith(label)) throw error;
    throw regularFileError(label, error);
  }
  return leaf!;
}

function sameStableFile(left: BigIntStats, right: BigIntStats): boolean {
  return left.dev === right.dev
    && left.ino === right.ino
    && left.mode === right.mode
    && left.nlink === right.nlink
    && left.size === right.size
    && left.mtimeNs === right.mtimeNs
    && left.ctimeNs === right.ctimeNs;
}

function regularFileError(label: string, cause?: unknown): Error {
  return new Error(
    `${label} must resolve to a regular file inside the package project without symbolic links or reparse points.`,
    cause === undefined ? undefined : { cause },
  );
}

function changedDuringReadError(label: string): Error {
  return new Error(`${label} changed while it was being read; refusing a mixed descriptor snapshot.`);
}

function isInside(root: string, path: string): boolean {
  const traversal = relative(resolve(root), resolve(path));
  return traversal.length > 0 && traversal !== ".." && !traversal.startsWith(`..${sep}`) && !isAbsolute(traversal);
}
