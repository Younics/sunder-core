import { existsSync } from "node:fs";
import { mkdtemp, mkdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { execNpm } from "./npm-process";

interface PackedServiceTools {
  readonly archives: readonly string[];
  readonly creator: string;
  dispose(): Promise<void>;
}

export async function disposePackedProject(root: string): Promise<void> {
  const esbuild = join(root, "node_modules/esbuild/lib/main.js");
  // Direct packed-tool consumers start this installation's native service in
  // the test process. Stop that exact owner before removing its Windows image.
  if (existsSync(esbuild)) await (require(esbuild) as typeof import("esbuild")).stop();
  // stop() submits process termination but does not await the OS image handle.
  await rm(root, { recursive: true, force: true, maxRetries: 3, retryDelay: 50 });
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
  const dispose = async () => {
    await disposePackedProject(join(root, "creator"));
    await rm(root, { recursive: true, force: true });
  };
  try {
    const packages = resolve(__dirname, "../../.."); const archives: string[] = [];
    for (const name of ["sdk", "package-tool", "create-sunder-package"]) {
      const result = await execNpm(["pack", "--ignore-scripts", "--json", "--pack-destination", root], { cwd: join(packages, name) });
      archives.push(join(root, (JSON.parse(result.stdout) as Array<{ filename: string }>)[0]!.filename));
    }
    const creator = join(root, "creator"); await mkdir(creator);
    await execNpm(["install", "--ignore-scripts", "--no-audit", "--no-fund", ...archives], { cwd: creator });
    return Object.freeze({ archives: Object.freeze(archives.slice(0, 2)), creator: join(creator, "node_modules/@younics/create-sunder-package/dist/cli.js"), dispose });
  } catch (error) {
    await dispose();
    throw error;
  }
}
