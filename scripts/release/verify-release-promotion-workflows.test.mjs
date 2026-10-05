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
    .filter((name) => /^sunder-.*release.*\.yml$/u.test(name)
      || name === "sunder-execution-runtime-publication.yml"
      || name === "sunder-production-acceptance.yml")
    .sort();
  assert.ok(workflowNames.includes("sunder-release-promotion.yml"));
  assert.ok(workflowNames.includes("sunder-production-acceptance.yml"));
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
  assert.match(source, /download-production-acceptance\.sh/u);
  assert.doesNotMatch(source, /name: sunder-\$\{\{ inputs\.component \}\}-production-acceptance-/u);
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
  const acceptanceWorkflow = await workflow("sunder-production-acceptance.yml");
  const verifier = await readFile(
    resolve(repositoryRoot, "scripts/release/verify-production-acceptance.sh"),
    "utf8",
  );
  const uploader = await readFile(
    resolve(repositoryRoot, "scripts/release/upload-production-acceptance.sh"),
    "utf8",
  );
  const uploadFixture = await readFile(
    resolve(repositoryRoot, "scripts/release/upload-production-acceptance.fixture.sh"),
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
  assert.match(verifier, /runtime-native-output-\$rid\.json/u);
  assert.match(verifier, /nativeOutputReceiptSha256/u);
  assert.match(verifier, /releaseDriverSha256 == \$native_receipt\[0\]\.releaseDriverSha256/u);
  assert.match(verifier, /does not bind the canonical runtime receipts/u);
  assert.match(acceptanceWorkflow, /workflow_dispatch:/u);
  assert.doesNotMatch(acceptanceWorkflow, /\n\s+push:|\n\s+pull_request:/u);
  assert.match(acceptanceWorkflow, /test "\$GITHUB_REF" = refs\/heads\/main/u);
  assert.match(acceptanceWorkflow, /environment: sunder-core-production-acceptance/u);
  assert.match(acceptanceWorkflow, /eng\/execution-runtimes\/production\.lock\.json/u);
  assert.match(acceptanceWorkflow, /SUNDER_PRODUCTION_ACCEPTANCE_CANDIDATE_ONLY: "true"/u);
  assert.match(acceptanceWorkflow, /name: sunder-core-production-native-\$\{\{ matrix\.rid \}\}-\$\{\{ github\.run_attempt \}\}/u);
  assert.match(acceptanceWorkflow, /pattern: sunder-core-production-native-\*-\$\{\{ github\.run_attempt \}\}/u);
  assert.match(acceptanceWorkflow, /name: sunder-\$\{\{ inputs\.component \}\}-production-acceptance-/u);
  assert.match(acceptanceWorkflow, /upload-production-acceptance\.sh/u);
  assert.match(acceptanceWorkflow, /download-production-acceptance\.sh/u);
  const nativeStart = acceptanceWorkflow.indexOf("\n  native-acceptance:");
  const archiveStart = acceptanceWorkflow.indexOf("\n  archive-decision:");
  assert.ok(nativeStart >= 0 && archiveStart > nativeStart, "acceptance jobs are missing.");
  assert.doesNotMatch(acceptanceWorkflow.slice(nativeStart, archiveStart), /contents:\s*write/u);
  assert.doesNotMatch(
    acceptanceWorkflow.slice(nativeStart, archiveStart),
    /GH_TOKEN|NUGET_API_KEY|SUNDER_REGISTRY_(?:CLI|PUBLISH)_TOKEN/u,
  );
  assert.match(acceptanceWorkflow.slice(archiveStart), /contents:\s*write/u);
  assert.match(acceptanceWorkflow, /SUNDER_CORE_PRODUCTION_ACCEPTANCE_DRIVER_SHA256/u);
  assert.match(acceptanceWorkflow, /SUNDER_CORE_PRODUCTION_ACCEPTANCE_COORDINATOR_SHA256/u);
  assert.equal([...acceptanceWorkflow.matchAll(/driver_snapshot=/gu)].length, 2);
  assert.equal([...acceptanceWorkflow.matchAll(/chmod 0555 "\$driver_snapshot"/gu)].length, 2);
  assert.equal([...acceptanceWorkflow.matchAll(/chmod 0555 "\$driver_snapshot_root"/gu)].length, 2);
  assert.equal([...acceptanceWorkflow.matchAll(/hash_file "\$driver_snapshot"/gu)].length, 4);
  assert.match(acceptanceWorkflow, /--driver-sha256/u);
  assert.match(acceptanceWorkflow, /--coordinator-sha256/u);
  assert.match(acceptanceWorkflow, /--runtime-native-receipt/u);
  assert.match(acceptanceWorkflow, /nativeOutputReceiptSha256/u);
  assert.match(uploader, /production-acceptance-\$release_id-\$run_id-\$run_attempt/u);
  assert.match(
    uploader,
    /bash "\$uploader" "\$release_id" "\$tag" "\$\{upload_paths\[@\]\}" "\$receipt"/u,
  );
  for (const scenario of [
    "equal and partial retry",
    "immutable mismatch",
    "null and mismatched metadata",
    "finalized-release refusal",
    "new-attempt recovery",
  ]) {
    assert.match(uploadFixture, new RegExp(scenario, "u"));
  }
  for (const rid of ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]) {
    assert.match(acceptanceWorkflow, new RegExp(`- rid: ${rid}`, "u"));
  }
});

