import { spawnSync } from "node:child_process";
import { statSync } from "node:fs";
import { posix, win32, resolve } from "node:path";
import { pathToFileURL } from "node:url";

// Resolve argv, never shell text. Windows npm may be an executable shim or the
// official npm.cmd; run the latter's JS entry point with this selected Node.
export function npmCommand(arguments_, options = {}) {
  const windows = (options.platform ?? process.platform) === "win32";
  const path = windows ? win32 : posix;
  const environment = options.environment ?? process.env;
  const fileProbe = options.fileProbe ?? isFile;
  const configured = environment.SUNDER_NPM_EXECUTABLE;
  const searchPath = Object.entries(environment).find(([key]) => windows ? key.toLowerCase() === "path" : key === "PATH")?.[1] ?? "";
  const candidates = configured !== undefined ? [configured]
    : searchPath.split(windows ? ";" : ":").flatMap(directory => {
      const value = directory.replace(/^"(.*)"$/u, "$1");
      return path.isAbsolute(value) ? (windows ? ["npm.exe", "npm.cmd"] : ["npm"]).map(name => path.join(value, name)) : [];
    });
  if (configured !== undefined && !path.isAbsolute(configured)) {
    throw new Error("SUNDER_NPM_EXECUTABLE must name an absolute npm executable or npm-cli.js path.");
  }
  const executable = candidates.find(fileProbe);
  if (executable === undefined) throw new Error("npm is unavailable; add it to PATH or set SUNDER_NPM_EXECUTABLE to an absolute path.");
  const extension = path.extname(executable).toLowerCase();
  if (extension === ".cmd" || extension === ".bat" || extension === ".ps1") {
    const cli = path.join(path.dirname(executable), "node_modules", "npm", "bin", "npm-cli.js");
    if (!fileProbe(cli)) throw new Error(`npm launcher has no adjacent npm-cli.js: ${executable}. Set SUNDER_NPM_EXECUTABLE explicitly.`);
    return { command: options.nodeExecutable ?? process.execPath, arguments: [cli, ...arguments_] };
  }
  return extension === ".js" || extension === ".cjs" || extension === ".mjs"
    ? { command: options.nodeExecutable ?? process.execPath, arguments: [executable, ...arguments_] }
    : { command: executable, arguments: [...arguments_] };
}

function isFile(path) {
  try { return statSync(path).isFile(); }
  catch { return false; }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const invocation = npmCommand(process.argv.slice(2));
  const result = spawnSync(invocation.command, invocation.arguments, { stdio: "inherit" });
  if (result.error) throw result.error;
  process.exitCode = result.status ?? 1;
}
