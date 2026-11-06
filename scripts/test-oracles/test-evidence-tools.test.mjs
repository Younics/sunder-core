import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdir, mkdtemp, readFile, readdir, rm, stat, symlink, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import {
  discoverDotnetTests,
  loadCoreTestModel,
  scopeModel,
  validateLaneReachability,
  validateProjectLaneOwnership,
  validateWorkflowTestSelections,
} from "./core-test-policy.mjs";
import { missingPrerequisites } from "./test-lane-prerequisites.mjs";
import { dotnetTestArguments } from "./dotnet-test-command.mjs";
import { selectScenario } from "./ci-scenarios.mjs";
import { createNodeTestWorkspace } from "./node-test-workspace.mjs";
import { nativePartitionNames, selectNativePartition } from "./native-test-partitions.mjs";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const fixtureSource = "languages/csharp/dotnet/tests/Inventory.Tests/Declarations.cs";
const policy = JSON.parse(await readFile(resolve(repositoryRoot, "eng/test-oracles/core-test-policy.json"), "utf8"));

test("OS eligibility follows conditional attributes and every lane has a compatible runner", () => {
  const tests = discoverDotnetTests(fixtureSource.replace("Declarations", "MacOsDeclarations"), `
    namespace Example.Tests;
    public class MacOsCases {
      [Fact] public void MacOsFake() { }
      [Theory, InlineData(1)] public void MacOsContent(int value) { }
      [MacOsFact] public void NativeFilesystem() { }
    }
  `, policy);
  assert.ok(tests.slice(0, 2).every(test => test.primaryLane === "dotnet-linux" && test.operatingSystems.includes("Linux")));
  assert.equal(tests[2].primaryLane, "host-platform");
  assert.deepEqual(tests[2].operatingSystems, ["MacOS"]);
  validateLaneReachability(tests);
  assert.throws(() => validateLaneReachability([{ ...tests[2], primaryLane: "dotnet-linux" }]), /unreachable/u);
  const model = loadCoreTestModel();
  for (const scope of Object.keys(policy.scopes)) validateLaneReachability(scopeModel(model, scope).declarations);
});

test("native method partitions cover each scoped OS exactly once and distribute partial classes", () => {
  const all = loadCoreTestModel();
  for (const scope of ["all", "sdk", "dotnet", "npm", "app", "repository", "host"]) {
    const model = scopeModel(all, scope);
    const names = nativePartitionNames(model);
    assert.equal(names.length, ["all", "sdk", "dotnet"].includes(scope) ? 4 : 1);
    for (const os of ["Windows", "Linux", "MacOS"]) {
      const expected = model.declarations.filter(test => test.framework === "dotnet"
        && test.primaryLane === "host-platform" && test.operatingSystems.includes(os)).map(test => test.id).sort();
      const partitions = names.map(name => selectNativePartition(model, name, os));
      const actual = partitions.flat().map(test => test.id);
      assert.equal(new Set(actual).size, actual.length, `${scope}/${os}: overlapping partitions`);
      assert.deepEqual(actual.sort(), expected, `${scope}/${os}: omitted declarations`);
      for (const name of names.slice(1)) {
        assert.ok(selectNativePartition(model, name, os).some(test => test.id.includes("ImplementationFirstRpcBuildTests.")));
        assert.deepEqual(selectNativePartition({ ...model, declarations: [...model.declarations].reverse() }, name, os).map(test => test.id).sort(),
          selectNativePartition(model, name, os).map(test => test.id).sort());
      }
    }
    assert.throws(() => selectNativePartition(model, "missing", "Linux"), /Unknown native partition/u);
  }
  for (const os of ["Windows", "Linux", "MacOS"]) {
    for (const partition of nativePartitionNames(all)) {
      const split = ["dotnet", "host", "app"].flatMap(scope => {
        const component = scopeModel(all, scope);
        return nativePartitionNames(component).includes(partition) ? selectNativePartition(component, partition, os) : [];
      }).map(test => test.id);
      assert.equal(new Set(split).size, split.length, "Component matrices must not duplicate native tests");
      assert.deepEqual(split.sort(), selectNativePartition(all, partition, os).map(test => test.id).sort());
    }
  }
  const runner = resolve(repositoryRoot, "scripts/test-oracles/run-dotnet-tests-with-results.mjs");
  const preview = spawnSync(process.execPath, [runner, "--scope", "sdk", "--native-partition", "package-build-1", "--list"], { encoding: "utf8" });
  assert.equal(preview.status, 0, preview.stderr);
  const selection = JSON.parse(preview.stdout);
  assert.equal(selection.nativePartition, "package-build-1");
  assert.deepEqual(selection.declarations, selectNativePartition(scopeModel(all, "sdk"), "package-build-1", selection.operatingSystem).map(test => test.id));
  assert.deepEqual(selection.projects[0].filter.split("|").map(value => value.replace(/^FullyQualifiedName=/u, "")).sort(),
    [...selection.declarations].sort());
  const mixed = spawnSync(process.execPath, [runner, "--native-partition", "main", "--primary-lane", "host-platform", "--list"], { encoding: "utf8" });
  assert.notEqual(mixed.status, 0);
  assert.match(mixed.stderr, /Select a native partition/u);
});

