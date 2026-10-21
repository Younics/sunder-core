import { readFile, writeFile } from "node:fs/promises";
import { withOutputLeases } from "../src/output";

async function main(): Promise<void> {
  const [mode, output, ready, release, timeout] = process.argv.slice(2);
  if (output === undefined || ready === undefined) throw new Error("Lock probe requires output and ready paths.");
  await withOutputLeases([output], async () => {
    await writeFile(ready, await readFile(`${output}.sunder-output.lock/owner.json`));
    if (mode === "abandon") process.exit(23); // No finally: leave authentic recoverable ownership evidence.
    if (mode === "hold") {
      while (true) {
        try { await readFile(release!); break; } catch { await new Promise((done) => setTimeout(done, 20)); }
      }
    }
  }, timeout === undefined ? undefined : Number(timeout));
}

void main().catch((error: unknown) => {
  process.stderr.write(`${error instanceof Error ? error.stack : String(error)}\n`);
  process.exitCode = 1;
});
