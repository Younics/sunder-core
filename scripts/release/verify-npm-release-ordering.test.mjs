import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const workflowPath = resolve(repositoryRoot, ".github/workflows/sunder-sdk-release.yml");
const verifierPath = resolve(repositoryRoot, "scripts/release/verify-npm-release.mjs");

test("npm publication verifies each exact package before advancing and retains aggregate verification", async () => {
  const workflow = await readFile(workflowPath, "utf8");
  const publishJobStart = workflow.indexOf("\n  publish-npm:");
  const publishStepStart = workflow.indexOf("\n      - name: Publish with trusted publishing", publishJobStart);
  assert.ok(publishJobStart >= 0 && publishStepStart > publishJobStart, "npm publication job is missing.");
  const publishJobHeader = workflow.slice(publishJobStart, publishStepStart);
  assert.match(publishJobHeader, /needs:[\s\S]+- npm-preflight/u);
  assert.match(publishJobHeader, /permissions:[\s\S]+id-token: write/u);

  const helperStart = workflow.indexOf("          verify_published_package_before_advance() {");
  const loopStart = workflow.indexOf('          for index in "${!package_ids[@]}"; do', helperStart);
  const aggregateStart = workflow.indexOf("          verified=false", loopStart);
  assert.ok(helperStart >= 0 && loopStart > helperStart && aggregateStart > loopStart, "npm publication verification structure is missing.");

  const helper = workflow.slice(helperStart, loopStart);
  assert.match(helper, /node scripts\/release\/verify-npm-release\.mjs[\s\S]+"\$GITHUB_SHA"/u);
  assert.match(helper, /"\$GITHUB_SHA" \\\n+\s+publication \\\n+\s+"\$package_id"; then/u);

  const publicationLoop = workflow.slice(loopStart, aggregateStart);
  const existenceCheck = publicationLoop.indexOf('npm view "$package_id@$VERSION" version');
  const publish = publicationLoop.indexOf('npm publish "$package_path"');
  const conditionalEnd = publicationLoop.indexOf("\n            fi", publish);
  const exactVerification = publicationLoop.indexOf('verify_published_package_before_advance "$package_id"');
  const loopEnd = publicationLoop.lastIndexOf("\n          done");
  assert.ok(existenceCheck >= 0 && publish > existenceCheck, "npm publication must remain conditional on an exact version lookup.");
  assert.ok(conditionalEnd > publish && exactVerification > conditionalEnd, "both existing and newly published packages must reach exact verification.");
  assert.ok(loopEnd > exactVerification, "exact package verification must complete before the publication loop advances.");

  const aggregate = workflow.slice(aggregateStart, workflow.indexOf("\n  publication-complete:", aggregateStart));
  assert.match(aggregate, /node scripts\/release\/verify-npm-release\.mjs[\s\S]+"\$GITHUB_SHA" \\\n+\s+publication; then/u);
  assert.doesNotMatch(aggregate, /publication \\\n+\s+"\$package_id"; then/u);
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
