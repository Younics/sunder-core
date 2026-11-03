import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const verifierPath = resolve(repositoryRoot, "scripts/release/verify-npm-release.mjs");

test("npm publication verifies each exact package before advancing and retains aggregate verification", async () => {
  const script = await readFile(resolve(repositoryRoot, "scripts/release/publish-sdk.sh"), "utf8");
  const publicationLoop = script.slice(script.indexOf('elif [[ "$action" == npm ]]'), script.indexOf('elif [[ "$action" == promote ]]'));
  assert.match(publicationLoop, /\$\{NODE_AUTH_TOKEN:\?Set the NPM_TOKEN secret/u);
  assert.match(publicationLoop, /npm publish[^\n]+--ignore-scripts --access public[\s\S]+--provenance/u);
  const existenceCheck = publicationLoop.indexOf('sdk_release.py" package "$version" "$package_id"');
  const publish = publicationLoop.indexOf('npm publish "$directory/');
  const conditionalEnd = publicationLoop.indexOf("\n    fi", publish);
  const exactVerification = publicationLoop.indexOf('retry verify_npm "accept-v$version" publication "$package_id"');
  const loopEnd = publicationLoop.indexOf("\n  done", exactVerification);
  assert.ok(existenceCheck >= 0 && publish > existenceCheck, "npm publication must remain conditional on an exact version lookup.");
  assert.match(publicationLoop.slice(existenceCheck, publish), /\[\[ "\$lookup_status" == 4 \]\] \|\| exit "\$lookup_status"/u);
  assert.ok(conditionalEnd > publish && exactVerification > conditionalEnd, "both existing and newly published packages must reach exact verification.");
  assert.ok(loopEnd > exactVerification, "exact package verification must complete before the publication loop advances.");
  assert.match(publicationLoop.slice(loopEnd), /verify_npm "accept-v\$version" publication\s*$/u);
});

test("npm release verifier rejects an unknown single-package selector before registry access", () => {
  assert.throws(
    () => execFileSync(process.execPath, [
      verifierPath,
      "1.2.3",
      ">=1.2.0 <1.3.0",
      "accept-v1.2.3",
      repositoryRoot,
      "Younics/sunder-core",
      ".github/workflows/sunder-sdk-release.yml",
      "0123456789012345678901234567890123456789",
      "publication",
      "@other/package",
    ], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }),
    (error) => error?.stderr?.includes("Expected one coordinated npm package name"),
  );
});
