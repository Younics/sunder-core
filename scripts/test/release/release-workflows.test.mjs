import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile, readdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const directory = resolve(root, ".github/workflows");
const workflow = (name) => readFile(resolve(directory, `sunder-${name}.yml`), "utf8");

function requiresJobs(source, jobs) {
  const needs = /^\s*needs:\s*\[([^\]]+)\]/mu.exec(source)?.[1].split(",").map((name) => name.trim()) ?? [];
  for (const job of jobs) assert.ok(needs.includes(job), `Missing required job dependency: ${job}`);
}

function shellBlocks(source) {
  const lines = source.split("\n");
  const blocks = [];
  for (let index = 0; index < lines.length; index++) {
    const match = /^( *)run:\s*\|[-+]?\s*$/u.exec(lines[index]);
    if (!match) continue;
    const indent = match[1].length;
    let start = index - 1;
    while (start >= 0 && !/^\s*- /u.test(lines[start])) start--;
    const shell = lines.slice(start, index).find((line) => /^\s*shell:/u.test(line))?.trim().slice(7);
    let end = index + 1;
    while (end < lines.length && (!lines[end].trim() || /^ */u.exec(lines[end])[0].length > indent)) end++;
    const script = lines.slice(index + 1, end).map((line) => line.slice(indent + 2)).join("\n")
      .replaceAll(/\$\{\{[\s\S]*?\}\}/gu, "GITHUB_EXPRESSION");
    if (shell !== "pwsh") blocks.push({ line: index + 1, script });
    index = end - 1;
  }
  return blocks;
}

