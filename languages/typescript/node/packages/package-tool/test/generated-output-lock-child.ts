import { readFile, writeFile } from "node:fs/promises";

// Only this subprocess fixture substitutes the OS account lookup. Production has
// no environment override for the trusted marker location.
const accountHome = process.env.SUNDER_TEST_ACCOUNT_HOME;
if (accountHome !== undefined) {
  const os = require("node:os") as { userInfo: () => import("node:os").UserInfo<string> };
  const original = os.userInfo;
  os.userInfo = () => ({ ...original(), homedir: accountHome });
}

async function main(): Promise<void> {
  const [mode, output, ready, release, timeout] = process.argv.slice(2);
  if (output === undefined || ready === undefined) throw new Error("Lock probe requires output and ready paths.");
  if (mode?.startsWith("recover-denied-") || mode === "recover-transient") {
    const fs = require("node:fs/promises") as typeof import("node:fs/promises");
    const rename = fs.rename;
    let denied = 0;
    fs.rename = async (source, destination) => {
      if (source === `${output}.sunder-output.lock` && (mode !== "recover-transient" || denied++ < 2)) {
        throw Object.assign(new Error("Fixture-held directory handle"), { code: mode === "recover-transient" ? "EPERM" : mode.slice("recover-denied-".length) });
      }
      return rename(source, destination);
    };
  }
  const { withOutputLeases } = require("../src/output") as typeof import("../src/output");
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
