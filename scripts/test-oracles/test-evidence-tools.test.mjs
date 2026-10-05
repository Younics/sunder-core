import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFile, stat } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  loadCoreTestModel,
  validateCharacterization,
  validateProjectLaneOwnership,
} from "./core-test-policy.mjs";
import { missingPrerequisites } from "./test-lane-prerequisites.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

test("project-scoped test selection excludes process cases from the fast lane and keeps them in full validation", () => {
  const runner = resolve(repositoryRoot, "scripts/test-oracles/run-dotnet-tests-with-results.mjs");
  const select = (...args) => spawnSync(process.execPath, [runner, "--list", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  const fast = select("--project", "Sunder.Package.Build.Tests", "--primary-lane", "dotnet-linux");
  assert.equal(fast.status, 0, fast.stderr);
  const fastIds = JSON.parse(fast.stdout).declarations;
  assert.ok(fastIds.includes("Sunder.Package.Build.Tests.ServiceApiEditorGeneratorTests.ReusedDriverTracksUnsavedRenameRemovalAndIncompleteBody"));
  const projectIds = new Set(loadCoreTestModel().declarations.filter((test) => test.project === "Sunder.Package.Build.Tests").map((test) => test.id));
  assert.ok(fastIds.every((id) => projectIds.has(id)));
  const workspace = "Sunder.Package.Build.Tests.ImplementationFirstRpcBuildTests.SingleProject_ActualWorkspaceLoadsUnbuiltReferencesAndRefreshesBothGenerators";
  assert.ok(!fastIds.includes(workspace));
  const packageBuild = "Sunder.Package.Build.Tests.PrimaryPackageIntegrationTests.PrimaryProject_ComposesBuildPacksPrebuiltLeavesAndCleansSafely";
  assert.ok(!fastIds.includes(packageBuild));
  const full = select("--project", "Sunder.Package.Build.Tests");
  assert.equal(full.status, 0, full.stderr);
  const fullIds = JSON.parse(full.stdout).declarations;
  assert.ok(fullIds.includes(workspace));
  assert.ok(fullIds.includes(packageBuild));
  assert.ok(fastIds.every((id) => fullIds.includes(id)));
  const unknown = select("--project", "does-not-exist");
  assert.notEqual(unknown.status, 0);
  assert.match(unknown.stderr, /Unknown .NET test project/u);
});

test("compact Core policy deterministically partitions every test project into owned lanes", async () => {
  const first = loadCoreTestModel();
  const second = loadCoreTestModel();
  assert.deepEqual(first, second);
  assert.ok(first.declarations.length > 0);
  assert.equal(first.declarations.every((item) => first.policy.primaryLanes.includes(item.primaryLane)), true);
  assert.equal(first.policy.primaryLanes.every((lane) =>
    first.declarations.some((item) => item.primaryLane === lane)), true);

  const policyPath = resolve(repositoryRoot, "eng/test-oracles/core-test-policy.json");
  assert.ok((await stat(policyPath)).size < 16 * 1024);

  const mutation = structuredClone(first.declarations);
  mutation[0].primaryLane = mutation[0].primaryLane === "node" ? "dotnet-linux" : "node";
  assert.throws(
    () => validateProjectLaneOwnership(mutation, first.policy),
    /project\/lane ownership drift/u,
  );
});

test("runtime model retains theories, dynamic Node declarations, platform ownership, and release fixtures", () => {
  const { declarations } = loadCoreTestModel();
  assert.ok(declarations.some((item) => item.id
    === "Sunder.Package.Format.Tests.SemanticVersionTests.TryParse_RejectsMalformedVersions"));
  assert.ok(declarations.some((item) => item.framework === "node" && item.dynamicTemplate));
  assert.ok(declarations.filter((item) => item.framework === "shell").length >= 5);

  const publicPackageContract = declarations.find((item) => item.id
    === "Sunder.Package.Build.Tests.CorePublicPackageContractTests.EveryPublicNuget_HasEvaluatedIdentityRoleClosureAndDeterministicArtifact");
  assert.equal(publicPackageContract.primaryLane, "host-platform");
  const unix = declarations.find((item) => item.id
    === "Sunder.App.Tests.RuntimePersistentLauncherTests.HostServiceCommandRunner_ReturnsBoundedSanitizedOutputAndExitCode");
  assert.deepEqual(unix.operatingSystems, ["Linux", "MacOS"]);
  const live = declarations.find((item) => item.id
    === "Sunder.Runtime.Host.Tests.LivePlatformRegistryRuntimeTests.RealCatalogPolicyAdmitsOfflineStackAndRevocationSurvivesRestart");
  assert.ok(live.externalPrerequisites.some((item) => item.name === "SUNDER_LIVE_PLATFORM_INPUT" && item.pathKind === "file"));
  assert.ok(live.externalPrerequisites.some((item) => item.name === "SUNDER_LIVE_PLATFORM_PHASE" && item.kind === "environment"));
});

test("characterization ledger rejects missing evidence and overstated gap coverage", async () => {
  const { declarations } = loadCoreTestModel();
  const declarationIds = new Set(declarations
    .filter((item) => item.framework === "dotnet")
    .map((item) => item.id));
  const characterizationPath = resolve(
    repositoryRoot,
    "eng/test-oracles/core-large-object-characterization.json",
  );
  const missingEvidence = JSON.parse(await readFile(characterizationPath, "utf8"));
  missingEvidence.targets[0].requiredBehaviors[0].evidence[0] = "seeded.missing.Test";
  assert.throws(
    () => validateCharacterization(missingEvidence, declarationIds),
    /seeded\.missing\.Test/u,
  );

  const missingCategory = JSON.parse(await readFile(characterizationPath, "utf8"));
  delete missingCategory.targets[0].requiredBehaviors[0].category;
  assert.throws(
    () => validateCharacterization(missingCategory, declarationIds),
    /behavior category/u,
  );

  const overstated = JSON.parse(await readFile(characterizationPath, "utf8"));
  const bootstrap = overstated.targets.find((target) => target.type === "AppWebBridge")
    .requiredBehaviors.find((behavior) => behavior.id === "javascript-bootstrap-parity");
  bootstrap.status = "covered";
  bootstrap.gap = "Seeded mutation must not coexist with covered evidence.";
  assert.throws(
    () => validateCharacterization(overstated, declarationIds),
    /covered but also declares an uncovered gap/u,
  );
});

test("selected lane prerequisite gate rejects an absent required artifact", () => {
  const tests = [{
    id: "fixture.release.test",
    externalPrerequisites: [{
      kind: "environment",
      name: "REQUIRE_FIXTURE",
      expectedValue: "1",
      requiredWhenSelected: true,
    }, {
      kind: "environmentPath",
      name: "REQUIRED_FIXTURE_PATH",
      pathKind: "file",
      requiredWhenSelected: true,
    }],
  }];
  const missing = missingPrerequisites(tests, { environment: {} });
  assert.equal(missing.length, 2);
  assert.match(missing[0].reason, /must equal '1' exactly/u);
  assert.deepEqual(missing[1].tests, ["fixture.release.test"]);
});

test("selected lane prerequisite gate requires exact selection and every typed input", () => {
  const tests = [{
    id: "fixture.release.test",
    externalPrerequisites: [
      { kind: "environment", name: "REQUIRE_FIXTURE", expectedValue: "1", requiredWhenSelected: true },
      { kind: "environmentPath", name: "FIXTURE_FILE", pathKind: "file", requiredWhenSelected: true },
      { kind: "environmentPath", name: "FIXTURE_DIR", pathKind: "directory", requiredWhenSelected: true },
      { kind: "executable", name: "fixture-tool", requiredWhenSelected: true },
    ],
  }, {
    id: "fixture.second.test",
    externalPrerequisites: [
      { kind: "executable", name: "fixture-tool", requiredWhenSelected: true },
    ],
  }];
  let executableProbeCount = 0;
  const probes = {
    fileProbe: (path) => path === "/fixture/file",
    directoryProbe: (path) => path === "/fixture/directory",
    executableProbe: () => {
      executableProbeCount++;
      return { available: true };
    },
  };
  assert.equal(missingPrerequisites(tests, {
    ...probes,
    environment: { REQUIRE_FIXTURE: "true", FIXTURE_FILE: "/fixture/file", FIXTURE_DIR: "/fixture/directory" },
  }).length, 1);
  assert.equal(executableProbeCount, 1);
  executableProbeCount = 0;
  assert.equal(missingPrerequisites(tests, {
    ...probes,
    environment: { REQUIRE_FIXTURE: "1", FIXTURE_FILE: "/fixture/file", FIXTURE_DIR: "/fixture/directory" },
  }).length, 0);
  assert.equal(executableProbeCount, 1);
});

test("candidate-safe workflows retain lane gates, runtime reports, and compact evidence", async () => {
  const workflowPath = resolve(repositoryRoot, ".github/workflows/sunder-test-oracles.yml");
  const workflow = await readFile(workflowPath, "utf8");
  assert.doesNotMatch(workflowPath, /(?:ci|release|validation)\.yml$/u);
  assert.match(workflow, /workflow_dispatch:/u);
  assert.match(workflow, /schedule:/u);
  assert.doesNotMatch(workflow, /pull_request:|push:/u);
  assert.match(workflow, /Sunder\.Core\.TestSupport\.Tests\/Sunder\.Core\.TestSupport\.Tests\.csproj/u);
  assert.match(workflow, /check-core-test-policy\.mjs/u);
  assert.match(workflow, /run-core-test-lane\.mjs/u);
  assert.match(workflow, /run-node-test-matrix-with-results\.mjs/u);
  assert.match(workflow, /--allow-partial[\s\S]*--require-test/u);
  assert.match(workflow, /if: always\(\)/u);

  const sdk = await readFile(resolve(repositoryRoot, ".github/workflows/sunder-sdk-ci.yml"), "utf8");
  const host = await readFile(resolve(repositoryRoot, ".github/workflows/sunder-host-validation.yml"), "utf8");
  const node = await readFile(resolve(repositoryRoot, ".github/workflows/sunder-node-ci.yml"), "utf8");
  const app = await readFile(resolve(repositoryRoot, ".github/workflows/sunder-app-packaging-ci.yml"), "utf8");
  assert.match(sdk, /--primary-lane dotnet-linux/u);
  assert.match(host, /--primary-lane host-platform/u);
  assert.match(host, /ubuntu-24\.04[\s\S]*windows-2022[\s\S]*macos-14/u);
  assert.match(node, /--primary-lane node/u);
  assert.doesNotMatch(node, /run: npm test/u);
  assert.match(node, /template-lifecycle:[\s\S]*npm --prefix "\$sample" run package/u);
  assert.match(node, /run contracts:accept[\s\S]*run contracts:check[\s\S]*run typecheck[\s\S]*run contracts:freeze[\s\S]*run package/u);
  assert.match(node, /SUNDER_REQUIRE_NODE_MODULE_INTEROP: "1"/u);
  assert.match(node, /SUNDER_REQUIRE_NODE_TEMPLATE_HOST_SMOKE: "1"/u);
  assert.match(node, /GeneratedNodeTemplate_ActivatesThroughSignedRuntimeHost/u);
  assert.match(node, /sunder-node-package-ownership-evidence/u);
  assert.match(sdk, /SUNDER_REQUIRE_DOTNET_TEMPLATE_HOST_SMOKE: "1"/u);
  assert.match(sdk, /GeneratedDotNetTemplate_ActivatesThroughSignedRuntimeHost/u);
  assert.match(app, /--test Sunder\.App\.Tests\.BundledBuildOutputTests\.AppBuildContainsSupervisorRuntimeAndCliExecutables/u);
  for (const ciWorkflow of [sdk, host, node, app, workflow]) {
    assert.match(ciWorkflow, /--results-directory/u);
    assert.match(ciWorkflow, /if: always\(\)[\s\S]*upload-artifact/u);
  }
  for (const dotnetWorkflow of [sdk, node, app, workflow]) assert.match(dotnetWorkflow, /-m:1/u);

  const dotnetRunner = await readFile(
    resolve(repositoryRoot, "scripts/test-oracles/run-dotnet-tests-with-results.mjs"),
    "utf8",
  );
  assert.match(dotnetRunner, /"-m:1"/u);
  const nodeRunner = await readFile(
    resolve(repositoryRoot, "scripts/test-oracles/run-node-tests-with-results.mjs"),
    "utf8",
  );
  assert.match(nodeRunner, /--test-reporter=junit/u);
  assert.match(nodeRunner, /--test-reporter-destination=\$\{outputPath\}/u);
});
