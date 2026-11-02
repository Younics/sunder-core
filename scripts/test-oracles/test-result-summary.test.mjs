import assert from "node:assert/strict";
import test from "node:test";
import { loadCoreTestModel } from "./core-test-policy.mjs";
import {
  auditCoreTestReports,
  buildCoreTestEvidence,
  materializeExpectedSkip,
  parseJunitReport,
  parseTrxReport,
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
      <ResultSummary><Counters total="2" executed="1" /></ResultSummary>
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

test("suspension evidence permits only its Windows reason and never substitutes a skip or omission for required execution", () => {
  const model = loadCoreTestModel();
  const expected = model.policy.expectedSkips.find((item) => item.id === "node-process-suspension");
  assert.ok(expected);
  const declaration = model.declarations.find((item) => item.framework === "node"
    && item.source === expected.source && item.name === expected.name);
  assert.ok(declaration);
  const report = parseJunitReport(`<testsuites><testcase name="${expected.name}" classname="test" file="${expected.source}">
    <skipped message="SIGSTOP/SIGCONT unavailable on Windows"/></testcase></testsuites>`, "node-package-tool.junit.xml");
  const result = { ...report.results[0], declaration, primaryLane: declaration.primaryLane, project: declaration.project };
  assert.equal(materializeExpectedSkip(result, model.policy, "Windows").owner, "node");
  assert.throws(() => materializeExpectedSkip(result, model.policy, "MacOS"), /Unexpected skipped test/u);
  assert.throws(() => materializeExpectedSkip({ ...result, skipReason: "some other failure" }, model.policy, "Windows"), /unapproved reason/u);
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
      <ResultSummary><Counters total="0" executed="0" /></ResultSummary>
    </TestRun>`, "mutation.trx"), /expected 1/u);
});
