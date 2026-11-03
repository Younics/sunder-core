import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile, readdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const directory = resolve(root, ".github/workflows");
const workflow = (name) => readFile(resolve(directory, `sunder-${name}.yml`), "utf8");
const owned = (name) => readFile(resolve(root, name), "utf8");
const matrices = JSON.parse(spawnSync("python3", ["-B", "scripts/ci/metadata.py", "matrices"], { cwd: root, encoding: "utf8" }).stdout);

function requiresJobs(source, jobs) {
  const needs = /^\s*needs:\s*\[([^\]]+)\]/mu.exec(source)?.[1].split(",").map((name) => name.trim()) ?? [];
  for (const job of jobs) assert.ok(needs.includes(job), `Missing required job dependency: ${job}`);
}

function workflowStep(source, name) {
  const start = source.indexOf(`      - name: ${name}\n`);
  assert.ok(start >= 0, `Missing workflow step: ${name}`);
  const end = source.indexOf("\n      - ", start + 1);
  return source.slice(start, end < 0 ? undefined : end);
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
      assert.match(action, /^(?:\.\/\.github\/(?:workflows\/[\w.-]+\.ya?ml|actions\/[\w.-]+)|[\w-]+\/[\w-]+@[a-f0-9]{40})$/u);
    }
    for (const [runner] of source.matchAll(/\bmacos-[\w-]+\b/gu)) {
      assert.ok(["macos-26", "macos-26-intel"].includes(runner), `${name}: unexpected macOS runner ${runner}`);
    }
    assert.doesNotMatch(source, /--clobber|overwrite:\s*true/u);
  }
});

test("component tags finalize the same verified assets after their build and test jobs", async () => {
  for (const component of ["app", "cli", "host"]) {
    const source = await workflow(`${component}-release`);
    assert.match(source, new RegExp(`tags: \\["${component}/v\\*"\\]`, "u"));
    assert.doesNotMatch(source, /production.acceptance|SUNDER_.*ACCEPTANCE_DRIVER/u);
    assert.doesNotMatch(source, /workflow_dispatch:/u);
    assert.match(source, /group: sunder-release-\$\{\{ github\.ref_name \}\}/u);
    assert.match(source, /github_release\.py stage/u);
    assert.match(source, /github_release\.py finalize/u);
    assert.match(source, /environment: sunder-github-release-promotion/u);
    const publication = source.slice(source.lastIndexOf("\n  release:"));
    requiresJobs(publication, ["prepare", "package"]);
    assert.match(source, /matrix: \$\{\{ fromJSON\(needs.prepare.outputs.matrix\) \}\}/u);
    const matrix = matrices[component === "app" ? "app" : "native"].include;
    assert.equal(matrix.find(row => row.rid === "win-x64").os, "windows-2025");
    // App signing intentionally runs its x64 client for both Windows packages.
    const armRunner = component === "app" ? "windows-2025" : "windows-11-arm";
    assert.equal(matrix.find(row => row.rid === "win-arm64").os, armRunner);
    assert.equal(matrix.find(row => row.rid === "osx-x64").os, "macos-26-intel");
    assert.equal(matrix.find(row => row.rid === "osx-arm64").os, "macos-26");
  }
});

