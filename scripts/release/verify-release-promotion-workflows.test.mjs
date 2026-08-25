import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile, readdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const workflowDirectory = resolve(repositoryRoot, ".github/workflows");
const workflow = (name) => readFile(resolve(workflowDirectory, name), "utf8");

function indentation(line) {
  return /^ */u.exec(line)[0].length;
}

function multilineShellBlocks(source, workflowName) {
  const lines = source.replaceAll("\r\n", "\n").split("\n");
  const blocks = [];
  for (let index = 0; index < lines.length; index += 1) {
    const run = /^( *)run:\s*\|[-+]?\s*$/u.exec(lines[index]);
    if (run === null) continue;
    const runIndent = run[1].length;
    let stepStart = index - 1;
    while (stepStart >= 0) {
      const line = lines[stepStart];
      if (line.trim() !== "" && indentation(line) < runIndent && /^\s*-\s+/u.test(line)) break;
      stepStart -= 1;
    }
    assert.ok(stepStart >= 0, `${workflowName}:${index + 1} has no enclosing workflow step.`);
    const stepHeader = lines.slice(stepStart, index);
    const shellLine = stepHeader.find((line) => /^\s*shell:\s*/u.test(line));
    const shell = shellLine?.replace(/^\s*shell:\s*/u, "").trim().replace(/^['"]|['"]$/gu, "");

    let blockEnd = index + 1;
    while (blockEnd < lines.length) {
      if (lines[blockEnd].trim() !== "" && indentation(lines[blockEnd]) <= runIndent) break;
      blockEnd += 1;
    }
    const contentIndent = runIndent + 2;
    const script = lines.slice(index + 1, blockEnd)
      .map((line) => line.trim() === "" ? "" : line.slice(contentIndent))
      .join("\n")
      .replace(/\$\{\{[\s\S]*?\}\}/gu, "GITHUB_EXPRESSION");
    blocks.push({ line: index + 1, script, shell, workflowName });
    index = blockEnd - 1;
  }
  return blocks;
}

test("every multiline shell block in release workflows passes Bash syntax validation", async () => {
  const workflowNames = (await readdir(workflowDirectory))
    .filter((name) => /^sunder-.*release.*\.yml$/u.test(name))
    .sort();
  assert.ok(workflowNames.includes("sunder-release-promotion.yml"));
  const blocks = [];
  for (const workflowName of workflowNames) {
    blocks.push(...multilineShellBlocks(await workflow(workflowName), workflowName));
  }
  const unsupportedShells = blocks.filter(({ shell }) => shell !== undefined && shell !== "bash" && shell !== "pwsh");
  assert.deepEqual(unsupportedShells, [], "A release workflow multiline block uses an unclassified shell.");
  const bashBlocks = blocks.filter(({ shell }) => shell === undefined || shell === "bash");
  assert.ok(bashBlocks.length > 0, "No release workflow Bash blocks were discovered.");
  assert.ok(
    bashBlocks.some((block) => block.workflowName === "sunder-release-promotion.yml"
      && block.script.includes("make_latest=false")),
    "The GitHub finalization block was not included in Bash syntax validation.",
  );
  for (const block of bashBlocks) {
    const result = spawnSync("bash", ["-n", "-s"], { input: block.script, encoding: "utf8" });
    assert.equal(
      result.status,
      0,
      `${block.workflowName}:${block.line} failed bash -n:\n${result.stderr}`,
    );
  }
});

test("tag workflows stop after immutable publication and verified private drafts", async () => {
  const publicationWorkflows = new Map([
    ["app", await workflow("sunder-app-release.yml")],
    ["cli", await workflow("sunder-cli-release.yml")],
    ["host", await workflow("sunder-host-release.yml")],
    ["sdk", await workflow("sunder-sdk-release.yml")],
  ]);

  for (const [component, source] of publicationWorkflows) {
    assert.match(source, new RegExp(`tags:\\n\\s+- "${component}/v\\*"`, "u"));
    assert.doesNotMatch(source, /workflow_dispatch:/u);
    assert.doesNotMatch(source, /gh (?:"\$\{?[^\n]*\}?" )*release edit[^\n]*--draft=false|edit_args=\(release edit/u);
    assert.doesNotMatch(source, /releases\/tags\//u);
    assert.doesNotMatch(source, /npm dist-tag/u);
    assert.match(source, /group: sunder-release-draft-\$\{\{ github\.ref_name \}\}/u);
    assert.match(source, /resolve-github-release\.sh/u);
    assert.match(source, /upload-github-release-assets\.sh/u);
    assert.match(source, new RegExp(`sunder-${component}-publication-evidence-\\$RELEASE_ID-\\$EVIDENCE_RUN_ATTEMPT`, "u"));
    assert.match(source, /verify-github-release-assets\.sh/u);
    assert.match(source, /retention-days: 90/u);
    assert.match(source, /"\$created_release" == true && "\$verified_release" != true/u);
  }

  const sdk = publicationWorkflows.get("sdk");
  assert.match(sdk, /npm_tag="accept-v\$version"/u);
  assert.doesNotMatch(sdk, /npm_tag="(?:latest|next)"/u);
  assert.match(sdk, /npm publish "\$package_path"[\s\S]+--tag "\$NPM_TAG"/u);
  assert.match(sdk, /dotnet nuget push/u);
  assert.match(sdk, /publication-complete:/u);
  assert.match(sdk, /Sunder\.Sdk\.\$\{\{ needs\.prepare\.outputs\.version \}\}\.nupkg/u);
  assert.match(sdk, /name: sunder-sdk-release-packages[\s\S]{0,2000}retention-days: 90/u);
  assert.match(sdk, /Rebind an existing immutable package artifact on retry/u);
  assert.match(sdk, /actions\/runs\/\$GITHUB_RUN_ID\/artifacts/u);
  assert.match(sdk, /\.expired == false/u);
  assert.match(sdk, /Rebinding this retry to the original immutable SDK package bytes/u);
  assert.doesNotMatch(sdk, /overwrite:\s*true/u);
  assert.match(sdk, /if: steps\.package-artifact\.outputs\.upload == 'true'/u);

  const app = publicationWorkflows.get("app");
  assert.match(app, /StrictRelease = \$true/u);
  assert.match(app, /--strict-release/u);
  assert.match(app, /\.Assets as \$assets[\s\S]+length == 1/u);
  assert.match(app, /vpk upload local/u);
  assert.doesNotMatch(app, /vpk upload github/u);
  assert.match(app, /name: sunder-app-release-\$\{\{ matrix\.runtime \}\}/u);
  assert.match(app, /pattern: sunder-app-release-\*/u);
  assert.doesNotMatch(app, /pattern: sunder-app-\*/u);
  assert.match(app, /name: sunder-app-release-\$\{\{ matrix\.runtime \}\}[\s\S]{0,400}retention-days: 90/u);

  const host = publicationWorkflows.get("host");
  assert.match(host, /pattern: sunder-runtime-host-\*/u);
  assert.doesNotMatch(host, /pattern: sunder-host-\*/u);
  assert.match(host, /name: sunder-runtime-host-\$\{\{ matrix\.runtime \}\}[\s\S]{0,400}retention-days: 90/u);

  const cli = publicationWorkflows.get("cli");
  assert.match(cli, /name: sunder-cli-\$\{\{ matrix\.runtime \}\}[\s\S]{0,400}retention-days: 90/u);
});

test("promotion is dispatch-only, numeric-ID bound, production-gated, and credential-separated", async () => {
  const source = await workflow("sunder-release-promotion.yml");
  assert.match(source, /workflow_dispatch:/u);
  assert.doesNotMatch(source, /\n\s+push:|\n\s+tags:/u);
  for (const input of [
    "component",
    "release_id",
    "tag",
    "source_sha",
    "publication_run_id",
    "publication_run_attempt",
    "production_acceptance_run_id",
    "production_acceptance_run_attempt",
    "production_acceptance_workflow_sha",
  ]) {
    assert.match(source, new RegExp(`\\n      ${input}:`, "u"));
  }
  assert.match(source, /group: sunder-release-draft-\$\{\{ inputs\.tag \}\}/u);
  assert.match(source, /run-id: \$\{\{ inputs\.publication_run_id \}\}/u);
  assert.match(source, /publication-evidence-\$\{\{ inputs\.release_id \}\}-\$\{\{ inputs\.publication_run_attempt \}\}/u);
  assert.match(source, /run-id: \$\{\{ inputs\.production_acceptance_run_id \}\}/u);
  assert.match(source, /production-acceptance-\$\{\{ inputs\.release_id \}\}-\$\{\{ inputs\.production_acceptance_run_attempt \}\}/u);
  assert.match(source, /verify-release-promotion\.sh/u);
  assert.match(source, /verify-production-acceptance\.sh/u);
  assert.doesNotMatch(source, /releases\/tags\/|gh release (?:view|edit)/u);
  assert.doesNotMatch(source, /dotnet\s+(?:build|pack|publish)|npm\s+publish|vpk\s+(?:pack|upload)|codesign|notarytool|signtool/iu);
  assert.doesNotMatch(source, /id-token:\s*write|NUGET_API_KEY|APPLE_BUILD_CERTIFICATE|AZURE_TRUSTED_SIGNING/iu);

  const npmStart = source.indexOf("\n  promote-npm:");
  const githubStart = source.indexOf("\n  finalize-github:");
  assert.ok(npmStart >= 0 && githubStart > npmStart, "promotion mutation jobs are missing.");
  const npmJob = source.slice(npmStart, githubStart);
  const githubJob = source.slice(githubStart);
  assert.match(npmJob, /environment: sunder-sdk-npm-promotion/u);
  assert.match(npmJob, /NPM_DIST_TAG_TOKEN/u);
  assert.match(npmJob, /npm dist-tag add/u);
  assert.doesNotMatch(npmJob, /contents:\s*write/u);
  assert.match(githubJob, /environment: sunder-github-release-promotion/u);
  assert.match(githubJob, /contents:\s*write/u);
  assert.match(githubJob, /repos\/\$GH_REPO\/releases\/\$RELEASE_ID/u);
  assert.match(githubJob, /gh api --method PATCH/u);
  assert.match(githubJob, /\{draft:false,prerelease:\$prerelease,make_latest:\$make_latest\}/u);
  assert.match(githubJob, /make_latest=false[\s\S]+"\$COMPONENT" == "app" && "\$IS_PRERELEASE" == "false"[\s\S]+make_latest=true/u);
  assert.match(githubJob, /Numeric release changed after final byte verification/u);
  assert.doesNotMatch(githubJob, /NPM_DIST_TAG_TOKEN|npm dist-tag/u);
});

test("production acceptance is a separate, archived, trusted-workflow decision", async () => {
  const verifier = await readFile(
    resolve(repositoryRoot, "scripts/release/verify-production-acceptance.sh"),
    "utf8",
  );
  assert.match(verifier, /workflow_path='\.github\/workflows\/sunder-production-acceptance\.yml'/u);
  assert.match(verifier, /workflow_branch='main'/u);
  assert.match(verifier, /\.decision == "accepted"/u);
  assert.match(verifier, /workflowRef: \$workflow_ref/u);
  assert.match(verifier, /workflowSha: \$workflow_sha/u);
  assert.equal([...verifier.matchAll(/\.head_branch == \$workflow_branch/gu)].length, 2);
  assert.equal([...verifier.matchAll(/\.head_sha == \$workflow_sha/gu)].length, 2);
  assert.match(verifier, /\.id == "released-byte-provenance"/u);
  assert.match(verifier, /\.id == "production-recovery"/u);
  assert.match(verifier, /\.id == \$component_check/u);
  assert.match(verifier, /\.status == "completed"[\s\S]+\.conclusion == "success"/u);
  assert.match(verifier, /\/attempts\/\$production_run_attempt/u);
  assert.match(verifier, /Production acceptance artifact contains unmanifested or missing files/u);
});

test("strict App release mode cannot import mutable history or emit a delta", async () => {
  const shell = await readFile(resolve(repositoryRoot, "scripts/release/package-sunder.sh"), "utf8");
  const powershell = await readFile(resolve(repositoryRoot, "scripts/release/package-sunder.ps1"), "utf8");
  assert.match(shell, /--strict-release\)[\s\S]+strict_release="true"/u);
  assert.match(shell, /strict_release" != "true" && -n "\$github_repository_url"/u);
  assert.match(shell, /strict_delta_packages[\s\S]+history-dependent delta/u);
  assert.match(powershell, /\[switch\]\$StrictRelease/u);
  assert.match(powershell, /-not \$StrictRelease -and -not \[string\]::IsNullOrWhiteSpace\(\$GitHubRepositoryUrl\)/u);
  assert.match(powershell, /strictDeltaPackages[\s\S]+history-dependent delta/u);
});

test("compatibility generator inventories promotion workflows", async () => {
  const generator = await readFile(
    resolve(repositoryRoot, "scripts/compatibility/generate-core-compatibility-candidate.mjs"),
    "utf8",
  );
  assert.match(generator, /ci\|release\|validation\|promotion/u);
});
