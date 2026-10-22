#!/usr/bin/env node
import { createHash, randomBytes } from "node:crypto";
import { constants, type BigIntStats } from "node:fs";
import { lstat, open, rename, unlink, type FileHandle } from "node:fs/promises";
import { basename, dirname, join, parse, relative, resolve, sep } from "node:path";
import { parseRpcContractDescriptor } from "@sunder/sdk";
import {
  SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR,
  canonicalRpcAbiProjectionBytes,
  canonicalRpcDescriptorBytes,
} from "@sunder/sdk";
import { generateTypeScriptBindings } from "./generator";

async function main(): Promise<void> {
  const [input, output] = process.argv.slice(2);
  if (input === undefined || output === undefined || process.argv.length !== 4) {
    throw new Error("Usage: sunder-rpc-gen <descriptor.json> <bindings.ts>");
  }
  const inputPath = resolve(input);
  const outputPath = resolve(output);
  if (samePath(inputPath, outputPath)) {
    throw new Error("The descriptor input and bindings output must be different paths.");
  }

  const expectedOutput = await inspectOutput(outputPath);
  const source = await readDescriptorSource(inputPath);
  if (expectedOutput !== undefined && sameFile(source.entry, expectedOutput)) {
    throw new Error("The descriptor input and bindings output must identify different files.");
  }

  const descriptor = parseRpcContractDescriptor(source.bytes);
  const descriptorSha256 = createHash("sha256")
    .update(canonicalRpcDescriptorBytes(descriptor))
    .digest("hex");
  const options = {
    descriptorSha256,
    abiSha256: createHash("sha256")
      .update(SUNDER_RPC_V1_ABI_DOMAIN_SEPARATOR, "utf8")
      .update(canonicalRpcAbiProjectionBytes(descriptor))
      .digest("hex"),
  };
  const bindings = generateTypeScriptBindings(descriptor, options);
  await replaceOutput(outputPath, bindings, expectedOutput);
}

async function readDescriptorSource(path: string): Promise<Readonly<{ bytes: Buffer; entry: BigIntStats }>> {
  const pathEntry = await tryLstat(path);
  if (pathEntry === undefined || pathEntry.isSymbolicLink() || !pathEntry.isFile()) {
    throw new Error(`Descriptor input '${path}' must be a regular file and not a symbolic link.`);
  }

  const noFollow = process.platform === "win32" ? 0 : constants.O_NOFOLLOW;
  let handle: FileHandle;
  try {
    handle = await open(path, constants.O_RDONLY | noFollow);
  } catch (error) {
    throw new Error(`Descriptor input '${path}' could not be opened without following a symbolic link.`, { cause: error });
  }
  try {
    const before = await handle.stat({ bigint: true });
    if (!before.isFile() || !sameFile(pathEntry, before)) {
      throw new Error(`Descriptor input '${path}' changed before it could be read safely.`);
    }
    const bytes = await handle.readFile();
    const after = await handle.stat({ bigint: true });
    const currentPathEntry = await tryLstat(path);
    if (!sameSnapshot(before, after)
      || currentPathEntry === undefined
      || currentPathEntry.isSymbolicLink()
      || !sameSnapshot(after, currentPathEntry)) {
      throw new Error(`Descriptor input '${path}' changed while it was read.`);
    }
    return { bytes, entry: after };
  } finally {
    await handle.close();
  }
}

async function replaceOutput(path: string, content: string, expected: BigIntStats | undefined): Promise<void> {
  const mode = expected === undefined ? 0o666 : Number(expected.mode & 0o777n);
  let temporaryPath: string | undefined;
  try {
    const temporary = await createAdjacentTemporaryFile(path, mode);
    temporaryPath = temporary.path;
    try {
      if (expected !== undefined) await temporary.handle.chmod(mode);
      const bytes = Buffer.from(content, "utf8");
      let offset = 0;
      while (offset < bytes.byteLength) {
        const { bytesWritten } = await temporary.handle.write(bytes, offset, bytes.byteLength - offset, null);
        if (bytesWritten === 0) throw new Error("Writing generated bindings made no progress.");
        offset += bytesWritten;
      }
      await temporary.handle.sync();
    } finally {
      await temporary.handle.close();
    }

    const current = await inspectOutput(path);
    if (!sameOptionalSnapshot(expected, current)) {
      throw new Error(`Bindings output '${path}' changed while generation was in progress.`);
    }
    await rename(temporaryPath, path);
    temporaryPath = undefined;
  } catch (error) {
    if (temporaryPath !== undefined) {
      try {
        await unlink(temporaryPath);
      } catch (cleanupError) {
        if ((cleanupError as NodeJS.ErrnoException).code !== "ENOENT") {
          throw new AggregateError([error, cleanupError], `Generation failed and temporary output '${temporaryPath}' could not be removed.`);
        }
      }
    }
    throw error;
  }
}

async function createAdjacentTemporaryFile(
  outputPath: string,
  mode: number,
): Promise<Readonly<{ path: string; handle: FileHandle }>> {
  const directory = dirname(outputPath);
  const stem = basename(outputPath).slice(0, 32);
  for (let attempt = 0; attempt < 16; attempt++) {
    const path = join(directory, `.${stem}.sunder-${process.pid}-${randomBytes(8).toString("hex")}.tmp`);
    try {
      return { path, handle: await open(path, constants.O_CREAT | constants.O_EXCL | constants.O_WRONLY, mode) };
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw error;
    }
  }
  throw new Error(`Could not allocate an exclusive temporary file adjacent to '${outputPath}'.`);
}

async function inspectOutput(path: string): Promise<BigIntStats | undefined> {
  await assertSafeDirectoryChain(dirname(path));
  const entry = await tryLstat(path);
  if (entry !== undefined && (entry.isSymbolicLink() || !entry.isFile())) {
    throw new Error(`Bindings output '${path}' must be absent or an existing regular file; symbolic links and other file types are not allowed.`);
  }
  return entry;
}

async function assertSafeDirectoryChain(directory: string): Promise<void> {
  const absolute = resolve(directory);
  const root = parse(absolute).root;
  const segments = relative(root, absolute).split(sep).filter((segment) => segment.length > 0);
  let current = root;
  for (const segment of ["", ...segments]) {
    if (segment.length > 0) current = join(current, segment);
    const entry = await tryLstat(current);
    if (entry === undefined || entry.isSymbolicLink() || !entry.isDirectory()) {
      throw new Error(`Bindings output parent '${current}' must be an existing regular directory without symbolic links.`);
    }
  }
}

async function tryLstat(path: string): Promise<BigIntStats | undefined> {
  try {
    return await lstat(path, { bigint: true });
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return undefined;
    throw error;
  }
}

function samePath(left: string, right: string): boolean {
  return process.platform === "win32"
    ? left.toLowerCase() === right.toLowerCase()
    : left === right;
}

function sameFile(left: BigIntStats, right: BigIntStats): boolean {
  return left.dev === right.dev && left.ino === right.ino;
}

function sameSnapshot(left: BigIntStats, right: BigIntStats): boolean {
  return sameFile(left, right)
    && left.size === right.size
    && left.mtimeNs === right.mtimeNs
    && left.ctimeNs === right.ctimeNs;
}

function sameOptionalSnapshot(left: BigIntStats | undefined, right: BigIntStats | undefined): boolean {
  return left === undefined ? right === undefined : right !== undefined && sameSnapshot(left, right);
}

void main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
