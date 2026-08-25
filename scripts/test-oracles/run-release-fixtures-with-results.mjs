import { spawnSync } from "node:child_process";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { loadCoreTestModel } from "./core-test-policy.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const { policy, declarations } = loadCoreTestModel();
const tests = declarations.filter((test) => test.framework === "python" || test.framework === "shell");
if (tests.length === 0) throw new Error("Release fixture discovery selected no Python or shell tests.");
const outputDirectory = resolve(repositoryRoot, "artifacts/test-results");
const outputPath = resolve(outputDirectory, policy.reports.releaseFixtures);
mkdirSync(outputDirectory, { recursive: true });
rmSync(outputPath, { force: true });

const results = [];
for (const test of tests) {
  const command = test.framework === "python" ? "python3" : "bash";
  const arguments_ = test.framework === "python"
    ? [test.source, test.runnerSelector]
    : [test.source];
  const started = process.hrtime.bigint();
  const result = spawnSync(command, arguments_, {
    cwd: repositoryRoot,
    encoding: "utf8",
    env: { ...process.env, NO_COLOR: "1" },
  });
  const durationSeconds = Number(process.hrtime.bigint() - started) / 1_000_000_000;
  const output = `${result.stdout ?? ""}${result.stderr ?? ""}`;
  process.stdout.write(output);
  results.push({
    ...test,
    durationSeconds,
    exitCode: result.status ?? 1,
    output: result.error === undefined ? output : `${output}\n${result.error.stack ?? result.error.message}`,
  });
}

const failures = results.filter((result) => result.exitCode !== 0);
const duration = results.reduce((total, result) => total + result.durationSeconds, 0);
const cases = results.map((result) => {
  const attributes = `name="${xml(result.name)}" time="${result.durationSeconds.toFixed(6)}" classname="${result.framework}" file="${xml(result.source)}"`;
  return result.exitCode === 0
    ? `  <testcase ${attributes}/>`
    : `  <testcase ${attributes}><failure message="exit code ${result.exitCode}">${xml(result.output)}</failure></testcase>`;
});
writeFileSync(outputPath, [
  "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
  `<testsuite name="release-fixtures" tests="${results.length}" failures="${failures.length}" time="${duration.toFixed(6)}">`,
  ...cases,
  "</testsuite>",
  "",
].join("\n"), "utf8");
process.stdout.write(`Wrote ${relative(repositoryRoot, outputPath)} with ${results.length} release fixture result(s).\n`);
if (failures.length > 0) process.exitCode = 1;

function xml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll('"', "&quot;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");
}
