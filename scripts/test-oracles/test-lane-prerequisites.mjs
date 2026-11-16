import { spawnSync } from "node:child_process";
import { existsSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";

export function currentOperatingSystem(platform = process.platform) {
  if (platform === "win32") return "Windows";
  if (platform === "linux") return "Linux";
  if (platform === "darwin") return "MacOS";
  throw new Error(`Unsupported test operating system '${platform}'.`);
}

export function missingPrerequisites(tests, options = {}) {
  const environment = options.environment ?? process.env;
  const executableProbe = options.executableProbe ?? probeExecutable;
  const fileProbe = options.fileProbe ?? ((path) => probePath(path, "file"));
  const directoryProbe = options.directoryProbe ?? ((path) => probePath(path, "directory"));
  const executableResults = new Map();
  const missing = new Map();
  for (const test of tests) {
    for (const prerequisite of test.externalPrerequisites ?? []) {
      if (prerequisite.requiredWhenSelected !== true) continue;
      let reason;
      if (prerequisite.kind === "environment") {
        const value = environment[prerequisite.name];
        if (prerequisite.expectedValue !== undefined && value !== prerequisite.expectedValue) {
          reason = `environment variable ${prerequisite.name} must equal '${prerequisite.expectedValue}' exactly (observed ${value === undefined ? "absent" : JSON.stringify(value)})`;
        } else if (value?.trim() === "") reason = `environment variable ${prerequisite.name} is empty`;
        else if (value === undefined) reason = `environment variable ${prerequisite.name} is absent`;
      } else if (prerequisite.kind === "environmentPath") {
        const value = environment[prerequisite.name];
        if (value === undefined) reason = `environment path variable ${prerequisite.name} is absent`;
        else if (value.trim() === "") reason = `environment path variable ${prerequisite.name} is empty`;
        else if (prerequisite.pathKind === "file" && !fileProbe(value)) reason = `file from ${prerequisite.name} is absent or not a regular file: ${value}`;
        else if (prerequisite.pathKind === "directory" && !directoryProbe(value)) reason = `directory from ${prerequisite.name} is absent or not a directory: ${value}`;
        else if (!['file', 'directory'].includes(prerequisite.pathKind)) reason = `environment path variable ${prerequisite.name} has unknown path kind ${prerequisite.pathKind}`;
      } else if (prerequisite.kind === "executable") {
        const key = `${prerequisite.name}:${prerequisite.minimumMajorVersion ?? ""}:${prerequisite.minimumVersion ?? ""}`;
        let result = executableResults.get(key);
        if (result === undefined) {
          result = executableProbe(prerequisite, environment);
          executableResults.set(key, result);
        }
        if (!result.available) reason = result.reason;
      } else if (prerequisite.kind === "file") {
        if (!fileProbe(prerequisite.path)) reason = `file ${prerequisite.path} is absent`;
      } else if (prerequisite.kind === "directory") {
        if (!directoryProbe(prerequisite.path)) reason = `directory ${prerequisite.path} is absent`;
      } else {
        reason = `unknown prerequisite kind ${prerequisite.kind}`;
      }
      if (reason !== undefined) {
        const key = `${prerequisite.kind}:${prerequisite.name ?? prerequisite.path}:${prerequisite.pathKind ?? ""}`;
        const current = missing.get(key) ?? { prerequisite, reason, tests: [] };
        current.tests.push(test.id);
        missing.set(key, current);
      }
    }
  }
  return [...missing.values()];
}

function probePath(path, kind) {
  if (!existsSync(path)) return false;
  try {
    const status = statSync(path);
    return kind === "file" ? status.isFile() : status.isDirectory();
  } catch {
    return false;
  }
}

function probeExecutable({ name, minimumMajorVersion, minimumVersion }, environment) {
  const result = name === "bash"
    ? spawnSync("python3", ["-B", fileURLToPath(new URL("../ci/bash_runtime.py", import.meta.url))], { encoding: "utf8", env: environment, timeout: 20000 })
    : spawnSync(name, ["--version"], { encoding: "utf8", env: environment, timeout: 15000 });
  if (result.error !== undefined || result.status !== 0) {
    return { available: false, reason: `executable ${name} could not be started` };
  }
  if (minimumMajorVersion !== undefined || minimumVersion !== undefined) {
    const required = minimumVersion ?? String(minimumMajorVersion);
    if (!/^\d+(?:\.\d+){0,2}$/u.test(required)) return { available: false, reason: `invalid minimum version for executable ${name}: ${required}` };
    const match = /\b(?:v)?(\d+(?:\.\d+){0,2})/u.exec(result.stdout.trim());
    const expected = required.split(".").map(Number);
    const actual = match?.[1].split(".").map(Number);
    const difference = actual === undefined ? -1 : expected.map((part, index) => (actual[index] ?? 0) - part).find(value => value !== 0) ?? 0;
    if (difference < 0) {
      return { available: false, reason: `executable ${name} is below required ${minimumVersion === undefined ? "major version" : "version"} ${required}` };
    }
  }
  return { available: true };
}
