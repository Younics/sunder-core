import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  discoverDotnetTests,
  loadCoreTestModel,
  validateCharacterization,
  validateProjectLaneOwnership,
} from "./core-test-policy.mjs";
import { missingPrerequisites } from "./test-lane-prerequisites.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const fixtureSource = "languages/csharp/dotnet/tests/Inventory.Tests/Declarations.cs";
const policy = JSON.parse(await readFile(resolve(repositoryRoot, "eng/test-oracles/core-test-policy.json"), "utf8"));

test("C# declaration inventory ignores comment and literal fixtures without losing real tests after them", () => {
  const content = String.raw`
    // namespace Wrong.Line; public class Comment { [Fact] public void Fake() {} }
    /* namespace Wrong.Block; public class Comment { [Theory] public void Fake() {} } */
    namespace Example.Tests;
    public sealed partial class RealTests
    {
      const string Normal = "namespace Wrong.Normal; public class Normal { [Fact] public void Fake() {} } \\\"";
      const char Quote = '\'';
      const char Brace = '}';
      const string Verbatim = @"quoted ""value""
        namespace Wrong.Verbatim;
        public sealed class Fixture { [Fact] public void Fake() {} }
        // not a real comment
      ";
      const string Raw = """
        namespace Wrong.Raw;
        public sealed class Fixture { [Theory] public void Fake() {} }
        /* not a real comment
        #if DEBUG
      """;
      const string LongerRaw = """""
        """" namespace Wrong.Delimiter;
        public class Fixture { [Fact] public void Fake() {} }
      """"";
      string Interpolated = $"{Format("class Fixture { }", new { Value = "namespace Wrong.Interpolation;" })} {{escaped}}";
      string InterpolatedVerbatim = @$"{Format(@"""quoted""")} public class Fixture {}";
      string InterpolatedRaw = $$""""
        {{Format("value")}} { """ quotes and braces } namespace Wrong.RawInterpolation;
        public class Fixture { [Fact] public void Fake() {} }
      """";
      string NestedRawInterpolation = $$"""
        {{{Format("""nested raw literal""")}}}
        public class Fixture { [Fact] public void Fake() {} }
      """;
      // [Fact] public void CommentedOut() {}
      /* [Fact] public void AlsoCommentedOut() {} */
      [InlineData("Fact")]
      public void NotATest() {}
      [Fact(Skip = "UnixFact ExternalArtifactFact")]
      public void AfterFixtures() { }
      [Theory, InlineData("macos", "[WindowsFact]", new[] { "[Trait(\"Category\",\"Release\")]" })]
      public async Task AfterDataFixture() { }
    }
    public class Fixture { [Fact] public void RealFixtureClass() { } }
  `;
  const declarations = discoverDotnetTests(fixtureSource, content, policy);
  assert.deepEqual(declarations.map((item) => item.id), [
    "Example.Tests.RealTests.AfterFixtures",
    "Example.Tests.RealTests.AfterDataFixture",
    "Example.Tests.Fixture.RealFixtureClass",
  ]);
  assert.ok(declarations.every((item) => item.primaryLane === "dotnet-linux"));
  assert.ok(declarations.every((item) => item.operatingSystems.length === 3 && item.externalEnvironment === undefined));
});

