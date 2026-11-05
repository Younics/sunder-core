import { appendFileSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { basename, dirname, extname, relative, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { componentFor, expectedScriptReports, loadCoreTestModel, nodeProject, repositoryRoot } from "./core-test-policy.mjs";
import { currentOperatingSystem } from "./test-lane-prerequisites.mjs";
import { ciScenarios, selectScenario } from "./ci-scenarios.mjs";

export function parseTrxReport(xml, source) {
  const summary = /<ResultSummary\b([^>]*?)>/u.exec(xml);
  const runOutcome = summary === null ? undefined : parseAttributes(summary[1]).outcome;
  if (!["Completed", "Passed", "Failed"].includes(runOutcome)) {
    throw new Error(`TRX report '${source}' has no completed run summary (outcome=${runOutcome ?? "missing"}).`);
  }
  const project = basename(source, ".trx");
  const definitions = new Map();
  for (const match of xml.matchAll(/<UnitTest\b([^>]*)>([\s\S]*?)<\/UnitTest>/gu)) {
    const unitTest = parseAttributes(match[1]);
    const methodMatch = /<TestMethod\b([^>]*?)\/>/u.exec(match[2]);
    if (methodMatch === null) continue;
    const method = parseAttributes(methodMatch[1]);
    const testId = decodeXml(unitTest.id ?? "");
    const className = decodeXml(method.className ?? "");
    const methodName = decodeXml(method.name ?? "");
    if (testId === "" || className === "" || methodName === "") {
      throw new Error(`TRX report '${source}' contains an incomplete test definition.`);
    }
    if (definitions.has(testId)) throw new Error(`TRX report '${source}' repeats test id '${testId}'.`);
    definitions.set(testId, `${className}.${methodName}`);
  }

  const results = [];
  const observedExecutions = new Set();
  for (const match of xml.matchAll(/<UnitTestResult\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/gu)) {
    const attributes = parseAttributes(match[1]);
    const testId = decodeXml(attributes.testId ?? "");
    const declarationId = definitions.get(testId);
    const name = decodeXml(attributes.testName ?? "");
    if (declarationId === undefined || name === "") {
      throw new Error(`TRX result '${name || "<unnamed>"}' in '${source}' has no complete definition.`);
    }
    const executionId = decodeXml(attributes.executionId ?? "");
    if (executionId === "" || observedExecutions.has(executionId)) {
      throw new Error(`TRX report '${source}' contains a missing or repeated execution id '${executionId}'.`);
    }
    observedExecutions.add(executionId);
    const outcome = normalizeOutcome(attributes.outcome);
    results.push({
      report: source,
      framework: "dotnet",
      project,
      name,
      declarationId,
      outcome,
      skipReason: outcome === "Skipped" ? trxSkipReason(match[2] ?? "") : undefined,
    });
  }

  assertNonzeroReport(source, results.length, results.length);
  const countersMatch = /<Counters\b([^>]*?)\/>/u.exec(xml);
  if (countersMatch !== null) {
    const counters = parseAttributes(countersMatch[1]);
    assertCounter(source, "total", counters.total, results.length);
    assertCounter(source, "executed", counters.executed, results.filter((item) => item.outcome !== "Skipped").length);
    for (const name of ["error", "timeout", "aborted", "passedButRunAborted", "inProgress", "pending", "disconnected"]) {
      assertCounter(source, name, counters[name], 0);
    }
  }
  // VSTest can write only passing completed cases after a testhost crash. Case
  // counters are not evidence that the enclosing run completed successfully.
  const runErrors = [...xml.matchAll(/<RunInfo\b([^>]*?)>/gu)].some((match) => parseAttributes(match[1]).outcome === "Error");
  if ((runOutcome === "Failed" || runErrors) && !results.some((result) => result.outcome === "Failed")) {
    throw new Error(`TRX report '${source}' failed or aborted despite its completed cases having no failures.`);
  }
  return { source, framework: "dotnet", discovered: results.length, results };
}

export function parseJunitReport(xml, source) {
  const results = [];
  // Node's JUnit reporter permits literal '>' inside quoted names (for example
  // "Accept -> Check"). A tag ends only outside attribute quotes.
  for (const match of xml.matchAll(/<testcase\b((?:"[^"]*"|'[^']*'|[^<>"'])*?)(?:\/>|>([\s\S]*?)<\/testcase>)/gu)) {
    const attributes = parseAttributes(match[1]);
    const body = match[2] ?? "";
    const name = decodeXml(attributes.name ?? "");
    const testSource = normalizeTestSource(decodeXml(attributes.file ?? ""));
    if (name === "" || testSource === "") throw new Error(`JUnit report '${source}' contains a testcase without a name or source.`);
    const framework = ["python", "shell"].includes(attributes.classname) ? attributes.classname : "node";
    const skipMatch = /<(?:skipped|disabled)\b([^>]*)/u.exec(body);
    const outcome = /<(?:failure|error)\b/u.test(body)
      ? "Failed"
      : skipMatch === null
        ? "Passed"
        : "Skipped";
    results.push({
      report: source,
      framework,
      project: framework === "node" ? nodeProject(testSource) : "scripts/test/release",
      source: testSource,
      name,
      outcome,
      skipReason: outcome === "Skipped"
        ? decodeXml(parseAttributes(skipMatch?.[1] ?? "").message ?? "")
        : undefined,
    });
  }
  assertNonzeroReport(source, results.length, results.length);
  return { source, framework: "junit", discovered: results.length, results };
}

export function buildCoreTestEvidence(reports, model = loadCoreTestModel(), options = {}) {
  const results = ownedResults(reports, model);
  const operatingSystem = options.operatingSystem ?? currentOperatingSystem();

  validateResultCoverage(reports, results, model, {
    ...options,
    operatingSystem,
  });
  const expectedSkips = results
    .filter((result) => result.outcome === "Skipped")
    .map((result) => materializeExpectedSkip(result, model.policy, operatingSystem))
    .sort(compareSkip);

  const reportCounts = reports.map((report) => countEntry(
    { report: report.source },
    results.filter((result) => result.report === report.source),
  )).sort((left, right) => ordinal(left.report, right.report));
  const projectLaneKeys = [...new Set(results.map((result) => `${result.project}\0${result.primaryLane}`))].sort(ordinal);
  const projects = projectLaneKeys.map((key) => {
    const [project, lane] = key.split("\0");
    return countEntry(
      { project, lane },
      results.filter((result) => result.project === project && result.primaryLane === lane),
    );
  });
  const lanes = model.policy.primaryLanes
    .filter((lane) => results.some((result) => result.primaryLane === lane))
    .map((lane) => countEntry(
      { lane },
      results.filter((result) => result.primaryLane === lane),
    ));
  return {
    schemaVersion: 1,
    ...(model.scope === undefined ? {} : { scope: model.scope }),
    authority: "TRX definitions/results and JUnit testcases",
    operatingSystem,
    counts: counts(results),
    reports: reportCounts,
    projects,
    lanes,
    expectedSkips,
  };
}

export function auditCoreTestReports(reports, model = loadCoreTestModel(), requiredTests = [], issues) {
  const results = ownedResults(reports, model);
  const check = action => {
    try { action(); }
    catch (error) { if (issues === undefined) throw error; issues.push(error.message); }
  };
  for (const name of expectedScriptReports(model)) {
    check(() => { if (reports.filter((report) => basename(report.source) === name).length !== 1) {
      throw new Error(`Core aggregate requires exactly one '${name}' report from its owning job.`);
    } });
  }
  // Owning jobs validate platform-specific skips and required external-input
  // selections. This cross-platform audit checks their combined discovery;
  // unselected live/release artifacts are not ordinary CI prerequisites.
  check(() => requireDotnetDeclarations(model.declarations.filter((item) =>
    item.framework === "dotnet" && item.externalEnvironment === undefined), results));
  check(() => requireFixtureDeclarations(model.declarations.filter((item) => ["python", "shell"].includes(item.framework)), results));
  check(() => requireNodeSources(model.declarations, results));
  const components = model.policy.scopes?.[model.scope ?? "all"];
  const scenarios = components === undefined ? [] : ["portable", "official"].flatMap(profile => ciScenarios(components, profile));
  check(() => requireSelectedTests([...requiredTests, ...scenarios.flatMap(name => selectScenario(name, model.declarations).tests.map(test => test.id))], model, results));
  return counts(results);
}

export function auditNativeEvidence(directory, model, platforms, issues) {
  const components = model.policy.scopes[model.scope];
  const families = [
    ...(components.some(owner => ["sdk", "host", "app"].includes(owner)) ? ["sunder-platform"] : []),
    ...(components.includes("host") ? ["official-runtime"] : []),
  ];
  return families.flatMap(family => platforms.map(({ rid }) => {
    const artifact = `${family}-${model.scope}-${rid}`;
    const root = resolve(directory, family === "sunder-platform" ? "platform" : "official", artifact);
    const evidence = existsSync(root) ? listFiles(root).filter(path => basename(path) === "core-test-evidence.json") : [];
    const expectedLanes = family === "official-runtime" ? ["official-runtime"]
      : ["host-platform", ...(components.includes("app") ? ["app-packaging"] : [])];
    const lanes = new Set();
    for (const path of evidence) {
      try {
        const document = JSON.parse(readFileSync(path, "utf8"));
        if (document.schemaVersion !== 1 || document.scope !== model.scope
            || document.operatingSystem !== ({ win: "Windows", linux: "Linux", osx: "MacOS" })[rid.split("-")[0]]
            || !Number.isSafeInteger(document.counts?.executed) || document.counts.executed <= 0
            || document.counts.failed !== 0 || !Array.isArray(document.lanes)) throw new Error("invalid owning-job evidence");
        for (const row of document.lanes) {
          if (Number.isSafeInteger(row.executed) && row.executed > 0 && row.failed === 0) lanes.add(row.lane);
        }
      } catch (error) { issues.push(`${artifact}: ${error.message}`); }
    }
    const missing = expectedLanes.filter(lane => !lanes.has(lane));
    if (missing.length > 0) issues.push(`Missing required native evidence: ${artifact} (${missing.join(", ")}).`);
    return { artifact, rid, complete: missing.length === 0 };
  }));
}

function ownedResults(reports, model) {
  if (reports.length === 0) throw new Error("Core test evidence contains no reports.");
  for (const report of reports) assertNonzeroReport(report.source, report.discovered, report.results.length);
  const declarationsById = new Map(model.declarations.map((item) => [item.id, item]));
  const declarationsByRuntimeKey = new Map(model.declarations
    .filter((item) => item.framework !== "dotnet")
    .map((item) => [runtimeKey(item.framework, item.source, item.name), item]));
  const scriptSources = [...new Set((model.allDeclarations ?? model.declarations)
    .filter(item => item.framework !== "dotnet").map(item => item.source))];
  return reports.flatMap((report) => report.results).map((result) => {
    if (result.framework !== "dotnet" && /^(?:\/|[A-Za-z]:\/)/u.test(result.source ?? "")) {
      // Owning jobs may run in different checkout roots. Match a complete declared
      // source path, including foreign components so scope checks still apply.
      const matches = scriptSources.filter(source => result.source.endsWith("/" + source));
      if (matches.length > 1) throw new Error(`Ambiguous test source '${result.source}'.`);
      if (matches.length === 1) result = { ...result, source: matches[0],
        ...(result.framework === "node" ? { project: nodeProject(matches[0]) } : {}) };
    }
    const declaration = result.framework === "dotnet"
      ? declarationsById.get(result.declarationId)
      : declarationsByRuntimeKey.get(runtimeKey(result.framework, result.source, result.name));
    if (result.framework === "dotnet" && declaration === undefined) {
      throw new Error(`Runtime discovery found unowned .NET test '${result.declarationId}'.`);
    }
    const primaryLane = declaration?.primaryLane
      ?? (result.framework === "node" ? "node" : "release-artifact");
    const project = declaration?.project ?? result.project;
    if (model.scope && model.scope !== "all" && !model.policy.scopes[model.scope].includes(
      declaration?.component ?? componentFor({ ...result, project }, model.policy))) {
      throw new Error(`Result '${result.name}' is outside validation scope '${model.scope}'.`);
    }
    if (!(model.policy.projectLanes[project] ?? []).includes(primaryLane)) {
      throw new Error(`Runtime result '${result.name}' has unowned project/lane '${project}/${primaryLane}'.`);
    }
    return { ...result, project, primaryLane, declaration };
  });
}

export function validateResultCoverage(reports, results, model, options = {}) {
  const allowPartial = options.allowPartial === true;
  const requiredTests = options.requiredTests ?? [];
  const requiredPrimaryLanes = options.requiredPrimaryLanes ?? [];
  if (allowPartial && requiredTests.length === 0 && requiredPrimaryLanes.length === 0) {
    throw new Error("Partial result coverage requires at least one --require-test or --require-primary-lane selection.");
  }

  if (!allowPartial) {
    const expectedReports = [
      ...model.declarations
        .filter((item) => item.framework === "dotnet")
        .map((item) => item.project)
        .filter((project, index, values) => values.indexOf(project) === index)
        .sort(ordinal)
        .map((project) => `dotnet/${project}.trx`),
      ...expectedScriptReports(model),
    ].sort(ordinal);
    const actualReports = reports.map((report) => report.source).sort(ordinal);
    if (JSON.stringify(actualReports) !== JSON.stringify(expectedReports)) {
      throw new Error(`Core result report coverage drift: expected=${expectedReports.join(",")} actual=${actualReports.join(",")}`);
    }
    requireDotnetDeclarations(model.declarations.filter((item) => item.framework === "dotnet"), results);
    requireFixtureDeclarations(model.declarations.filter((item) => ["python", "shell"].includes(item.framework)), results);
    requireNodeSources(model.declarations, results);
    requireProjectLanePairs(expectedProjectLanePairs(model.policy), projectLanePairs(results), "complete Core result set");
  }

  requireSelectedTests(requiredTests, model, results);

  if (requiredPrimaryLanes.length > 0) {
    const requiredLaneSet = new Set(requiredPrimaryLanes);
    for (const lane of requiredPrimaryLanes) {
      if (!model.policy.primaryLanes.includes(lane)) throw new Error(`Required primary lane '${lane}' is not allowed.`);
    }
    const outOfPartition = results.find((result) => !requiredLaneSet.has(result.primaryLane));
    if (outOfPartition !== undefined) {
      throw new Error(`Primary-lane result set included out-of-partition test '${outOfPartition.name}'.`);
    }
    const selectedDeclarations = model.declarations.filter((item) =>
      requiredLaneSet.has(item.primaryLane) && item.operatingSystems.includes(options.operatingSystem));
    if (selectedDeclarations.length === 0) {
      throw new Error(`Required primary lane selection has zero runnable declarations on ${options.operatingSystem}.`);
    }
    const expectedPairs = [...new Set(selectedDeclarations.map((item) => `${item.project}\0${item.primaryLane}`))].sort(ordinal);
    requireProjectLanePairs(expectedPairs, projectLanePairs(results), "selected primary lane");
    requireDotnetDeclarations(selectedDeclarations.filter((item) => item.framework === "dotnet"), results);
    requireNodeSources(selectedDeclarations, results);
  }
}

function requireSelectedTests(selections, model, results) {
  for (const selection of new Set(selections)) {
    const declaration = model.declarations.find(item => item.id === selection);
    if (declaration === undefined) throw new Error(`Required test selection '${selection}' is not declared.`);
    const selectedResults = results.filter(result => result.declaration?.id === selection || result.name === selection);
    if (selectedResults.length === 0) throw new Error(`Required selected test '${selection}' produced no result.`);
    if (selectedResults.some(result => result.outcome === "Skipped")) {
      throw new Error(`Required selected test '${selection}' was reported as skipped after its prerequisite gate was READY.`);
    }
  }
}

export function materializeExpectedSkip(result, policy, operatingSystem) {
  for (const expected of policy.expectedSkips ?? []) {
    if (expected.framework !== result.framework) continue;
    if (expected.declarationId !== undefined && expected.declarationId !== result.declaration?.id) continue;
    if (expected.source !== undefined && expected.source !== result.source) continue;
    if (expected.name !== undefined && expected.name !== result.name) continue;
    if (expected.skipOn !== undefined && !expected.skipOn.includes(operatingSystem)) continue;
    if (expected.condition === "externalArtifactNotSelected" && result.declaration?.externalEnvironment === undefined) continue;
    if (expected.condition === "unsupportedOperatingSystem"
        && (result.declaration?.operatingSystems.includes(operatingSystem) ?? true)) continue;
    if (expected.owner !== result.primaryLane) {
      throw new Error(`Expected skip '${expected.id}' owns '${expected.owner}', but '${result.name}' belongs to '${result.primaryLane}'.`);
    }
    if (expected.runnerReasonPattern !== undefined
        && !new RegExp(expected.runnerReasonPattern, "u").test(result.skipReason ?? "")) {
      throw new Error(`Expected skip '${expected.id}' reported an unapproved reason: ${JSON.stringify(result.skipReason ?? "")}.`);
    }
    return {
      project: result.project,
      lane: result.primaryLane,
      test: result.name,
      owner: expected.owner,
      reason: expected.reason,
      runnerReason: result.skipReason ?? "",
    };
  }
  throw new Error(`Unexpected skipped test '${result.name}' (${JSON.stringify(result.skipReason ?? "no reason")}).`);
}

function main() {
  const arguments_ = process.argv.slice(2);
  const scopeIndex = arguments_.indexOf("--scope");
  if (scopeIndex >= 0 && (!arguments_[scopeIndex + 1] || arguments_[scopeIndex + 1].startsWith("--")
      || arguments_.indexOf("--scope", scopeIndex + 1) >= 0)) {
    throw new Error("--scope must be specified once with one value.");
  }
  const scope = scopeIndex < 0 ? "all" : arguments_.splice(scopeIndex, 2)[1];
  const model = loadCoreTestModel(repositoryRoot, undefined, scope);
  const aggregateIndex = arguments_.indexOf("--aggregate");
  const aggregate = aggregateIndex >= 0;
  if (aggregate) arguments_.splice(aggregateIndex, 1);
  const allowPartialIndex = arguments_.indexOf("--allow-partial");
  const allowPartial = allowPartialIndex >= 0;
  if (allowPartial) arguments_.splice(allowPartialIndex, 1);
  const requiredTests = [];
  const requiredPrimaryLanes = [];
  for (let index = 0; index < arguments_.length;) {
    if (!["--require-test", "--require-primary-lane", "--require-scenario"].includes(arguments_[index])) {
      index++;
      continue;
    }
    if (arguments_[index + 1] === undefined) throw new Error(`${arguments_[index]} requires a value.`);
    if (arguments_[index] === "--require-scenario") {
      requiredTests.push(...selectScenario(arguments_[index + 1], model.declarations, aggregate ? undefined : currentOperatingSystem()).tests.map(test => test.id));
    } else (arguments_[index] === "--require-test" ? requiredTests : requiredPrimaryLanes).push(arguments_[index + 1]);
    arguments_.splice(index, 2);
  }
  if (arguments_.length > 1 || aggregate && (allowPartial || requiredPrimaryLanes.length > 0)) {
    throw new Error("Usage: summarize-core-test-results.mjs [directory] [--aggregate | --allow-partial (--require-test <id> | --require-primary-lane <lane>) ...].");
  }
  const directory = resolve(repositoryRoot, arguments_[0] ?? "artifacts/test-results");
  summarizeResults(directory, model, { aggregate, allowPartial, requiredTests, requiredPrimaryLanes });
}

export function summarizeResults(directory, model, options = {}) {
  if (!options.aggregate) rmSync(resolve(directory, "core-test-evidence.json"), { force: true });
  const issues = [];
  const jobs = [];
  if (options.aggregate && process.env.SUNDER_CI_JOB_RESULTS !== undefined) {
    try {
      const results = JSON.parse(process.env.SUNDER_CI_JOB_RESULTS);
      const nativeRequired = model.policy.scopes[model.scope].some(owner => ["sdk", "host", "app"].includes(owner));
      for (const name of ["build-test-pack", "native"]) {
        const result = results[name]?.result ?? "missing";
        jobs.push({ job: name, result });
        if (result !== "success" && !(name === "native" && !nativeRequired && result === "skipped")) {
          issues.push(`Owning job '${name}' ended with '${result}'.`);
        }
      }
    } catch (error) { issues.push(`Invalid owning-job results: ${error.message}`); }
  }
  const files = existsSync(directory) ? listFiles(directory) : [];
  const reports = [];
  for (const path of files) {
    try {
      if (path.endsWith(".process.json")) {
        const { status, signal, error, cleanupComplete } = JSON.parse(readFileSync(path, "utf8"));
        if (status !== 0 || signal !== null || error !== null || cleanupComplete !== true) {
          throw new Error(`Test command failed: '${relative(directory, path)}': ${JSON.stringify({ status, signal, error, cleanupComplete })}`);
        }
      } else if (extname(path) === ".trx" || path.endsWith(".junit.xml")) {
        const xml = readFileSync(path, "utf8");
        const source = relative(directory, path).replaceAll("\\", "/");
        reports.push(path.endsWith(".trx") ? parseTrxReport(xml, source) : parseJunitReport(xml, source));
      }
    } catch (error) { issues.push(`${relative(directory, path)}: ${error.message}`); }
  }
  reports.sort((left, right) => ordinal(left.source, right.source));
  const results = reports.flatMap(report => report.results);
  const outcomes = counts(results);
  let evidence;
  try {
    if (options.aggregate) auditCoreTestReports(reports, model, options.requiredTests, issues);
    else evidence = buildCoreTestEvidence(reports, model, options);
  } catch (error) { issues.push(error.message); }
  const native = options.aggregate ? auditNativeEvidence(directory, model,
    JSON.parse(readFileSync(resolve(repositoryRoot, "eng/ci/platforms.json"), "utf8")).platforms, issues) : [];
  const failures = results.filter(result => result.outcome === "Failed").map(({ report, name }) => ({ report, name }));
  const summary = { schemaVersion: 1, scope: model.scope, accepted: issues.length === 0 && failures.length === 0,
    counts: outcomes, failures, issues, jobs, native };
  mkdirSync(directory, { recursive: true });
  writeFileSync(resolve(directory, "core-test-summary.json"), `${JSON.stringify(summary, null, 2)}\n`, "utf8");
  // Failed command metadata is never promoted to accepted owning-job evidence.
  if (evidence !== undefined && summary.accepted) {
    writeFileSync(resolve(directory, "core-test-evidence.json"), `${JSON.stringify(evidence, null, 2)}\n`, "utf8");
  }
  const text = `Core ${model.scope ?? "test"} summary: passed=${outcomes.passed}; failed=${outcomes.failed}; skipped=${outcomes.skipped}; issues=${issues.length}\n`
    + [...failures.map(value => `FAILED ${value.report}: ${value.name}`), ...issues].join("\n") + "\n";
  process.stdout.write(text);
  if (issues.length > 0) process.stderr.write(issues.join("\n") + "\n");
  if (process.env.GITHUB_STEP_SUMMARY) {
    const escaped = text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
    appendFileSync(process.env.GITHUB_STEP_SUMMARY, `<h3>Core test results</h3><pre>${escaped}</pre>\n`);
  }
  if (!summary.accepted) process.exitCode = 1;
  return summary;
}

function requireDotnetDeclarations(declarations, results) {
  const observed = new Set(results
    .filter((result) => result.framework === "dotnet")
    .map((result) => result.declaration?.id));
  const missing = declarations.filter((declaration) => !observed.has(declaration.id));
  if (missing.length > 0) {
    throw new Error(`Runtime discovery omitted ${missing.length} selected .NET declaration(s): ${missing.slice(0, 10).map((item) => item.id).join(", ")}`);
  }
}

function requireFixtureDeclarations(declarations, results) {
  const observed = new Set(results.map((result) => runtimeKey(result.framework, result.source, result.name)));
  const missing = declarations.filter((declaration) => !observed.has(runtimeKey(declaration.framework, declaration.source, declaration.name)));
  if (missing.length > 0) {
    throw new Error(`Release fixture results omitted ${missing.length} declaration(s): ${missing.map((item) => item.id).join(", ")}`);
  }
}

function requireNodeSources(declarations, results) {
  // Node expands dynamic declarations and nested cases at runtime. Every selected
  // source must still contribute evidence, including files sharing one report.
  const observed = new Set(results.filter(result => result.framework === "node").map(result => result.source));
  const missing = [...new Set(declarations.filter(test => test.framework === "node").map(test => test.source))]
    .filter(source => !observed.has(source));
  if (missing.length > 0) throw new Error(`Node results omitted selected source(s): ${missing.join(", ")}`);
}

function requireProjectLanePairs(expected, actual, label) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${label} project/lane coverage drift: expected=${expected.join(",")} actual=${actual.join(",")}`);
  }
}

function expectedProjectLanePairs(policy) {
  return Object.entries(policy.projectLanes)
    .flatMap(([project, lanes]) => lanes.map((lane) => `${project}\0${lane}`))
    .sort(ordinal);
}

function projectLanePairs(results) {
  return [...new Set(results.map((result) => `${result.project}\0${result.primaryLane}`))].sort(ordinal);
}

function countEntry(identity, results) {
  return { ...identity, ...counts(results) };
}

function counts(results) {
  return {
    discovered: results.length,
    executed: results.filter((result) => result.outcome !== "Skipped").length,
    passed: results.filter((result) => result.outcome === "Passed").length,
    failed: results.filter((result) => result.outcome === "Failed").length,
    skipped: results.filter((result) => result.outcome === "Skipped").length,
  };
}

function assertNonzeroReport(source, discovered, results) {
  if (discovered === 0 || results === 0) throw new Error(`Test report '${source}' contained zero tests.`);
}

function assertCounter(source, name, value, expected) {
  if (value === undefined) return;
  if (!/^\d+$/u.test(value) || Number(value) !== expected) {
    throw new Error(`TRX report '${source}' has ${name}=${value}, expected ${expected}.`);
  }
}

function trxSkipReason(body) {
  const message = /<Message>([\s\S]*?)<\/Message>/u.exec(body)?.[1] ?? "";
  return decodeXml(message).trim();
}

function normalizeOutcome(value) {
  if (value === "Passed") return "Passed";
  if (value === "NotExecuted") return "Skipped";
  return "Failed";
}

function listFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = resolve(directory, entry.name);
    return entry.isDirectory() ? listFiles(path) : entry.isFile() ? [path] : [];
  });
}

function parseAttributes(value) {
  return Object.fromEntries([...value.matchAll(/([A-Za-z_:][A-Za-z0-9_.:-]*)\s*=\s*(?:"([^"]*)"|'([^']*)')/gu)]
    .map((match) => [match[1], match[2] ?? match[3]]));
}

function decodeXml(value) {
  return value
    .replaceAll("&quot;", '"')
    .replaceAll("&apos;", "'")
    .replaceAll("&lt;", "<")
    .replaceAll("&gt;", ">")
    .replaceAll("&amp;", "&");
}

function normalizeTestSource(value) {
  let normalized = value.replaceAll("\\", "/");
  const root = repositoryRoot.replaceAll("\\", "/");
  if (normalized.startsWith(root + "/")) normalized = normalized.slice(root.length + 1);
  if (normalized.includes("/dist-test/test/")) {
    normalized = normalized.replace("/dist-test/test/", "/test/").replace(/\.js$/u, ".ts");
  }
  return normalized;
}

function runtimeKey(framework, source, name) {
  return `${framework}\0${source}\0${name}`;
}

function compareSkip(left, right) {
  return ordinal(`${left.project}\0${left.lane}\0${left.test}`, `${right.project}\0${right.lane}\0${right.test}`);
}

function ordinal(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.stack ?? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}
