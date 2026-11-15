import { execFile, type ExecFileOptions } from "node:child_process";
import { resolve } from "node:path";
import { promisify } from "node:util";

const execute = promisify(execFile);
const { npmCommand } = require(resolve(__dirname, "../../../../../../../scripts/npm-command.mjs")) as {
  npmCommand(arguments_: readonly string[]): { command: string; arguments: string[] };
};

export function execNpm(arguments_: readonly string[], options: ExecFileOptions = {}) {
  const invocation = npmCommand(arguments_);
  return execute(invocation.command, invocation.arguments, { ...options, encoding: "utf8" });
}
