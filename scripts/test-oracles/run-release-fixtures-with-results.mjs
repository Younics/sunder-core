import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { loadCoreTestModel } from "./core-test-policy.mjs";
import { testProcessEnvironment } from "./test-process-environment.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const args = process.argv.slice(2);
if (args.length !== 0 && (args.length !== 4 || args[0] !== "--scope" || args[2] !== "--results-directory")) {
  throw new Error("Usage: run-release-fixtures-with-results.mjs [--scope <scope> --results-directory <directory>]");
}
const { policy, declarations } = loadCoreTestModel(repositoryRoot, undefined, args[1] ?? "all");
const tests = declarations.filter((test) => test.framework === "python" || test.framework === "shell");
if (tests.length === 0) throw new Error("Release fixture discovery selected no Python or shell tests.");
const outputDirectory = resolve(repositoryRoot, args[3] ?? "artifacts/test-results");
const outputPath = resolve(outputDirectory, policy.reports.releaseFixtures);
mkdirSync(outputDirectory, { recursive: true });
rmSync(outputPath, { force: true });

const results = [];
const groups = Map.groupBy(tests, test => test.source);
const temporary = mkdtempSync(resolve(tmpdir(), "sunder-script-results-"));
try {
for (const group of groups.values()) {
  const test = group[0];
  const report = resolve(temporary, "results.json");
  rmSync(report, { force: true });
  const command = test.framework === "python" ? "python3" : "bash";
  const arguments_ = test.framework === "python"
    ? ["-B", "scripts/test-oracles/run-python-fixture.py", test.source, report, ...group.map(item => item.runnerSelector)]
    : [test.source];
  const started = process.hrtime.bigint();
  const result = spawnSync(command, arguments_, {
    cwd: repositoryRoot,
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024,
    env: { ...testProcessEnvironment(), NO_COLOR: "1" },
  });
  const durationSeconds = Number(process.hrtime.bigint() - started) / 1_000_000_000;
  const output = `${result.stdout ?? ""}${result.stderr ?? ""}`;
  process.stdout.write(output);
  const rows = existsSync(report) ? JSON.parse(readFileSync(report, "utf8")) : [];
  const complete = rows.length === group.length && new Set(rows.map(row => row.selector)).size === group.length
    && group.every(item => rows.some(row => row.selector === item.runnerSelector));
  const reportedFailure = rows.some(row => row.status === "failed");
  for (const item of group) {
    const row = complete && (result.status === 0 || reportedFailure) ? rows.find(row => row.selector === item.runnerSelector) : undefined;
    results.push({
    ...item,
    durationSeconds,
    exitCode: result.status ?? 1,
    output: result.error === undefined ? output : `${output}\n${result.error.stack ?? result.error.message}`,
    ...(row === undefined ? {} : { durationSeconds: row.durationSeconds, exitCode: row.status === "failed" ? 1 : 0,
      skipped: row.status === "skipped", output: row.output }),
    ...(test.framework === "python" && row === undefined ? { exitCode: 1 } : {}),
  });
  }
}
} finally {
  rmSync(temporary, { recursive: true, force: true });
}

const failures = results.filter((result) => result.exitCode !== 0);
const duration = results.reduce((total, result) => total + result.durationSeconds, 0);
const cases = results.map((result) => {
  const attributes = `name="${xml(result.name)}" time="${result.durationSeconds.toFixed(6)}" classname="${result.framework}" file="${xml(result.source)}"`;
  return result.skipped ? `  <testcase ${attributes}><skipped message="${xml(result.output)}"/></testcase>` : result.exitCode === 0
    ? `  <testcase ${attributes}/>`
    : `  <testcase ${attributes}><failure message="exit code ${result.exitCode}">${xml(result.output)}</failure></testcase>`;
});
writeFileSync(outputPath, [
  "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
  `<testsuite name="release-fixtures" tests="${results.length}" failures="${failures.length}" skipped="${results.filter(result => result.skipped).length}" time="${duration.toFixed(6)}">`,
  ...cases,
  "</testsuite>",
  "",
].join("\n"), "utf8");
process.stdout.write(`Wrote ${relative(repositoryRoot, outputPath)} with ${results.length} release fixture result(s).\n`);
if (results.some(result => result.skipped)) process.stderr.write("Selected release fixtures must execute; unexpected skipped fixture.\n");
if (failures.length > 0 || results.some(result => result.skipped)) process.exitCode = 1;

function xml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll('"', "&quot;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");
}
