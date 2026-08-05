import { randomUUID } from "node:crypto";
import { createWriteStream } from "node:fs";
import { mkdir, rename, rm } from "node:fs/promises";
import { dirname } from "node:path";
import { Transform } from "node:stream";
import { pipeline } from "node:stream/promises";

export interface BoundedDownloadOptions {
  readonly label: string;
  readonly maximumBytes: number;
  readonly timeoutMilliseconds: number;
  readonly fetchImplementation?: typeof fetch;
}

export async function fetchBoundedText(url: string, options: BoundedDownloadOptions): Promise<string> {
  const { response, signal } = await fetchBoundedResponse(url, options);
  const chunks: Buffer[] = [];
  let length = 0;
  try {
    for await (const chunk of response.body!) {
      const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      length += bytes.byteLength;
      if (length > options.maximumBytes) throw byteLimit(options);
      chunks.push(bytes);
    }
    return Buffer.concat(chunks, length).toString("utf8");
  } catch (error) {
    await response.body?.cancel().catch(() => undefined);
    throw timeoutError(error, options, signal);
  }
}

export async function downloadBoundedFile(
  url: string,
  outputPath: string,
  options: BoundedDownloadOptions,
): Promise<void> {
  const { response, signal } = await fetchBoundedResponse(url, options);
  await mkdir(dirname(outputPath), { recursive: true });
  const temporaryPath = `${outputPath}.download-${randomUUID()}`;
  let length = 0;
  const meter = new Transform({
    transform(chunk: Buffer, _encoding, callback) {
      const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      length += bytes.byteLength;
      callback(length <= options.maximumBytes ? null : byteLimit(options), bytes);
    },
  });
  try {
    await pipeline(response.body!, meter, createWriteStream(temporaryPath, { flags: "wx" }), { signal });
    await replaceFile(temporaryPath, outputPath);
  } catch (error) {
    await rm(temporaryPath, { force: true });
    await response.body?.cancel().catch(() => undefined);
    throw timeoutError(error, options, signal);
  }
}

async function fetchBoundedResponse(
  url: string,
  options: BoundedDownloadOptions,
): Promise<{ readonly response: Response; readonly signal: AbortSignal }> {
  validateOptions(options);
  const signal = AbortSignal.timeout(options.timeoutMilliseconds);
  try {
    const response = await (options.fetchImplementation ?? fetch)(url, { redirect: "error", signal });
    if (!response.ok || response.body === null) {
      throw new Error(`${options.label} failed: HTTP ${response.status}.`);
    }
    const declaredLength = response.headers.get("content-length");
    if (declaredLength !== null
      && (!/^[0-9]+$/u.test(declaredLength) || Number(declaredLength) > options.maximumBytes)) {
      await response.body.cancel().catch(() => undefined);
      throw byteLimit(options);
    }
    return { response, signal };
  } catch (error) {
    throw timeoutError(error, options, signal);
  }
}

async function replaceFile(stagedPath: string, finalPath: string): Promise<void> {
  const backupPath = `${finalPath}.backup-${randomUUID()}`;
  let backedUp = false;
  try {
    try {
      await rename(finalPath, backupPath);
      backedUp = true;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
    }
    await rename(stagedPath, finalPath);
  } catch (error) {
    if (backedUp) await rename(backupPath, finalPath).catch(() => undefined);
    throw error;
  }
  if (backedUp) await rm(backupPath, { force: true }).catch(() => undefined);
}

function validateOptions(options: BoundedDownloadOptions): void {
  if (!Number.isSafeInteger(options.maximumBytes) || options.maximumBytes <= 0) {
    throw new RangeError("Download maximumBytes must be a positive safe integer.");
  }
  if (!Number.isSafeInteger(options.timeoutMilliseconds) || options.timeoutMilliseconds <= 0) {
    throw new RangeError("Download timeoutMilliseconds must be a positive safe integer.");
  }
}

function byteLimit(options: BoundedDownloadOptions): Error {
  return new Error(`${options.label} exceeds its ${options.maximumBytes}-byte limit.`);
}

function timeoutError(
  error: unknown,
  options: BoundedDownloadOptions,
  signal?: AbortSignal,
): Error {
  if (signal?.aborted === true || (error instanceof DOMException && error.name === "TimeoutError")) {
    return new Error(`${options.label} timed out after ${options.timeoutMilliseconds} milliseconds.`, { cause: error });
  }
  return error instanceof Error ? error : new Error(String(error));
}
