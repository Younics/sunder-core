import { spawn } from "node:child_process";
import { stat } from "node:fs/promises";
import { resolve } from "node:path";
import { currentRid, type SunderRid } from "./model";
import type { RegistryOriginOptions } from "./rpc-contract-lock";
import { revalidateOwnedRpc } from "./rpc-authoring-plugin";
import {
  assertReplaceableGeneratedDirectory,
  cleanupStagedOutputs,
  commitStagedOutputs,
  stageGeneratedDirectory,
  withOutputLocks,
} from "./output";
import {
  NODE_EXECUTION_RUNTIME,
  buildDevPackage,
  bundleProductionWithGeneratedRpcBindings,
  prepareProject,
  writeTargetLeaf,
} from "./package";
import {
  generateRpcBindingsForLoadedProjectLocked,
  withRpcBindingsOutputLock,
} from "./rpc-bindings";

export interface ModuleBuildOptions extends RegistryOriginOptions {
  readonly projectPath: string;
  readonly rid?: SunderRid;
}

export async function buildModuleTarget(
  options: ModuleBuildOptions,
): Promise<{ leafPath: string; modulePath: string; bytes: number }> {
  const rid = options.rid ?? currentRid();
  const project = await prepareProject(options.projectPath, options);
  return await withRpcBindingsOutputLock(project.root, async () => {
    await generateRpcBindingsForLoadedProjectLocked(project);
    const workRoot = resolve(project.outputRoot, ".sunder-module", rid);
    return await withOutputLocks([workRoot], async () => {
      await assertReplaceableGeneratedDirectory(workRoot, true);
      let leafPath = "";
      let bytes = 0;
      const staged = await stageGeneratedDirectory(
        workRoot,
        "Sunder Node module work output v1\n",
        async (stagingRoot) => {
          const modulePath = resolve(stagingRoot, "worker.cjs");
          const workerNoticePath = await bundleProductionWithGeneratedRpcBindings(project, rid, modulePath);
          bytes = (await stat(modulePath)).size;
          leafPath = await writeTargetLeaf({
            project,
            rid,
            modulePath,
            workerNoticePath,
            discoveryRoot: resolve(project.outputRoot, "targets"),
          });
        },
      );
      const stagedOutputs = [staged.output, staged.marker];
      try {
        await revalidateOwnedRpc(project);
        await commitStagedOutputs(stagedOutputs);
      } finally {
        await cleanupStagedOutputs(stagedOutputs);
      }
      const modulePath = resolve(workRoot, "worker.cjs");
      process.stdout.write(`Sunder Worker module size (${rid}): ${bytes} bytes\n`);
      return { leafPath, modulePath, bytes };
    });
  });
}

export async function smokeModuleTarget(projectPath: string, options: RegistryOriginOptions = {}): Promise<void> {
  await buildDevPackage({ ...options, projectPath, watch: false });
  const project = await prepareProject(projectPath, options);
  const devRoot = resolve(project.outputRoot, "sunder-dev");
  const cli = process.env.SUNDER_CLI ?? "sunder";
  try {
    await runChecked(cli, ["dev", "package", "smoke", devRoot], project.root);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    throw new Error(
      `${message}\nEnsure a trusted Node runtime first with: sunder runtime ensure ${NODE_EXECUTION_RUNTIME.id} "${NODE_EXECUTION_RUNTIME.versionRange}" ${NODE_EXECUTION_RUNTIME.moduleAbi}`,
      { cause: error },
    );
  }
  process.stdout.write("Sunder runtime-backed Node Worker smoke passed.\n");
}

async function runChecked(file: string, argumentsList: readonly string[], cwd: string): Promise<void> {
  await new Promise<void>((resolvePromise, reject) => {
    const child = spawn(file, [...argumentsList], { cwd, stdio: "inherit", windowsHide: true });
    child.once("error", (error) => reject(new Error(
      (error as NodeJS.ErrnoException).code === "ENOENT"
        ? `Sunder CLI '${file}' was not found. Install it or set SUNDER_CLI to its executable path.`
        : `Sunder CLI could not start: ${error.message}`,
      { cause: error },
    )));
    child.once("exit", (code, signal) => {
      if (code === 0) resolvePromise();
      else reject(new Error(`Sunder managed Runtime smoke exited with ${code ?? signal}.`));
    });
  });
}