test("C# inventory follows namespace and nested partial type scopes while retaining conditional and release metadata", () => {
  const content = `
    namespace Example.Outer {
      namespace Inner {
        public partial class Cases {
          [global::Xunit.FactAttribute] public void BeforeNested() { if (true) { } }
          public class Nested {
            [UnixTheory, InlineData(1)] public Task Conditional() => Task.CompletedTask;
          }
          [Fact, Trait /* gap */ ("Capability", "Process")]
          public void AfterNested() { }
        }
        public partial class Cases {
          [ExternalArtifactTheory("SUNDER_REQUIRE_NODE_MODULE_INTEROP", "SUNDER_NODE_MODULE_PACKAGE_PATH", "SUNDER_NODE_MODULE_LEAF_PATH")]
          [InlineData("case")]
          public Task CrossLanguageRelease() => Task.CompletedTask;
          [Fact, Xunit.TraitAttribute("Category", "Release")]
          public void ReleaseTrait() { }
          #if WINDOWS
          [WindowsFact] public void ConditionalWindows() { }
          #else
          [LinuxFact] public void ConditionalLinux() { }
          #endif
        }
      }
    }
    namespace Other { public class Cases { [Fact] public void SiblingNamespace() { } } }
    public class GlobalTests { [Fact] public ValueTask GlobalNamespace() => default; }
  `;
  const declarations = discoverDotnetTests(fixtureSource, content, policy);
  assert.deepEqual(declarations.map((item) => [item.id, item.primaryLane]), [
    ["Example.Outer.Inner.Cases.BeforeNested", "dotnet-linux"],
    ["Example.Outer.Inner.Cases+Nested.Conditional", "host-platform"],
    ["Example.Outer.Inner.Cases.AfterNested", "host-platform"],
    ["Example.Outer.Inner.Cases.CrossLanguageRelease", "release-artifact"],
    ["Example.Outer.Inner.Cases.ReleaseTrait", "release-artifact"],
    ["Example.Outer.Inner.Cases.ConditionalWindows", "host-platform"],
    ["Example.Outer.Inner.Cases.ConditionalLinux", "host-platform"],
    ["Other.Cases.SiblingNamespace", "dotnet-linux"],
    ["GlobalTests.GlobalNamespace", "dotnet-linux"],
  ]);
  assert.deepEqual(declarations[1].operatingSystems, ["Linux", "MacOS"]);
  assert.deepEqual(declarations[5].operatingSystems, ["Windows"]);
  assert.deepEqual(declarations[6].operatingSystems, ["Linux"]);
  assert.deepEqual(declarations[3].externalPrerequisites, [
    { kind: "executable", name: "dotnet", requiredWhenSelected: true },
    { kind: "executable", name: "node", minimumMajorVersion: 24, requiredWhenSelected: true },
    { kind: "environment", name: "SUNDER_REQUIRE_NODE_MODULE_INTEROP", expectedValue: "1", requiredWhenSelected: true },
    { kind: "environmentPath", name: "SUNDER_NODE_MODULE_PACKAGE_PATH", pathKind: "file", requiredWhenSelected: true },
    { kind: "environmentPath", name: "SUNDER_NODE_MODULE_LEAF_PATH", pathKind: "directory", requiredWhenSelected: true },
  ]);
  const secondPart = discoverDotnetTests(fixtureSource.replace(".cs", ".Part.cs"), `
    namespace Example.Outer.Inner;
    public partial class Cases { [Fact] public void InAnotherFile() { } }
  `, policy);
  assert.equal(secondPart[0].id, "Example.Outer.Inner.Cases.InAnotherFile");
});

