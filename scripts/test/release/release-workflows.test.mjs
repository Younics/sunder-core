import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile, readdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const directory = resolve(root, ".github/workflows");
const owned = name => readFile(resolve(root, name), "utf8");
const workflow = await owned(".github/workflows/sunder-sdk-release.yml");
const jobs = Object.fromEntries([...workflow.slice(workflow.indexOf("\njobs:\n")).matchAll(/\n  ([\w-]+):\n([\s\S]*?)(?=\n  [\w-]+:\n|$)/gu)]
  .map(([, name, definition]) => [name, definition]));

function dependencies(name) {
  const value = /^    needs: (.*)$/mu.exec(jobs[name])?.[1] ?? "";
  return value.replaceAll(/[\[\]]/gu, "").split(",").map(value => value.trim()).filter(Boolean);
}

function mayRun(name, needs) {
  const expression = /^    if: (.*)$/mu.exec(jobs[name])?.[1];
  const success = dependencies(name).every(name => needs[name]?.result === "success");
  if (expression === undefined) return success;
  if (!/\b(?:always|cancelled|success|failure)\(/u.test(expression) && !success) return false;
  const code = expression.replace(/^\$\{\{\s*|\s*\}\}$/gu, "").replaceAll(/needs\.([\w-]+)/gu, 'needs["$1"]');
  return new Function("needs", "always", "cancelled", `return (${code});`)(needs, () => true, () => false);
}

function state(outputs = {}) {
  const needs = Object.fromEntries(Object.keys(jobs).map(name => [name, { result: "skipped", outputs: {} }]));
  needs.select = { result: "success", outputs: { work: "false", selected_count: "0", nuget_count: "0", npm_count: "0", recover: "false",
    native_count: "0", native_build_count: "0", app_packages: "false", binary_packages: "false", ...outputs } };
  return needs;
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
    const shell = lines.slice(start, index).find(line => /^\s*shell:/u.test(line))?.trim().slice(7);
    let end = index + 1;
    while (end < lines.length && (!lines[end].trim() || /^ */u.exec(lines[end])[0].length > indent)) end++;
    const script = lines.slice(index + 1, end).map(line => line.slice(indent + 2)).join("\n")
      .replaceAll(/\$\{\{[\s\S]*?\}\}/gu, "GITHUB_EXPRESSION");
    if (shell !== "pwsh") blocks.push({ line: index + 1, script });
    index = end - 1;
  }
  return blocks;
}

