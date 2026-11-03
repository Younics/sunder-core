import { execFile, spawn } from "node:child_process";
import { closeSync, mkdirSync, openSync, readFileSync, statfsSync, writeFileSync, writeSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import { performance } from "node:perf_hooks";
import { setTimeout as delay } from "node:timers/promises";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";

const executeFile = promisify(execFile);
const windowsSupervisor = fileURLToPath(new URL("./run-owned-test-process.ps1", import.meta.url));
export const maximumProcessTimeoutMilliseconds = 2 ** 31 - 1 - 30_000;

// Test tooling only: keep a timed-out launcher from leaving build/test processes
// racing the next project. Windows uses a non-breakaway Job Object, not a PID scan.
export async function runOwnedTestProcess(command, args, {
  cwd, logStem, timeoutMilliseconds, maximumOutputBytes = 128 * 1024 * 1024, streamOutput = true,
  cancellationSignal,
}) {
  if (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds <= 0 || timeoutMilliseconds > maximumProcessTimeoutMilliseconds) {
    throw new Error(`Test command timeout must be between 1 and ${maximumProcessTimeoutMilliseconds} ms.`);
  }
  mkdirSync(dirname(logStem), { recursive: true });
  const startedAt = new Date().toISOString();
  const started = performance.now();
  const memoryBefore = linuxMemory();
  const diskBefore = diskSpace([cwd, dirname(logStem), process.env.RUNNER_TEMP ?? tmpdir()]);
  // A killed/crashed reporter must leave a failed receipt even if a surviving
  // testhost subsequently writes a passing TRX. Complete only after cleanup.
  writeFileSync(`${logStem}.process.json`, `${JSON.stringify({ command, args, startedAt, timeoutMilliseconds,
    status: null, signal: null, error: { code: "EINCOMPLETE", message: "Test command has not completed cleanup." },
    cleanupComplete: false, memoryBefore, diskBefore }, null, 2)}\n`, { flag: "wx" });
  const output = openSync(`${logStem}.stdout.log`, "wx");
  const errors = openSync(`${logStem}.stderr.log`, "wx");
  const nativeResult = `${logStem}.native.json`;
  const cancelPath = `${logStem}.cancel`;
  const windows = process.platform === "win32";
  if (windows) {
    writeFileSync(`${logStem}.command.json`, JSON.stringify({ command, args, cwd, timeoutMilliseconds, cancelPath }));
  }
  let child;
  let commandPid = null;
  let status = null;
  let signal = null;
  let error = null;
  let cleanupComplete = false;
  let outputBytes = 0;
  let endWait;
  const interrupted = new Promise((resolve) => { endWait = resolve; });
  const interrupt = (code, message) => {
    if (error !== null) return;
    error = { code, message };
    if (windows) {
      try { writeFileSync(cancelPath, code); }
      catch (failure) { error.message += ` Cancellation request could not be written: ${failure.message}`; }
    }
    endWait();
  };
  const onInterrupt = () => interrupt("ECANCELED", "Test command cancelled by the runner signal.");
  const capture = (descriptor, destination) => (chunk) => {
    outputBytes += chunk.length;
    if (error !== null) return;
    if (outputBytes > maximumOutputBytes) {
      interrupt("ENOBUFS", `Test command exceeded ${maximumOutputBytes} captured output bytes.`);
      return;
    }
    try {
      writeSync(descriptor, chunk);
      if (streamOutput) destination.write(chunk);
    } catch (failure) {
      // A full/unwritable results filesystem must still go through owned-tree
      // cleanup, not throw from the stream callback and orphan the testhost.
      interrupt(failure.code ?? "EOUTPUT", `Test command output could not be retained: ${failure.message}`);
    }
  };
  let timer;
  try {
    child = spawn(windows ? "pwsh" : command, windows
      ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", windowsSupervisor,
        "-RequestPath", `${logStem}.command.json`, "-ResultPath", nativeResult]
      : args, { cwd, detached: !windows, stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
    commandPid = windows ? null : child.pid ?? null;
    child.stdout.on("data", capture(output, process.stdout));
    child.stderr.on("data", capture(errors, process.stderr));
    const exited = new Promise((resolve) => {
      child.once("error", (failure) => {
        error ??= { code: failure.code ?? "ESPAWN", message: failure.message };
        resolve();
      });
      child.once("exit", (code, receivedSignal) => { status = code; signal = receivedSignal; resolve(); });
    });
    const closed = new Promise((resolve) => child.once("close", resolve));
    process.once("SIGINT", onInterrupt);
    process.once("SIGTERM", onInterrupt);
    cancellationSignal?.addEventListener("abort", onInterrupt, { once: true });
    if (cancellationSignal?.aborted) onInterrupt();
    // Windows enforces the actual deadline inside its job owner, leaving this
    // extra bound only for a failed supervisor/cleanup. Never advance on that path.
    timer = setTimeout(() => interrupt("ETIMEDOUT", `Test command exceeded ${timeoutMilliseconds} ms.`),
      timeoutMilliseconds + (windows ? 30_000 : 0));
    await Promise.race([exited, interrupted]);
    clearTimeout(timer);
    if (windows) {
      if (error !== null) {
        await settlesWithin(closed, 15_000);
        if (child.exitCode === null && child.signalCode === null) child.kill("SIGKILL");
      }
      try {
        const native = JSON.parse(readFileSync(nativeResult, "utf8"));
        if (status !== 0 || signal !== null) {
          error ??= { code: "ESUPERVISOR", message: `Windows job owner failed: status=${status}; signal=${signal}.` };
        }
        commandPid = native.ProcessId;
        status = native.Status;
        if (native.Error !== null) error ??= { code: native.Error, message: native.Message };
        cleanupComplete = native.CleanupComplete === true;
      } catch (failure) {
        error ??= { code: "ESUPERVISOR", message: `Windows job owner produced no valid result: ${failure.message}` };
      }
    } else if (child.pid === undefined) {
      cleanupComplete = true; // spawn failed: no process group was created.
    } else {
      try {
        process.kill(-child.pid, "SIGKILL");
      } catch (failure) {
        if (failure.code !== "ESRCH") throw failure;
      }
      const deadline = performance.now() + 10_000;
      do {
        const { stdout } = await executeFile("ps", ["-axo", "pid=,pgid=,stat="], { timeout: 2_000, killSignal: "SIGKILL" });
        // Reparented zombies cannot execute or hold outputs; init owns their
        // eventual reap. The direct child is still reaped through Node's exit.
        const live = stdout.trim().split("\n").some((line) => {
          const [, group, state] = line.trim().split(/\s+/u);
          return Number(group) === child.pid && !state.startsWith("Z");
        });
        if (!live) { cleanupComplete = true; break; }
        await delay(50);
      } while (performance.now() < deadline);
    }
    const drained = await settlesWithin(closed, 2_000);
    if (!drained) {
      error ??= { code: "EOUTPUTDRAIN", message: "Owned command output did not close after cleanup." };
      cleanupComplete = false;
    }
  } catch (failure) {
    error ??= { code: failure.code ?? "ECLEANUP", message: failure.message };
  } finally {
    clearTimeout(timer);
    process.removeListener("SIGINT", onInterrupt);
    process.removeListener("SIGTERM", onInterrupt);
    cancellationSignal?.removeEventListener("abort", onInterrupt);
    child?.stdout.destroy();
    child?.stderr.destroy();
    closeSync(output);
    closeSync(errors);
  }
  const result = { command, args, startedAt, elapsedMilliseconds: Math.round(performance.now() - started),
    timeoutMilliseconds, pid: child?.pid ?? null, commandPid, status, signal, error, cleanupComplete, outputBytes,
    memoryBefore, memoryAfter: linuxMemory(), diskBefore, diskAfter: diskSpace([cwd, dirname(logStem), process.env.RUNNER_TEMP ?? tmpdir()]) };
  writeFileSync(`${logStem}.process.json`, `${JSON.stringify(result, null, 2)}\n`);
  return result;
}

async function settlesWithin(promise, milliseconds) {
  let timer;
  try {
    return await Promise.race([promise.then(() => true), new Promise((resolve) => { timer = setTimeout(() => resolve(false), milliseconds); })]);
  } finally {
    clearTimeout(timer);
  }
}

function linuxMemory() {
  if (process.platform !== "linux") return null;
  const read = (path) => { try { return readFileSync(path, "utf8").trim(); } catch { return null; } };
  const membership = read("/proc/self/cgroup");
  const group = membership?.split("\n").find((line) => line.startsWith("0::"))?.slice(3);
  const cgroup = resolve("/sys/fs/cgroup", `.${group ?? "/"}`);
  return { meminfo: read("/proc/meminfo"), cgroup,
    current: read(`${cgroup}/memory.current`), peak: read(`${cgroup}/memory.peak`),
    max: read(`${cgroup}/memory.max`), events: read(`${cgroup}/memory.events`) };
}

function diskSpace(paths) {
  return [...new Set(paths)].map((path) => {
    try {
      const space = statfsSync(path);
      return { path, availableBytes: space.bavail * space.bsize, totalBytes: space.blocks * space.bsize };
    } catch { return { path, availableBytes: null, totalBytes: null }; }
  });
}
