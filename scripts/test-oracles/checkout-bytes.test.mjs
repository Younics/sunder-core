import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { verifyCheckoutBytes } from "./checkout-bytes.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

test("Windows checkout preserves byte-verified platform contracts and RPC schemas", () => {
  verifyCheckoutBytes(root);
  for (const path of [
    "eng/platform-contracts/sunder-contracts.owned.json",
    "specs/rpc/v1/authoring/authoring-receipt.schema.json",
    "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json",
    "languages/typescript/node/packages/create-sunder-package/template/sunder-contracts.lock.json",
    "languages/typescript/node/packages/create-sunder-package/template-react-node/sunder-contracts.lock.json",
    "languages/typescript/node/packages/package-tool/test/historical-csharp-owned.json",
  ]) {
    const blob = spawnSync("git", ["cat-file", "blob", `HEAD:${path}`], { cwd: root });
    const checkout = spawnSync("git", ["-c", "core.autocrlf=true", "cat-file", "--filters", `--path=${path}`, `HEAD:${path}`], { cwd: root });
    assert.equal(blob.status, 0, blob.stderr.toString());
    assert.equal(checkout.status, 0, checkout.stderr.toString());
    assert.deepEqual(checkout.stdout, blob.stdout, `${path}: Git checkout must not rewrite exact input bytes`);
  }
});

test("actual checkout preflight reports all encoding drift without rewriting edits, invalid fixtures or ignored outputs", async () => {
  const temporary = await mkdtemp(resolve(tmpdir(), "sunder-checkout-bytes-"));
  try {
    execFileSync("git", ["init", "--quiet", temporary]);
    await writeFile(resolve(temporary, ".gitattributes"), "*.json -text\n");
    await writeFile(resolve(temporary, ".gitignore"), "generated/\n");
    await mkdir(resolve(temporary, "test"));
    for (const name of ["edited.json", "deleted.json", "historical.json"]) await writeFile(resolve(temporary, "test", name), "{}\n");
    execFileSync("git", ["-c", "core.autocrlf=false", "add", "."], { cwd: temporary });
    await rm(resolve(temporary, "test/deleted.json"));
    await writeFile(resolve(temporary, "test/edited.json"), '{"meaningfulEdit":true}\n');
    await writeFile(resolve(temporary, "test/invalid.json"), '{"incomplete":\n');
    await writeFile(resolve(temporary, "test/packages.lock.json"), '{"version":1}\r\n');
    await writeFile(resolve(temporary, "appsettings.json"), '{}\r\n');
    await mkdir(resolve(temporary, "generated"));
    await writeFile(resolve(temporary, "generated/ignored.json"), "{}\r\n");
    assert.equal(verifyCheckoutBytes(temporary), 3);

    const drift = new Map([
      ["test/historical.json", Buffer.from("{}\r\n")],
      ["test/new # file.json", Buffer.from('\ufeff{"new":true}\n')],
      ["test/mixed.json", Buffer.from("{\r\n}\n\r")],
    ]);
    for (const [name, bytes] of drift) await writeFile(resolve(temporary, name), bytes);
    assert.throws(() => verifyCheckoutBytes(temporary), error => {
      assert.match(error.message, /\(3 files\)/u);
      for (const name of drift.keys()) assert.ok(error.message.includes(name));
      assert.match(error.message, /No files were changed/u);
      return true;
    });
    for (const [name, bytes] of drift) assert.deepEqual(await readFile(resolve(temporary, name)), bytes);
    assert.equal(await readFile(resolve(temporary, "test/edited.json"), "utf8"), '{"meaningfulEdit":true}\n');
    assert.equal(await readFile(resolve(temporary, "test/invalid.json"), "utf8"), '{"incomplete":\n');
    assert.equal(await readFile(resolve(temporary, "test/packages.lock.json"), "utf8"), '{"version":1}\r\n');
    assert.equal(await readFile(resolve(temporary, "appsettings.json"), "utf8"), '{}\r\n');
  } finally { await rm(temporary, { recursive: true, force: true }); }
});
