import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { basename, dirname, extname, relative, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { loadCoreTestModel, repositoryRoot } from "./core-test-policy.mjs";
import { currentOperatingSystem } from "./test-lane-prerequisites.mjs";

export function parseTrxReport(xml, source) {
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
  }
  return { source, framework: "dotnet", discovered: results.length, results };
}

export function parseJunitReport(xml, source) {
  const results = [];
  for (const match of xml.matchAll(/<testcase\b([^>]*?)(?:\/>|>([\s\S]*?)<\/testcase>)/gu)) {
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
      project: framework === "node" ? nodeProject(testSource) : "scripts/release",
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
  if (reports.length === 0) throw new Error("Core test evidence contains no reports.");
  for (const report of reports) assertNonzeroReport(report.source, report.discovered, report.results.length);
  const operatingSystem = options.operatingSystem ?? currentOperatingSystem();
  const declarationsById = new Map(model.declarations.map((item) => [item.id, item]));
  const declarationsByRuntimeKey = new Map(model.declarations
    .filter((item) => item.framework !== "dotnet")
    .map((item) => [runtimeKey(item.framework, item.source, item.name), item]));
  const results = reports.flatMap((report) => report.results).map((result) => {
    const declaration = result.framework === "dotnet"
      ? declarationsById.get(result.declarationId)
      : declarationsByRuntimeKey.get(runtimeKey(result.framework, result.source, result.name));
    if (result.framework === "dotnet" && declaration === undefined) {
      throw new Error(`Runtime discovery found unowned .NET test '${result.declarationId}'.`);
    }
    const primaryLane = declaration?.primaryLane
      ?? (result.framework === "node" ? "node" : "release-artifact");
    const project = declaration?.project ?? result.project;
    if (!(model.policy.projectLanes[project] ?? []).includes(primaryLane)) {
      throw new Error(`Runtime result '${result.name}' has unowned project/lane '${project}/${primaryLane}'.`);
    }
    return { ...result, project, primaryLane, declaration };
  });

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
    authority: "TRX definitions/results and JUnit testcases",
    operatingSystem,
    counts: counts(results),
    reports: reportCounts,
    projects,
    lanes,
    expectedSkips,
  };
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
      ...model.policy.reports.node,
      model.policy.reports.releaseFixtures,
    ].sort(ordinal);
    const actualReports = reports.map((report) => report.source).sort(ordinal);
    if (JSON.stringify(actualReports) !== JSON.stringify(expectedReports)) {
      throw new Error(`Core result report coverage drift: expected=${expectedReports.join(",")} actual=${actualReports.join(",")}`);
    }
    requireDotnetDeclarations(model.declarations.filter((item) => item.framework === "dotnet"), results);
    requireFixtureDeclarations(model.declarations.filter((item) => ["python", "shell"].includes(item.framework)), results);
    requireProjectLanePairs(expectedProjectLanePairs(model.policy), projectLanePairs(results), "complete Core result set");
  }

  for (const selection of requiredTests) {
    const declaration = model.declarations.find((item) => item.id === selection);
    if (declaration === undefined) throw new Error(`Required test selection '${selection}' is not declared.`);
    const selectedResults = results.filter((result) => result.declaration?.id === selection || result.name === selection);
    if (selectedResults.length === 0) throw new Error(`Required selected test '${selection}' produced no result.`);
    if (selectedResults.some((result) => result.outcome === "Skipped")) {
      throw new Error(`Required selected test '${selection}' was reported as skipped after its prerequisite gate was READY.`);
    }
  }

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
  const allowPartialIndex = arguments_.indexOf("--allow-partial");
  const allowPartial = allowPartialIndex >= 0;
  if (allowPartial) arguments_.splice(allowPartialIndex, 1);
  const requiredTests = [];
  const requiredPrimaryLanes = [];
  for (let index = 0; index < arguments_.length;) {
    if (arguments_[index] !== "--require-test" && arguments_[index] !== "--require-primary-lane") {
      index++;
      continue;
    }
    if (arguments_[index + 1] === undefined) throw new Error(`${arguments_[index]} requires a value.`);
    (arguments_[index] === "--require-test" ? requiredTests : requiredPrimaryLanes).push(arguments_[index + 1]);
    arguments_.splice(index, 2);
  }
  if (arguments_.length > 1) {
    throw new Error("Usage: summarize-core-test-results.mjs [directory] [--allow-partial (--require-test <id> | --require-primary-lane <lane>) ...].");
  }
  const directory = resolve(repositoryRoot, arguments_[0] ?? "artifacts/test-results");
  if (!existsSync(directory)) throw new Error(`Test result directory '${directory}' does not exist.`);
  const files = listFiles(directory).filter((path) => extname(path) === ".trx" || path.endsWith(".junit.xml"));
  if (files.length === 0) throw new Error(`No TRX or JUnit files were found under '${directory}'.`);
  const reports = files.map((path) => {
    const xml = readFileSync(path, "utf8");
    const source = relative(directory, path).replaceAll("\\", "/");
    return path.endsWith(".trx") ? parseTrxReport(xml, source) : parseJunitReport(xml, source);
  }).sort((left, right) => ordinal(left.source, right.source));
  const evidence = buildCoreTestEvidence(reports, loadCoreTestModel(), {
    allowPartial,
    requiredTests,
    requiredPrimaryLanes,
  });
  mkdirSync(directory, { recursive: true });
  const output = resolve(directory, "core-test-evidence.json");
  writeFileSync(output, `${JSON.stringify(evidence, null, 2)}\n`, "utf8");
  process.stdout.write(`Wrote compact evidence for ${evidence.counts.discovered} discovered and ${evidence.counts.executed} executed tests to ${relative(repositoryRoot, output)}.\n`);
  process.stdout.write(`Outcomes: passed=${evidence.counts.passed}; failed=${evidence.counts.failed}; skipped=${evidence.counts.skipped}\n`);
  if (evidence.counts.failed > 0) process.exitCode = 1;
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
  return Object.fromEntries([...value.matchAll(/([A-Za-z_:][A-Za-z0-9_.:-]*)="([^"]*)"/gu)]
    .map((match) => [match[1], match[2]]));
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

function nodeProject(source) {
  const match = /languages\/typescript\/node\/packages\/([^/]+)\//u.exec(source);
  if (match !== null) return match[1];
  return source.startsWith("scripts/test-oracles/") ? "scripts/test-oracles" : "scripts/release";
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