test("project selection precedes release packaging without tag redispatch or manual coordinates", async () => {
  const source = await workflow("sdk-release");
  const select = source.slice(source.indexOf("\n  select:"), source.indexOf("\n  validation:"));
  assert.match(select, /github.ref == 'refs\/heads\/main'/u);
  assert.match(select, /contents: write/u);
  assert.match(select, /sdk_release.py select/u);
  assert.doesNotMatch(select, /environment:|secrets\.|id-token:|npm (?:ci|install|pack)/u);
  assert.match(source, /group: sunder-core-package-release/u);
  assert.doesNotMatch(source, /tags:|dispatches|rerun-failed|COMPATIBILITY_BASE|REGISTRY_ORIGIN|ARTIFACT_SOURCE_COMMIT|APPROVAL_REFERENCE/u);
  const ci = await workflow("ci");
  const pack = ci.indexOf('sdk_release.py pack');
  assert.ok(ci.indexOf("core_ci.py source") > 0 && pack > ci.indexOf("core_ci.py source"));
  const packStep = ci.slice(ci.lastIndexOf("      - name:", pack), ci.indexOf("      - name:", pack));
  assert.match(packStep, /if: inputs.release-packages/u);
  assert.match(packStep, /sdk_release.py feed/u);
  assert.doesNotMatch(ci, /dotnet pack|npm pack --prefix/u);
  assert.doesNotMatch(ci, /all_new|coordinatedVersion|artifacts\/compatibility\//u);
  assert.match(ci, /path: artifacts\/packages\/\*/u);
  assert.doesNotMatch(ci, /^  push:/mu, "The main release pipeline owns main CI; do not build it twice.");
});

test("SDK publication consumes tested packages and keeps registry credentials in their owning jobs", async () => {
  const source = await workflow("sdk-release");
  assert.match(source, /uses: \.\/\.github\/workflows\/sunder-ci\.yml/u);
  assert.match(source, /name: sunder-tested-sdk/u);
  assert.doesNotMatch(source, /\b(?:dotnet (?:pack|publish)|npm pack)\b/u);
  assert.match(source, /sdk_release.py stage --directory artifacts\/retained-packages/u);
  for (const [, output] of source.matchAll(/sdk_release.py \w+ --directory ([^\s]+)/gu)) {
    assert.equal(spawnSync("git", ["check-ignore", "-q", "--no-index", `${output}/archive.nupkg`], { cwd: root }).status, 0,
      `Release handoff ${output} would violate the clean-source guard.`);
  }
  const npm = source.slice(source.indexOf("\n  publish-npm:"), source.indexOf("\n  publish-nuget:"));
  const nuget = source.slice(source.indexOf("\n  publish-nuget:"), source.indexOf("\n  finalize:"));
  const finalize = source.slice(source.indexOf("\n  finalize:"));
  assert.match(npm, /id-token: write/u);
  requiresJobs(npm, ["select", "stage"]);
  assert.match(npm, /NODE_AUTH_TOKEN: \$\{\{ secrets\.NPM_TOKEN \}\}/u);
  assert.match(npm, /sdk_release.py npm --directory artifacts\/retained-packages/u);
  assert.doesNotMatch(npm, /NUGET_API_KEY|NPM_DIST_TAG_TOKEN/u);
  requiresJobs(nuget, ["select", "stage", "publish-npm"]);
  assert.match(nuget, /environment: sunder-core-release/u);
  assert.match(nuget, /permissions:\n\s+contents: write\n\s+id-token: write/u);
  assert.match(nuget, /id: nuget-login\n\s+uses: NuGet\/login@[a-f0-9]{40} # v1\n\s+with:\n\s+user: \$\{\{ vars\.NUGET_USER \|\| secrets\.NUGET_USER \}\}/u);
  assert.match(nuget, /NUGET_API_KEY: \$\{\{ steps\.nuget-login\.outputs\.NUGET_API_KEY \}\}/u);
  const download = nuget.indexOf('sdk_release.py download');
  const login = nuget.indexOf("uses: NuGet/login@");
  const publish = nuget.indexOf('sdk_release.py nuget');
  assert.ok(download >= 0 && login > download && publish > login, "NuGet login must obtain a fresh credential after downloading retained bytes and before publication.");
  assert.doesNotMatch(nuget.slice(login, publish), /sdk_release\.py download|dotnet (?:build|pack)|npm (?:install|pack)/u);
  assert.doesNotMatch(nuget.replaceAll("secrets.NUGET_USER", ""), /secrets\.|NPM_TOKEN|NODE_AUTH_TOKEN/u);
  assert.doesNotMatch(source, /secrets\.NUGET_API_KEY/u);
  assert.equal([...source.matchAll(/secrets\.NPM_TOKEN\b/gu)].length, 1);
  assert.doesNotMatch(source, /NPM_DIST_TAG_TOKEN|preflight-npm-trusted-publishing/u);
  assert.match(finalize, /sdk_release\.py download[\s\S]+sdk_release\.py finalize/u);
  assert.doesNotMatch(finalize, /id-token: write|NUGET_API_KEY|NPM_TOKEN|NODE_AUTH_TOKEN/u);
});

test("NuGet-only and npm-only publication can skip the other registry without skipping finalization", async () => {
  const source = await workflow("sdk-release");
  assert.match(source, /if: always\(\) && needs.stage.result == 'success' && needs.select.outputs.npm_count != '0'/u);
  const nuget = source.slice(source.indexOf("\n  publish-nuget:"), source.indexOf("\n  finalize:"));
  assert.match(nuget, /always\(\)[^\n]+nuget_count != '0'[^\n]+needs.publish-npm.result == 'skipped'/u);
  const finalize = source.slice(source.indexOf("\n  finalize:"));
  requiresJobs(finalize, ["select", "stage", "publish-npm", "publish-nuget"]);
  assert.match(finalize, /always\(\)[^\n]+needs.publish-npm.result == 'skipped'[^\n]+needs.publish-nuget.result == 'skipped'/u);
});

test("release reuses ordinary native validation and source-derived catalog export", async () => {
  const source = await workflow("sdk-release");
  const stage = source.slice(source.indexOf("\n  stage:"), source.indexOf("\n  publish-npm:"));
  requiresJobs(stage, ["select", "validation"]);
  assert.match(stage, /needs.validation.result == 'success'/u);
  assert.match(stage, /needs.select.outputs.recover == 'true'/u);
  const ci = await workflow("ci");
  assert.match(ci, /uses: \.\/\.github\/workflows\/sunder-host-validation.yml/u);
  assert.match(ci, /core_ci.py templates/u);
  assert.match(await owned("scripts/ci/core_ci.py"), /"platform-contracts", "export"[\s\S]+sunder-platform-contracts.v1.json/u);
  assert.doesNotMatch(source, /qualification.json|recipe-templates|compatibility-corpus/u);
});

test("native validation requires both actual vendor-runtime tests on all six architectures", async () => {
  const source = await workflow("host-validation");
  const platform = source.slice(source.indexOf("\n  platform:"), source.indexOf("\n  official-runtimes:"));
  assert.deepEqual(matrices.native.include.map(row => row.rid).sort(), ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"]);
  assert.match(platform, /matrix: \$\{\{ fromJSON\(needs.prepare.outputs.native\) \}\}/u);
  assert.equal(matrices.native.include.find(row => row.rid === "linux-arm64").os, "ubuntu-24.04-arm");
  assert.equal(matrices.native.include.find(row => row.rid === "win-arm64").os, "windows-11-arm");
  assert.match(platform, /--primary-lane host-platform/u);
  const preflight = workflowStep(platform, "Verify owned test process cleanup on the native OS");
  assert.match(preflight, /id: process_preflight/u);
  assert.match(preflight, /run-node-tests-with-results\.mjs --results-directory artifacts\/process-preflight\s+node-process-preflight scripts\/test-oracles\/owned-test-process\.test\.mjs/u);
  assert.match(preflight, /SUNDER_TEST_PROCESS_DIAGNOSTICS_DIRECTORY:.*artifacts\/process-preflight/u);
  for (const prerequisite of ["actions/setup-dotnet@", "Prepare native/process prerequisites", "Execute the platform lane"]) {
    assert.ok(platform.indexOf("Verify owned test process cleanup") < platform.indexOf(prerequisite));
  }
  assert.match(platform, /--primary-lane app-packaging/u);
  assert.match(platform, /--scenario output-lock/u);
  assert.match(platform, /--require-scenario output-lock/u);
  const official = source.slice(source.indexOf("\n  official-runtimes:"), source.indexOf("\n  packaging:"));
  assert.match(official, /matrix: \$\{\{ fromJSON\(needs.prepare.outputs.native\) \}\}/u);
  assert.match(official, /--scenario official --build/u);
  assert.match(official, /--require-scenario official/u);
  const smoke = await owned("scripts/test/release/smoke-app-packaging.sh");
  const dmg = await owned("scripts/release/macos/verify-sunder-dmg.sh");
  assert.match(source, /smoke-app-packaging.sh/u);
  assert.match(smoke, /--strict-release/u);
  assert.match(smoke, /create-sunder-dmg\.sh/u);
  assert.match(dmg, /dmg_image\.py" attach/u);
  assert.match(dmg, /dmg_image\.py" detach/u);
  assert.doesNotMatch(dmg, /hdiutil detach.*\|\| true/u);
  assert.match(await owned("scripts/ci/check-powershell.ps1"), /Parser\]::ParseFile/u);
});

test("reporting follows attempted execution and uploads retain cancelled-run diagnostics", async () => {
  const sources = { native: await workflow("host-validation"), ci: await workflow("ci") };
  for (const [owner, name, identity, upload] of [
    ["native", "Summarize platform test evidence", "platform_tests", false],
    ["native", "Summarize App packaging test evidence", "app_packaging", false],
    ["native", "Require executed Windows output-lock interoperability", "output_lock_interop", false],
    ["native", "Require executed native results", "official_tests", false],
    ["native", "Retain process preflight diagnostics", "process_preflight", true],
    ["native", "Retain platform test results", "platform_tests", true],
    ["native", "Retain official runtime test results", "official_tests", true],
    ["ci", "Retain test reports", "node_tests", true],
  ]) {
    const source = sources[owner];
    assert.ok(source.includes(`id: ${identity}\n`));
    const block = workflowStep(source, name);
    const condition = /\n\s+if: ([^\n]+)/u.exec(block)?.[1];
    assert.ok(condition?.startsWith("always()"), name);
    const shouldRun = new Function("always", "steps", `return (${condition});`);
    for (const outcome of ["success", "failure", "skipped", "cancelled", ""]) {
      assert.equal(shouldRun(() => true, { [identity]: { outcome } }),
        ["success", "failure", ...(upload ? ["cancelled"] : [])].includes(outcome), `${name}: ${outcome}`);
    }
    assert.match(block, upload ? /if-no-files-found: error/u : /summarize-core-test-results\.mjs/u);
  }
  assert.match(sources.native, /id: app_packaging\n\s+if: success\(\) && inputs\.include-app-packaging/u);
  assert.match(sources.native, /id: output_lock_interop\n\s+if: runner\.os == 'Windows'/u);
  assert.doesNotMatch(sources.native, /continue-on-error|--allow-missing|--allow-empty/u);
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
  assert.doesNotMatch(audit, /dotnet (?:build|test)|npm (?:ci|run)|run-.*tests|if: always\(\)/u);
  assert.match(source, /group: sunder-core-ci-\$\{\{ github\.event\.pull_request\.number \|\| github\.run_id \}\}/u);
  assert.match(source, /cancel-in-progress: \$\{\{ github\.event_name == 'pull_request' \}\}/u);
  assert.match(source, /--require-scenario interop/u);
});

test("strict App packaging retains signing, notarization and Full-only feeds", async () => {
  const source = await workflow("app-release");
  assert.match(source, /environment: sunder-app-signing/u);
  assert.match(source, /-StrictRelease/u);
  assert.match(source, /configure-windows-signing.ps1/u);
  const signing = await owned("scripts/release/configure-windows-signing.ps1");
  const requiredSettings = /foreach \(\$name in @\(([^)]+)\)\)/u.exec(signing)?.[1] ?? "";
  assert.deepEqual([...requiredSettings.matchAll(/'([^']+)'/gu)].map((match) => match[1]), [
    "AZURE_TRUSTED_SIGNING_ENDPOINT", "AZURE_TRUSTED_SIGNING_ACCOUNT_NAME", "AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME",
  ], "Publisher pinning must not become an additional required signing setting");
  assert.doesNotMatch(signing, /WINDOWS_PUBLISHER_SUBJECT/u);
  assert.match(source, /WINDOWS_PUBLISHER_SUBJECT: \$\{\{ vars\.WINDOWS_PUBLISHER_SUBJECT \|\| secrets\.WINDOWS_PUBLISHER_SUBJECT \}\}/u);
  assert.match(source, /-WindowsPublisherSubject \$env:WINDOWS_PUBLISHER_SUBJECT/u);
  assert.match(source, /-WindowsSignParams "[^\n]+\/dlib [^\n]+\/dmdf/u);
  assert.match(source, /--strict-release/u);
  const dmg = await owned("scripts/release/macos/verify-sunder-dmg.sh");
  assert.match(source, /verify-sunder-dmg.sh/u);
  assert.match(dmg, /codesign --verify --deep --strict/u);
  assert.match(dmg, /stapler validate/u);
  const feeds = await owned("scripts/release/generate-app-feeds.py");
  assert.match(source, /generate-app-feeds.py/u);
  assert.match(feeds, /"vpk", "upload", "local"/u);
  assert.doesNotMatch(feeds, /"upload", "github"/u);
  const shell = await readFile(resolve(root, "scripts/release/package-sunder.sh"), "utf8");
  const powershell = await readFile(resolve(root, "scripts/release/package-sunder.ps1"), "utf8");
  assert.match(powershell, /if \(\$hasWindowsSignParams\) \{\s+\$signature = Get-AuthenticodeSignature -LiteralPath \$setupPath\s+if \(\$signature\.Status -ne \[System\.Management\.Automation\.SignatureStatus\]::Valid\) \{\s+throw/u);
  assert.match(powershell, /if \(-not \[string\]::IsNullOrWhiteSpace\(\$WindowsPublisherSubject\) -and\s+\$signature\.SignerCertificate\.Subject -cne \$WindowsPublisherSubject\) \{\s+throw/u);
  const central = await readFile(resolve(root, "languages/csharp/dotnet/Directory.Packages.props"), "utf8");
  const version = /Include="Velopack" Version="([^"]+)"/u.exec(central)?.[1];
  assert.ok(version, "Missing Velopack library version");
  const versions = JSON.parse(spawnSync("python3", ["-B", "scripts/ci/metadata.py", "toolchains"], { cwd: root, encoding: "utf8" }).stdout);
  assert.equal(versions.velopack, version);
  assert.match(await owned("scripts/release/install-velopack.ps1"), /vpk --version \$versions.velopack/u);
  for (const script of [shell, powershell]) {
    assert.equal(/dotnet tool install --global vpk --version ([\d.]+)/u.exec(script)?.[1], version);
  }
  assert.match(shell, /strict_release" != "true" && -n "\$github_repository_url"/u);
  assert.match(shell, /strict_delta_packages[\s\S]+history-dependent delta/u);
  assert.match(powershell, /-not \$StrictRelease -and -not \[string\]::IsNullOrWhiteSpace\(\$GitHubRepositoryUrl\)/u);
  assert.match(powershell, /strictDeltaPackages[\s\S]+history-dependent delta/u);
});
