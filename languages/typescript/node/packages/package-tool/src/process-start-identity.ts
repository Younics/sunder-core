import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { promisify } from "node:util";

// Bootstrap is paid once, before the contention wait starts. In particular, a
// cold Windows PowerShell is not a five-second lock-owner probe. Callers still
// bound the complete tool process; do not retry whole build/publish operations.
export const PROCESS_IDENTITY_BOOTSTRAP_MILLISECONDS = 30_000;
const OWNER_PROBE_MILLISECONDS = 5_000;
const execFileAsync = promisify(execFile);

export type ProcessStartIdentity =
  | { readonly state: "known"; readonly identity: string }
  | { readonly state: "absent" }
  | { readonly state: "unknown"; readonly diagnostic: string };

export class ProcessStartIdentityError extends Error {}

/** Internal dependency boundary, also used to exercise Windows failures off Windows. */
export function createProcessStartIdentityProbe(dependencies = {
  platform: process.platform,
  pid: process.pid,
  environment: process.env,
  execute: async (file: string, args: string[], timeout: number) => execFileAsync(file, args, {
    encoding: "utf8", timeout, windowsHide: true, maxBuffer: 4_096,
    env: process.platform === "darwin" ? { ...process.env, LANG: "C", LC_ALL: "C", TZ: "UTC" } : process.env,
  }),
  read: (path: string) => readFile(path, "utf8"),
  exists: processExists,
  now: () => performance.now(),
}) {
  let current: Promise<string> | undefined;

  async function probe(pid: number, timeout = OWNER_PROBE_MILLISECONDS): Promise<ProcessStartIdentity> {
    const started = dependencies.now();
    let executable = "process metadata";
    try {
      if (!Number.isSafeInteger(pid) || pid <= 0) throw new Error("Invalid process id.");
      if (timeout <= 0) throw new Error("The lock-owner probe deadline expired.");
      if (dependencies.platform === "linux") {
        const [stat, boot] = await Promise.all([
          dependencies.read(`/proc/${pid}/stat`), dependencies.read("/proc/sys/kernel/random/boot_id"),
        ]);
        const closing = stat.lastIndexOf(")");
        const ticks = closing < 0 ? undefined : stat.slice(closing + 1).trim().split(/\s+/u)[19];
        const bootId = boot.trim().toLowerCase();
        if (ticks === undefined || !/^\d+$/u.test(ticks) || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/u.test(bootId)) {
          throw new Error("Invalid Linux process-start metadata.");
        }
        return { state: "known", identity: `linux:${bootId}:${ticks}` };
      }
      if (dependencies.platform === "win32") {
        const systemRoot = dependencies.environment.SystemRoot ?? dependencies.environment.SYSTEMROOT ?? dependencies.environment.WINDIR;
        if (systemRoot === undefined || !/^[a-z]:[\\/]/iu.test(systemRoot)) {
          throw new Error("SystemRoot must identify the Windows system directory.");
        }
        // Do not resolve a package-controlled powershell.exe shim through PATH.
        executable = `${systemRoot.replace(/[\\/]$/u, "")}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe`;
        const script = [
          "$ErrorActionPreference = 'Stop'",
          `$p = [System.Diagnostics.Process]::GetProcessById(${pid})`,
          "$p.StartTime.ToUniversalTime().Ticks.ToString([System.Globalization.CultureInfo]::InvariantCulture)",
        ].join("; ");
        const { stdout } = await dependencies.execute(executable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script], timeout);
        const ticks = stdout.trim();
        // Keep the exact .NET ticks as text: a JS number would lose precision.
        if (!/^\d{1,19}$/u.test(ticks) || BigInt(ticks) <= 0n || BigInt(ticks) > 3155378975999999999n) {
          throw new Error(`Invalid Windows process-start output: ${boundedText(stdout)}`);
        }
        return { state: "known", identity: `windows:${ticks}` };
      }
      if (dependencies.platform === "darwin") {
        executable = "/bin/ps";
        const { stdout } = await dependencies.execute(executable, ["-p", String(pid), "-o", "lstart="], timeout);
        const value = Date.parse(`${stdout.trim()} UTC`);
        if (!Number.isFinite(value)) throw new Error(`Invalid macOS process-start output: ${boundedText(stdout)}`);
        return { state: "known", identity: `darwin:${new Date(Math.floor(value / 1_000) * 1_000).toISOString()}` };
      }
      throw new Error("Unsupported process identity platform.");
    } catch (error) {
      // Failure to inspect a process is never evidence that its PID is dead.
      if (dependencies.exists(pid) === false) return { state: "absent" };
      const details = error as Error & { code?: unknown; signal?: unknown; killed?: unknown; stdout?: unknown; stderr?: unknown };
      return { state: "unknown", diagnostic: `${executable}; elapsed=${Math.round(dependencies.now() - started)}ms; budget=${timeout}ms; `
        + `code=${boundedText(details.code)}; signal=${boundedText(details.signal)}; killed=${details.killed === true}; `
        + `${boundedText(details.message)}; stdout=${boundedText(details.stdout)}; stderr=${boundedText(details.stderr)}` };
    }
  }

  function currentIdentity(): Promise<string> {
    if (current !== undefined) return current;
    const pending = probe(dependencies.pid, PROCESS_IDENTITY_BOOTSTRAP_MILLISECONDS).then((result) => {
      if (result.state !== "known") {
        throw new ProcessStartIdentityError("Could not establish the current process start identity for generated output locking. "
          + (result.state === "unknown" ? result.diagnostic : "The current process was reported absent."));
      }
      return result.identity;
    });
    current = pending;
    // A successful identity is immutable. A failed initialization has published
    // no ownership evidence and must not poison every later operation/watch turn.
    void pending.catch(() => { if (current === pending) current = undefined; });
    return pending;
  }

  return {
    currentIdentity,
    ownerIdentity: (pid: number, remainingMilliseconds = OWNER_PROBE_MILLISECONDS) =>
      probe(pid, Math.min(OWNER_PROBE_MILLISECONDS, remainingMilliseconds)),
  };
}

function boundedText(value: unknown): string {
  return (typeof value === "string" || typeof value === "number" ? String(value) : "<none>")
    .replace(/[\x00-\x1f\x7f]/gu, " ").slice(0, 512);
}

function processExists(pid: number): boolean | undefined {
  try { process.kill(pid, 0); return true; }
  catch (error) {
    const code = (error as NodeJS.ErrnoException).code;
    return code === "ESRCH" ? false : code === "EPERM" ? true : undefined;
  }
}