test("CI scenarios discover added cases and reject missing owners without workflow test lists", () => {
  const { declarations } = loadCoreTestModel();
  const official = selectScenario("official", declarations, "Linux");
  assert.ok(official.tests.some(test => test.id.endsWith("WorkerSdkPackagedExecutable_ExactArchiveInstallsThroughRuntimeHostAndRecordsReleaseEvidence")));
  const added = { ...official.tests[0], id: "Sunder.Runtime.Host.Tests.CurrentRidDotNetRuntimeHostIntegrationTests.FutureLifecycleRegression" };
  assert.ok(selectScenario("official", [...declarations, added], "Linux").tests.includes(added));
  assert.throws(() => selectScenario("official", declarations.filter(test => !test.id.includes("CurrentRidNodeRuntimeHostIntegrationTests")), "Linux"), /no declared tests/u);
  assert.throws(() => selectScenario("missing", declarations), /Unknown/u);
  const interop = selectScenario("app-interop", declarations, "Linux");
  for (const name of ["ExecutesExactNativeContractAcrossNavigation", "RejectsSeededNonceOwnershipMutation"]) {
    assert.ok(interop.tests.some(test => test.id.endsWith(name)));
  }
});

test("component scopes partition the full inventory and cannot hide missing owned lanes", () => {
  const all = loadCoreTestModel();
  const sdk = loadCoreTestModel(repositoryRoot, undefined, "sdk");
  const repository = loadCoreTestModel(repositoryRoot, undefined, "repository");
  const app = loadCoreTestModel(repositoryRoot, undefined, "app");
  const leaves = all.policy.scopes.all.map(owner => scopeModel(all, owner === "ci" ? "repository" : owner));
  const ids = leaves.flatMap(model => model.declarations.map(test => test.id));
  assert.equal(new Set(ids).size, ids.length, "Each declaration must have exactly one component owner");
  assert.deepEqual(new Set(ids), new Set(all.declarations.map(test => test.id)));
  assert.deepEqual(new Set([...sdk.declarations, ...app.declarations].map(test => test.id)), new Set(all.declarations.map(test => test.id)));
  assert.ok(repository.declarations.every(test => test.component === "ci"));
  assert.ok(sdk.declarations.every(test => !test.project.startsWith("Sunder.App.")));
  for (const source of ["public-package-contracts", "policy-contracts", "toolchains", "checkout-bytes"]) {
    assert.ok(sdk.declarations.some(test => test.source === `scripts/test-oracles/${source}.test.mjs`),
      `${source} must gate SDK publication`);
  }
  for (const name of ["RuntimeHost_IsWorkerOnly", "App_RetainsAppModuleLoading"]) {
    assert.equal(sdk.declarations.some(test => test.name === name), name.startsWith("RuntimeHost"));
    assert.equal(app.declarations.some(test => test.name === name), name.startsWith("App_"));
  }
  assert.ok(selectScenario("interop", sdk.declarations).tests.length > 0);
  assert.throws(() => selectScenario("app-interop", sdk.declarations), /no declared tests/u);
  assert.ok(selectScenario("app-interop", app.declarations).tests.length > 0);
  const missing = { ...all, declarations: all.declarations.filter(test =>
    test.project !== "Sunder.Package.Build.Tests" || test.primaryLane !== "host-platform") };
  const narrowed = scopeModel(missing, "sdk");
  assert.throws(() => validateProjectLaneOwnership(narrowed.declarations, narrowed.policy), /ownership drift/u);
  const wrongScope = structuredClone(all.policy);
  wrongScope.scopes.sdk.push("unowned");
  assert.throws(() => loadCoreTestModel(repositoryRoot, wrongScope, "sdk"), /scope ownership/u);
  for (const script of ["run-core-test-lane", "summarize-core-test-results", "run-dotnet-tests-with-results"]) {
    for (const args of [["--scope"], ["--scope", "--list"], ["--scope", "sdk", "--scope", "app"]]) {
      const result = spawnSync(process.execPath, [resolve(repositoryRoot, `scripts/test-oracles/${script}.mjs`), ...args], { encoding: "utf8" });
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, /--scope must be specified once with one value/u);
    }
  }
});

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
          [Fact, Trait("Category", "CurrentRidExecutionRuntime")]
          public void OfficialRuntime() { }
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
    ["Example.Outer.Inner.Cases.OfficialRuntime", "official-runtime"],
    ["Example.Outer.Inner.Cases.ConditionalWindows", "host-platform"],
    ["Example.Outer.Inner.Cases.ConditionalLinux", "host-platform"],
    ["Other.Cases.SiblingNamespace", "dotnet-linux"],
    ["GlobalTests.GlobalNamespace", "dotnet-linux"],
  ]);
  assert.deepEqual(declarations[1].operatingSystems, ["Linux", "MacOS"]);
  assert.deepEqual(declarations[6].operatingSystems, ["Windows"]);
  assert.deepEqual(declarations[7].operatingSystems, ["Linux"]);
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

