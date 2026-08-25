import assert from "node:assert/strict";
import test from "node:test";
import { loadCoreTestModel } from "./core-test-policy.mjs";
import {
  buildCoreTestEvidence,
  materializeExpectedSkip,
  parseJunitReport,
  parseTrxReport,
} from "./summarize-core-test-results.mjs";

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

test("TRX counter mutation cannot hide omitted runtime cases", () => {
  assert.throws(() => parseTrxReport(`
    <TestRun>
      <Results><UnitTestResult executionId="one" testId="case" testName="Example.Tests.Passes" outcome="Passed" /></Results>
      <TestDefinitions><UnitTest id="case"><TestMethod className="Example.Tests" name="Passes" /></UnitTest></TestDefinitions>
      <ResultSummary><Counters total="0" executed="0" /></ResultSummary>
    </TestRun>`, "mutation.trx"), /expected 1/u);
});