test("project-scoped test selection excludes process cases from the fast lane and keeps them in full validation", async () => {
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
  assert.deepEqual(new Set(fullIds), projectIds);
  assert.ok(fullIds.includes("Sunder.Package.Build.Tests.SunderRpcCSharpImportedProviderTests.Compile_MultipleImportedServices_ReusesDescriptorAndVerifiesGeneratedRegistration"));
  assert.ok(fullIds.includes(workspace));
  assert.ok(fullIds.includes(packageBuild));
  assert.ok(fastIds.every((id) => fullIds.includes(id)));
  const unknown = select("--project", "does-not-exist");
  assert.notEqual(unknown.status, 0);
  assert.match(unknown.stderr, /Unknown .NET test project/u);
  const empty = select("--project", "Sunder.Package.Build.Tests", "--primary-lane", "node");
  assert.notEqual(empty.status, 0);
  assert.match(empty.stderr, /has no runnable .NET tests/u);

  const directory = await mkdtemp(resolve(tmpdir(), "sunder-reporter-list-"));
  try {
    const sentinel = resolve(directory, "existing.trx");
    await writeFile(sentinel, "existing report");
    const preview = select("--project", "Sunder.Package.Build.Tests", "--results-directory", directory);
    assert.equal(preview.status, 0, preview.stderr);
    assert.equal(await readFile(sentinel, "utf8"), "existing report");
    assert.deepEqual(JSON.parse(preview.stdout).declarations, fullIds);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("lane entry retains the renamed process suspension regression and fails closed on missing release inputs", () => {
  const model = loadCoreTestModel();
  const suspension = model.policy.expectedSkips.find((item) => item.id === "node-process-suspension");
  assert.ok(suspension);
  assert.equal(suspension.name, "a suspended CLI publisher reads the new source after acquiring the output lease");
  const declaration = model.declarations.find((item) => item.framework === "node"
    && item.source === suspension.source && item.name === suspension.name);
  assert.equal(declaration?.primaryLane, "node");
  assert.deepEqual(suspension.skipOn, ["Windows"]);
  const stale = structuredClone(model.policy);
  stale.expectedSkips.find((item) => item.id === suspension.id).name = "missing suspension regression";
  assert.throws(() => loadCoreTestModel(repositoryRoot, stale), /node-process-suspension.*missing Node declaration/u);

  const runner = resolve(repositoryRoot, "scripts/test-oracles/run-core-test-lane.mjs");
  const env = { ...process.env, PATH: `${dirname(process.execPath)}${process.platform === "win32" ? ";" : ":"}${process.env.PATH ?? ""}` };
  const node = spawnSync(process.execPath, [runner, "--primary-lane", "node", "--list"], { cwd: repositoryRoot, env, encoding: "utf8" });
  assert.equal(node.status, 0, node.stderr);
  assert.match(node.stdout, /^READY primary-lane=node;/u);
  const selected = JSON.parse(node.stdout.slice(node.stdout.indexOf("\n") + 1));
  assert.ok(selected.runnable.some((item) => item.id === declaration.id));
  assert.ok(selected.runnable.every((item) => item.framework === "node"));

  const release = model.declarations.find((item) => item.externalEnvironment === "SUNDER_REQUIRE_NODE_MODULE_INTEROP");
  assert.ok(release);
  for (const input of release.externalPrerequisites.filter((item) => item.kind.startsWith("environment"))) delete env[input.name];
  const gate = spawnSync(process.execPath, [runner, "--test", release.id, "--list"], { cwd: repositoryRoot, env, encoding: "utf8" });
  assert.equal(gate.status, 2, gate.stderr);
  assert.match(gate.stderr, /SUNDER_REQUIRE_NODE_MODULE_INTEROP must equal '1' exactly/u);
  assert.match(gate.stderr, /SUNDER_NODE_MODULE_PACKAGE_PATH/u);
  assert.doesNotMatch(gate.stdout, /^READY/mu);
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
  for (const name of ["preflight-npm-trusted-publishing.fixture.sh", "verify-nuget-package.fixture.sh",
    "verify-nuget-source.fixture.sh", "verify-release-tag.fixture.sh"]) {
    assert.ok(declarations.some((item) => item.framework === "shell" && item.name === name));
  }

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
  const workflowPath = resolve(repositoryRoot, ".github/workflows/sunder-ci.yml");
  const workflow = await readFile(workflowPath, "utf8");
  assert.match(workflow, /workflow_dispatch:/u);
  assert.match(workflow, /schedule:/u);
  assert.match(workflow, /pull_request:|push:/u);
  assert.match(workflow, /complete-audit:/u);
  assert.match(workflow, /check-core-test-policy\.mjs/u);
  assert.match(workflow, /run-core-test-lane\.mjs/u);
  assert.match(workflow, /run-node-test-matrix-with-results\.mjs/u);
  assert.match(workflow, /--allow-partial[\s\S]*--require-test/u);
  assert.match(workflow, /if: always\(\)/u);

  const host = await readFile(resolve(repositoryRoot, ".github/workflows/sunder-host-validation.yml"), "utf8");
  assert.match(workflow, /--primary-lane dotnet-linux/u);
  assert.match(host, /--primary-lane host-platform/u);
  assert.match(host, /ubuntu-24\.04[\s\S]*windows-2022[\s\S]*macos-15/u);
  assert.match(workflow, /--primary-lane node/u);
  assert.match(workflow, /run contracts:check[\s\S]*run typecheck[\s\S]*run package/u);
  assert.doesNotMatch(workflow, /run contracts:accept|run contracts:freeze/u);
  assert.match(workflow, /SUNDER_REQUIRE_NODE_MODULE_INTEROP: "1"/u);
  assert.match(workflow, /SUNDER_REQUIRE_NODE_TEMPLATE_HOST_SMOKE: "1"/u);
  assert.match(workflow, /GeneratedNodeTemplate_ActivatesThroughOfficialRuntimeHost/u);
  assert.match(workflow, /SUNDER_REQUIRE_DOTNET_TEMPLATE_HOST_SMOKE: "1"/u);
  assert.match(workflow, /GeneratedDotNetTemplate_ActivatesThroughOfficialRuntimeHost/u);
  assert.match(host, /--primary-lane app-packaging/u);
  for (const ciWorkflow of [host, workflow]) {
    assert.match(ciWorkflow, /--results-directory/u);
    assert.match(ciWorkflow, /if: always\(\)[\s\S]*upload-artifact/u);
  }
  for (const dotnetWorkflow of [host, workflow]) assert.doesNotMatch(dotnetWorkflow, /-m:1/u);

  const dotnetRunner = await readFile(
    resolve(repositoryRoot, "scripts/test-oracles/run-dotnet-tests-with-results.mjs"),
    "utf8",
  );
  assert.doesNotMatch(dotnetRunner, /"-m:1"/u);
  const nodeRunner = await readFile(
    resolve(repositoryRoot, "scripts/test-oracles/run-node-tests-with-results.mjs"),
    "utf8",
  );
  assert.match(nodeRunner, /--test-reporter=junit/u);
  assert.match(nodeRunner, /--test-reporter-destination=\$\{outputPath\}/u);
});
