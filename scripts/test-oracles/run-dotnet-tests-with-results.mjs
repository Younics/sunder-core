import { spawnSync } from "node:child_process";
import { mkdirSync, rmSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { loadCoreTestModel } from "./core-test-policy.mjs";
import { currentOperatingSystem } from "./test-lane-prerequisites.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const { policy, declarations } = loadCoreTestModel();
const requestedArguments = process.argv.slice(2);
const primaryLane = takeOption("--primary-lane");
const project = takeOption("--project");
const timeoutMilliseconds = Number(takeOption("--timeout-ms") ?? 60 * 60 * 1000);
if (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds <= 0) {
  throw new Error("--timeout-ms must be a positive safe integer.");
}
const list = takeFlag("--list");
const requestedOutputDirectory = takeOption("--results-directory")
  ?? (project === undefined ? "artifacts/test-results/dotnet" : `artifacts/test-results/local/${project}/${primaryLane ?? "full"}`);
const build = takeFlag("--build");
if (requestedArguments.some((argument) => ["--filter", "--logger", "-l", "--results-directory", "-m", "--maxcpucount"].includes(argument))) {
  throw new Error("The Core .NET reporter owns filtering, logging, result paths, and serialized MSBuild execution.");
}
const allDotnetTests = declarations.filter((test) => test.framework === "dotnet");
const operatingSystem = currentOperatingSystem();
if (project !== undefined && !allDotnetTests.some((test) => test.project === project)) {
  throw new Error(`Unknown .NET test project '${project}'. Use its project name, such as Sunder.Package.Build.Tests.`);
}
if (primaryLane !== undefined && !policy.primaryLanes.includes(primaryLane)) {
  throw new Error(`Unknown primary lane '${primaryLane}'.`);
}
const laneTests = primaryLane === undefined
  ? allDotnetTests
  : allDotnetTests.filter((test) => test.primaryLane === primaryLane && test.operatingSystems.includes(operatingSystem));
const selectedTests = project === undefined ? laneTests : laneTests.filter((test) => test.project === project);
if (selectedTests.length === 0) {
  throw new Error(`Selection project='${project ?? "all"}', lane='${primaryLane ?? "all"}' has no runnable .NET tests on ${operatingSystem}.`);
}
if (list) {
  const output = `${JSON.stringify({ project: project ?? "all", primaryLane: primaryLane ?? "all", operatingSystem,
    declarations: selectedTests.map((test) => test.id) }, null, 2)}\n`;
  await new Promise((resolve, reject) => process.stdout.write(output, (error) => error ? reject(error) : resolve()));
  process.exit(0);
}
const outputDirectory = resolve(repositoryRoot, requestedOutputDirectory);
const cleanDirectory = project === undefined && primaryLane === undefined && requestedOutputDirectory === "artifacts/test-results/dotnet"
  ? dirname(outputDirectory)
  : outputDirectory;
rmSync(cleanDirectory, { recursive: true, force: true });
mkdirSync(outputDirectory, { recursive: true });
const projectPaths = [...new Set(selectedTests
  .map((test) => `languages/csharp/dotnet/tests/${test.project}/${test.project}.csproj`))].sort();

const failed = [];
for (const projectPath of projectPaths) {
  const projectName = projectPath.split("/").at(-1).replace(/\.csproj$/u, "");
  const filter = primaryLane === undefined ? undefined : projectFilter(projectName, selectedTests, allDotnetTests);
  const hangTimeout = projectName === "Sunder.Package.Templates.Tests" ? "10m" : "5m";
  process.stdout.write(`\n=== ${projectName} ===\n`);
  const result = spawnSync("dotnet", [
    "test",
    projectPath,
    "--nologo",
    "--no-restore",
    ...(!build ? ["--no-build"] : []),
    "-m:1",
    "--blame-hang",
    "--blame-hang-timeout",
    hangTimeout,
    "--blame-hang-dump-type",
    "none",
    "--logger",
    `trx;LogFileName=${projectName}.trx`,
    "--results-directory",
    outputDirectory,
    ...(filter === undefined ? [] : ["--filter", filter]),
    ...requestedArguments,
  ], { cwd: repositoryRoot, encoding: "utf8", timeout: timeoutMilliseconds, maxBuffer: 128 * 1024 * 1024 });
  process.stdout.write(result.stdout ?? "");
  process.stderr.write(result.stderr ?? "");
  if (result.error !== undefined || result.status !== 0) failed.push(projectName);
}
if (failed.length > 0) {
  throw new Error(`Core .NET test projects failed: ${failed.join(", ")}`);
}

function projectFilter(project, selected, all) {
  const selectedDeclarations = new Set(selected
    .filter((test) => test.project === project)
    .map((test) => test.id));
  const allDeclarations = [...new Set(all
    .filter((test) => test.project === project)
    .map((test) => test.id))].sort(ordinal);
  if (selectedDeclarations.size === allDeclarations.length) return undefined;
  const excludedDeclarations = allDeclarations.filter((id) => !selectedDeclarations.has(id));
  if (selectedDeclarations.size <= excludedDeclarations.length) {
    return [...selectedDeclarations].sort(ordinal).map((id) => `FullyQualifiedName=${id}`).join("|");
  }
  return excludedDeclarations.map((id) => `FullyQualifiedName!=${id}`).join("&");
}

function takeOption(name) {
  const index = requestedArguments.indexOf(name);
  if (index < 0) return undefined;
  if (requestedArguments[index + 1] === undefined || requestedArguments.indexOf(name, index + 1) >= 0) {
    throw new Error(`${name} must be specified once with one value.`);
  }
  return requestedArguments.splice(index, 2)[1];
}

function takeFlag(name) {
  const indexes = requestedArguments.flatMap((argument, index) => argument === name ? [index] : []);
  if (indexes.length > 1) throw new Error(`${name} may be specified only once.`);
  if (indexes.length === 0) return false;
  requestedArguments.splice(indexes[0], 1);
  return true;
}

function ordinal(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}
