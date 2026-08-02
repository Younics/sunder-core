import { randomUUID } from "node:crypto";
import { access, mkdir, rename, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import {
  assertReplaceableGeneratedDirectory,
  cleanupStagedOutputs,
  markerPath,
  stageGeneratedDirectory,
  withOutputLocks,
} from "../src/output";

async function main(): Promise<void> {
  const [mode, rawOutputPath, readyPath, releasePath] = process.argv.slice(2);
  if (mode === undefined || rawOutputPath === undefined) throw new Error("output-child requires a mode and output path.");
  const outputPath = resolve(rawOutputPath);
  if (mode === "hold") {
    if (readyPath === undefined || releasePath === undefined) throw new Error("hold requires ready and release paths.");
    await withOutputLocks([outputPath], async () => {
      await writeSignal(readyPath);
      while (!await isPresent(releasePath)) await delay(20);
    });
    return;
  }
  if (mode === "crash-swap") {
    if (readyPath === undefined) throw new Error("crash-swap requires a ready path.");
    await withOutputLocks([outputPath], async () => {
      await assertReplaceableGeneratedDirectory(outputPath);
      const staged = await stageGeneratedDirectory(
        outputPath,
        "Sunder output child replacement\n",
        async (stagingPath) => {
          await writeFile(resolve(stagingPath, "replacement.txt"), "replacement\n", "utf8");
        },
      );
      const outputs = [staged.output, staged.marker];
      try {
        const token = randomUUID();
        const transactionPath = `${outputPath}.sunder-output-transaction.json`;
        const transactionOutputs = outputs.map((output) => ({
          finalPath: output.finalPath,
          stagedPath: output.stagedPath,
          backupPath: `${output.finalPath}.backup-${token}`,
          existed: true,
        }));
        await writeFile(transactionPath, `${JSON.stringify({
          schemaVersion: 1,
          token,
          coordinatorPath: outputPath,
          outputs: transactionOutputs,
        }, null, 2)}\n`, "utf8");
        await rename(outputPath, transactionOutputs[0]!.backupPath);
        await rename(markerPath(outputPath), transactionOutputs[1]!.backupPath);
        await writeSignal(readyPath);
        while (true) await delay(1_000);
      } finally {
        await cleanupStagedOutputs(outputs);
      }
    });
    return;
  }
  if (mode === "recover") {
    await withOutputLocks([outputPath], async () => undefined);
    return;
  }
  throw new Error(`Unknown output-child mode '${mode}'.`);
}

async function writeSignal(path: string): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, "ready\n", "utf8");
}

async function isPresent(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

async function delay(milliseconds: number): Promise<void> {
  await new Promise((resolvePromise) => setTimeout(resolvePromise, milliseconds));
}

void main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.stack ?? error.message : String(error)}\n`);
  process.exitCode = 1;
});
