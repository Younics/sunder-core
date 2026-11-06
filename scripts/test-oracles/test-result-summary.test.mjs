import assert from "node:assert/strict";
import test from "node:test";
import { spawnSync } from "node:child_process";
import { access, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { expectedScriptReports, loadCoreTestModel, repositoryRoot, scopeModel } from "./core-test-policy.mjs";
import { ciScenarios, selectScenario } from "./ci-scenarios.mjs";
import { nativePartitionNames, selectNativePartition } from "./native-test-partitions.mjs";
import {
  auditCoreTestReports,
  auditNativeEvidence,
  buildCoreTestEvidence,
  materializeExpectedSkip,
  parseJunitReport,
  parseTrxReport,
  summarizeResults,
} from "./summarize-core-test-results.mjs";

test("aggregate audit covers the union of owning jobs without requiring unselected external artifacts", () => {
  const dotnet = (name, primaryLane, externalEnvironment) => ({
    id: `Example.Tests.Cases.${name}`, framework: "dotnet", project: "Example.Tests", primaryLane, externalEnvironment,
  });
  const declarations = [
    dotnet("Portable", "dotnet-linux"),
    dotnet("WindowsProcess", "host-platform"),
    dotnet("VendorRuntime", "official-runtime"),
    dotnet("LiveRegistry", "release-artifact", "SUNDER_REQUIRE_LIVE_INSTALLATION"),
    { framework: "python", source: "scripts/test/release/test_fixture.py", name: "test_fixture", primaryLane: "release-artifact" },
  ];
  const model = { declarations, policy: {
    reports: { node: ["node.junit.xml"], releaseFixtures: "release-fixtures.junit.xml" },
    projectLanes: { "Example.Tests": ["dotnet-linux", "host-platform", "official-runtime", "release-artifact"],
      "scripts/test-oracles": ["node"], "scripts/test/release": ["release-artifact"] },
  } };
  const reports = declarations.slice(0, 3).map((declaration) => ({
    source: `${declaration.primaryLane}/Example.Tests.trx`, discovered: 1,
    results: [{ framework: "dotnet", declarationId: declaration.id, name: declaration.id, outcome: "Passed" }],
  }));
  reports.push(parseJunitReport('<testcase name="node case" file="scripts/test-oracles/fixture.test.mjs"/>', "core/node.junit.xml"));
  reports.push(parseJunitReport('<testcase name="test_fixture" file="scripts/test/release/test_fixture.py" classname="python"/>', "core/release-fixtures.junit.xml"));
  assert.deepEqual(auditCoreTestReports(reports, model), { discovered: 5, executed: 5, passed: 5, failed: 0, skipped: 0 });

  for (const missing of reports) {
    assert.throws(() => auditCoreTestReports(reports.filter((report) => report !== missing), model),
      /omitted.*\.NET declaration|exactly one|Release fixture results omitted/u);
  }
  assert.throws(() => auditCoreTestReports([...reports, reports.at(-2)], model), /exactly one/u);
  const failed = structuredClone(reports);
  failed[0].results[0].outcome = "Failed";
  assert.equal(auditCoreTestReports(failed, model).failed, 1);
  const unknown = structuredClone(reports);
  unknown[0].results[0].declarationId = "Unowned.Tests.Case";
  assert.throws(() => auditCoreTestReports(unknown, model), /unowned \.NET test/u);
  const missingFixture = structuredClone(reports);
  missingFixture.at(-1).results[0].name = "not_the_fixture";
  assert.throws(() => auditCoreTestReports(missingFixture, model), /Release fixture results omitted/u);
});

test("TRX and JUnit parsers retain expanded discovery, execution, and skip reasons", () => {
  const trx = parseTrxReport(`
    <TestRun>
      <Results>
        <UnitTestResult executionId="one" testId="case" testName="Example.Tests.Cases(value: 1)" outcome="Passed" />
        <UnitTestResult executionId="two" testId="case" testName="Example.Tests.Cases(value: 2)" outcome="NotExecuted">
          <Output><ErrorInfo><Message>Requires Linux.</Message></ErrorInfo></Output>
        </UnitTestResult>
      </Results>
      <TestDefinitions>
        <UnitTest id="case"><TestMethod className="Example.Tests" name="Cases" /></UnitTest>
      </TestDefinitions>
      <ResultSummary outcome="Completed"><Counters total="2" executed="1" /></ResultSummary>
    </TestRun>`, "Example.Tests.trx");
  assert.equal(trx.discovered, 2);
  assert.deepEqual(trx.results.map((item) => item.declarationId), ["Example.Tests.Cases", "Example.Tests.Cases"]);
  assert.equal(trx.results[1].skipReason, "Requires Linux.");

  const junit = parseJunitReport(
    '<testsuites><testcase name="node skip" classname="test" file="scripts/test-oracles/example.test.mjs"><skipped message="fixture unavailable"/></testcase></testsuites>',
    "example.junit.xml",
  );
  assert.equal(junit.discovered, 1);
  assert.equal(junit.results[0].outcome, "Skipped");
  assert.equal(junit.results[0].skipReason, "fixture unavailable");
});

test("scoped audits reject missing sources, required integration skips and foreign component reports", () => {
  for (const scope of ["sdk", "repository", "cli"]) {
    const model = loadCoreTestModel(repositoryRoot, undefined, scope);
    const required = ["portable", "official"].flatMap(profile => ciScenarios(model.policy.scopes[scope], profile))
      .flatMap(name => selectScenario(name, model.declarations).tests.map(test => test.id));
    const selected = model.declarations.filter(test => test.externalEnvironment === undefined || required.includes(test.id));
    const groups = Map.groupBy(selected, test => test.framework === "dotnet" ? `${test.project}.trx`
      : test.framework === "node" ? test.source.startsWith("scripts/") ? "node-core-oracles.junit.xml" : `node-${test.project}.junit.xml`
        : "release-fixtures.junit.xml");
    const reports = [...groups].map(([source, tests]) => ({ source, discovered: tests.length,
      results: tests.map(test => ({ report: source, framework: test.framework, project: test.project,
        declarationId: test.id, name: test.name, source: test.source, outcome: "Passed" })) }));
    assert.equal(auditCoreTestReports(reports, model).passed, selected.length);
    for (const source of expectedScriptReports(model)) {
      assert.throws(() => auditCoreTestReports(reports.filter(report => report.source !== source), model), /exactly one/u);
    }
    if (scope === "sdk") {
      const withoutScript = structuredClone(reports);
      const scripts = withoutScript.find(report => report.source === "node-core-oracles.junit.xml");
      const removed = scripts.results[0].source;
      scripts.results = scripts.results.filter(result => result.source !== removed);
      assert.throws(() => auditCoreTestReports(withoutScript, model), /Node results omitted/u);
      for (const change of ["missing", "skipped"]) {
        const changed = structuredClone(reports);
        const report = changed.find(report => report.results.some(result => result.declarationId === required[0]));
        if (change === "missing") report.results = report.results.filter(result => result.declarationId !== required[0]);
        else report.results.find(result => result.declarationId === required[0]).outcome = "Skipped";
        assert.throws(() => auditCoreTestReports(changed, model), /produced no result|was reported as skipped|omitted.*declaration/u);
      }
      const app = model.allDeclarations.find(test => test.project === "Sunder.App.Integration.Tests");
      assert.throws(() => auditCoreTestReports([...reports, { source: "app.trx", discovered: 1,
        results: [{ framework: "dotnet", declarationId: app.id, name: app.id, outcome: "Passed" }] }], model), /unowned .NET test/u);
    }
  }
});

test("JUnit audits resolve declared sources across checkout roots without admitting foreign components", () => {
  const node = (source, name, project, component = "sdk") => ({
    id: name, framework: "node", source, name, project, component,
    primaryLane: "node", operatingSystems: ["Linux", "MacOS", "Windows"],
  });
  const declarations = [node("scripts/compatibility/known.test.mjs", "script", "scripts/test/release"),
    node("languages/typescript/node/packages/sdk/test/known.test.ts", "node", "sdk")];
  const foreign = node("scripts/test-oracles/reporter.test.mjs", "reporter", "scripts/test-oracles", "ci");
  const model = { scope: "sdk", declarations, allDeclarations: [...declarations, foreign], policy: {
    scopes: { sdk: ["sdk"] }, defaultComponent: "sdk", primaryLanes: ["node"],
    componentRules: [{ component: "ci", source: "^scripts/test-oracles/" }],
    projectLanes: { "scripts/test/release": ["node"], sdk: ["node"] },
  } };
  const options = { allowPartial: true, requiredPrimaryLanes: ["node"], operatingSystem: "Linux" };
  for (const root of ["/home/runner/work/core", "/private/snapshot/core", "D:\\work\\core", "/home/scripts/work/core"]) {
    const report = parseJunitReport(declarations.map(item => {
      const source = item.source.replace("/test/", "/dist-test/test/").replace(/\.ts$/u, ".js");
      return `<testcase name="${item.name}" file="${root}/${source}"/>`;
    }).join(""), "node.junit.xml");
    assert.equal(buildCoreTestEvidence([report], model, options).counts.passed, 2);
    const external = parseJunitReport(`<testcase name="${foreign.name}" file="${root}/${foreign.source}"/>`, "foreign.junit.xml");
    assert.throws(() => buildCoreTestEvidence([report, external], model, options), /outside validation scope/u);
    const missing = parseJunitReport('<testcase name="script" file="/unowned/known.test.mjs"/>', "missing.junit.xml");
    assert.throws(() => buildCoreTestEvidence([missing], model, options), /coverage drift|Node results omitted/u);
  }
});

test("JUnit quoted greater-than characters do not swallow adjacent testcases or hide failures", () => {
  const report = parseJunitReport(`<testsuites>
    <testcase name="Accept -> Check -> Freeze" classname="test" file="scripts/test-oracles/example.test.mjs"/>
    <testcase name='required -> optional' classname='test' file='scripts/test-oracles/example.test.mjs'>
      <failure message="observed > expected">failure evidence</failure>
    </testcase>
    <testcase name="still accounted for" classname="test" file="scripts/test-oracles/example.test.mjs"/>
  </testsuites>`, "quoted.junit.xml");
  assert.equal(report.discovered, 3);
  assert.deepEqual(report.results.map(({ name, outcome }) => ({ name, outcome })), [
    { name: "Accept -> Check -> Freeze", outcome: "Passed" },
    { name: "required -> optional", outcome: "Failed" },
    { name: "still accounted for", outcome: "Passed" },
  ]);
  assert.throws(() => parseJunitReport('<testcase name="missing -> source"/>', "invalid.junit.xml"), /without a name or source/u);
});

test("compact evidence records per-project and lane discovered/executed counts", () => {
  const model = loadCoreTestModel();
  const id = "Sunder.Core.TestSupport.Tests.TestSupportFaultProbeTests.ManualTime_DelayCannotCompleteBeforeItsClockBoundary";
  const report = {
    source: "Sunder.Core.TestSupport.Tests.trx",
    framework: "dotnet",
    discovered: 1,
    results: [{
      report: "Sunder.Core.TestSupport.Tests.trx",
      framework: "dotnet",
      project: "Sunder.Core.TestSupport.Tests",
      name: id,
      declarationId: id,
      outcome: "Passed",
    }],
  };
  const evidence = buildCoreTestEvidence([report], model, {
    allowPartial: true,
    requiredTests: [id],
    operatingSystem: "MacOS",
  });
  assert.deepEqual(evidence.counts, { discovered: 1, executed: 1, passed: 1, failed: 0, skipped: 0 });
  assert.deepEqual(evidence.projects, [{
    project: "Sunder.Core.TestSupport.Tests",
    lane: "dotnet-linux",
    discovered: 1,
    executed: 1,
    passed: 1,
    failed: 0,
    skipped: 0,
  }]);
  assert.deepEqual(evidence.lanes, [{
    lane: "dotnet-linux",
    discovered: 1,
    executed: 1,
    passed: 1,
    failed: 0,
    skipped: 0,
  }]);
  assert.ok(Buffer.byteLength(JSON.stringify(evidence), "utf8") < 2048);
});

test("evidence rejects zero tests and a READY-selected skip", () => {
  const model = loadCoreTestModel();
  assert.throws(
    () => buildCoreTestEvidence([], model),
    /no reports/u,
  );
  assert.throws(
    () => buildCoreTestEvidence([{ source: "empty.trx", discovered: 0, results: [] }], model),
    /zero tests/u,
  );

  const id = "Sunder.Core.TestSupport.Tests.TestSupportFaultProbeTests.ManualTime_DelayCannotCompleteBeforeItsClockBoundary";
  const skipped = {
    source: "selected.trx",
    discovered: 1,
    results: [{
      report: "selected.trx",
      framework: "dotnet",
      project: "Sunder.Core.TestSupport.Tests",
      name: id,
      declarationId: id,
      outcome: "Skipped",
      skipReason: "seeded",
    }],
  };
  assert.throws(
    () => buildCoreTestEvidence([skipped], model, {
      allowPartial: true,
      requiredTests: [id],
      operatingSystem: "MacOS",
    }),
    /prerequisite gate was READY/u,
  );
});

test("expected skip evidence requires an explicit reason and owning lane", () => {
  const model = loadCoreTestModel();
  const id = "Sunder.Runtime.Host.Tests.PackageStorageServicesTests.MacOsKeychain_OptInIntegration_AddUpdateLoadDelete";
  const declaration = model.declarations.find((item) => item.id === id);
  const expected = materializeExpectedSkip({
    framework: "dotnet",
    project: declaration.project,
    primaryLane: declaration.primaryLane,
    declaration,
    name: id,
    skipReason: "Set SUNDER_RUN_EXTERNAL_KEYSTORE_TESTS=1 on macOS to permit Keychain mutation.",
  }, model.policy, "MacOS");
  assert.equal(expected.owner, "host-platform");
  assert.match(expected.reason, /Keychain mutation/u);

  const ordinary = model.declarations.find((item) => item.id
    === "Sunder.Core.TestSupport.Tests.TestSupportFaultProbeTests.ManualTime_DelayCannotCompleteBeforeItsClockBoundary");
  assert.throws(() => materializeExpectedSkip({
    framework: "dotnet",
    project: ordinary.project,
    primaryLane: ordinary.primaryLane,
    declaration: ordinary,
    name: ordinary.id,
    skipReason: "seeded unknown skip",
  }, model.policy, "MacOS"), /Unexpected skipped test/u);
});

test("IPC publisher evidence rejects obsolete Windows skips and omissions", () => {
  const model = loadCoreTestModel();
  const declaration = model.declarations.find((item) => item.framework === "node"
    && item.name === "a suspended CLI publisher reads the new source after acquiring the output lease");
  assert.ok(declaration);
  const report = parseJunitReport(`<testsuites><testcase name="${declaration.name}" classname="test" file="${declaration.source}">
    <skipped message="SIGSTOP/SIGCONT unavailable on Windows"/></testcase></testsuites>`, "node-package-tool.junit.xml");
  const result = { ...report.results[0], declaration, primaryLane: declaration.primaryLane, project: declaration.project };
  assert.throws(() => materializeExpectedSkip(result, model.policy, "Windows"), /Unexpected skipped test/u);
  assert.throws(() => materializeExpectedSkip(result, model.policy, "MacOS"), /Unexpected skipped test/u);
  const selection = { allowPartial: true, requiredTests: [declaration.id], operatingSystem: "Windows" };
  assert.throws(() => buildCoreTestEvidence([report], model, selection), /prerequisite gate was READY/u);
  const missing = structuredClone(report);
  missing.results[0].name = "another Node test";
  missing.results[0].outcome = "Passed";
  assert.throws(() => buildCoreTestEvidence([missing], model, selection), /produced no result/u);
  report.results[0].outcome = "Passed";
  assert.deepEqual(buildCoreTestEvidence([report], model, selection).counts,
    { discovered: 1, executed: 1, passed: 1, failed: 0, skipped: 0 });
});

test("TRX counter mutation cannot hide omitted runtime cases", () => {
  assert.throws(() => parseTrxReport(`
    <TestRun>
      <Results><UnitTestResult executionId="one" testId="case" testName="Example.Tests.Passes" outcome="Passed" /></Results>
      <TestDefinitions><UnitTest id="case"><TestMethod className="Example.Tests" name="Passes" /></UnitTest></TestDefinitions>
      <ResultSummary outcome="Completed"><Counters total="0" executed="0" /></ResultSummary>
    </TestRun>`, "mutation.trx"), /expected 1/u);
});

test("TRX run failures and incomplete summaries cannot be hidden by passing case counters", () => {
  const report = (summary) => `<TestRun>
    <Results><UnitTestResult executionId="one" testId="case" testName="Example.Tests.Passes" outcome="Passed" /></Results>
    <TestDefinitions><UnitTest id="case"><TestMethod className="Example.Tests" name="Passes" /></UnitTest></TestDefinitions>
    ${summary}</TestRun>`;
  for (const summary of [
    '<ResultSummary outcome="Failed"><Counters total="1" executed="1" passed="1" /></ResultSummary>',
    '<ResultSummary outcome="Aborted"><Counters total="1" executed="1" passed="1" /></ResultSummary>',
    '<ResultSummary outcome="Completed"><Counters total="1" executed="1" aborted="1" /></ResultSummary>',
    '<ResultSummary outcome="Completed"><Counters total="1" executed="1" passedButRunAborted="1" /></ResultSummary>',
    '<ResultSummary outcome="Completed"><RunInfo outcome="Error"><Text>Test host process crashed</Text></RunInfo><Counters total="1" executed="1" /></ResultSummary>',
    '<ResultSummary><Counters total="1" executed="1" /></ResultSummary>',
    '',
  ]) {
    assert.throws(() => parseTrxReport(report(summary), "crashed.trx"), /failed or aborted|completed run summary|aborted=1|passedButRunAborted=1/u);
  }
  // Keep ordinary failed cases available to the evidence counter/reporting path.
  const failure = report('<ResultSummary outcome="Failed"><Counters total="1" executed="1" /></ResultSummary>')
    .replace('outcome="Passed"', 'outcome="Failed"');
  assert.equal(parseTrxReport(failure, "failed.trx").results[0].outcome, "Failed");
});

test("evidence cannot accept a passing TRX left by a timed-out or unclean runner", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-failed-command-evidence-"));
  const id = "Sunder.Core.TestSupport.Tests.TestSupportFaultProbeTests.ManualTime_DelayCannotCompleteBeforeItsClockBoundary";
  try {
    const parentSummary = resolve(root, "parent-summary.md");
    await writeFile(parentSummary, "parent summary\n");
    await writeFile(resolve(root, "Sunder.Core.TestSupport.Tests.trx"), `<TestRun>
      <Results><UnitTestResult executionId="one" testId="case" testName="${id}" outcome="Passed" /></Results>
      <TestDefinitions><UnitTest id="case"><TestMethod className="Sunder.Core.TestSupport.Tests.TestSupportFaultProbeTests"
        name="ManualTime_DelayCannotCompleteBeforeItsClockBoundary" /></UnitTest></TestDefinitions>
      <ResultSummary outcome="Completed"><Counters total="1" executed="1" /></ResultSummary></TestRun>`);
    for (const failure of [{ error: { code: "ETIMEDOUT" } }, { status: null, error: { code: "EINCOMPLETE" } },
      { status: 1 }, { signal: "SIGKILL" }, { cleanupComplete: false }]) {
      await writeFile(resolve(root, "command.process.json"), JSON.stringify({ status: 0, signal: null, error: null, cleanupComplete: true, ...failure }));
      const result = spawnSync(process.execPath, [fileURLToPath(new URL("./summarize-core-test-results.mjs", import.meta.url)),
         root, "--allow-partial", "--require-test", id], { encoding: "utf8",
           env: { ...process.env, GITHUB_STEP_SUMMARY: parentSummary } });
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, /Test command failed/u);
      const summary = JSON.parse(await readFile(resolve(root, "core-test-summary.json"), "utf8"));
      assert.equal(summary.accepted, false);
      assert.equal(summary.counts.passed, 1);
      assert.match(summary.issues.join("\n"), /Test command failed/u);
      await assert.rejects(access(resolve(root, "core-test-evidence.json")));
      assert.equal(await readFile(parentSummary, "utf8"), "parent summary\n");
    }
    // Finalize failed commands, failed cases and missing coverage together.
    const report = resolve(root, "Sunder.Core.TestSupport.Tests.trx");
    await writeFile(report, (await readFile(report, "utf8")).replace('outcome="Passed"', 'outcome="Failed"'));
    const result = spawnSync(process.execPath, [fileURLToPath(new URL("./summarize-core-test-results.mjs", import.meta.url)),
      root, "--allow-partial", "--require-test", id, "--require-test", id.replace("ManualTime_DelayCannotCompleteBeforeItsClockBoundary", "missing")], { encoding: "utf8" });
    assert.equal(result.status, 1);
    const summary = JSON.parse(await readFile(resolve(root, "core-test-summary.json"), "utf8"));
    assert.equal(summary.counts.failed, 1);
    assert.equal(summary.failures[0].name, id);
    assert.match(summary.issues.join("\n"), /Test command failed/u);
    assert.match(summary.issues.join("\n"), /not declared/u);
    const published = spawnSync(process.execPath, [fileURLToPath(new URL("./summarize-core-test-results.mjs", import.meta.url)),
      root, "--allow-partial", "--require-test", id, "--github-summary"], { encoding: "utf8",
        env: { ...process.env, GITHUB_STEP_SUMMARY: parentSummary } });
    assert.equal(published.status, 1, "Publishing diagnostics must preserve rejection");
    assert.match(await readFile(parentSummary, "utf8"), /Core all — Failed/u);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("native aggregate requires every RID and complete disjoint partition reports", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-native-evidence-"));
  const model = loadCoreTestModel(repositoryRoot, undefined, "sdk");
  const platforms = JSON.parse(await readFile(resolve(repositoryRoot, "eng/ci/platforms.json"), "utf8")).platforms;
  const reports = [];
  try {
    for (const family of ["sunder-platform", "official-runtime"]) {
      for (const { rid } of platforms) {
        const operatingSystem = { win: "Windows", linux: "Linux", osx: "MacOS" }[rid.split("-")[0]];
        for (const partition of family === "sunder-platform" ? nativePartitionNames(model) : [undefined]) {
          const name = `${family}-sdk-${rid}` + (partition === undefined || partition === "main" ? "" : "-" + partition);
          const prefix = `${family === "sunder-platform" ? "platform" : "official"}/${name}`;
          const directory = resolve(root, prefix);
          await mkdir(directory, { recursive: true });
          let evidence = { schemaVersion: 1, scope: "sdk", operatingSystem,
            counts: { executed: 1, failed: 0 }, lanes: [{ lane: "official-runtime", executed: 1, failed: 0 }] };
          if (partition !== undefined) {
            const groups = Map.groupBy(selectNativePartition(model, partition, operatingSystem), test => test.project);
            const owned = [...groups].map(([project, tests]) => {
              const source = `${prefix}/${project}.trx`;
              return { source, discovered: tests.length, results: tests.map(test => ({ report: source,
                framework: "dotnet", declarationId: test.id, name: test.id, outcome: "Passed" })) };
            });
            evidence = buildCoreTestEvidence(owned, model, { allowPartial: true, requiredNativePartition: partition, operatingSystem });
            reports.push(...owned);
          }
          await writeFile(resolve(directory, "core-test-evidence.json"), JSON.stringify(evidence));
        }
      }
    }
    const issues = [];
    assert.equal(auditNativeEvidence(root, model, platforms, issues, reports).length, 12);
    assert.deepEqual(issues, []);
    const selected = reports.find(report => report.source.includes("osx-arm64-package-build-1/"));
    const other = reports.find(report => report.source.includes("osx-arm64-package-build-2/"));
    const overlap = structuredClone(reports);
    overlap.find(report => report.source === selected.source).results.push(other.results[0]);
    const overlapIssues = [];
    assert.equal(auditNativeEvidence(root, model, platforms, overlapIssues, overlap).filter(row => !row.complete).length, 1);
    assert.match(overlapIssues.join("\n"), /out-of-partition test/u);
    const omitted = structuredClone(reports);
    omitted.find(report => report.source === selected.source).results.pop();
    const omittedIssues = [];
    assert.equal(auditNativeEvidence(root, model, platforms, omittedIssues, omitted).filter(row => !row.complete).length, 1);
    assert.match(omittedIssues.join("\n"), /omitted 1 selected/u);
    // A whole missing shard cannot be satisfied by another shard or RID.
    const shard = resolve(root, "platform/sunder-platform-sdk-osx-x64-package-build-3/core-test-evidence.json");
    const saved = await readFile(shard, "utf8");
    await rm(shard);
    const shardIssues = [];
    assert.equal(auditNativeEvidence(root, model, platforms, shardIssues, reports).filter(row => !row.complete).length, 1);
    assert.match(shardIssues.join("\n"), /Missing required native evidence: sunder-platform-sdk-osx-x64-package-build-3/u);
    await writeFile(shard, saved);
    await rm(resolve(root, "platform/sunder-platform-sdk-osx-arm64"), { recursive: true });
    const rows = auditNativeEvidence(root, model, platforms, issues, reports);
    assert.equal(rows.filter(row => !row.complete).length, 1);
    assert.match(issues.join("\n"), /Missing required native evidence: sunder-platform-sdk-osx-arm64/u);
    const invalid = resolve(root, "official/official-runtime-sdk-win-arm64/core-test-evidence.json");
    const document = JSON.parse(await readFile(invalid, "utf8"));
    delete document.counts.executed;
    await writeFile(invalid, JSON.stringify(document));
    assert.equal(auditNativeEvidence(root, model, platforms, issues, reports).filter(row => !row.complete).length, 2);
    assert.match(issues.join("\n"), /official-runtime-sdk-win-arm64: invalid owning-job evidence/u);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("aggregate retains upstream failure even when no selected native lane or failed testcase reports it", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-job-summary-"));
  const previous = process.env.SUNDER_CI_JOB_RESULTS;
  const exitCode = process.exitCode;
  const previousSummary = process.env.GITHUB_STEP_SUMMARY;
  try {
    const model = { scope: "cli", policy: { scopes: { cli: ["cli"] }, projectLanes: { "Example.Tests": ["dotnet-linux"] } },
      declarations: [{ id: "Example.Tests.Cases.Pass", project: "Example.Tests", component: "cli", framework: "dotnet", primaryLane: "dotnet-linux" }] };
    await writeFile(resolve(root, "Example.Tests.trx"), `<TestRun>
      <Results><UnitTestResult testId="case" executionId="run" testName="Example.Tests.Cases.Pass" outcome="Passed" /></Results>
      <TestDefinitions><UnitTest id="case"><TestMethod className="Example.Tests.Cases" name="Pass" /></UnitTest></TestDefinitions>
      <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary></TestRun>`);
    process.env.GITHUB_STEP_SUMMARY = resolve(root, "parent-summary.md");
    await writeFile(process.env.GITHUB_STEP_SUMMARY, "parent summary\n");
    process.env.SUNDER_CI_JOB_RESULTS = JSON.stringify({ "build-test-pack": { result: "success" }, native: { result: "skipped" } });
    assert.equal(summarizeResults(root, model, { aggregate: true }).accepted, true);
    for (const result of ["failure", "cancelled"]) {
      process.env.SUNDER_CI_JOB_RESULTS = JSON.stringify({ "build-test-pack": { result }, native: { result: "skipped" } });
      const summary = summarizeResults(root, model, { aggregate: true });
      assert.equal(summary.accepted, false);
      assert.equal(summary.counts.passed, 1);
      assert.deepEqual(summary.issues, [`Owning job 'build-test-pack' ended with '${result}'.`]);
      assert.deepEqual(JSON.parse(await readFile(resolve(root, "core-test-summary.json"), "utf8")), summary);
    }
    assert.equal(await readFile(process.env.GITHUB_STEP_SUMMARY, "utf8"), "parent summary\n");
    const component = resolve(root, "sunder-results-cli-component-cli");
    await mkdir(component);
    await writeFile(resolve(component, "Example.Tests.trx"), await readFile(resolve(root, "Example.Tests.trx")));
    await rm(resolve(root, "Example.Tests.trx"));
    const stepsPath = resolve(component, "steps.json");
    await writeFile(stepsPath, JSON.stringify({ component: "cli", steps: [{ stage: "dotnet_build", outcome: "success" }, { stage: "dotnet_test", outcome: "success" }] }));
    process.env.SUNDER_CI_JOB_RESULTS = JSON.stringify(Object.fromEntries(["preflight", "components", "cross-language", "templates", "native"]
      .map(name => [name, { result: ["preflight", "components"].includes(name) ? "success" : "skipped" }])));
    const jobUrl = "https://github.com/Younics/sunder-core/actions/runs/1/job/2";
    await writeFile(resolve(root, "jobs.json"), JSON.stringify([{}, null, { name: "Validation / CLI / Build, test and pack", html_url: jobUrl }]));
    const accepted = summarizeResults(root, model, { aggregate: true, componentLayout: true });
    assert.equal(accepted.accepted, true);
    assert.equal(accepted.components[0].url, jobUrl);
    await writeFile(resolve(root, "jobs.json"), "unavailable optional API response");
    for (const outcome of ["failure", "cancelled"]) {
      await writeFile(stepsPath, JSON.stringify({ component: "cli", steps: [{ stage: "dotnet_test", outcome }] }));
      const rejected = summarizeResults(root, model, { aggregate: true, componentLayout: true });
      assert.equal(rejected.accepted, false);
      assert.ok(rejected.issues.includes(`CLI / dotnet_test: ${outcome}.`));
    }
    await rm(stepsPath);
    assert.equal(summarizeResults(root, model, { aggregate: true, componentLayout: true }).accepted, false,
      "Passing TRX cannot replace missing component stage results");
    assert.equal(await readFile(process.env.GITHUB_STEP_SUMMARY, "utf8"), "parent summary\n");
  } finally {
    if (previous === undefined) delete process.env.SUNDER_CI_JOB_RESULTS;
    else process.env.SUNDER_CI_JOB_RESULTS = previous;
    process.exitCode = exitCode;
    if (previousSummary === undefined) delete process.env.GITHUB_STEP_SUMMARY;
    else process.env.GITHUB_STEP_SUMMARY = previousSummary;
    await rm(root, { recursive: true, force: true });
  }
});

test("component aggregate reconciles separate script owners and every official runtime RID", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-component-audit-"));
  const previous = process.env.SUNDER_CI_JOB_RESULTS;
  const exitCode = process.exitCode;
  const model = loadCoreTestModel();
  const xml = value => value.replaceAll("&", "&amp;").replaceAll('"', "&quot;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
  async function reportsFor(directory, declarations) {
    await mkdir(resolve(root, directory), { recursive: true });
    const groups = Map.groupBy(declarations, test => test.framework === "dotnet" ? `${test.project}.trx`
      : test.framework === "node" ? test.source.startsWith("scripts/") ? "node-core-oracles.junit.xml" : `node-${test.project}.junit.xml`
        : "release-fixtures.junit.xml");
    const reports = [];
    for (const [file, tests] of groups) {
      const source = `${directory}/${file}`;
      let text;
      if (file.endsWith(".trx")) {
        text = `<TestRun><Results>${tests.map((test, i) => `<UnitTestResult testId="${i}" executionId="${i}" testName="${xml(test.id)}" outcome="Passed"/>`).join("")}</Results>
          <TestDefinitions>${tests.map((test, i) => `<UnitTest id="${i}"><TestMethod className="${xml(test.id.slice(0, test.id.lastIndexOf(".")))}" name="${xml(test.name)}"/></UnitTest>`).join("")}</TestDefinitions>
          <ResultSummary outcome="Completed"><Counters total="${tests.length}" executed="${tests.length}"/></ResultSummary></TestRun>`;
        reports.push(parseTrxReport(text, source));
      } else {
        text = `<testsuites>${tests.map(test => `<testcase name="${xml(test.name)}" classname="${test.framework}" file="${xml(test.source)}"/>`).join("")}</testsuites>`;
        reports.push(parseJunitReport(text, source));
      }
      await writeFile(resolve(root, source), text);
    }
    return reports;
  }
  try {
    const platforms = JSON.parse(await readFile(resolve(repositoryRoot, "eng/ci/platforms.json"), "utf8")).platforms;
    for (const owner of model.policy.scopes.all) {
      const scope = owner === "ci" ? "repository" : owner;
      const leaf = scopeModel(model, scope);
      const directory = `sunder-results-all-component-${scope}`;
      await reportsFor(directory, leaf.declarations.filter(test => test.framework !== "dotnet"
        || test.primaryLane === "dotnet-linux" && test.externalEnvironment === undefined));
      await writeFile(resolve(root, directory, "steps.json"), JSON.stringify({ component: scope,
        steps: [{ stage: "build", outcome: "success" }, { stage: "test", outcome: "success" }] }));
      if (!["dotnet", "host", "app"].includes(owner)) continue;
      for (const { rid } of platforms) {
        const operatingSystem = { win: "Windows", linux: "Linux", osx: "MacOS" }[rid.split("-")[0]];
        for (const partition of nativePartitionNames(leaf)) {
          const path = `sunder-results-all-platform-${scope}-${rid}${partition === "main" ? "" : "-" + partition}/platform`;
          const reports = await reportsFor(path, selectNativePartition(leaf, partition, operatingSystem));
          await writeFile(resolve(root, path, "core-test-evidence.json"), JSON.stringify(buildCoreTestEvidence(reports, leaf,
            { allowPartial: true, operatingSystem, requiredNativePartition: partition })));
          if (owner === "app") {
            const appPath = path.replace(/\/platform$/u, "/app-packaging");
            const appReports = await reportsFor(appPath, leaf.declarations.filter(test => test.primaryLane === "app-packaging"));
            await writeFile(resolve(root, appPath, "core-test-evidence.json"), JSON.stringify(buildCoreTestEvidence(appReports, leaf,
              { allowPartial: true, operatingSystem, requiredPrimaryLanes: ["app-packaging"] })));
          }
        }
        if (owner === "host") {
          const path = `sunder-results-all-official-host-${rid}/official`;
          const tests = selectScenario("official", leaf.declarations, operatingSystem).tests;
          const reports = await reportsFor(path, tests);
          await writeFile(resolve(root, path, "core-test-evidence.json"), JSON.stringify(buildCoreTestEvidence(reports, leaf,
            { allowPartial: true, operatingSystem, requiredTests: tests.map(test => test.id) })));
        }
      }
    }
    for (const scenario of ["interop", "app-interop"]) {
      await reportsFor(`sunder-results-all-consumers-${scenario}`, selectScenario(scenario, model.declarations, "Linux").tests);
    }
    for (const { rid } of platforms.filter(row => row.packagingSmoke)) {
      const path = resolve(root, `sunder-results-all-packaging-app-${rid}`);
      await mkdir(path);
      await writeFile(resolve(path, "packaging.process.json"), JSON.stringify({ status: 0, signal: null, error: null, cleanupComplete: true }));
    }
    process.env.SUNDER_CI_JOB_RESULTS = JSON.stringify(Object.fromEntries(["preflight", "components", "cross-language", "templates", "native"]
      .map(name => [name, { result: name === "templates" ? "skipped" : "success" }])));
    const result = summarizeResults(root, model, { aggregate: true, componentLayout: true });
    assert.equal(result.accepted, true, result.issues.join("\n"));
    assert.equal(result.components.length, 6);
    const path = resolve(root, "sunder-results-all-official-host-osx-x64/official/Sunder.Runtime.Host.Tests.trx");
    await rm(path);
    const missing = summarizeResults(root, model, { aggregate: true, componentLayout: true });
    assert.equal(missing.accepted, false);
    assert.match(missing.issues.join("\n"), /official-host-osx-x64/u);
  } finally {
    if (previous === undefined) delete process.env.SUNDER_CI_JOB_RESULTS;
    else process.env.SUNDER_CI_JOB_RESULTS = previous;
    process.exitCode = exitCode;
    await rm(root, { recursive: true, force: true });
  }
});
