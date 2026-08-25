import { spawnSync } from "node:child_process";
import { existsSync, statSync } from "node:fs";

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
    for (const prerequisite of test.externalPrerequisites) {
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
        const key = `${prerequisite.name}:${prerequisite.minimumMajorVersion ?? ""}`;
        let result = executableResults.get(key);
        if (result === undefined) {
          result = executableProbe(prerequisite.name, prerequisite.minimumMajorVersion);
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

function probeExecutable(name, minimumMajorVersion) {
  const argument = name === "dotnet" ? "--version" : "--version";
  const result = spawnSync(name, [argument], { encoding: "utf8" });
  if (result.error !== undefined || result.status !== 0) {
    return { available: false, reason: `executable ${name} could not be started` };
  }
  if (minimumMajorVersion !== undefined) {
    const match = /v?(\d+)/u.exec(result.stdout.trim());
    if (match === null || Number(match[1]) < minimumMajorVersion) {
      return { available: false, reason: `executable ${name} is below required major version ${minimumMajorVersion}` };
    }
  }
  return { available: true };
}