test("one workflow retains publisher identity, valid Bash and pinned actions", async () => {
  assert.deepEqual((await readdir(directory)).filter(name => /\.ya?ml$/u.test(name)), ["sunder-sdk-release.yml"]);
  assert.match(workflow, /^name: Release\n/u);
  assert.doesNotMatch(workflow, /uses: \.\/\.github\/workflows\/|workflow_call:|tags:|dispatches|rerun-failed|--clobber|overwrite:\s*true/u);
  for (const { line, script } of shellBlocks(workflow)) {
    const result = spawnSync("bash", ["-n", "-s"], { input: script, encoding: "utf8" });
    assert.equal(result.status, 0, `line ${line}: ${result.stderr}`);
  }
  for (const [, action] of workflow.matchAll(/uses:\s+([^\s#]+)/gu)) {
    assert.match(action, /^(?:\.\/\.github\/actions\/[\w.-]+|[\w-]+\/[\w-]+@[a-f0-9]{40})$/u);
  }
  assert.match(workflow, /'release-main'/u);
  assert.match(workflow, /cancel-in-progress: \$\{\{ github.event_name == 'pull_request' \}\}/u);
});

test("selection is toolchain-free and PR checks never receive a write-capable job", () => {
  assert.match(jobs.select, /github.ref == 'refs\/heads\/main'/u);
  assert.match(jobs.select, /github.repository == 'Younics\/sunder-core'/u);
  assert.match(jobs.select, /contents: write/u);
  assert.match(jobs["select-checks"], /contents: read/u);
  assert.doesNotMatch(jobs["select-checks"], /contents: write|GH_TOKEN:|environment:|secrets\.|id-token:/u);
  for (const name of ["select", "select-checks"]) {
    assert.match(jobs[name], /select_releases.py select/u);
    assert.doesNotMatch(jobs[name], /setup-dotnet|setup-node|\.github\/actions\/setup|npm (?:ci|install|pack)|dotnet (?:restore|build|test)/u);
  }
  for (const name of ["components", "platform", "native-consumers", "package-consumers", "validation"]) {
    assert.match(jobs[name], /contents: read/u);
    assert.doesNotMatch(jobs[name], /contents: write|environment:|secrets\.|id-token:/u);
  }
});

test("no-op pushes stop after selection and failed selection cannot start validation", () => {
  const needs = state();
  for (const name of Object.keys(jobs).filter(name => !["select", "select-checks"].includes(name))) {
    assert.equal(mayRun(name, needs), false, name);
  }
  needs.select.outputs.work = "true";
  needs.select.result = "failure";
  assert.equal(mayRun("preflight", needs), false);
  assert.equal(mayRun("summary", needs), true);
});

test("selected checks cross the skipped entry job and stop at failed prerequisites", () => {
  for (const entry of ["select", "select-checks"]) {
    const needs = state();
    needs.select.result = "skipped";
    needs[entry] = { result: "success", outputs: { work: "true" } };
    assert.equal(mayRun("preflight", needs), true);
    needs.preflight = { result: "success", outputs: { platform_tests: "true", consumer_tests: "true", scenario_tests: "true" } };
    for (const name of ["components", "platform", "native-consumers", "package-consumers"]) {
      assert.match(jobs[name], /if: \$\{\{ !cancelled\(\)/u);
      needs.components.result = "success";
      needs.platform.result = "success";
      assert.equal(mayRun(name, needs), true);
      for (const dependency of dependencies(name)) {
        const broken = structuredClone(needs);
        broken[dependency].result = "skipped";
        assert.equal(mayRun(name, broken), false, `${name}: ${dependency}`);
      }
    }
  }
});

test("App Host and CLI publication work with skipped SDK jobs and reject missing required packaging", () => {
  for (const app of [true, false]) {
    const needs = state({ work: "true", native_count: "1", native_build_count: "1", app_packages: String(app), binary_packages: String(!app) });
    for (const name of ["preflight", "validation", "prepare-native"]) needs[name].result = "success";
    if (!app) needs["package-native"].result = "success";
    assert.equal(mayRun(app ? "package-app" : "package-native", needs), true);
    needs[app ? "package-app" : "package-native"].result = "success";
    assert.equal(mayRun("publish-native", needs), true);
    assert.equal(mayRun("stage", needs), false);
    for (const missing of ["failure", "cancelled", "skipped"]) {
      const broken = structuredClone(needs);
      broken[app ? "package-app" : "package-native"].result = missing;
      assert.equal(mayRun("publish-native", broken), false, missing);
    }
    needs.validation.result = "failure";
    assert.equal(mayRun("publish-native", needs), false);
  }
});

test("single-registry publication skips only unselected uploads and failed validation blocks staging", () => {
  for (const registry of ["npm", "nuget"]) {
    const needs = state({ work: "true", selected_count: "1", [`${registry}_count`]: "1" });
    needs.validation.result = "success";
    assert.equal(mayRun("stage", needs), true);
    needs.stage.result = "success";
    assert.equal(mayRun(`publish-${registry}`, needs), true);
    const other = registry === "npm" ? "nuget" : "npm";
    assert.equal(mayRun(`publish-${other}`, needs), false);
    for (const result of ["success", "failure", "cancelled", "skipped"]) {
      needs[`publish-${registry}`].result = result;
      assert.equal(mayRun("finalize", needs), result === "success");
    }
    needs.validation.result = "failure";
    assert.equal(mayRun("stage", needs), false);
  }
  const mixed = state({ work: "true", selected_count: "2", npm_count: "1", nuget_count: "1" });
  mixed.stage.result = "success";
  assert.equal(mayRun("publish-nuget", mixed), false);
  mixed["publish-npm"].result = "success";
  assert.equal(mayRun("publish-nuget", mixed), true);
});

test("retained-only recovery skips builds but still requires successful selected checks", () => {
  const needs = state({ selected_count: "1", nuget_count: "1", recover: "true", native_count: "1" });
  assert.equal(mayRun("stage", needs), true);
  assert.equal(mayRun("publish-native", needs), true);
  assert.equal(mayRun("prepare-native", needs), false);
  needs.select.outputs.work = "true";
  for (const result of ["failure", "cancelled", "skipped", "success"]) {
    needs.validation.result = result;
    assert.equal(mayRun("stage", needs), result === "success");
    assert.equal(mayRun("publish-native", needs), result === "success");
  }
});

test("SDK publication consumes tested archives and scopes registry credentials to uploads", () => {
  assert.match(jobs.components, /core_ci.py pack --scope/u);
  assert.ok(jobs.components.indexOf("core_ci.py pack") > jobs.components.indexOf("core_ci.py test"));
  assert.match(jobs["package-consumers"], /pattern: sunder-candidate-\*/u);
  assert.ok(jobs["package-consumers"].indexOf("core_ci.py template-consumers") < jobs["package-consumers"].indexOf("name: sunder-tested-sdk"));
  assert.match(jobs.stage, /name: sunder-tested-sdk/u);
  assert.match(jobs.stage, /sdk_release.py stage --directory artifacts\/retained-packages/u);
  assert.doesNotMatch(workflow, /\b(?:dotnet (?:pack|publish)|npm pack)\b/u);
  assert.match(jobs["publish-npm"], /NODE_AUTH_TOKEN: \$\{\{ secrets.NPM_TOKEN \}\}/u);
  assert.match(jobs["publish-nuget"], /uses: NuGet\/login@[a-f0-9]{40}/u);
  assert.match(jobs["publish-nuget"], /NUGET_API_KEY: \$\{\{ steps.nuget-login.outputs.NUGET_API_KEY \}\}/u);
  const download = jobs["publish-nuget"].indexOf("sdk_release.py download");
  const login = jobs["publish-nuget"].indexOf("uses: NuGet/login@");
  const publish = jobs["publish-nuget"].indexOf("sdk_release.py nuget");
  assert.ok(download >= 0 && login > download && publish > login);
  assert.doesNotMatch(jobs["publish-nuget"].slice(login, publish), /sdk_release.py download|dotnet (?:build|pack)|npm (?:install|pack)/u);
  assert.equal([...workflow.matchAll(/secrets.NPM_TOKEN\b/gu)].length, 1);
  assert.doesNotMatch(workflow, /secrets.NUGET_API_KEY|NPM_DIST_TAG_TOKEN/u);
  assert.doesNotMatch(jobs.finalize, /id-token: write|secrets\.|NODE_AUTH_TOKEN|setup-dotnet/u);
  for (const name of ["select", "stage", "publish-npm", "publish-nuget", "finalize", "publish-native"]) {
    assert.match(jobs[name], /fetch-depth: 0/u, name);
  }
  for (const [, directory] of workflow.matchAll(/(?:sdk_release|github_release|native_release).py [^\n]+--directory ([^\s]+)/gu)) {
    assert.equal(spawnSync("git", ["check-ignore", "-q", "--no-index", `${directory}/archive.zip`], { cwd: root }).status, 0);
  }
});

test("native validation preserves all partitions, vendor runtimes and the six-runner budget", async () => {
  assert.deepEqual(dependencies("platform"), ["preflight", "components"]);
  assert.deepEqual(dependencies("native-consumers"), ["preflight", "platform"]);
  for (const name of ["platform", "native-consumers", "package-native", "package-app"]) assert.match(jobs[name], /max-parallel: 6/u);
  assert.match(jobs.platform, /--partition "\$NATIVE_PARTITION"/u);
  assert.match(jobs["native-consumers"], /core_ci.py official --scope[^\n]+--rid/u);
  assert.match(jobs["native-consumers"], /core_ci.py packaging --scope app/u);
  const preflight = jobs.platform.indexOf("Verify owned test process cleanup");
  assert.ok(preflight < jobs.platform.indexOf("actions/setup-dotnet@"));
  assert.ok(preflight < jobs.platform.indexOf("Build and test selected native component"));
  assert.match(jobs.platform, /owned-test-process.test.mjs/u);
  const smoke = await owned("scripts/test/release/smoke-app-packaging.sh");
  assert.match(smoke, /--strict-release/u);
  assert.match(smoke, /create-sunder-dmg.sh/u);
  assert.doesNotMatch(jobs.platform + jobs["native-consumers"], /continue-on-error|--allow-missing|--allow-empty/u);
});

test("native packaging reserves its original run and retries reuse its verified platform bytes", () => {
  assert.deepEqual(dependencies("prepare-native"), ["select", "validation"]);
  assert.match(jobs["prepare-native"], /native_release.py reserve/u);
  for (const name of ["package-native", "package-app"]) {
    const definition = jobs[name];
    assert.match(definition, /native_release.py retained/u);
    assert.match(definition, /native_release.py check/u);
    assert.ok(definition.indexOf("native_release.py retained") < definition.indexOf("Verify evaluated"));
    assert.match(definition, /retention-days: 90/u);
  }
  assert.match(jobs["publish-native"], /native_release.py download/u);
  assert.match(jobs["publish-native"], /github_release.py stage "\$RELEASE_TAG"/u);
  assert.match(jobs["publish-native"], /github_release.py finalize "\$RELEASE_TAG"/u);
  assert.match(jobs["publish-native"], /environment: sunder-github-release-promotion/u);
  assert.doesNotMatch(jobs["publish-native"], /GITHUB_REF_NAME|GITHUB_REF:/u);
});

test("failed and cancelled execution retains diagnostics and selection-driven audits do not rebuild", () => {
  for (const [name, identity] of [["Retain process preflight diagnostics", "process_preflight"], ["Retain platform test results", "platform_tests"]]) {
    const block = workflowStep(jobs.platform, name);
    const condition = /\n\s+if: ([^\n]+)/u.exec(block)[1];
    const shouldRun = new Function("always", "steps", `return (${condition});`);
    for (const outcome of ["success", "failure", "cancelled", "skipped", ""]) {
      assert.equal(shouldRun(() => true, { [identity]: { outcome } }), ["success", "failure", "cancelled"].includes(outcome));
    }
    assert.match(block, /if-no-files-found: error/u);
  }
  for (const [job, name] of [["components", "Retain component reports"], ["package-consumers", "Retain consumer reports"]]) {
    assert.match(workflowStep(jobs[job], name), /if: always\(\)/u);
  }
  assert.match(jobs.validation, /--selection artifacts\/validation-selection.json --aggregate --component-layout/u);
  assert.match(jobs.validation, /SUNDER_CI_JOB_RESULTS: \$\{\{ toJSON\(needs\) \}\}/u);
  assert.doesNotMatch(jobs.validation, /dotnet (?:build|test)|npm (?:ci|run)|run-.*tests/u);
  assert.match(jobs.validation, /core-test-summary.json/u);
});

test("strict App packaging retains signing notarization mounted-DMG inspection and Full-only feeds", async () => {
  const source = jobs["package-app"];
  assert.match(source, /environment: sunder-app-signing/u);
  assert.match(source, /-StrictRelease/u);
  assert.match(source, /configure-windows-signing.ps1/u);
  const signing = await owned("scripts/release/configure-windows-signing.ps1");
  const settings = /foreach \(\$name in @\(([^)]+)\)\)/u.exec(signing)?.[1] ?? "";
  assert.deepEqual([...settings.matchAll(/'([^']+)'/gu)].map(match => match[1]), [
    "AZURE_TRUSTED_SIGNING_ENDPOINT", "AZURE_TRUSTED_SIGNING_ACCOUNT_NAME", "AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME",
  ]);
  assert.doesNotMatch(signing, /WINDOWS_PUBLISHER_SUBJECT/u);
  assert.match(source, /-WindowsPublisherSubject \$env:WINDOWS_PUBLISHER_SUBJECT/u);
  assert.match(source, /-WindowsSignParams "[^\n]+\/dlib [^\n]+\/dmdf/u);
  for (const name of ["Package Sunder for Windows", "Package Sunder for Unix", "Inspect the signed and notarized mounted DMG"]) {
    assert.match(workflowStep(source, name), /steps.retained.outputs.reuse != 'true'/u);
  }
  const dmg = await owned("scripts/release/macos/verify-sunder-dmg.sh");
  assert.match(dmg, /codesign --verify --deep --strict/u);
  assert.match(dmg, /stapler validate/u);
  assert.match(dmg, /dmg_image.py" attach/u);
  assert.match(dmg, /dmg_image.py" detach/u);
  assert.doesNotMatch(dmg, /hdiutil detach.*\|\| true/u);
  const feeds = await owned("scripts/release/generate-app-feeds.py");
  assert.match(jobs["publish-native"], /generate-app-feeds.py/u);
  assert.match(feeds, /"vpk", "upload", "local"/u);
  assert.doesNotMatch(feeds, /"upload", "github"/u);
  const shell = await owned("scripts/release/package-sunder.sh");
  const powershell = await owned("scripts/release/package-sunder.ps1");
  assert.match(powershell, /Get-AuthenticodeSignature[\s\S]+System.Management.Automation.SignatureStatus\]::Valid/u);
  assert.match(powershell, /SignerCertificate.Subject -cne \$WindowsPublisherSubject/u);
  const central = await owned("languages/csharp/dotnet/Directory.Packages.props");
  const version = /Include="Velopack" Version="([^"]+)"/u.exec(central)[1];
  const versions = JSON.parse(spawnSync("python3", ["-B", "scripts/ci/metadata.py", "toolchains", "--packaging"], { cwd: root, encoding: "utf8" }).stdout);
  assert.equal(versions.velopack, version);
  assert.match(await owned("scripts/release/install-velopack.ps1"), /vpk --version \$versions.velopack/u);
  for (const script of [shell, powershell]) assert.equal(/dotnet tool install --global vpk --version ([\d.]+)/u.exec(script)?.[1], version);
  assert.match(shell, /strict_delta_packages[\s\S]+history-dependent delta/u);
  assert.match(powershell, /strictDeltaPackages[\s\S]+history-dependent delta/u);
});
