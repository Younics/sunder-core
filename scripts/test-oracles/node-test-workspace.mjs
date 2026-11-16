import { constants, cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, relative, resolve, sep } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { verifyCheckoutBytes } from "./checkout-bytes.mjs";

// Producers get their own source/dependency tree. Even npm workspace links must
// resolve into this copy: sharing node_modules wholesale resolves the SDK back to
// the checkout's mutable dist directory, defeating output isolation.
export function createNodeTestWorkspace(repositoryRoot, parent = tmpdir()) {
  verifyCheckoutBytes(repositoryRoot);
  const source = resolve(repositoryRoot, "languages/typescript/node");
  if (!existsSync(resolve(source, "node_modules"))) throw new Error("Install the locked Node workspace dependencies before running tests.");
  const workspaces = readdirSync(resolve(source, "packages"), { withFileTypes: true })
    .filter(entry => entry.isDirectory()).map(directory => ({
      directory: directory.name,
      name: JSON.parse(readFileSync(resolve(source, "packages", directory.name, "package.json"), "utf8")).name,
    }));
  const workspaceLinks = new Set(workspaces.map(({ name }) => resolve(source, "node_modules", name)));
  mkdirSync(parent, { recursive: true });
  const root = mkdtempSync(resolve(parent, "sunder-node-"));
  const nodeRoot = resolve(root, "languages/typescript/node");
  try {
    cpSync(source, nodeRoot, {
      recursive: true, verbatimSymlinks: true, mode: constants.COPYFILE_FICLONE,
      filter: path => {
        // Recreate these below: filtered cpSync turns Windows junctions into privileged symlinks.
        if (workspaceLinks.has(path)) return false;
        const parts = relative(source, path).split(sep);
        return parts.includes("node_modules") || !parts.some(part => ["dist", "dist-test", ".sunder"].includes(part));
      },
    });
    // Wire parity consumes authored contracts; packed fixtures share the owning
    // npm launcher rather than assuming npm is a native executable on Windows.
    for (const input of ["specs", "languages/csharp/dotnet/tests/Sunder.Runtime.Host.InteropWorker/Contracts", "scripts/npm-command.mjs"]) {
      cpSync(resolve(repositoryRoot, input), resolve(root, input), { recursive: true, mode: constants.COPYFILE_FICLONE });
    }
    for (const { directory, name } of workspaces) {
      const path = resolve(nodeRoot, "packages", directory);
      const link = resolve(nodeRoot, "node_modules", name);
      mkdirSync(dirname(link), { recursive: true });
      symlinkSync(path, link, process.platform === "win32" ? "junction" : "dir");
    }
    return { root, nodeRoot };
  } catch (error) {
    rmSync(root, { recursive: true, force: true });
    throw error;
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
  process.stdout.write(JSON.stringify(createNodeTestWorkspace(repositoryRoot, process.argv[2])) + "\n");
}