test("workflows have valid Bash, pinned actions and no release-asset overwrites", async () => {
  const names = (await readdir(directory)).filter((name) => /\.ya?ml$/u.test(name));
  assert.ok(names.length > 0);
  for (const name of names) {
    const source = await readFile(resolve(directory, name), "utf8");
    for (const { line, script } of shellBlocks(source)) {
      const result = spawnSync("bash", ["-n", "-s"], { input: script, encoding: "utf8" });
      assert.equal(result.status, 0, `${name}:${line}: ${result.stderr}`);
    }
    for (const [, action] of source.matchAll(/uses:\s+([^\s#]+)/gu)) {
      assert.match(action, /^(?:\.\/\.github\/workflows\/[\w.-]+\.ya?ml|[\w-]+\/[\w-]+@[a-f0-9]{40})$/u);
    }
    for (const [runner] of source.matchAll(/\bmacos-[\w-]+\b/gu)) {
      assert.ok(["macos-26", "macos-26-intel"].includes(runner), `${name}: unexpected macOS runner ${runner}`);
    }
    assert.doesNotMatch(source, /--clobber|overwrite:\s*true/u);
  }
});

test("component tags finalize the same verified assets after their build and test jobs", async () => {
  for (const component of ["app", "cli", "host", "sdk"]) {
    const source = await workflow(`${component}-release`);
    assert.match(source, new RegExp(`tags: \\["${component}/v\\*"\\]`, "u"));
    assert.doesNotMatch(source, /workflow_dispatch:|production.acceptance|SUNDER_.*ACCEPTANCE_DRIVER/u);
    assert.match(source, /group: sunder-release-\$\{\{ github\.ref_name \}\}/u);
    assert.match(source, /github_release\.py stage/u);
    assert.match(source, /github_release\.py finalize/u);
    assert.match(source, /environment: sunder-github-release-promotion/u);
    const publication = source.slice(source.lastIndexOf(component === "sdk" ? "\n  finalize:" : "\n  release:"));
    requiresJobs(publication, component === "sdk" ? ["prepare", "stage", "promote-npm"] : ["prepare", "package"]);
    const coordinate = component === "app" ? "runtime" : component === "sdk" ? "lane" : "rid";
    assert.match(source, new RegExp(`${coordinate}: win-x64, os: windows-2025`, "u"));
    // App signing intentionally runs its x64 client for both Windows packages.
    const armRunner = component === "app" ? "windows-2025" : "windows-11-arm";
    assert.match(source, new RegExp(`${coordinate}: win-arm64, os: ${armRunner}`, "u"));
    assert.match(source, new RegExp(`${coordinate}: osx-x64, os: macos-26-intel`, "u"));
    assert.match(source, new RegExp(`${coordinate}: osx-arm64, os: macos-26`, "u"));
  }
});

test("SDK publication consumes tested packages and keeps registry credentials in their owning jobs", async () => {
  const source = await workflow("sdk-release");
  assert.match(source, /uses: \.\/\.github\/workflows\/sunder-ci\.yml/u);
  assert.match(source, /name: sunder-tested-sdk/u);
  assert.match(source, /cp candidate\/sdk\/\*\.nupkg candidate\/npm\/\*\.tgz release-assets\//u);
  assert.doesNotMatch(source, /dotnet (?:pack|publish)|npm pack/u);
  assert.match(source, /core_catalog_release\.py export/u);
  assert.match(source, /core_catalog_release\.py retain/u);
  assert.match(source, /sunder-platform-contracts\.receipt\.v1\.json/u);
  const npm = source.slice(source.indexOf("\n  publish-npm:"), source.indexOf("\n  publish-nuget:"));
  const nuget = source.slice(source.indexOf("\n  publish-nuget:"), source.indexOf("\n  promote-npm:"));
  const promote = source.slice(source.indexOf("\n  promote-npm:"), source.indexOf("\n  finalize:"));
  const finalize = source.slice(source.indexOf("\n  finalize:"));
  assert.match(npm, /id-token: write/u);
  requiresJobs(npm, ["prepare", "stage"]);
  assert.match(npm, /preflight-npm-trusted-publishing\.sh[\s\S]+publish-sdk\.sh npm/u);
  assert.doesNotMatch(npm, /NUGET_API_KEY|NPM_DIST_TAG_TOKEN|contents: write/u);
  requiresJobs(nuget, ["prepare", "stage", "publish-npm"]);
  assert.match(nuget, /NUGET_API_KEY/u);
  assert.doesNotMatch(nuget, /id-token: write|NPM_DIST_TAG_TOKEN|contents: write/u);
  requiresJobs(promote, ["prepare", "stage", "publish-nuget"]);
  assert.match(promote, /environment: sunder-sdk-npm-promotion/u);
  assert.match(promote, /NPM_DIST_TAG_TOKEN/u);
  assert.doesNotMatch(promote, /id-token: write|NUGET_API_KEY|contents: write/u);
  assert.match(finalize, /publish-sdk\.sh verify[\s\S]+github_release\.py finalize/u);
  assert.doesNotMatch(finalize, /id-token: write|NUGET_API_KEY|NPM_DIST_TAG_TOKEN/u);
});

test("SDK stable and prerelease publication require a new retained compatibility qualification", async () => {
  const source = await workflow("sdk-release");
  const stage = source.slice(source.indexOf("\n  stage:"), source.indexOf("\n  publish-npm:"));
  const gate = stage.indexOf("sdk_release_compatibility.py prepare");
  assert.ok(gate > stage.indexOf("name: sunder-tested-sdk"));
  assert.ok(gate < stage.indexOf("github_release.py create"));
  assert.ok(gate < stage.indexOf("github_release.py stage"));
  assert.match(stage, /SUNDER_SDK_COMPATIBILITY_BASE_COMMIT: \$\{\{ vars\.SUNDER_SDK_COMPATIBILITY_BASE_COMMIT \}\}/u);
  assert.match(stage, /id: qualification\b/u);
  for (const job of ["publish-npm", "publish-nuget", "promote-npm", "finalize"]) {
    const section = source.slice(source.indexOf(`\n  ${job}:`) + 1).split(/\n  [a-z-]+:/u)[0];
    requiresJobs(section, ["prepare", "stage"]);
    for (const coordinate of ["sha256", "base_commit", "base_sha256"]) {
      assert.ok(section.includes(`needs.stage.outputs.compatibility_${coordinate}`), `${job}: ${coordinate}`);
    }
    assert.match(section, /actions\/setup-node@/u);
  }
  const publisher = await readFile(resolve(root, "scripts/release/publish-sdk.sh"), "utf8");
  assert.ok(publisher.indexOf('github_release.py" verify') < publisher.indexOf("npm publish"));
  assert.ok(publisher.indexOf('github_release.py" verify') < publisher.indexOf("dotnet nuget push"));
  assert.ok(publisher.indexOf('github_release.py" verify') < publisher.indexOf("npm dist-tag add"));
  const transport = await readFile(resolve(root, "scripts/release/github_release.py"), "utf8");
  assert.match(transport, /if component == "sdk":\n\s+verify_sdk_compatibility\(args.directory\)/u);
  assert.match(transport, /if component == "sdk":\n\s+verify_sdk_compatibility\(directory\)/u);
  const ci = await workflow("ci");
  assert.match(ci, /artifacts\/compatibility\//u);
  assert.doesNotMatch(ci, /--release-version|sdk_release_compatibility.py prepare/u);
});

test("SDK corpus collection owns native context and transports evidence separately from candidate claims", async () => {
  const source = await workflow("sdk-release");
  const collector = source.slice(source.indexOf("\n  compatibility-corpus:"), source.indexOf("\n  stage:"));
  const stage = source.slice(source.indexOf("\n  stage:"), source.indexOf("\n  publish-npm:"));
  requiresJobs(collector, ["prepare", "validation"]);
  requiresJobs(stage, ["prepare", "validation", "compatibility-corpus"]);
  for (const lane of ["portable", "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]) {
    assert.match(collector, new RegExp(`lane: ${lane}, os:`, "u"));
  }
  assert.match(collector, /lane: win-arm64, os: windows-11-arm/u);
  assert.match(collector, /lane: osx-x64, os: macos-26-intel/u);
  assert.match(collector, /sdk_compatibility_execution.py collect[\s\S]+--lane "\$CORPUS_LANE"/u);
  assert.match(collector, /name: sunder-sdk-compatibility-lane-\$\{\{ matrix.lane \}\}/u);
  assert.doesNotMatch(collector, /if:|continue-on-error:|contents: write|secrets\.|id-token: write|ACCEPTANCE_DRIVER/u);
  assert.match(stage, /pattern: sunder-sdk-compatibility-lane-\*[\s\S]+path: compatibility-executions[\s\S]+merge-multiple: false/u);
  assert.match(stage, /prepare --candidate candidate --directory release-assets --executions compatibility-executions/u);
  const gate = await readFile(resolve(root, "scripts/compatibility/sdk-release-qualification.mjs"), "utf8");
  assert.doesNotMatch(gate, /parseTrxReport|parseJunitReport|result\.tests|receipt\.results/u);
  assert.match(gate, /sdk_compatibility_execution.py/u);
  assert.doesNotMatch(await workflow("ci"), /sdk_compatibility_execution.py collect/u);
});

test("native validation requires both actual vendor-runtime tests on all six architectures", async () => {
  const source = await workflow("host-validation");
  const platform = source.slice(source.indexOf("\n  platform:"), source.indexOf("\n  official-runtimes:"));
  for (const rid of ["linux-x64", "win-x64", "win-arm64", "osx-x64", "osx-arm64"]) {
    assert.match(platform, new RegExp(`rid: ${rid}`, "u"));
  }
  assert.match(platform, /rid: win-arm64, os: windows-11-arm/u);
  assert.match(platform, /--primary-lane host-platform/u);
  assert.match(platform, /rid: osx-x64, os: macos-26-intel/u);
  assert.match(platform, /rid: osx-arm64, os: macos-26/u);
  assert.match(platform, /--primary-lane app-packaging/u);
  assert.match(platform, /SUNDER_REQUIRE_NODE_OUTPUT_LOCK_INTEROP: "1"/u);
  assert.match(platform, /--require-test Sunder\.Package\.Build\.Tests\.PackageOutputLockTests\.SharedOutputLock_InteroperatesWithNodeProducerWhenAvailable/u);
  const official = source.slice(source.indexOf("\n  official-runtimes:"), source.indexOf("\n  packaging:"));
  for (const rid of ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]) {
    assert.match(official, new RegExp(`rid: ${rid}`, "u"));
  }
  assert.match(official, /rid: win-x64, os: windows-2025/u);
  assert.match(official, /rid: win-arm64, os: windows-11-arm/u);
  assert.match(source, /rid: osx-arm64, os: macos-26/u);
  assert.match(source, /--require-test Sunder\.Runtime\.Host\.Tests\.CurrentRidDotNetRuntimeHostIntegrationTests\.OfficialCurrentRidDotNetRuntime_LaunchesThinWorkerAndRebindIsAtomic/u);
  assert.match(source, /--require-test Sunder\.Runtime\.Host\.Tests\.CurrentRidNodeRuntimeHostIntegrationTests\.OfficialCurrentRidNode24Runtime_LaunchesThinWorkerAndRoutesCrossLanguageRpc/u);
  assert.match(official, /--require-test Sunder\.Runtime\.Host\.Tests\.CurrentRidDotNetRuntimeHostIntegrationTests\.OfficialCurrentRidDotNetRuntime_LifecycleFaultDrainsRealSdkHandlerBeforeDisposal/u);
  assert.match(official, /--require-test Sunder\.Runtime\.Host\.Tests\.ProcessRuntimeWorkerTests\.WorkerSdkPackagedExecutable_ExactArchiveInstallsThroughRuntimeHostAndRecordsReleaseEvidence/u);
  assert.match(source, /--strict-release/u);
  assert.match(source, /create-sunder-dmg\.sh/u);
  assert.match(source, /dmg_image\.py attach/u);
  assert.match(source, /dmg_image\.py detach/u);
  assert.doesNotMatch(source, /hdiutil detach.*\|\| true/u);
  assert.match(source, /Parser\]::ParseFile/u);
});

test("packaging evidence is required after success or failure but not when packaging was never attempted", async () => {
  const source = await workflow("host-validation");
  const platform = source.slice(source.indexOf("\n  platform:"), source.indexOf("\n  official-runtimes:"));
  assert.match(platform, /id: app_packaging\n\s+if: success\(\) && inputs\.include-app-packaging/u);
  const summary = /if: ([^\n]+)\n\s+run: (node scripts\/test-oracles\/summarize-core-test-results\.mjs artifacts\/test-results\/app-packaging[^\n]+)/u.exec(platform);
  assert.ok(summary, "Missing outcome-bound packaging evidence step");
  assert.match(summary[2], /summarize-core-test-results\.mjs artifacts\/test-results\/app-packaging --allow-partial --require-primary-lane app-packaging/u);
  const shouldSummarize = new Function("always", "steps", `return (${summary[1]});`);
  for (const outcome of ["success", "failure", "skipped", "cancelled", ""]) {
    assert.equal(shouldSummarize(() => true, { app_packaging: { outcome } }), ["success", "failure"].includes(outcome), outcome);
  }
  assert.doesNotMatch(platform, /continue-on-error|--allow-missing|--allow-empty/u);
  assert.match(platform, /if: always\(\)[\s\S]+--require-primary-lane host-platform/u);
});

test("Host releases omit App packaging while Core and App validation keep it enabled", async () => {
  const host = await workflow("host-release");
  assert.match(host, /include-app-packaging: false/u);
  for (const caller of ["ci", "app-release"]) {
    assert.doesNotMatch(await workflow(caller), /include-app-packaging: false/u);
  }
  const validation = await workflow("host-validation");
  assert.match(validation, /include-app-packaging:[\s\S]*?type: boolean\s+default: true/u);
  assert.match(validation.slice(validation.indexOf("\n  packaging:")), /if: inputs\.include-app-packaging/u);
});

test("CI audits successful owning jobs without repeating builds or suites", async () => {
  const source = await workflow("ci");
  const audit = source.slice(source.indexOf("\n  test-results:"));
  requiresJobs(audit, ["build-test-pack", "native"]);
  assert.match(audit, /--aggregate/u);
  for (const artifact of ["sunder-core-results", "sunder-platform-\\*", "official-runtime-\\*"]) {
    assert.match(audit, new RegExp(`(?:name|pattern): ${artifact}`, "u"));
  }
  assert.doesNotMatch(audit, /dotnet|npm|run-.*tests|if: always\(\)/u);
  assert.match(source, /group: sunder-core-ci-\$\{\{ github\.event\.pull_request\.number \|\| github\.run_id \}\}/u);
  assert.match(source, /cancel-in-progress: \$\{\{ github\.event_name == 'pull_request' \}\}/u);
  for (const testName of ["ExecutesExactNativeContractAcrossNavigation", "RejectsSeededNonceOwnershipMutation"]) {
    assert.ok(source.includes(`--require-test Sunder.App.Tests.AppWebBridgeTests.BootstrapJavaScriptCrossLanguageVm_${testName}`));
  }
});

test("strict App packaging retains signing, notarization and Full-only feeds", async () => {
  const source = await workflow("app-release");
  assert.match(source, /environment: sunder-app-signing/u);
  assert.match(source, /StrictRelease = \$true/u);
  const signing = source.split("      - name: Configure Azure Trusted Signing\n")[1]?.split("      - name: Configure Apple signing and notarization\n")[0];
  assert.ok(signing, "Missing Azure signing configuration step");
  const requiredSettings = /foreach \(\$name in @\(([^)]+)\)\)/u.exec(signing)?.[1] ?? "";
  assert.deepEqual([...requiredSettings.matchAll(/"([^"]+)"/gu)].map((match) => match[1]), [
    "AZURE_TRUSTED_SIGNING_ENDPOINT", "AZURE_TRUSTED_SIGNING_ACCOUNT_NAME", "AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME",
  ], "Publisher pinning must not become an additional required signing setting");
  assert.doesNotMatch(signing, /WINDOWS_PUBLISHER_SUBJECT/u);
  assert.match(source, /WINDOWS_PUBLISHER_SUBJECT: \$\{\{ vars\.WINDOWS_PUBLISHER_SUBJECT \|\| secrets\.WINDOWS_PUBLISHER_SUBJECT \}\}/u);
  assert.match(source, /WindowsPublisherSubject = \$env:WINDOWS_PUBLISHER_SUBJECT/u);
  assert.match(source, /WindowsSignParams = "[^\n]+\/dlib [^\n]+\/dmdf/u);
  assert.match(source, /--strict-release/u);
  assert.match(source, /codesign --verify --deep --strict/u);
  assert.match(source, /stapler validate/u);
  assert.match(source, /vpk upload local/u);
  assert.doesNotMatch(source, /vpk upload github/u);
  const shell = await readFile(resolve(root, "scripts/release/package-sunder.sh"), "utf8");
  const powershell = await readFile(resolve(root, "scripts/release/package-sunder.ps1"), "utf8");
  assert.match(powershell, /if \(\$hasWindowsSignParams\) \{\s+\$signature = Get-AuthenticodeSignature -LiteralPath \$setupPath\s+if \(\$signature\.Status -ne \[System\.Management\.Automation\.SignatureStatus\]::Valid\) \{\s+throw/u);
  assert.match(powershell, /if \(-not \[string\]::IsNullOrWhiteSpace\(\$WindowsPublisherSubject\) -and\s+\$signature\.SignerCertificate\.Subject -cne \$WindowsPublisherSubject\) \{\s+throw/u);
  const central = await readFile(resolve(root, "languages/csharp/dotnet/Directory.Packages.props"), "utf8");
  const version = /Include="Velopack" Version="([^"]+)"/u.exec(central)?.[1];
  assert.ok(version, "Missing Velopack library version");
  assert.equal(/VELOPACK_VERSION: "([^"]+)"/u.exec(source)?.[1], version);
  const validation = await workflow("host-validation");
  assert.equal(/vpk --version ([\d.]+)/u.exec(validation)?.[1], version);
  for (const script of [shell, powershell]) {
    assert.equal(/dotnet tool install --global vpk --version ([\d.]+)/u.exec(script)?.[1], version);
  }
  assert.match(shell, /strict_release" != "true" && -n "\$github_repository_url"/u);
  assert.match(shell, /strict_delta_packages[\s\S]+history-dependent delta/u);
  assert.match(powershell, /-not \$StrictRelease -and -not \[string\]::IsNullOrWhiteSpace\(\$GitHubRepositoryUrl\)/u);
  assert.match(powershell, /strictDeltaPackages[\s\S]+history-dependent delta/u);
});
