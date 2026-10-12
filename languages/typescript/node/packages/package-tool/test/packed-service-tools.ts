import { execFile } from "node:child_process";
import { mkdtemp, mkdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { promisify } from "node:util";

interface PackedServiceTools {
  readonly archives: readonly string[];
  readonly creator: string;
  dispose(): Promise<void>;
}

// One lazy preparation per test-file scope, including concurrent callers. Each
// scenario still owns fresh consumer directories; only packed tools are shared.
export function createPackedServiceTools(prepare: () => Promise<PackedServiceTools> = packServiceTools) {
  let tools: Promise<PackedServiceTools> | undefined;
  return {
    get: () => tools ??= prepare(),
    dispose: async () => { await tools?.then(value => value.dispose(), () => {}); },
  };
}

async function packServiceTools(): Promise<PackedServiceTools> {
  const root = await mkdtemp(join(tmpdir(), "sunder-packed-services-"));
  const dispose = () => rm(root, { recursive: true, force: true });
  const exec = promisify(execFile);
  try {
    const packages = resolve(__dirname, "../../.."); const archives: string[] = [];
    for (const name of ["sdk", "package-tool", "create-sunder-package"]) {
      const result = await exec("npm", ["pack", "--ignore-scripts", "--json", "--pack-destination", root], { cwd: join(packages, name) });
      archives.push(join(root, (JSON.parse(result.stdout) as Array<{ filename: string }>)[0]!.filename));
    }
    const creator = join(root, "creator"); await mkdir(creator);
    await exec("npm", ["install", "--ignore-scripts", "--no-audit", "--no-fund", ...archives], { cwd: creator });
    return Object.freeze({ archives: Object.freeze(archives.slice(0, 2)), creator: join(creator, "node_modules/create-sunder-package/dist/cli.js"), dispose });
  } catch (error) {
    await dispose();
    throw error;
  }
}
