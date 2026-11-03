import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const verifierPath = resolve(repositoryRoot, "scripts/release/verify-npm-release.mjs");

test("npm publication verifies a selected archive before advancing its channel", async () => {
  const script = await readFile(resolve(repositoryRoot, "scripts/release/publish-sdk.sh"), "utf8");
  assert.match(script, /npm publish "\$archive" --ignore-scripts --access public[^\n]+--provenance/u);
  assert.ok(script.indexOf('sdk_release.py" history') < script.indexOf('npm publish'));
  assert.ok(script.indexOf('retry verify publication') < script.indexOf('npm dist-tag add'));
  assert.ok(script.indexOf('Refusing to move an npm consumer channel backwards') < script.indexOf('npm dist-tag add'));
  assert.match(script, /retry verify promotion/u);
});

test("npm release verifier rejects an unknown single-package selector before registry access", () => {
  assert.throws(
    () => execFileSync(process.execPath, [
      verifierPath,
      "@other/package",
      "1.2.3", "other-1.2.3.tgz", "0123456789012345678901234567890123456789", "publication",
    ], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }),
    (error) => error?.stderr?.includes("Expected one public npm package archive"),
  );
});
