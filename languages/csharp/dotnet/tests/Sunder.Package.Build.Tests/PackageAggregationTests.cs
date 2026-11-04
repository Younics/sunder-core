using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;
using Xunit;
using static Sunder.Package.Build.Tests.PackageBuildProcessFixture;
using static Sunder.Package.Build.Tests.PackageManifestFixture;
using static Sunder.Package.Build.Tests.PackageOutputFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageAggregationTests
{
    [Fact]
    public async Task AggregateTask_FactorsIdenticalFilesAndProducesDeterministicUniversalTree()
    {
        var root = CreateTempDirectory();
        var appLeaf = CreateCanonicalLeaf(root, "app", "win-x64", "app-native");
        var runtimeLeaf = CreateCanonicalLeaf(root, "runtime", "win-x64", "runtime-native");
        var managedRidPath = Path.Combine("lib", "runtimes", "win-x64", "lib", "net10.0", "SharedManaged.dll");
        var nativeExecutablePath = Path.Combine("lib", "package-native");
        foreach (var (leaf, role) in new[] { (appLeaf, "app"), (runtimeLeaf, "runtime") })
        {
            var managedDestination = Path.Combine(leaf, "payload", role, "win-x64", managedRidPath);
            Directory.CreateDirectory(Path.GetDirectoryName(managedDestination)!);
            File.Copy(Assembly.GetExecutingAssembly().Location, managedDestination);
            File.WriteAllBytes(
                Path.Combine(leaf, "payload", role, "win-x64", nativeExecutablePath),
                [0x7f, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3, 4]);
            PackageContentIndexer.Write(leaf);
        }
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();
        var task = new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(appLeaf), TargetLeafItem(runtimeLeaf)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        };

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        var firstIndex = await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json"));
        Assert.True(File.Exists(Path.Combine(output, "payload", "app", "win-x64", "lib", Path.GetFileName(Assembly.GetExecutingAssembly().Location))));
        Assert.True(File.Exists(Path.Combine(
            output,
            "payload",
            "runtime",
            "win-x64",
            "lib",
            CSharpWorkerModuleName())));
        Assert.True(File.Exists(Path.Combine(output, "payload", "app", "win-x64", "lib", "runtimes", "win-x64", "native", "fixture.bin")));
        Assert.True(File.Exists(Path.Combine(output, "payload", "runtime", "win-x64", "lib", "runtimes", "win-x64", "native", "fixture.bin")));
        Assert.False(Directory.Exists(Path.Combine(output, "payload", "app", "shared", "lib", "runtimes")));
        Assert.False(Directory.Exists(Path.Combine(output, "payload", "runtime", "shared", "lib", "runtimes")));
        Assert.True(File.Exists(Path.Combine(output, "payload", "shared", managedRidPath)));
        Assert.False(File.Exists(Path.Combine(output, "payload", "app", "win-x64", managedRidPath)));
        Assert.False(File.Exists(Path.Combine(output, "payload", "runtime", "win-x64", managedRidPath)));
        Assert.True(File.Exists(Path.Combine(output, "payload", "app", "win-x64", nativeExecutablePath)));
        Assert.True(File.Exists(Path.Combine(output, "payload", "runtime", "win-x64", nativeExecutablePath)));
        Assert.False(File.Exists(Path.Combine(output, "payload", "shared", nativeExecutablePath)));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal(2, validation.Manifest!.Targets!.Count);

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.Equal(firstIndex, await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json")));
    }

    [Fact]
    public async Task AggregateTask_UnionsRoleScopedRpcDeclarationsAndDeduplicatesRidLeaves()
    {
        var root = CreateTempDirectory();
        var appWin = CreateRpcLeaf(root, "app", "win-x64", required: false, includeProvider: false);
        var appLinux = CreateRpcLeaf(root, "app", "linux-x64", required: false, includeProvider: false);
        var runtime = CreateRpcLeaf(root, "runtime", "linux-arm64", required: true);
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();
        var task = new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(runtime), TargetLeafItem(appLinux), TargetLeafItem(appWin)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        };

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var bundle = Assert.Single(validation.Manifest!.ContractBundles!)!;
        Assert.Equal("owned", bundle.Source);
        Assert.Equal("package", bundle.AuthorityKind);
        Assert.Equal("test.package.build.fixture", bundle.AuthorityId);
        Assert.Null(bundle.SelectedAuthorityRelease);
        Assert.Null(bundle.SelectedAuthorityContentIdentity);
        Assert.Equal("required", Assert.Single(validation.Manifest.DependsOn!).Presence);
        Assert.Empty(validation.Manifest.PackageArtifactImports!);
        Assert.Empty(validation.Manifest.PackageApiImports!);
        Assert.Empty(validation.Manifest.ProviderImports!);
        Assert.Empty(validation.Manifest.PlatformImports!);
        var uses = validation.Manifest.UsesContracts!;
        Assert.Equal(
            ["app", "runtime"],
            uses.Select(static use => use!.Role!).ToArray());
        Assert.False(uses[0]!.Required ?? true);
        Assert.True(uses[1]!.Required == true);
        Assert.Equal("runtime", Assert.Single(validation.Manifest.Provides!)!.Role);
        Assert.All(validation.Manifest.Targets!, targetManifest =>
        {
            Assert.Contains("rpc.v1", targetManifest!.RequiredHostCapabilities!);
            Assert.Contains(SunderSdkCapabilities.CallbacksV1, targetManifest.RequiredHostCapabilities!);
        });
        Assert.True(File.Exists(Path.Combine(output, "payload", "shared", "contracts", "minimal.rpc.json")));
    }

    [Theory]
    [InlineData("use")]
    [InlineData("provider")]
    public void AggregateTask_RejectsConflictingRoleScopedRpcDeclarations(string declaration)
    {
        var root = CreateTempDirectory();
        var first = declaration == "use"
            ? CreateRpcLeaf(root, "runtime", "linux-x64", required: true)
            : CreateRpcLeaf(root, "runtime", "linux-x64", audience: "owner");
        var second = declaration == "use"
            ? CreateRpcLeaf(root, "runtime", "linux-arm64", required: false)
            : CreateRpcLeaf(root, "runtime", "linux-arm64", audience: "public");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(first), TargetLeafItem(second)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains($"conflicting {(declaration == "use" ? "contract use" : "provider")}", StringComparison.Ordinal));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("use")]
    [InlineData("provider")]
    public void AggregateTask_RejectsSameRoleRidRpcDeclarationOmissions(string declaration)
    {
        var root = CreateTempDirectory();
        var complete = CreateRpcLeaf(root, "runtime", "linux-x64");
        var omitted = CreateRpcLeaf(
            root,
            "runtime",
            "linux-arm64",
            includeUse: declaration != "use",
            includeProvider: declaration != "provider");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(complete), TargetLeafItem(omitted)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains(
            $"identical {(declaration == "use" ? "contract use" : "provider")} sets for role 'runtime'",
            StringComparison.Ordinal));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void AggregateTask_RequiresIdenticalPackageWideContractBundles()
    {
        var root = CreateTempDirectory();
        var app = CreateRpcLeaf(root, "app", "win-x64", exposure: "public", includeProvider: false);
        var runtime = CreateRpcLeaf(root, "runtime", "linux-x64", exposure: "package");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(app), TargetLeafItem(runtime)],
            OutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("conflicting 'contractBundles' evidence", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("packageArtifactImports")]
    [InlineData("packageApiImports")]
    [InlineData("providerImports")]
    [InlineData("platformImports")]
    public async Task AggregateTask_FingerprintsExplicitEmptyImportGroups(string group)
    {
        var root = CreateTempDirectory();
        var first = CreateCanonicalLeaf(root, "app", "win-x64", "native");
        var second = CreateCanonicalLeaf(root, "app", "linux-x64", "native");
        var manifestPath = Path.Combine(second, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!.AsObject();
        Assert.Empty(manifest[group]!.AsArray());
        Assert.True(manifest.Remove(group));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        PackageContentIndexer.Write(second);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(second);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));

        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();
        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(first), TargetLeafItem(second)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());

        Assert.Contains(buildEngine.Errors, error => error.Contains("package-wide manifest metadata", StringComparison.Ordinal));
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("optional-dependency")]
    [InlineData("package-api")]
    [InlineData("package-provider")]
    [InlineData("platform-provider")]
    public async Task AggregateTask_PreservesImportsAndNonRpcOptionalDependencies(string metadata)
    {
        var root = CreateTempDirectory();
        var leaf = CreateRpcLeaf(
            root,
            "runtime",
            "linux-x64",
            includeUse: metadata is "optional-dependency" or "package-api",
            includeProvider: metadata != "package-api");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var initialEngine = new TestBuildEngine();
        var task = new AggregateSunderPackageTask
        {
            BuildEngine = initialEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        };
        Assert.True(task.Execute(), string.Join(Environment.NewLine, initialEngine.Errors));
        var outputManifestPath = Path.Combine(output, "manifest", "sunder-package.json");
        var outputIndexPath = Path.Combine(output, "manifest", "content-index.json");
        var priorManifest = await File.ReadAllBytesAsync(outputManifestPath);
        var priorIndex = await File.ReadAllBytesAsync(outputIndexPath);

        var manifestPath = Path.Combine(leaf, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!.AsObject();
        if (metadata == "optional-dependency")
        {
            manifest["dependsOn"]![0]!["presence"] = "optional";
        }
        else
        {
            var platform = metadata == "platform-provider";
            var authority = new JsonObject
            {
                ["authorityKind"] = platform ? "platform" : "package",
                ["authorityId"] = platform ? "sunder.core" : "test.dependency",
                ["selectedAuthorityRelease"] = "1.2.3",
                ["selectedAuthorityContentIdentity"] = new string('1', 64),
            };
            var bundle = Assert.Single(manifest["contractBundles"]!.AsArray())!;
            bundle["source"] = "imported";
            foreach (var property in authority)
            {
                bundle[property.Key] = property.Value?.DeepClone();
            }
            var bundleRef = new JsonObject
            {
                ["contractId"] = "example.rpc.v1.minimal",
                ["contractVersion"] = "2.0.0",
            };
            if (!platform)
            {
                manifest["packageArtifactImports"] = new JsonArray(new JsonObject
                {
                    ["packageId"] = "test.dependency",
                    ["requestedVersionRange"] = ">=1.2.3 <2.0.0",
                    ["includePrerelease"] = false,
                    ["selectedPackageVersion"] = "1.2.3",
                    ["selectedPackageContentIdentity"] = new string('1', 64),
                    ["publicContractCount"] = 1,
                    ["publicInventorySha256"] = new string('3', 64),
                    ["authorityConstraints"] = new JsonArray(authority.DeepClone()),
                });
            }
            if (metadata == "package-api")
            {
                var useRef = (JsonObject)bundleRef.DeepClone();
                useRef["role"] = "runtime";
                manifest["packageApiImports"] = new JsonArray(new JsonObject
                {
                    ["packageId"] = "test.dependency",
                    ["presence"] = "required",
                    ["bundleRefs"] = new JsonArray(bundleRef),
                    ["useRefs"] = new JsonArray(useRef),
                });
            }
            else
            {
                var providerRef = (JsonObject)bundleRef.DeepClone();
                providerRef["role"] = "runtime";
                providerRef["providerId"] = "test.rpc.provider";
                var import = platform ? authority : new JsonObject { ["packageId"] = "test.dependency" };
                import["key"] = "minimalProvider";
                import["contractId"] = "example.rpc.v1.minimal";
                import["contractVersion"] = "2.0.0";
                import["bundleRef"] = bundleRef;
                import["providerRefs"] = new JsonArray(providerRef);
                if (platform)
                {
                    import["authorityReleaseRange"] = ">=1.0.0 <2.0.0";
                    import["includePrerelease"] = false;
                }
                manifest[platform ? "platformImports" : "providerImports"] = new JsonArray(import);
            }
        }
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        PackageContentIndexer.Write(leaf);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(leaf);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));

        var aggregateEngine = new TestBuildEngine();
        task.BuildEngine = aggregateEngine;
        if (metadata is "package-api" or "package-provider" or "platform-provider")
        {
            Assert.True(task.Execute(), string.Join(Environment.NewLine, aggregateEngine.Errors));
            Assert.Empty(aggregateEngine.Errors);
            var aggregateValidation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
            Assert.True(aggregateValidation.Success, string.Join(Environment.NewLine, aggregateValidation.Errors));
            using var sourceDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
            using var aggregateDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(outputManifestPath));
            foreach (var name in new[]
                     {
                         "dependsOn", "contractBundles", "usesContracts", "provides",
                         "packageArtifactImports", "packageApiImports", "providerImports", "platformImports",
                     })
            {
                Assert.True(JsonElement.DeepEquals(sourceDocument.RootElement.GetProperty(name), aggregateDocument.RootElement.GetProperty(name)),
                    $"Aggregation changed '{name}' for supported {metadata} metadata.");
            }
            var result = aggregateValidation.Manifest!;
            if (metadata == "platform-provider") Assert.Empty(result.PackageArtifactImports!);
            else Assert.Single(result.PackageArtifactImports!);
            Assert.Equal("imported", Assert.Single(result.ContractBundles!)!.Source);
            if (metadata == "package-api")
            {
                Assert.Equal("runtime", Assert.Single(Assert.Single(result.PackageApiImports!)!.UseRefs!)!.Role);
                Assert.Single(result.UsesContracts!);
                Assert.Empty(result.ProviderImports!);
                Assert.Empty(result.Provides!);
            }
            else
            {
                var references = metadata == "platform-provider" ? Assert.Single(result.PlatformImports!)!.ProviderRefs : Assert.Single(result.ProviderImports!)!.ProviderRefs;
                Assert.Equal("runtime", Assert.Single(references!)!.Role);
                Assert.Single(result.Provides!);
                Assert.Empty(result.PackageApiImports!);
                Assert.Empty(result.UsesContracts!);
            }
            Assert.NotEqual(priorManifest, await File.ReadAllBytesAsync(outputManifestPath));
            return;
        }
        Assert.True(task.Execute(), string.Join(Environment.NewLine, aggregateEngine.Errors));
        var optional = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(optional.Success, string.Join(Environment.NewLine, optional.Errors));
        Assert.Equal("optional", Assert.Single(optional.Manifest!.DependsOn!).Presence);
        Assert.NotEqual(priorManifest, await File.ReadAllBytesAsync(outputManifestPath));
        Assert.NotEqual(priorIndex, await File.ReadAllBytesAsync(outputIndexPath));
    }

    [Theory]
    [InlineData("contractBundles", "source", "must declare source")]
    [InlineData("contractBundles", "authorityKind", "must declare package authority")]
    [InlineData("contractBundles", "authorityId", "must declare package authority")]
    [InlineData("dependsOn", "presence", "must declare presence")]
    public async Task AggregateTask_RejectsMissingProducerMetadataWithoutInferringDefaults(
        string group,
        string property,
        string diagnostic)
    {
        var root = CreateTempDirectory();
        var leaf = CreateRpcLeaf(root, "runtime", "linux-x64");
        var manifestPath = Path.Combine(leaf, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(manifestPath))!.AsObject();
        Assert.True(manifest[group]![0]!.AsObject().Remove(property));
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        PackageContentIndexer.Write(leaf);

        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();
        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());

        Assert.Contains(buildEngine.Errors, error => error.Contains(diagnostic, StringComparison.Ordinal));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task AggregateTask_WaitsForConfiguredAncestorDiscoveryRootAcrossNestedSwapGap()
    {
        var root = CreateTempDirectory();
        var discoveryRoot = Path.Combine(root, "inputs");
        var leaf = CreateCanonicalLeaf(discoveryRoot, "runtime", "linux-x64", "native");
        var backup = leaf + ".manual-backup-" + Guid.NewGuid().ToString("N");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();
        var aggregate = new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf, discoveryRoot)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        };
        using var gapLock = GeneratedOutputLock.Acquire(
            [GeneratedOutputLock.TargetLeafDiscoveryKey(discoveryRoot, leaf)],
            []);
        Directory.Move(leaf, backup);
        using var aggregateContender = OutputLockContenderBarrier<bool>.Start(
            GeneratedOutputLock.TargetLeafDiscoveryKey(discoveryRoot, leaf),
            aggregate.Execute,
            () => $"discoveryRoot={discoveryRoot}; leafPresent={Directory.Exists(leaf)}; backupPresent={Directory.Exists(backup)}");
        var aggregateTask = aggregateContender.Completion;
        try
        {
            await aggregateContender.WaitForAttemptAsync(TimeSpan.FromSeconds(5));
            await aggregateContender.AssertContendedAsync();
            Directory.Move(backup, leaf);
            gapLock.Dispose();
            Assert.True(await aggregateTask.WaitAsync(TimeSpan.FromSeconds(5)), string.Join(Environment.NewLine, buildEngine.Errors));
        }
        finally
        {
            if (Directory.Exists(backup) && !Directory.Exists(leaf)) Directory.Move(backup, leaf);
            gapLock.Dispose();
            try
            {
                await aggregateTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Preserve the primary assertion failure.
            }
        }
    }

    [Fact]
    public async Task AggregateAndPackTasks_SerializeConcurrentMsbuildProcesses()
    {
        var root = CreateTempDirectory();
        var appLeaf = CreateCanonicalLeaf(root, "app", "win-x64", "app-native");
        var runtimeLeaf = CreateCanonicalLeaf(root, "runtime", "win-x64", "runtime-native");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var archive = Path.Combine(project, "bin", "concurrent.sunderpkg");
        Directory.CreateDirectory(project);
        var projectFile = Path.Combine(project, "Concurrent.proj");
        File.WriteAllText(projectFile, $$"""
            <Project>
              <UsingTask TaskName="Sunder.Package.Build.Tasks.AggregateSunderPackageTask"
                         AssemblyFile="{{typeof(AggregateSunderPackageTask).Assembly.Location}}" />
              <UsingTask TaskName="Sunder.Package.Build.Tasks.PackSunderPackageTask"
                         AssemblyFile="{{typeof(PackSunderPackageTask).Assembly.Location}}" />
              <ItemGroup>
                <Leaf Include="{{appLeaf}}" DiscoveryRoot="{{Path.GetDirectoryName(appLeaf)}}" />
                <Leaf Include="{{runtimeLeaf}}" DiscoveryRoot="{{Path.GetDirectoryName(runtimeLeaf)}}" />
              </ItemGroup>
              <Target Name="Run">
                <AggregateSunderPackageTask TargetLeaves="@(Leaf)"
                                             OutputPath="{{output}}"
                                             OutputDiscoveryRoot="{{target}}"
                                             ProjectDirectory="{{project}}"
                                             TargetDirectory="{{target}}"
                                             ExpectedPackageVersion="1.2.3" />
                <PackSunderPackageTask DevPackagePath="{{output}}"
                                       DevPackageDiscoveryRoot="{{target}}"
                                       PackageOutputPath="{{archive}}"
                                       ProjectDirectory="{{project}}" />
              </Target>
            </Project>
            """);

        var first = StartMsbuild(projectFile, project);
        var second = StartMsbuild(projectFile, project);
        var results = await Task.WhenAll(ReadProcessAsync(first), ReadProcessAsync(second));

        Assert.All(results, result => Assert.True(result.ExitCode == 0, result.Output));
        var aggregateValidation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(aggregateValidation.Success, string.Join(Environment.NewLine, aggregateValidation.Errors));
        var archiveValidation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            archive,
            Path.Combine(root, "concurrent-validation"));
        Assert.True(archiveValidation.Success, string.Join(Environment.NewLine, archiveValidation.Errors));
        Assert.Equal(2, archiveValidation.Manifest!.Targets!.Count);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(target),
            path => Path.GetFileName(path).Contains(".stage-", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains(".backup-", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains("transaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AggregateTask_RollsBackInterruptedCrossProcessReplacementBeforeReadingInputs()
    {
        var root = CreateTempDirectory();
        var leaf = CreateCanonicalLeaf(root, "runtime", "linux-x64", "native");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var marker = output + AggregateSunderPackageTask.MarkerFileSuffix;
        var firstEngine = new TestBuildEngine();
        Assert.True(new AggregateSunderPackageTask
        {
            BuildEngine = firstEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute(), string.Join(Environment.NewLine, firstEngine.Errors));
        var priorIndex = await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json"));
        var priorFiles = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Append(marker).Append(output + SunderPackageFormat.DevelopmentImportsSuffix)
            .ToDictionary(path => path, File.ReadAllBytes);

        var token = Guid.NewGuid().ToString("D");
        var outputStage = output + ".stage-" + Guid.NewGuid().ToString("N");
        var markerStage = marker + ".stage-" + Guid.NewGuid().ToString("N");
        var outputBackup = output + ".backup-" + token;
        var markerBackup = marker + ".backup-" + token;
        Directory.CreateDirectory(outputStage);
        File.WriteAllText(Path.Combine(outputStage, "replacement.txt"), "replacement");
        File.WriteAllText(markerStage, "replacement marker");
        Directory.Move(output, outputBackup);
        File.Move(marker, markerBackup);
        File.WriteAllText(output + ".sunder-output-transaction.json", JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            token,
            coordinatorPath = output,
            outputs = new[]
            {
                new { finalPath = output, stagedPath = outputStage, backupPath = outputBackup, existed = true },
                new { finalPath = marker, stagedPath = markerStage, backupPath = markerBackup, existed = true },
            },
        }));

        var recoveryEngine = new TestBuildEngine();
        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = recoveryEngine,
            TargetLeaves = [TargetLeafItem(Path.Combine(root, "missing-leaf"))],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());

        Assert.Equal(priorIndex, await File.ReadAllBytesAsync(Path.Combine(output, "manifest", "content-index.json")));
        Assert.All(priorFiles, entry => Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));
        Assert.True(File.Exists(marker));
        Assert.False(Directory.Exists(outputStage));
        Assert.False(Directory.Exists(outputBackup));
        Assert.False(File.Exists(output + ".sunder-output-transaction.json"));
    }

    [Fact]
    public void AggregateTask_RejectsDuplicateExactTargets()
    {
        var root = CreateTempDirectory();
        var leaf = CreateCanonicalLeaf(root, "runtime", "linux-x64", "native");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf), TargetLeafItem(leaf)],
            OutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.2.3",
        }.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AggregateTask_PreservesWorkerTargetLeavesWithoutForeignRidLeakage()
    {
        var root = CreateTempDirectory();
        var first = CreateProcessLeaf(root, "linux-x64", "linux-sea", "worker");
        var second = CreateProcessLeaf(root, "osx-arm64", "mac-sea", "worker");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var output = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName);
        var buildEngine = new TestBuildEngine();

        Assert.True(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(first), TargetLeafItem(second)],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.0.0",
        }.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));

        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(output);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.All(validation.Manifest!.Targets!, target => Assert.Equal("worker", target!.Kind));
        var linux = SunderPackageTargetResolver.CreateProjectionPlan(
            validation.Manifest,
            validation.ContentIndex!,
            new SunderPackageTargetKey("runtime", "linux-x64"));
        Assert.Contains(linux.Files, file => file.LogicalPath.ToString() == "lib/worker.cjs");
        Assert.DoesNotContain(linux.Files, file => file.PhysicalPath.ToString().Contains("osx-arm64", StringComparison.Ordinal));
    }

    [Fact]
    public void AggregateTask_RejectsLegacyProcessTargetLeaf()
    {
        var root = CreateTempDirectory();
        var leaf = CreateProcessLeaf(root, "linux-x64", "legacy", "process", validate: false);
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.0.0",
        }.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("kind 'process'", StringComparison.Ordinal)
            && error.Contains("invalid for role 'runtime'", StringComparison.Ordinal));
    }

    [Fact]
    public void AggregateTask_RejectsOldWorkerV3CapabilityLeaf()
    {
        var root = CreateTempDirectory();
        var leaf = CreateProcessLeaf(
            root,
            "linux-x64",
            "legacy",
            "worker",
            validate: false,
            requiredHostCapabilities:
            [
                "rpc.v1",
                "worker-protocol.v1",
                "worker-protocol.v3",
            ]);
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.0.0",
        }.Execute());
        Assert.Contains(
            buildEngine.Errors,
            error => error.Contains(
                "cannot require unsupported worker protocol capability 'worker-protocol.v3'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AggregateTask_RejectsLeafVersionThatDiffersFromAggregateVersion()
    {
        var root = CreateTempDirectory();
        var leaf = CreateCanonicalLeaf(root, "runtime", "linux-x64", "native");
        var project = Path.Combine(root, "aggregate-project");
        var target = Path.Combine(project, "bin", "Debug", "net10.0");
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(leaf)],
            OutputPath = Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "9.9.9",
        }.Execute());

        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("aggregate expected version '9.9.9'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("wrong-name")]
    [InlineData("nested")]
    [InlineData("source")]
    public void AggregateTask_RejectsUnsafeGeneratedOutputPath(string kind)
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "aggregate-project");
        var target = kind == "source"
            ? Path.Combine(project, "src", "generated")
            : Path.Combine(project, "bin", "Debug", "net10.0");
        var output = kind switch
        {
            "outside" => Path.Combine(root, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            "wrong-name" => Path.Combine(target, "universal"),
            "nested" => Path.Combine(target, "nested", ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
            _ => Path.Combine(target, ValidateSunderDevOutputPathTask.GeneratedDirectoryName),
        };
        var buildEngine = new TestBuildEngine();

        Assert.False(new AggregateSunderPackageTask
        {
            BuildEngine = buildEngine,
            TargetLeaves = [TargetLeafItem(Path.Combine(root, "unused-leaf"))],
            OutputPath = output,
            OutputDiscoveryRoot = target,
            ProjectDirectory = project,
            TargetDirectory = target,
            ExpectedPackageVersion = "1.0.0",
        }.Execute());

        Assert.Contains(buildEngine.Errors, error => error.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(output));
    }

    private static string CreateProcessLeaf(
        string root,
        string rid,
        string executableContent,
        string kind,
        bool validate = true,
        IReadOnlyList<string?>? requiredHostCapabilities = null)
    {
        var leaf = Path.Combine(root, "process-" + rid);
        var manifestPath = Path.Combine(leaf, "manifest", "sunder-package.json");
        var executablePath = Path.Combine(leaf, "payload", "runtime", rid, "lib", "worker.cjs");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
        File.WriteAllText(executablePath, executableContent);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new SunderPackageManifest
        {
            ArchiveFormatVersion = 1,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = "test.process",
            Name = "Test Process",
            Version = "1.0.0",
            DependsOn = [],
            Targets =
            [
                new SunderPackageTargetManifest
                {
                    Role = "runtime",
                    Rid = rid,
                    Kind = kind,
                    EntryPoint = "lib/worker.cjs",
                    TargetFramework = "node24",
                    SdkVersion = "1.0.0",
                    RequiredHostCapabilities = requiredHostCapabilities
                        ?? ["execution-runtime.v1", "rpc.v1", "worker-protocol.v1"],
                    ExecutionRuntime = kind == SunderPackageFormat.WorkerTargetKind
                        ? new SunderPackageExecutionRuntimeManifest
                        {
                            Id = SunderPackageFormat.NodeExecutionRuntimeId,
                            VersionRange = ">=24.18.1 <25.0.0",
                            ModuleAbi = SunderPackageFormat.NodeWorkerModuleAbi,
                        }
                        : null,
                },
            ],
            ContractBundles = [],
            UsesContracts = [],
            Provides = [],
            PackageArtifactImports = [],
            PackageApiImports = [],
            ProviderImports = [],
            PlatformImports = [],
        }));
        var projectionPath = Path.Combine(leaf, SunderPackageFormat.RpcModelProjectionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(projectionPath)!);
        File.WriteAllBytes(projectionPath, new SunderRpcModelProjectionDocument(1, "test.process", "1.0.0", []).ToCanonicalUtf8());
        PackageContentIndexer.Write(leaf);
        if (validate)
        {
            var validation = SunderPackageArchiveInspector.ValidateExtractedPackageAsync(leaf).GetAwaiter().GetResult();
            Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        }
        return leaf;
    }
}
