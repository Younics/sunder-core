import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile, readdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const directory = resolve(root, ".github/workflows");
const workflow = (name) => readFile(resolve(directory, `sunder-${name}.yml`), "utf8");

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

test("the six workflow entry points have valid Bash and pinned actions", async () => {
  const names = (await readdir(directory)).filter((name) => name.endsWith(".yml")).sort();
  assert.deepEqual(names, ["sunder-app-release.yml", "sunder-ci.yml", "sunder-cli-release.yml",
    "sunder-host-release.yml", "sunder-host-validation.yml", "sunder-sdk-release.yml"]);
  for (const name of names) {
    const source = await readFile(resolve(directory, name), "utf8");
    for (const { line, script } of shellBlocks(source)) {
      const result = spawnSync("bash", ["-n", "-s"], { input: script, encoding: "utf8" });
      assert.equal(result.status, 0, `${name}:${line}: ${result.stderr}`);
    }
    for (const [, action] of source.matchAll(/uses:\s+([^\s#]+)/gu)) {
      assert.match(action, /^(?:\.\/\.github\/workflows\/sunder-[a-z-]+\.yml|[\w-]+\/[\w-]+@[a-f0-9]{40})$/u);
    }
    assert.doesNotMatch(source, /-m:1|macos-14|--clobber|overwrite:\s*true/u);
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
    assert.match(source, /needs: \[prepare, (?:package|stage, promote-npm)\]/u);
  }
});

test("SDK publication consumes tested packages and keeps registry credentials in their owning jobs", async () => {
  const source = await workflow("sdk-release");
  assert.match(source, /uses: \.\/\.github\/workflows\/sunder-ci\.yml/u);
  assert.match(source, /name: sunder-tested-sdk/u);
  assert.doesNotMatch(source, /dotnet (?:pack|publish)|npm pack/u);
  assert.match(source, /core_catalog_release\.py export/u);
  assert.match(source, /core_catalog_release\.py retain/u);
  assert.match(source, /sunder-platform-contracts\.receipt\.v1\.json/u);
  const npm = source.slice(source.indexOf("\n  publish-npm:"), source.indexOf("\n  publish-nuget:"));
  const nuget = source.slice(source.indexOf("\n  publish-nuget:"), source.indexOf("\n  promote-npm:"));
  const promote = source.slice(source.indexOf("\n  promote-npm:"), source.indexOf("\n  finalize:"));
  const finalize = source.slice(source.indexOf("\n  finalize:"));
  assert.match(npm, /id-token: write/u);
  assert.match(npm, /preflight-npm-trusted-publishing\.sh[\s\S]+publish-sdk\.sh npm/u);
  assert.doesNotMatch(npm, /NUGET_API_KEY|NPM_DIST_TAG_TOKEN|contents: write/u);
  assert.match(nuget, /needs: \[prepare, stage, publish-npm\]/u);
  assert.match(nuget, /NUGET_API_KEY/u);
  assert.doesNotMatch(nuget, /id-token: write|NPM_DIST_TAG_TOKEN|contents: write/u);
  assert.match(promote, /needs: \[prepare, stage, publish-nuget\]/u);
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
  assert.match(stage, /id: qualification\n        run: python3 -B scripts\/compatibility\/sdk_release_compatibility.py prepare/u);
  for (const job of ["publish-npm", "publish-nuget", "promote-npm", "finalize"]) {
    const section = source.slice(source.indexOf(`\n  ${job}:`) + 1).split(/\n  [a-z-]+:/u)[0];
    assert.match(section, /needs: \[prepare, stage(?:, [a-z-]+)?\]/u);
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
  assert.match(collector, /needs: \[prepare, validation\]/u);
  assert.match(stage, /needs: \[prepare, validation, compatibility-corpus\]/u);
  for (const lane of ["portable", "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]) {
    assert.match(collector, new RegExp(`lane: ${lane}, os:`, "u"));
  }
  assert.match(collector, /lane: win-arm64, os: windows-11-arm/u);
  assert.match(collector, /lane: osx-x64, os: macos-15-intel/u);
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
  for (const rid of ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"]) {
    assert.match(source, new RegExp(`rid: ${rid}`, "u"));
  }
  assert.match(source, /rid: win-arm64, os: windows-11-arm/u);
  assert.match(source, /rid: osx-arm64, os: macos-15/u);
  assert.match(source, /--require-test Sunder\.Runtime\.Host\.Tests\.CurrentRidDotNetRuntimeHostIntegrationTests\.OfficialCurrentRidDotNetRuntime_LaunchesThinWorkerAndRebindIsAtomic/u);
  assert.match(source, /--require-test Sunder\.Runtime\.Host\.Tests\.CurrentRidNodeRuntimeHostIntegrationTests\.OfficialCurrentRidNode24Runtime_LaunchesThinWorkerAndRoutesCrossLanguageRpc/u);
  assert.match(source, /--primary-lane app-packaging/u);
  assert.match(source, /--strict-release/u);
  assert.match(source, /create-sunder-dmg\.sh/u);
  assert.match(source, /Parser\]::ParseFile/u);
});

test("strict App packaging retains signing, notarization and Full-only feeds", async () => {
  const source = await workflow("app-release");
  assert.match(source, /environment: sunder-app-signing/u);
  assert.match(source, /StrictRelease = \$true/u);
  assert.match(source, /--strict-release/u);
  assert.match(source, /codesign --verify --deep --strict/u);
  assert.match(source, /stapler validate/u);
  assert.match(source, /vpk upload local/u);
  assert.doesNotMatch(source, /vpk upload github/u);
  const shell = await readFile(resolve(root, "scripts/release/package-sunder.sh"), "utf8");
  const powershell = await readFile(resolve(root, "scripts/release/package-sunder.ps1"), "utf8");
  assert.match(shell, /strict_release" != "true" && -n "\$github_repository_url"/u);
  assert.match(shell, /strict_delta_packages[\s\S]+history-dependent delta/u);
  assert.match(powershell, /-not \$StrictRelease -and -not \[string\]::IsNullOrWhiteSpace\(\$GitHubRepositoryUrl\)/u);
  assert.match(powershell, /strictDeltaPackages[\s\S]+history-dependent delta/u);
});