test(".NET reporter scopes integration budgets and crash diagnostics without relaxing the fast lane", () => {
  const runner = resolve(repositoryRoot, "scripts/test-oracles/run-dotnet-tests-with-results.mjs");
  for (const [project, lane, hang, minutes] of [
    ["Sunder.Package.Build.Tests", "host-platform", "20m", 90],
    ["Sunder.Package.Build.Tests", undefined, "20m", 90],
    ["Sunder.Package.Build.Tests", "dotnet-linux", "5m", 60],
    ["Sunder.Package.Templates.Tests", undefined, "20m", 60],
    ["Sunder.Package.Templates.Tests", "release-artifact", "10m", 60],
    ["Sunder.ExecutionRuntime.Build.Tests", "host-platform", "15m", 60],
    ["Sunder.ExecutionRuntime.Build.Tests", "dotnet-linux", "5m", 60],
    ["Sunder.Sdk.PublicApi.Tests", "dotnet-linux", "5m", 60],
  ]) {
    const result = spawnSync(process.execPath, [runner, "--list", "--project", project,
      ...(lane === undefined ? [] : ["--primary-lane", lane])], { cwd: repositoryRoot, encoding: "utf8" });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const budget = JSON.parse(result.stdout).projects[0];
    assert.equal(budget.hangTimeout, hang);
    assert.equal(budget.timeoutMilliseconds, minutes * 60_000);
    const options = { projectPath: `${project}.csproj`, projectName: project,
      outputDirectory: "results", logStem: "results/diagnostics/project", build: false, budget, extraArguments: [] };
    const normal = dotnetTestArguments({ ...options, diagnostics: false });
    assert.equal(normal.includes("--diag"), false);
    assert.equal(normal.includes("--blame-crash"), false);
    const args = dotnetTestArguments({ ...options, diagnostics: true });
    assert.equal(args[args.indexOf("--blame-hang-timeout") + 1], hang);
    assert.equal(args.includes("--blame-crash"), project === "Sunder.Package.Build.Tests");
    assert.equal(args.includes("--diag"), budget.diagnostics);
    assert.equal(args.includes("console;verbosity=detailed"), budget.diagnostics);
    assert.ok(args.includes("--no-build"));
  }
  const override = spawnSync(process.execPath, [runner, "--list", "--project", "Sunder.Package.Build.Tests",
    "--timeout-ms", "1234"], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(override.status, 0, override.stderr);
  assert.equal(JSON.parse(override.stdout).projects[0].timeoutMilliseconds, 1234);
  for (const invalid of ["0", "2147483648"]) {
    const rejected = spawnSync(process.execPath, [runner, "--list", "--project", "Sunder.Package.Build.Tests",
      "--timeout-ms", invalid], { cwd: repositoryRoot, encoding: "utf8" });
    assert.notEqual(rejected.status, 0);
    assert.match(rejected.stderr, /--timeout-ms must be a positive integer/u);
  }
});

test(".NET reporter refuses to start the next project when process cleanup is unconfirmed", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-reporter-cleanup-failure-"));
  try {
    const log = resolve(root, "started.jsonl");
    const hook = resolve(root, "unclean process #100%.mjs");
    const preload = `
      import childProcess from 'node:child_process';
      import { syncBuiltinESMExports } from 'node:module';
      import { EventEmitter } from 'node:events';
      import { PassThrough } from 'node:stream';
      import { appendFileSync, writeFileSync } from 'node:fs';
      childProcess.spawn = (command, args) => {
        appendFileSync(${JSON.stringify(log)}, JSON.stringify({ command, args }) + '\\n');
        const child = new EventEmitter();
        child.pid = 2147483646;
        child.exitCode = 0; child.signalCode = null;
        child.stdout = new PassThrough(); child.stderr = new PassThrough();
        if (process.platform === 'win32') {
          writeFileSync(args[args.indexOf('-ResultPath') + 1], JSON.stringify({
            ProcessId: child.pid, Status: 0, Error: 'ECLEANUP',
            Message: 'seeded cleanup failure', CleanupComplete: false,
          }));
        }
        process.nextTick(() => { child.emit('exit', 0, null); child.stdout.end(); child.stderr.end(); child.emit('close', 0, null); });
        return child;
      };
      process.kill = () => { throw Object.assign(new Error('seeded cleanup failure'), { code: 'EPERM' }); };
      syncBuiltinESMExports();
    `;
    const runner = resolve(repositoryRoot, "scripts/test-oracles/run-dotnet-tests-with-results.mjs");
    for (const platform of new Set([process.platform, "win32"])) {
      await writeFile(log, "");
      await writeFile(hook, `${preload}\nObject.defineProperty(process, 'platform', { value: ${JSON.stringify(platform)} });`);
      const result = spawnSync(process.execPath, ["--import", pathToFileURL(hook).href, runner, "--primary-lane", "host-platform",
        "--results-directory", resolve(root, "results")], { cwd: repositoryRoot, encoding: "utf8" });
      assert.notEqual(result.status, 0);
      assert.match(result.stderr, /Stopping before the next project/u);
      assert.match(result.stdout, /seeded cleanup failure/u);
      assert.equal((await readFile(log, "utf8")).trim().split("\n").length, 1);
    }
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("Node reporter keeps failed preflight evidence and removes successful process fixtures", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-preflight-reports-"));
  try {
    const hook = resolve(root, "fail assertions #100%.mjs");
    await writeFile(hook, `import assert from 'node:assert/strict';
      assert.deepEqual = () => { throw new Error('seeded preflight assertion failure'); };`);
    for (const fail of [false, true]) {
      const output = resolve(root, fail ? "failed" : "passed");
      const diagnostics = resolve(output, "fixtures");
      const env = { ...process.env, NODE_OPTIONS: fail ? `--import=${pathToFileURL(hook).href}` : "",
        SUNDER_NODE_TEST_NAME_PATTERN: "^owned command preserves",
        SUNDER_TEST_PROCESS_DIAGNOSTICS_DIRECTORY: diagnostics };
      // This is a separate CLI invocation, not a child in this test runner's IPC protocol.
      delete env.NODE_TEST_CONTEXT;
      const result = spawnSync(process.execPath, [resolve(repositoryRoot, "scripts/test-oracles/run-node-tests-with-results.mjs"),
        "--results-directory", output, "preflight", resolve(repositoryRoot, "scripts/test-oracles/owned-test-process.test.mjs")], {
        cwd: repositoryRoot, encoding: "utf8", timeout: 45_000, env,
      });
      assert.equal(result.status, fail ? 1 : 0, result.stdout + result.stderr);
      const report = await readFile(resolve(output, "preflight.junit.xml"), "utf8");
      assert.match(report, /owned command preserves/u);
      assert.equal(report.includes("seeded preflight assertion failure"), fail);
      const fixtures = await readdir(diagnostics);
      assert.equal(fixtures.length, fail ? 1 : 0);
      if (fail) {
        assert.match(result.stdout + result.stderr, /Failed process fixture retained at/u);
        const receipt = JSON.parse(await readFile(resolve(diagnostics, fixtures[0], "command with spaces.process.json"), "utf8"));
        assert.equal(receipt.status, 7);
        assert.equal(receipt.cleanupComplete, true);
      }
    }
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("lane entry includes the IPC publisher regression on every OS and fails closed on missing release inputs", () => {
  const model = loadCoreTestModel();
  const declaration = model.declarations.find((item) => item.framework === "node"
    && item.name === "a suspended CLI publisher reads the new source after acquiring the output lease");
  assert.equal(declaration?.primaryLane, "node");
  assert.ok(!model.policy.expectedSkips.some(item => item.name === declaration.name));
  const stale = structuredClone(model.policy);
  stale.expectedSkips.find((item) => item.framework === "node").name = "missing regression";
  assert.throws(() => loadCoreTestModel(repositoryRoot, stale), /missing Node declaration/u);

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
  const maker = model.declarations.find((item) => item.externalEnvironment === "SUNDER_REQUIRE_MAKER_STANDALONE");
  assert.equal(maker?.primaryLane, "release-artifact");
  assert.deepEqual(maker.externalPrerequisites.filter((item) => item.kind === "environmentPath").map((item) => [item.name, item.pathKind]),
    [["SUNDER_MAKER_DEV_PACKAGE", "directory"], ["SUNDER_MAKER_IMPORT_CANDIDATE", "file"], ["SUNDER_MAKER_PLATFORM_CATALOG", "file"]]);
  for (const input of maker.externalPrerequisites.filter((item) => item.kind.startsWith("environment"))) delete env[input.name];
  const makerGate = spawnSync(process.execPath, [runner, "--test", maker.id, "--list"], { cwd: repositoryRoot, env, encoding: "utf8" });
  assert.equal(makerGate.status, 2, makerGate.stderr);
  assert.match(makerGate.stderr, /SUNDER_REQUIRE_MAKER_STANDALONE must equal '1' exactly/u);
  assert.match(makerGate.stderr, /SUNDER_MAKER_DEV_PACKAGE/u);
  assert.match(makerGate.stderr, /SUNDER_MAKER_IMPORT_CANDIDATE/u);
  assert.match(makerGate.stderr, /SUNDER_MAKER_PLATFORM_CATALOG/u);
  assert.doesNotMatch(makerGate.stdout, /^READY/mu);
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

test("audited inert C# history is excluded only while its bytes and unconditional Compile Remove remain intact", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-inert-test-source-"));
  const project = "Inventory.Tests";
  const projectRoot = resolve(root, "languages/csharp/dotnet/tests", project);
  const source = `languages/csharp/dotnet/tests/${project}/History.cs`;
  const history = "namespace Archived; public class History { [Fact] public void Frozen() { } }\n";
  const active = "namespace Current; public class Active { [Fact] public void Runs() { } }\n";
  const projectXml = '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Remove="History.cs" /></ItemGroup></Project>';
  const projectPath = resolve(projectRoot, `${project}.csproj`);
  const fixturePolicy = {
    ...structuredClone(policy),
    primaryLanes: ["dotnet-linux"],
    dotnetLaneRules: [],
    nativePartitions: [],
    projectLanes: { [project]: ["dotnet-linux"] },
    expectedSkips: [],
    inertDotnetSources: [{ source, sha256Lf: createHash("sha256").update(history).digest("hex"),
      reason: "Frozen historical oracle, deliberately not compiled or executed." }],
  };
  try {
    await mkdir(projectRoot, { recursive: true });
    await writeFile(resolve(projectRoot, "Active.cs"), active);
    await writeFile(resolve(root, source), history);
    await writeFile(projectPath, projectXml);
    assert.deepEqual(loadCoreTestModel(root, fixturePolicy).declarations.map((item) => item.id), ["Current.Active.Runs"]);
    await writeFile(resolve(root, source), history.replaceAll("\n", "\r\n"));
    assert.deepEqual(loadCoreTestModel(root, fixturePolicy).declarations.map((item) => item.id), ["Current.Active.Runs"]);

    for (const xml of [
      projectXml.replace('<Compile Remove="History.cs" />', ""),
      projectXml.replace("<ItemGroup>", '<ItemGroup Condition="\'$(Configuration)\' == \'Release\'">'),
      projectXml.replace('Remove="History.cs"', 'Remove="History.cs" Condition="\'$(Configuration)\' == \'Release\'"'),
      projectXml.replace('<Compile Remove="History.cs" />', '<!-- <Compile Remove="History.cs" /> -->'),
      projectXml.replace("</ItemGroup>", '<Compile Include="History.cs" /></ItemGroup>'),
      projectXml.replace("</ItemGroup>", '<Compile Include="*.cs" /></ItemGroup>'),
      projectXml.replace('Remove="History.cs"', 'Remove="*.cs"'),
      projectXml.replace("</Project>", '<Import Project="Other.targets" /></Project>'),
    ]) {
      await writeFile(projectPath, xml);
      assert.throws(() => loadCoreTestModel(root, fixturePolicy), /unconditional literal Compile Remove/u, xml);
    }
    await writeFile(projectPath, projectXml);
    await writeFile(resolve(root, source), history + "// changed history\n");
    assert.throws(() => loadCoreTestModel(root, fixturePolicy), /inert.*hash/iu);
    await writeFile(resolve(root, source), history);
    const hiddenActive = structuredClone(fixturePolicy);
    hiddenActive.inertDotnetSources.push({ source: source.replace("History.cs", "Active.cs"),
      sha256Lf: createHash("sha256").update(active).digest("hex"), reason: "Must not hide runnable tests." });
    assert.throws(() => loadCoreTestModel(root, hiddenActive), /unconditional literal Compile Remove/u);
    const missingReason = structuredClone(fixturePolicy);
    missingReason.inertDotnetSources[0].reason = "";
    assert.throws(() => loadCoreTestModel(root, missingReason), /inert.*reason/iu);
    const duplicate = structuredClone(fixturePolicy);
    duplicate.inertDotnetSources.push(duplicate.inertDotnetSources[0]);
    assert.throws(() => loadCoreTestModel(root, duplicate), /inert.*unique/iu);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("current inventory never selects the retained noncompiled Stack migration oracle", () => {
  const model = loadCoreTestModel();
  assert.ok(model.policy.inertDotnetSources.some((item) => item.source.endsWith("/StackContributorRpcMigrationBaselineTests.cs")));
  assert.ok(!model.declarations.some((item) => item.id.startsWith("Sunder.Sdk.PublicApi.Tests.StackContributorRpcMigrationBaselineTests.")));
  assert.ok(model.declarations.some((item) => item.project === "Sunder.Sdk.PublicApi.Tests"));
});

test("runtime model retains theories, dynamic Node declarations, platform ownership, and release fixtures", () => {
  const { declarations } = loadCoreTestModel();
  assert.ok(declarations.some((item) => item.id
    === "Sunder.Package.Format.Tests.SemanticVersionTests.TryParse_RejectsMalformedVersions"));
  assert.ok(declarations.some((item) => item.framework === "node" && item.dynamicTemplate));
  for (const name of ["preflight-npm-publishing.fixture.sh", "verify-nuget-package.fixture.sh",
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
  const official = declarations.filter((item) => item.source.endsWith("/CurrentRidExecutionRuntimeHostIntegrationTests.cs"));
  assert.ok(official.length > 0);
  assert.ok(official.every((item) => item.primaryLane === "official-runtime"));
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

test("workflow required-test selections resolve current declarations and reject a stale owner", async () => {
  const workflowPath = resolve(repositoryRoot, ".github/workflows/sunder-ci.yml");
  const workflow = await readFile(workflowPath, "utf8");
  const host = await readFile(resolve(repositoryRoot, ".github/workflows/sunder-host-validation.yml"), "utf8");
  const declarations = loadCoreTestModel().declarations;
  validateWorkflowTestSelections([{ source: "ci", content: workflow }, { source: "host", content: host }], declarations);
  const oldOwner = `${workflow}\n        run: node reporter.mjs --require-scenario removed-interop\n`;
  assert.throws(() => validateWorkflowTestSelections([{ source: "ci", content: oldOwner }], declarations), /Unknown or empty CI scenario/u);
  assert.throws(() => validateWorkflowTestSelections([{ source: "ci", content: "--require-test Removed.Tests.Case" }], declarations), /required workflow test.*not declared/u);
});

test("Node matrix builds every packed input before selecting all packed scenarios", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-node-matrix-"));
  try {
    const scripts = resolve(root, "scripts/test-oracles");
    await mkdir(scripts, { recursive: true });
    const runner = resolve(scripts, "run-node-test-matrix-with-results.mjs");
    await writeFile(runner, await readFile(resolve(repositoryRoot, "scripts/test-oracles/run-node-test-matrix-with-results.mjs")));
    await writeFile(resolve(scripts, "node-test-workspace.mjs"), await readFile(resolve(repositoryRoot, "scripts/test-oracles/node-test-workspace.mjs")));
    const nodeRoot = resolve(root, "languages/typescript/node");
    await mkdir(resolve(nodeRoot, "node_modules"), { recursive: true });
    await mkdir(resolve(root, "specs"));
    await mkdir(resolve(root, "languages/csharp/dotnet/tests/Sunder.Runtime.Host.InteropWorker/Contracts"), { recursive: true });
    for (const [directory, name] of [["sdk", "@younics/sunder-sdk"], ["package-tool", "@younics/sunder-package-tool"], ["create-sunder-package", "@younics/create-sunder-package"]]) {
      const path = resolve(nodeRoot, "packages", directory);
      await mkdir(resolve(path, "dist"), { recursive: true });
      await writeFile(resolve(path, "package.json"), JSON.stringify({ name }));
      await writeFile(resolve(path, "dist/previous.js"), "unchanged checkout output");
    }
    await writeFile(resolve(scripts, "core-test-policy.mjs"), `import { loadCoreTestModel as load } from ${JSON.stringify(pathToFileURL(resolve(repositoryRoot, "scripts/test-oracles/core-test-policy.mjs")).href)};
      export const loadCoreTestModel = (_, policy, scope) => load(${JSON.stringify(repositoryRoot)}, policy, scope);`);
    await writeFile(resolve(scripts, "fixture.test.mjs"), "");
    const log = resolve(root, "commands.jsonl");
    const hook = resolve(root, "record commands #100%.mjs");
    await writeFile(hook, `
      import childProcess from "node:child_process";
      import { syncBuiltinESMExports } from "node:module";
      import { appendFileSync, mkdirSync, writeFileSync } from "node:fs";
      import { dirname, resolve } from "node:path";
      childProcess.spawnSync = (command, args, options) => {
        appendFileSync(${JSON.stringify(log)}, JSON.stringify({ args, cwd: options.cwd, env: {
          packedClosure: options.env.SUNDER_TEST_PACKED_CLOSURE,
          packedServices: options.env.SUNDER_TEST_PACKED_SERVICES,
        } }) + "\\n");
        if (args[0] === "exec") {
          const tests = resolve(options.cwd, dirname(args.at(-1)), "dist-test/test");
          mkdirSync(tests, { recursive: true });
          writeFileSync(resolve(tests, "fixture.test.js"), "");
        }
        return { status: 0 };
      };
      syncBuiltinESMExports();
    `);
    const result = spawnSync(process.execPath, ["--import", pathToFileURL(hook).href, runner], { encoding: "utf8" });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    const commands = (await readFile(log, "utf8")).trim().split("\n").map((line) => JSON.parse(line));
    const toolReport = commands.findIndex(({ args }) => args.includes("node-package-tool"));
    assert.ok(toolReport >= 0);
    for (const workspace of ["@younics/sunder-sdk", "@younics/sunder-package-tool", "@younics/create-sunder-package"]) {
      const build = commands.findIndex(({ args }) => args.join(" ") === `run build --workspace ${workspace}`);
      assert.ok(build >= 0 && build < toolReport, `${workspace} must be built before packed package-tool tests`);
      assert.notEqual(commands[build].cwd, nodeRoot);
    }
    assert.equal(await readFile(resolve(nodeRoot, "packages/sdk/dist/previous.js"), "utf8"), "unchanged checkout output");
    assert.deepEqual(commands[toolReport].env, { packedClosure: "1", packedServices: "1" });
    assert.ok(commands.some(({ args }) => args.includes("node-sdk")));
    assert.ok(commands.some(({ args }) => args.includes("node-create-sunder-package")));
    assert.ok(commands.some(({ args }) => args.join(" ") === "run templates:typecheck"));
    // Hosted steps must reuse one producer, preserve the complete packed closure,
    // and test exactly the same package selection as the local all-in-one entry.
    await writeFile(log, "");
    const work = resolve(root, "staged-work");
    for (const args of [["--action", "build"], ...["@younics/sunder-sdk", "@younics/sunder-package-tool", "@younics/create-sunder-package"]
      .map(name => ["--action", "test", "--project", name]), ["--action", "templates"]]) {
      const staged = spawnSync(process.execPath, ["--import", pathToFileURL(hook).href, runner, "--scope", "npm", "--work", work, ...args], { encoding: "utf8" });
      assert.equal(staged.status, 0, staged.stdout + staged.stderr);
    }
    const staged = (await readFile(log, "utf8")).trim().split("\n").map(line => JSON.parse(line));
    const builds = staged.filter(({ args }) => args[0] === "run" && args[1] === "build");
    assert.equal(builds.length, 3);
    assert.equal(new Set(builds.map(row => row.cwd)).size, 1);
    for (const report of ["node-sdk", "node-package-tool", "node-create-sunder-package"]) {
      assert.equal(staged.filter(({ args }) => args.includes(report)).length, 1);
    }
    assert.deepEqual(staged.find(({ args }) => args.includes("node-package-tool")).env, commands[toolReport].env);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("Node producers own workspace links and dependency writes", async () => {
  const root = await mkdtemp(resolve(tmpdir(), "sunder-node-isolation-"));
  try {
    const checkout = resolve(root, "checkout");
    const source = resolve(checkout, "languages/typescript/node");
    const sdk = resolve(source, "packages/sdk");
    const contracts = "languages/csharp/dotnet/tests/Sunder.Runtime.Host.InteropWorker/Contracts";
    const oracle = `${contracts}/example.rpc.json`;
    await mkdir(resolve(sdk, "dist"), { recursive: true });
    await mkdir(resolve(source, "node_modules/@younics"), { recursive: true });
    await mkdir(resolve(source, "node_modules/dependency"));
    await mkdir(resolve(sdk, "node_modules/private-dependency/dist"), { recursive: true });
    await mkdir(resolve(checkout, "specs"));
    await mkdir(resolve(checkout, contracts), { recursive: true });
    await writeFile(resolve(checkout, oracle), "authored wire oracle");
    await writeFile(resolve(sdk, "package.json"), JSON.stringify({ name: "@younics/sunder-sdk", main: "dist/index.js" }));
    await writeFile(resolve(sdk, "dist/index.js"), "checkout");
    await writeFile(resolve(source, "node_modules/dependency/value"), "locked dependency");
    await writeFile(resolve(sdk, "node_modules/private-dependency/dist/index.js"), "workspace dependency");
    await symlink(sdk, resolve(source, "node_modules/@younics/sunder-sdk"), process.platform === "win32" ? "junction" : "dir");
    const producers = [createNodeTestWorkspace(checkout, root), createNodeTestWorkspace(checkout, root)];
    for (const [index, producer] of producers.entries()) {
      const output = resolve(producer.nodeRoot, "packages/sdk/dist");
      await assert.rejects(stat(output), { code: "ENOENT" });
      await mkdir(output);
      await writeFile(resolve(output, "index.js"), `producer ${index}`);
      const require = createRequire(resolve(producer.nodeRoot, "consumer.cjs"));
      assert.equal(await readFile(require.resolve("@younics/sunder-sdk"), "utf8"), `producer ${index}`);
      assert.equal(await readFile(resolve(output, "../node_modules/private-dependency/dist/index.js"), "utf8"), "workspace dependency");
      assert.equal(await readFile(resolve(producer.root, oracle), "utf8"), "authored wire oracle");
    }
    await writeFile(resolve(producers[0].nodeRoot, "node_modules/dependency/value"), "private mutation");
    await writeFile(resolve(producers[0].root, oracle), "private oracle mutation");
    await rm(resolve(producers[0].nodeRoot, "packages/sdk/dist"), { recursive: true });
    assert.equal(await readFile(resolve(producers[1].nodeRoot, "packages/sdk/dist/index.js"), "utf8"), "producer 1");
    assert.equal(await readFile(resolve(producers[1].nodeRoot, "node_modules/dependency/value"), "utf8"), "locked dependency");
    assert.equal(await readFile(resolve(source, "node_modules/dependency/value"), "utf8"), "locked dependency");
    assert.equal(await readFile(resolve(sdk, "dist/index.js"), "utf8"), "checkout");
    assert.equal(await readFile(resolve(producers[1].root, oracle), "utf8"), "authored wire oracle");
    assert.equal(await readFile(resolve(checkout, oracle), "utf8"), "authored wire oracle");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
