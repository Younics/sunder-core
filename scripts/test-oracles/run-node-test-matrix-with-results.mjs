import { existsSync, readdirSync, rmSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const nodeRoot = resolve(repositoryRoot, "languages/typescript/node");
const wrapper = resolve(repositoryRoot, "scripts/test-oracles/run-node-tests-with-results.mjs");
const npm = process.platform === "win32" ? "npm.cmd" : "npm";

for (const packageName of ["sdk", "package-tool", "create-sunder-package"]) {
  const packageRoot = resolve(nodeRoot, "packages", packageName);
  rmSync(resolve(packageRoot, "dist"), { recursive: true, force: true });
  rmSync(resolve(packageRoot, "dist-test"), { recursive: true, force: true });
}

runNodeReport("node-core-oracles", [
  ...testFiles(resolve(repositoryRoot, "scripts/compatibility"), ".test.mjs"),
  ...testFiles(resolve(repositoryRoot, "scripts/test/release"), ".test.mjs"),
  ...testFiles(resolve(repositoryRoot, "scripts/test-oracles"), ".test.mjs"),
]);

runWorkspace("@sunder/sdk", "packages/sdk", "node-sdk");
runWorkspace("@sunder/package-tool", "packages/package-tool", "node-package-tool");
runWorkspace("create-sunder-package", "packages/create-sunder-package", "node-create-sunder-package");
runRequired(npm, ["run", "templates:typecheck"], nodeRoot, "Node template generation and typecheck");

function runWorkspace(workspace, project, report) {
  runRequired(npm, ["run", "typecheck", "--workspace", workspace], nodeRoot, `${workspace} typecheck`);
  runRequired(npm, ["run", "build", "--workspace", workspace], nodeRoot, `${workspace} build`);
  runRequired(
    npm,
    ["exec", "--", "tsc", "-p", `${project}/tsconfig.test.json`],
    nodeRoot,
    `${workspace} test compilation`,
  );
  runNodeReport(report, testFiles(resolve(nodeRoot, project, "dist-test/test"), ".test.js"));
}

function runNodeReport(report, files) {
  if (files.length === 0) throw new Error(`${report} selected no Node test files.`);
  runRequired(process.execPath, [wrapper, report, ...files], repositoryRoot, `${report} test report`);
}

function testFiles(directory, suffix) {
  if (!existsSync(directory)) return [];
  return readdirSync(directory, { withFileTypes: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith(suffix))
    .map((entry) => resolve(directory, entry.name))
    .sort(ordinal);
}

function runRequired(command, arguments_, cwd, label) {
  process.stdout.write(`\n=== ${label} ===\n`);
  const result = spawnSync(command, arguments_, {
    cwd,
    env: { ...process.env, NO_COLOR: "1" },
    stdio: "inherit",
  });
  if (result.error !== undefined) throw new Error(`${label} could not start.`, { cause: result.error });
  if (result.status !== 0) throw new Error(`${label} failed with exit code ${result.status ?? "unknown"}.`);
}

function ordinal(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}
