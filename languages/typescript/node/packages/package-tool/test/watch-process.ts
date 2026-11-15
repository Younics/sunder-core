import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { resolve } from "node:path";

export function startWatchCli(cli: string, root: string, environment = process.env): ChildProcessWithoutNullStreams {
  const arguments_ = [cli, "dev", "--watch", "--project", root];
  if (process.platform === "win32") arguments_.unshift(resolve(__dirname, "watch-cli-child.js"));
  return spawn(process.execPath, arguments_, {
    env: environment,
    stdio: process.platform === "win32" ? ["pipe", "pipe", "pipe", "ipc"] : "pipe",
  }) as ChildProcessWithoutNullStreams;
}

export function interruptWatchCli(child: ChildProcessWithoutNullStreams): void {
  // Node's Windows kill(SIGINT) uses TerminateProcess; it cannot test graceful
  // cleanup. Dispatch to the real CLI signal handler in this subprocess instead.
  // Unix keeps actual OS signal delivery. Neither path accepts a killed exit.
  if (process.platform === "win32") child.send("SIGINT");
  else child.kill("SIGINT");
}
