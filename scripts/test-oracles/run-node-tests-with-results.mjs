import { mkdirSync, rmSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const [reportName, ...testFiles] = process.argv.slice(2);
if (reportName === undefined || !/^[a-z0-9][a-z0-9-]*$/u.test(reportName) || testFiles.length === 0) {
  throw new Error("Usage: run-node-tests-with-results.mjs <report-name> <test-file> [...].");
}
const outputDirectory = resolve(repositoryRoot, "artifacts/test-results");
mkdirSync(outputDirectory, { recursive: true });
const outputPath = resolve(outputDirectory, `${reportName}.junit.xml`);
rmSync(outputPath, { force: true });
const namePattern = process.env.SUNDER_NODE_TEST_NAME_PATTERN;
const result = spawnSync(process.execPath, [
  "--test",
  "--test-concurrency=1",
  ...(namePattern === undefined ? [] : [`--test-name-pattern=${namePattern}`]),
  "--test-reporter=spec",
  "--test-reporter=junit",
  "--test-reporter-destination=stdout",
  `--test-reporter-destination=${outputPath}`,
  ...testFiles,
], { stdio: "inherit" });
if (result.error !== undefined) throw result.error;
process.exitCode = result.status ?? 1;
