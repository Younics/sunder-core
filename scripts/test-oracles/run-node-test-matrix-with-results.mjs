import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { loadCoreTestModel } from "./core-test-policy.mjs";
import { createNodeTestWorkspace } from "./node-test-workspace.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const wrapper = resolve(repositoryRoot, "scripts/test-oracles/run-node-tests-with-results.mjs");
const npm = process.platform === "win32" ? "npm.cmd" : "npm";
const { values } = parseArgs({ options: { scope: { type: "string", default: "all" },
  "results-directory": { type: "string", default: "artifacts/test-results" },
  action: { type: "string", default: "all" }, work: { type: "string" }, project: { type: "string" } } });
if (!["all", "scripts", "build", "test", "templates"].includes(values.action)) throw new Error("Unknown npm stage.");
if (["build", "test", "templates"].includes(values.action) && !values.work) throw new Error("Staged npm execution requires --work.");
const { declarations } = loadCoreTestModel(repositoryRoot, undefined, values.scope);
const tests = declarations.filter(test => test.framework === "node");
const selectedProjects = new Set(tests.map(test => test.project));
const outputArgs = ["--results-directory", values["results-directory"]];

const scripts = [...new Set(tests.filter(test => test.source.startsWith("scripts/")).map(test => resolve(repositoryRoot, test.source)))].sort();
if (["all", "scripts"].includes(values.action) && scripts.length > 0) runNodeReport("node-core-oracles", scripts);

const workspaces = [
  ["@sunder/sdk", "packages/sdk", "node-sdk"],
  ["@sunder/package-tool", "packages/package-tool", "node-package-tool"],
  ["create-sunder-package", "packages/create-sunder-package", "node-create-sunder-package"],
].filter(([, project]) => selectedProjects.has(project.split("/").at(-1)));
const statePath = values.work && resolve(values.work, "workspace.json");
const workspace = values.action === "scripts" || workspaces.length === 0 ? undefined
  : ["all", "build"].includes(values.action) ? createNodeTestWorkspace(repositoryRoot, values.work)
    : JSON.parse(readFileSync(statePath, "utf8"));
if (values.action === "build") {
  mkdirSync(values.work, { recursive: true });
  writeFileSync(statePath, JSON.stringify(workspace), { flag: "wx" });
}
const nodeRoot = workspace?.nodeRoot;
let completed = false;
try {
  // Packed package-tool cases consume the scaffolder as well as the SDK/tool.
  // Build the complete production closure before any workspace artifact tests.
  for (const [workspace] of ["all", "build"].includes(values.action) ? workspaces : []) {
    runRequired(npm, ["run", "typecheck", "--workspace", workspace], nodeRoot, `${workspace} typecheck`);
    runRequired(npm, ["run", "build", "--workspace", workspace], nodeRoot, `${workspace} build`);
  }
  if (values.action === "test" && !workspaces.some(([name]) => name === values.project)) throw new Error("Select a declared npm project with --project.");
  for (const [workspace, project, report] of values.action === "all" ? workspaces : values.action === "test" ? workspaces.filter(([name]) => name === values.project) : []) {
    runWorkspace(workspace, project, report);
  }
  if (["all", "templates"].includes(values.action) && selectedProjects.has("create-sunder-package")) runRequired(npm, ["run", "templates:typecheck"], nodeRoot, "Node template generation and typecheck");
  completed = true;
} finally {
  if (workspace && completed && values.action === "all") rmSync(workspace.root, { recursive: true, force: true });
  else if (workspace && !completed) process.stderr.write(`Failed Node producer workspace retained at ${workspace.root}\n`);
}

function runWorkspace(workspace, project, report) {
  runRequired(
    npm,
    ["exec", "--", "tsc", "-p", `${project}/tsconfig.test.json`],
    nodeRoot,
    `${workspace} test compilation`,
  );
  runNodeReport(report, testFiles(resolve(nodeRoot, project, "dist-test/test"), ".test.js"),
    workspace === "@sunder/package-tool"
      ? { SUNDER_TEST_PACKED_CLOSURE: "1", SUNDER_TEST_PACKED_SERVICES: "1" }
      : {});
}

function runNodeReport(report, files, environment = {}) {
  if (files.length === 0) throw new Error(`${report} selected no Node test files.`);
  runRequired(process.execPath, [wrapper, ...outputArgs, report, ...files], repositoryRoot, `${report} test report`, environment);
}

function testFiles(directory, suffix) {
  if (!existsSync(directory)) return [];
  return readdirSync(directory, { withFileTypes: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith(suffix))
    .map((entry) => resolve(directory, entry.name))
    .sort(ordinal);
}

function runRequired(command, arguments_, cwd, label, environment = {}) {
  process.stdout.write(`\n=== ${label} ===\n`);
  const result = spawnSync(command, arguments_, {
    cwd,
    env: { ...process.env, ...environment, NO_COLOR: "1" },
    stdio: "inherit",
  });
  if (result.error !== undefined) throw new Error(`${label} could not start.`, { cause: result.error });
  if (result.status !== 0) throw new Error(`${label} failed with exit code ${result.status ?? "unknown"}.`);
}

function ordinal(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}
