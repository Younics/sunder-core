import { watch } from "node:fs";
import { readFile, readdir } from "node:fs/promises";
import { homedir } from "node:os";
import { basename, dirname, resolve } from "node:path";

export interface ObservableOperation<T> {
  readonly attempting: Promise<void>;
  readonly completion: Promise<T>;
}

export function startOutputLockContender<T>(
  outputPath: string,
  operation: () => Promise<T>,
  diagnostics: () => string,
  timeoutMilliseconds = 20_000,
): ObservableOperation<T> {
  const lockPrefix = `${basename(outputPath)}.sunder-output.lock.transition`;
  let signal!: () => void;
  const observed = new Promise<void>((resolvePromise) => { signal = resolvePromise; });
  const watcher = watch(dirname(outputPath), { persistent: false }, (_event, fileName) => {
    if (fileName?.toString().startsWith(lockPrefix) === true) signal();
  });
  const attempting = withWatchdog(
    observed,
    timeoutMilliseconds,
    `output-lock transition for '${outputPath}'`,
    diagnostics,
  ).finally(() => watcher.close());
  const completion = Promise.resolve().then(operation);
  return { attempting, completion };
}

export async function assertPendingAfterTurn(operation: Promise<unknown>, diagnostics: () => string): Promise<void> {
  let outcome: "pending" | "resolved" | "rejected" = "pending";
  let rejection: unknown;
  void operation.then(
    () => { outcome = "resolved"; },
    (error: unknown) => { outcome = "rejected"; rejection = error; },
  );
  await immediate();
  await immediate();
  if (outcome !== "pending") {
    const detail = outcome === "rejected" ? `: ${String(rejection)}` : "";
    throw new Error(`Expected operation to remain pending, but it ${outcome}${detail}. ${diagnostics()}`);
  }
}

export async function observeOutputLockAttempt(
  outputPath: string,
  trigger: () => Promise<unknown>,
  diagnostics: () => string,
): Promise<void> {
  const lockPrefix = `${basename(outputPath)}.sunder-output.lock`;
  let signal!: () => void;
  const observed = new Promise<void>((resolvePromise) => { signal = resolvePromise; });
  const watcher = watch(dirname(outputPath), { persistent: false }, (_event, fileName) => {
    if (fileName?.toString().startsWith(lockPrefix) === true) signal();
  });
  try {
    const triggered = trigger();
    await withWatchdog(observed, 20_000, `output-lock attempt for '${outputPath}'`, diagnostics);
    await triggered;
  } finally {
    watcher.close();
  }
}

export async function waitForProcessOutputLockAttempt(
  pid: number | undefined,
  diagnostics: () => string,
): Promise<void> {
  if (pid === undefined) throw new Error("Output-lock contender has no process id.");
  const markerRoot = resolve(homedir(), ".sunder", "generated-output", "processes");
  const deadline = Date.now() + 20_000;
  while (Date.now() < deadline) {
    try {
      for (const name of await readdir(markerRoot)) {
        if (!name.endsWith(".json")) continue;
        try {
          const marker = JSON.parse(await readFile(resolve(markerRoot, name), "utf8")) as { readonly pid?: unknown };
          if (marker.pid === pid) return;
        } catch {
          // A concurrently published or removed marker is retried until the watchdog.
        }
      }
    } catch {
      // The marker root is created lazily by the first contender.
    }
    await new Promise<void>((resolvePromise) => setTimeout(resolvePromise, 25));
  }
  throw new Error(`Timed out waiting for process ${pid} to attempt an output lock. ${diagnostics()}`);
}

async function withWatchdog(
  operation: Promise<void>,
  milliseconds: number,
  label: string,
  diagnostics: () => string,
): Promise<void> {
  let timer: NodeJS.Timeout | undefined;
  try {
    await Promise.race([
      operation,
      new Promise<never>((_resolve, reject) => {
        timer = setTimeout(() => reject(new Error(`Timed out waiting for ${label}. ${diagnostics()}`)), milliseconds);
      }),
    ]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}

async function immediate(): Promise<void> {
  await new Promise<void>((resolvePromise) => setImmediate(resolvePromise));
}
