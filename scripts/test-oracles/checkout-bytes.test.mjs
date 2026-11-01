import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

test("Windows checkout preserves byte-verified platform contracts and RPC schemas", () => {
  for (const path of [
    "eng/platform-contracts/sunder-contracts.owned.json",
    "specs/rpc/v1/authoring/authoring-receipt.schema.json",
    "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json",
  ]) {
    const blob = spawnSync("git", ["cat-file", "blob", `HEAD:${path}`], { cwd: root });
    const checkout = spawnSync("git", ["-c", "core.autocrlf=true", "cat-file", "--filters", `--path=${path}`, `HEAD:${path}`], { cwd: root });
    assert.equal(blob.status, 0, blob.stderr.toString());
    assert.equal(checkout.status, 0, checkout.stderr.toString());
    assert.deepEqual(checkout.stdout, blob.stdout, `${path}: Git checkout must not rewrite exact input bytes`);
  }
});
