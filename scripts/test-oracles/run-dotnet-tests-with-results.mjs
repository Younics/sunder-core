import { mkdirSync, rmSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { loadCoreTestModel } from "./core-test-policy.mjs";
import { currentOperatingSystem } from "./test-lane-prerequisites.mjs";
import { dotnetTestArguments, dotnetTestBudget } from "./dotnet-test-command.mjs";
import { maximumProcessTimeoutMilliseconds, runOwnedTestProcess } from "./run-owned-test-process.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const { policy, declarations } = loadCoreTestModel();
const requestedArguments = process.argv.slice(2);
const primaryLane = takeOption("--primary-lane");
const project = takeOption("--project");
const timeoutOverride = takeOption("--timeout-ms");
const timeoutMilliseconds = timeoutOverride === undefined ? undefined : Number(timeoutOverride);
if (timeoutMilliseconds !== undefined && (!Number.isSafeInteger(timeoutMilliseconds) || timeoutMilliseconds <= 0 || timeoutMilliseconds > maximumProcessTimeoutMilliseconds)) {
  throw new Error(`--timeout-ms must be a positive integer no greater than ${maximumProcessTimeoutMilliseconds}.`);
}
const list = takeFlag("--list");
const requestedOutputDirectory = takeOption("--results-directory")
  ?? (project === undefined ? "artifacts/test-results/dotnet" : `artifacts/test-results/local/${project}/${primaryLane ?? "full"}`);
const build = takeFlag("--build");
if (requestedArguments.some((argument) => ["--filter", "--logger", "-l", "--results-directory", "-m", "--maxcpucount"].includes(argument))) {
  throw new Error("The Core .NET reporter owns filtering, logging, and result paths.");
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
    declarations: selectedTests.map((test) => test.id),
    projects: [...new Set(selectedTests.map((test) => test.project))].sort().map((name) => ({ project: name,
      ...dotnetTestBudget(name, selectedTests), ...(timeoutMilliseconds === undefined ? {} : { timeoutMilliseconds }) })) }, null, 2)}\n`;
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
  const budget = dotnetTestBudget(projectName, selectedTests);
  const logStem = resolve(outputDirectory, "diagnostics", projectName);
  process.env.SUNDER_TEST_COMMAND_LOG_DIRECTORY = `${logStem}.commands`;
  process.stdout.write(`\n=== ${projectName} ===\n`);
  const args = dotnetTestArguments({ projectPath, projectName, outputDirectory, logStem, build, filter, budget,
    extraArguments: requestedArguments });
  const result = await runOwnedTestProcess("dotnet", args, { cwd: repositoryRoot, logStem,
    timeoutMilliseconds: timeoutMilliseconds ?? budget.timeoutMilliseconds });
  process.stdout.write(`${projectName}: elapsed=${result.elapsedMilliseconds}ms; status=${result.status}; signal=${result.signal}; `
    + `error=${JSON.stringify(result.error)}; cleanupComplete=${result.cleanupComplete}\n`);
  if (result.error !== null || result.status !== 0 || !result.cleanupComplete) failed.push(projectName);
  if (!result.cleanupComplete || result.error?.code === "ECANCELED") {
    throw new Error(`Stopping before the next project: ${projectName} was cancelled or process cleanup could not be confirmed. See ${logStem}.process.json.`);
  }
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