test("execution-runtime tag production is separate from protected retained-asset publication", async () => {
  const runtimeWorkflow = await workflow("sunder-execution-runtime-native-release.yml");
  const publicationWorkflow = await workflow("sunder-execution-runtime-publication.yml");
  const runtimeValidationWorkflow = await workflow("sunder-execution-runtime-validation.yml");
  const nativeRelease = await readFile(
    resolve(repositoryRoot, "scripts/execution-runtimes/native-release.sh"),
    "utf8",
  );
  const preflight = await readFile(resolve(repositoryRoot, "scripts/execution-runtimes/native-release-preflight.sh"), "utf8");
  const publication = await readFile(
    resolve(repositoryRoot, "scripts/execution-runtimes/publish-release-set.sh"),
    "utf8",
  );
  const publicationDryRun = await readFile(
    resolve(repositoryRoot, "scripts/execution-runtimes/publication-dry-run.fixture.sh"),
    "utf8",
  );
  const evidenceUploader = await readFile(
    resolve(repositoryRoot, "scripts/release/upload-execution-runtime-release-evidence.sh"),
    "utf8",
  );
  const evidenceFixture = await readFile(
    resolve(repositoryRoot, "scripts/release/upload-execution-runtime-release-evidence.fixture.sh"),
    "utf8",
  );

  assert.match(runtimeWorkflow, /tags:\n\s+- "runtime\/v\*"/u);
  assert.doesNotMatch(runtimeWorkflow, /workflow_dispatch:|\n\s+pull_request:/u);
  assert.match(runtimeWorkflow, /needs: native-build[\s\S]+validate-release-set/u);
  assert.doesNotMatch(runtimeWorkflow, /publish-release-set\.sh|REGISTRY|registry-publication/u);
  assert.match(runtimeWorkflow, /release_coordinates\.py runtime --release-id/u);
  assert.match(runtimeWorkflow, /SUNDER_CORE_ARTIFACT_SOURCE_COMMIT/u);
  assert.match(runtimeWorkflow, /SUNDER_CORE_SDK_TAG/u);
  assert.match(runtimeWorkflow, /environment: sunder-execution-runtime-native-release/u);
  assert.match(runtimeWorkflow, /archive-release-evidence:/u);
  assert.match(runtimeWorkflow, /retained_native\.py upload/u);
  assert.match(runtimeWorkflow, /runtime-verifier\.zip/u);
  assert.match(runtimeWorkflow, /pattern: sunder-execution-runtimes-\*-\$\{\{ github\.run_attempt \}\}/u);
  assert.match(runtimeWorkflow, /retention-days: 1/u);
  assert.match(publicationWorkflow, /workflow_dispatch:/u);
  assert.doesNotMatch(publicationWorkflow, /\n\s+push:|\n\s+tags:|id-token: write/u);
  for (const input of ["runtime_tag", "release_id", "native_run_id", "native_run_attempt", "artifact_source_commit", "publication_evidence_commit", "runtime_lock_sha256"]) {
    assert.match(publicationWorkflow, new RegExp(`\\n      ${input}:`, "u"));
  }
  assert.match(publicationWorkflow, /test "\$GITHUB_REF" = refs\/heads\/main/u);
  assert.match(publicationWorkflow, /retained_native\.py download/u);
  assert.match(publicationWorkflow, /publish-release-set\.sh/u);
  assert.match(publicationWorkflow, /upload-execution-runtime-release-evidence\.sh/u);
  assert.doesNotMatch(publicationWorkflow + publication, /dotnet\s+(?:run|build|pack|publish)|native-release\.sh/u);
  assert.match(runtimeValidationWorkflow, /publication-dry-run\.fixture\.sh/u);
  assert.match(runtimeValidationWorkflow, /upload-execution-runtime-release-evidence\.fixture\.sh/u);
  assert.match(publicationDryRun, /publication-resume\.fixture\.sh/u);
  assert.equal(
    [...runtimeWorkflow.matchAll(/--output "\$first"|--output "\$second"/gu)].length,
    2,
  );
  for (const rid of ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]) {
    assert.match(runtimeWorkflow, new RegExp(`- rid: ${rid}`, "u"));
  }

  assert.match(nativeRelease, /SUNDER_EXECUTION_RUNTIME_RELEASE_DRIVER_SHA256/u);
  assert.match(nativeRelease, /driver_snapshot/u);
  assert.match(nativeRelease, /chmod 0555 "\$driver_snapshot"/u);
  assert.match(nativeRelease, /chmod 0555 "\$driver_snapshot_root"/u);
  assert.equal([...nativeRelease.matchAll(/hash_file "\$driver_snapshot"/gu)].length, 2);
  assert.match(nativeRelease, /validate-native-output/u);
  assert.match(nativeRelease, /SUNDER_EXECUTION_RUNTIME_BUILD_PHASE:-acceptance/u);
  assert.match(preflight, /phase="acceptance"/u);
  assert.match(preflight, /if \[\[ "\$phase" == native \]\]; then[\s\S]+else[\s\S]+require_approval SUNDER_EXECUTION_RUNTIME_PUBLICATION_APPROVED/u);
  assert.match(runtimeWorkflow, /SUNDER_EXECUTION_RUNTIME_BUILD_PHASE: native/u);
  assert.match(publication, /--dry-run/u);
  assert.match(publication, /sunder-runtime-publication-inputs/u);
  assert.match(publication, /execution-runtimes\/stages\/lookup/u);
  assert.match(publication, /chmod 600 "\$output_path"/u);
  assert.match(publication, /mv "\$temporary" "\$output_path"/u);
  assert.match(publication, /readyToPromote == true/u);
  assert.match(publication, /validate-release-set/u);
  assert.match(publication, /releaseDriversSha256/u);
  assert.match(publication, /nativeOutputReceipts/u);
  assert.match(publication, /\/promotion/u);
  assert.match(publication, /\/resolve/u);
  assert.match(evidenceUploader, /runtime-release-assets\/v1/u);
  assert.match(evidenceUploader, /falsely marked complete/u);
  assert.match(evidenceUploader, /bash "\$downloader"/u);
  assert.match(evidenceFixture, /upload runner loss/u);
  assert.match(evidenceFixture, /false-completion refusal/u);

  const fanInStart = runtimeWorkflow.indexOf("\n  release-fan-in:");
  const archiveStart = runtimeWorkflow.indexOf("\n  archive-release-evidence:");
  assert.ok(fanInStart >= 0 && archiveStart > fanInStart, "runtime retention jobs are missing.");
  assert.doesNotMatch(runtimeWorkflow.slice(fanInStart, archiveStart), /SUNDER_EXECUTION_RUNTIME_REGISTRY_ADMIN_TOKEN/u);
  assert.doesNotMatch(runtimeWorkflow.slice(fanInStart, archiveStart), /contents:\s*write/u);
  assert.match(runtimeWorkflow.slice(archiveStart), /contents:\s*write/u);
  assert.doesNotMatch(runtimeWorkflow.slice(archiveStart), /SUNDER_EXECUTION_RUNTIME_REGISTRY_ADMIN_TOKEN/u);
  const publicationArchive = publicationWorkflow.indexOf("\n  archive-publication:");
  assert.match(publicationWorkflow.slice(0, publicationArchive), /SUNDER_EXECUTION_RUNTIME_REGISTRY_ADMIN_TOKEN/u);
  assert.doesNotMatch(publicationWorkflow.slice(0, publicationArchive), /contents:\s*write/u);
  assert.doesNotMatch(publicationWorkflow.slice(publicationArchive), /SUNDER_EXECUTION_RUNTIME_REGISTRY_ADMIN_TOKEN/u);
});

test("SDK/catalog release exports the approved source and retains a closed numeric-asset receipt", async () => {
  const source = await workflow("sunder-sdk-release.yml");
  assert.match(source, /release_coordinates\.py sdk/u);
  assert.match(source, /SUNDER_CORE_ARTIFACT_SOURCE_COMMIT/u);
  assert.match(source, /SUNDER_CORE_REGISTRY_ORIGIN/u);
  assert.match(source, /core_catalog_release\.py export/u);
  assert.match(source, /core_catalog_release\.py retain/u);
  assert.match(source, /sunder-core-catalog-\$\{\{ github\.run_id \}\}-\$\{\{ github\.run_attempt \}\}/u);
  assert.match(source, /sunder-platform-contracts\.v1\.json/u);
  assert.match(source, /sunder-platform-contracts\.receipt\.v1\.json/u);
  assert.match(source, /--target "\$GITHUB_SHA"/u);
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
