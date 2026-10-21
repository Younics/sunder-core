using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Xunit;

namespace Sunder.Package.Build.Tests;

[CollectionDefinition(nameof(TaskShadowStagingTests), DisableParallelization = true)]
public sealed class TaskShadowStagingTestCollection;

[Collection(nameof(TaskShadowStagingTests))]
public sealed class TaskShadowStagingTests
{
    private const string MarkerFileName = ".sunder-task-set";

    private static readonly string[] TaskFileNames =
    [
        "Microsoft.CodeAnalysis.CSharp.dll",
        "Microsoft.CodeAnalysis.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "Sunder.Package.Build.dll",
        "Sunder.Package.Build.Editor.dll",
        "Sunder.Package.Format.dll",
        "Sunder.Sdk.dll",
    ];

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task ConcurrentPublishers_ObserveOneCompleteImmutableTaskSet()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-concurrent");
        var source = CopyCurrentTaskSet(scope);
        var shadowRoot = scope.CreateDirectory("shadow");
        var project = WriteStagingProject(scope, source, shadowRoot, "shared", "Concurrent.proj");

        var builds = Enumerable.Range(0, 8)
            .Select(_ => RunMsbuildAsync(project, scope.RootPath, "Run"))
            .ToArray();
        var results = await Task.WhenAll(builds);

        Assert.All(results, AssertSuccess);
        AssertPublishedSet(Path.Combine(shadowRoot, "shared"), source);
        AssertNoTemporaryPublishDirectories(shadowRoot);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task PartialUnmarkedShadow_IsRejectedWithoutDeletingUserFiles()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-partial");
        var source = CopyCurrentTaskSet(scope);
        var shadowRoot = scope.CreateDirectory("shadow");
        var shadow = scope.CreateDirectory("shadow", "shared");
        File.Copy(
            Path.Combine(source, "Sunder.Package.Build.dll"),
            Path.Combine(shadow, "Sunder.Package.Build.dll"));
        await File.WriteAllTextAsync(Path.Combine(shadow, "unexpected.dll"), "partial");
        var project = WriteStagingProject(scope, source, shadowRoot, "shared", "Partial.proj");

        var result = await RunMsbuildAsync(project, scope.RootPath, "Run");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("is not marker-owned and will not be replaced", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(shadow, "Sunder.Package.Build.dll")));
        Assert.Equal("partial", await File.ReadAllTextAsync(Path.Combine(shadow, "unexpected.dll")));
        Assert.False(File.Exists(Path.Combine(shadow, MarkerFileName)));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task InterruptedPublisherState_IsIgnoredWithoutDeletingUnprovenOrphans()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-crash");
        var source = CopyCurrentTaskSet(scope);
        var shadowRoot = scope.CreateDirectory("shadow");
        var crashProject = WriteCrashingPublisherProject(scope, shadowRoot);
        var crash = await RunMsbuildAsync(crashProject, scope.RootPath, "Run");
        Assert.NotEqual(0, crash.ExitCode);
        var abandonedStage = Path.Combine(shadowRoot, ".sunder-stage-abandoned");
        Assert.True(Directory.Exists(abandonedStage));
        var abandonedRecovery = scope.CreateDirectory("shadow", ".sunder-recovery-abandoned");
        await File.WriteAllTextAsync(Path.Combine(abandonedRecovery, "old.dll"), "old");
        var project = WriteStagingProject(scope, source, shadowRoot, "recovered", "Recovery.proj");

        var result = await RunMsbuildAsync(project, scope.RootPath, "Run");

        AssertSuccess(result);
        AssertPublishedSet(Path.Combine(shadowRoot, "recovered"), source);
        Assert.True(Directory.Exists(abandonedStage));
        Assert.True(Directory.Exists(abandonedRecovery));
    }

    [Theory]
    [InlineData("Sunder.Package.Format.dll")]
    [InlineData("Sunder.Sdk.dll")]
    [InlineData("Sunder.Package.Build.Editor.dll")]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task Fingerprint_CoversEveryDeclaredDependencyAndIgnoresUndeclaredDlls(string dependencyFileName)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-fingerprint");
        var source = CopyCurrentTaskSet(scope);
        var shadowRoot = scope.CreateDirectory("shadow");
        await File.WriteAllTextAsync(Path.Combine(source, "Unlisted.dll"), "not part of the task set");
        var mainAssembly = Path.Combine(source, "Sunder.Package.Build.dll");
        var mainTimestamp = File.GetLastWriteTimeUtc(mainAssembly);
        var mainHash = SHA256.HashData(await File.ReadAllBytesAsync(mainAssembly));
        var firstProject = WriteStagingProject(scope, source, shadowRoot, "first", "First.proj");

        AssertSuccess(await RunMsbuildAsync(firstProject, scope.RootPath, "_PrepareSunderPackageBuildTasks"));
        var firstShadow = Path.Combine(shadowRoot, "first");
        var firstFingerprint = ReadFingerprint(firstShadow);
        AssertPublishedSet(firstShadow, source);
        Assert.False(File.Exists(Path.Combine(firstShadow, "Unlisted.dll")));

        var dependencyAssembly = Path.Combine(source, dependencyFileName);
        var dependencyTimestamp = File.GetLastWriteTimeUtc(dependencyAssembly);
        var dependencyBytes = await File.ReadAllBytesAsync(dependencyAssembly);
        dependencyBytes[^1] ^= 0xff;
        await File.WriteAllBytesAsync(dependencyAssembly, dependencyBytes);
        File.SetLastWriteTimeUtc(dependencyAssembly, dependencyTimestamp);
        var secondProject = WriteStagingProject(scope, source, shadowRoot, "second", "Second.proj");

        AssertSuccess(await RunMsbuildAsync(secondProject, scope.RootPath, "_PrepareSunderPackageBuildTasks"));
        var secondShadow = Path.Combine(shadowRoot, "second");
        AssertPublishedSet(secondShadow, source);
        Assert.NotEqual(firstFingerprint, ReadFingerprint(secondShadow));
        Assert.Equal(mainTimestamp, File.GetLastWriteTimeUtc(mainAssembly));
        Assert.Equal(mainHash, SHA256.HashData(await File.ReadAllBytesAsync(mainAssembly)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task MissingSdkDependency_FailsBeforePublishingTaskShadow(bool immutable)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-missing-sdk");
        var source = CopyCurrentTaskSet(scope);
        File.Delete(Path.Combine(source, "Sunder.Sdk.dll"));
        var shadowRoot = scope.CreateDirectory("shadow");
        var project = WriteStagingProject(scope, source, shadowRoot, "missing-sdk", "MissingSdk.proj", immutable);

        var result = await RunMsbuildAsync(project, scope.RootPath, "_PrepareSunderPackageBuildTasks");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Required Sunder.Package.Build task dependency 'Sunder.Sdk.dll' was not produced",
            result.StandardOutput + result.StandardError,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(shadowRoot, "missing-sdk")));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task PublishedShadow_IsNeverRewrittenForAnotherDependencySet()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-immutable");
        var source = CopyCurrentTaskSet(scope);
        var shadowRoot = scope.CreateDirectory("shadow");
        var project = WriteStagingProject(scope, source, shadowRoot, "shared", "Immutable.proj", immutable: true);

        AssertSuccess(await RunMsbuildAsync(project, scope.RootPath, "_PrepareSunderPackageBuildTasks"));
        var shadow = Path.Combine(shadowRoot, "shared");
        var publishedFormat = await File.ReadAllBytesAsync(Path.Combine(shadow, "Sunder.Package.Format.dll"));
        await File.AppendAllTextAsync(Path.Combine(source, "Sunder.Package.Format.dll"), "different immutable set");

        var collision = await RunMsbuildAsync(project, scope.RootPath, "_PrepareSunderPackageBuildTasks");

        Assert.NotEqual(0, collision.ExitCode);
        Assert.Contains("Immutable Sunder.Package.Build task shadow", collision.StandardOutput + collision.StandardError, StringComparison.Ordinal);
        Assert.Equal(publishedFormat, await File.ReadAllBytesAsync(Path.Combine(shadow, "Sunder.Package.Format.dll")));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task PackagedTargets_WithPlatformQualifiedConsumerTfm_LoadNet10ImmutableTaskSet()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-packaged-tfm");
        var source = CopyCurrentTaskSet(scope);
        var packageRoot = scope.CreateDirectory("package");
        var packageTasks = scope.CreateDirectory("package", "tasks", "net10.0");
        var buildTransitive = scope.CreateDirectory("package", "buildTransitive");
        var targetDirectory = scope.CreateDirectory("target");
        foreach (var fileName in TaskFileNames)
        {
            File.Copy(Path.Combine(source, fileName), Path.Combine(packageTasks, fileName));
        }

        var repositoryTargets = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build");
        foreach (var fileName in new[] { "Sunder.Package.Build.targets", "Sunder.Package.Build.SourceLock.targets",
                     "Sunder.Package.Build.Authoring.targets", "Sunder.Package.Build.Authoring.Outer.targets",
                     "Sunder.Package.Build.Authoring.Dispatch.targets", "Sunder.Package.Build.Authoring.CrossTargeting.targets" })
        {
            File.Copy(Path.Combine(repositoryTargets, fileName), Path.Combine(buildTransitive, fileName));
        }

        var probeOutput = scope.GetPath("probe.txt");
        var consumer = scope.WriteAllText("PackagedConsumer.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows</TargetFramework>
                <EnableWindowsTargeting>true</EnableWindowsTargeting>
                <SunderPackageProject>false</SunderPackageProject>
                <TargetDir>{{targetDirectory}}{{Path.DirectorySeparatorChar}}</TargetDir>
              </PropertyGroup>
              <Import Project="{{Path.Combine(buildTransitive, "Sunder.Package.Build.targets")}}" />
              <Target Name="Probe" DependsOnTargets="_PrepareSunderPackageBuildTasks">
                <ValidateSunderDevOutputPathTask DevOutputPath="{{Path.Combine(targetDirectory, "sunder-dev")}}"
                                                  DiscoveryRoot="{{targetDirectory}}"
                                                  ProjectDirectory="{{scope.RootPath}}"
                                                  TargetDirectory="{{targetDirectory}}" />
                <WriteLinesToFile File="{{probeOutput}}"
                                  Lines="$(SunderPackageBuildTaskTargetFramework)|$(SunderPackageBuildTaskAssembly)|$(SunderPackageBuildTaskSourceIsImmutable)|$(SunderPackageBuildTaskShadowDirectory)"
                                  Overwrite="true" />
              </Target>
            </Project>
            """);

        var result = await RunMsbuildAsync(consumer, scope.RootPath, "Probe");

        AssertSuccess(result);
        var fields = (await File.ReadAllTextAsync(probeOutput)).Trim().Split('|', 4);
        Assert.Equal("net10.0", fields[0]);
        Assert.Equal(Path.Combine(packageRoot, "tasks", "net10.0", "Sunder.Package.Build.dll"), fields[1]);
        Assert.Equal("true", fields[2], ignoreCase: true);
        AssertPublishedSet(fields[3], source);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task CleanBuildFixture_BuildsAndRepairsTheCompleteTaskDependencySet()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-clean-build");
        var repositoryRoot = FindRepositoryRoot();
        var fixtureSource = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "TaskShadowCleanBuild");
        var fixture = scope.GetPath("fixture");
        CopyDirectory(fixtureSource, fixture);
        var taskProject = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var consumerProject = Path.Combine(fixture, "TaskShadowCleanBuild.proj");
        var targetsPath = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.targets");
        var probeOutput = scope.GetPath("clean-build-probe.txt");
        var redirectedOutput = scope.GetPath("redirected-bin") + Path.DirectorySeparatorChar;
        var redirectedIntermediate = scope.GetPath("redirected-obj") + Path.DirectorySeparatorChar;

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));
        var taskOutput = Path.Combine(fixture, "Sunder.Package.Build", "bin", "Debug", "net10.0");
        Assert.False(File.Exists(Path.Combine(taskOutput, "Sunder.Package.Build.dll")));

        AssertSuccess(await RunMsbuildAsync(
            consumerProject,
            fixture,
            "Run",
            $"-p:SunderPackageBuildTargetsPath={targetsPath}",
            $"-p:ProbeOutput={probeOutput}",
            "-p:Platform=x64",
            "-p:RuntimeIdentifier=win-x64",
            $"-p:BaseOutputPath={redirectedOutput}",
            $"-p:OutputPath={redirectedOutput}",
            $"-p:OutDir={redirectedOutput}",
            $"-p:BaseIntermediateOutputPath={redirectedIntermediate}",
            $"-p:IntermediateOutputPath={redirectedIntermediate}",
            $"-p:MSBuildProjectExtensionsPath={redirectedIntermediate}",
            "-p:BuildProjectReferences=false"));
        Assert.Equal("format-ready", (await File.ReadAllTextAsync(probeOutput)).Trim());
        AssertPublishedSet(Path.Combine(fixture, "obj", "consumer", "task-shadow", "clean-build"), taskOutput);

        File.Delete(Path.Combine(taskOutput, "Sunder.Package.Format.dll"));
        File.Delete(Path.Combine(taskOutput, "Sunder.Sdk.dll"));
        Assert.True(File.Exists(Path.Combine(taskOutput, "Sunder.Package.Build.dll")));

        AssertSuccess(await RunMsbuildAsync(
            consumerProject,
            fixture,
            "Run",
            $"-p:SunderPackageBuildTargetsPath={targetsPath}",
            $"-p:ProbeOutput={probeOutput}",
            "-p:SunderPackageBuildTaskShadowStamp=repaired"));
        Assert.Equal("format-ready", (await File.ReadAllTextAsync(probeOutput)).Trim());
        AssertPublishedSet(Path.Combine(fixture, "obj", "consumer", "task-shadow", "repaired"), taskOutput);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task ReusedNode_LoadsRebuiltTaskFromFreshEvaluationPath()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-node-reuse");
        var taskDirectory = scope.CreateDirectory("MutableTask");
        var targetDirectory = scope.CreateDirectory("target");
        var taskProject = Path.Combine(taskDirectory, "MutableTask.csproj");
        var taskAssembly = Path.Combine(taskDirectory, "bin", "Debug", "net10.0", "Sunder.Package.Build.dll");
        var firstConsumerProject = scope.GetPath("Consumer.First.csproj");
        var secondConsumerProject = scope.GetPath("Consumer.Second.csproj");
        var driverProject = scope.GetPath("Driver.proj");
        var firstOutput = scope.GetPath("first.txt");
        var secondOutput = scope.GetPath("second.txt");
        var targetsPath = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.targets");

        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "First.cs"), WriteMutableTaskSource("first"));
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "Second.cs"), WriteMutableTaskSource("second"));
        await File.WriteAllTextAsync(taskProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>Sunder.Package.Build</AssemblyName>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <Implementation Condition="'$(Implementation)' == ''">First</Implementation>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="$(Implementation).cs" />
                <Reference Include="Microsoft.Build.Framework" HintPath="$(MSBuildBinPath)/Microsoft.Build.Framework.dll" Private="false" />
                <Reference Include="Microsoft.Build.Utilities.Core" HintPath="$(MSBuildBinPath)/Microsoft.Build.Utilities.Core.dll" Private="false" />
              </ItemGroup>
              <Target Name="ForceSecondCompilation" BeforeTargets="CoreCompile" Condition="'$(Implementation)' == 'Second'">
                <Delete Files="$(TargetPath);$(IntermediateOutputPath)$(TargetName).dll" />
              </Target>
              <Target Name="CopyTaskCompanions" AfterTargets="Build">
                <ItemGroup>
                  <_TaskCompanion Include="Sunder.Package.Build.Editor.dll;Sunder.Package.Format.dll;Sunder.Sdk.dll;Microsoft.CodeAnalysis.dll;Microsoft.CodeAnalysis.CSharp.dll;Microsoft.Extensions.DependencyInjection.Abstractions.dll;Microsoft.Extensions.Logging.Abstractions.dll" />
                </ItemGroup>
                <Copy SourceFiles="@(_TaskCompanion->'$(TargetPath)')" DestinationFiles="@(_TaskCompanion->'$(TargetDir)%(Identity)')" />
              </Target>
            </Project>
            """);
        var consumerProjectContents = $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <SunderPackageProject>false</SunderPackageProject>
                <SunderPackageBuildProject>{{taskProject}}</SunderPackageBuildProject>
                <SunderPackageBuildTaskAssembly>{{taskAssembly}}</SunderPackageBuildTaskAssembly>
                <SunderPackageBuildTaskSourceIsImmutable>false</SunderPackageBuildTaskSourceIsImmutable>
                <SunderPackageBuildTaskHostArchitecture>{{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}}</SunderPackageBuildTaskHostArchitecture>
                <SunderPackageBuildTaskSourceGlobalProperties>Implementation</SunderPackageBuildTaskSourceGlobalProperties>
              </PropertyGroup>
              <Import Project="{{targetsPath}}" />
              <Target Name="Probe" DependsOnTargets="_PrepareSunderPackageBuildTasks">
                <ValidateSunderDevOutputPathTask DevOutputPath="$(OutputFile)"
                                                  DiscoveryRoot="{{targetDirectory}}"
                                                  ProjectDirectory="{{scope.RootPath}}"
                                                  TargetDirectory="{{targetDirectory}}" />
              </Target>
            </Project>
            """;
        await File.WriteAllTextAsync(firstConsumerProject, consumerProjectContents);
        await File.WriteAllTextAsync(secondConsumerProject, consumerProjectContents);
        await File.WriteAllTextAsync(driverProject, $$"""
            <Project>
              <Target Name="Run">
                <MSBuild Projects="{{taskProject}}" Targets="Rebuild" Properties="Configuration=Debug;Implementation=First" />
                <MSBuild Projects="{{firstConsumerProject}}" Targets="Probe" Properties="Configuration=Debug;Implementation=First;OutputFile={{firstOutput}}" />
                <MSBuild Projects="{{secondConsumerProject}}" Targets="Probe" Properties="Configuration=Debug;Implementation=Second;OutputFile={{secondOutput}}" />
                <ReadLinesFromFile File="{{secondOutput}}">
                  <Output TaskParameter="Lines" PropertyName="SecondResult" />
                </ReadLinesFromFile>
                <Error Condition="'$(SecondResult)' != 'second'" Text="Second task returned '$(SecondResult)' instead of the rebuilt implementation." />
              </Target>
            </Project>
            """);

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--nologo"));
        var result = await RunMsbuildAsync(driverProject, scope.RootPath, "Run", "-m:1");

        AssertSuccess(result);
        Assert.Equal("first", (await File.ReadAllTextAsync(firstOutput)).Trim());
        Assert.Equal("second", (await File.ReadAllTextAsync(secondOutput)).Trim());

        var shadowRoot = Path.Combine(scope.RootPath, "obj", "sunder-package-build-tasks", "Debug", "net10.0");
        Assert.True(Directory.EnumerateFiles(shadowRoot, MarkerFileName, SearchOption.AllDirectories).Count() >= 2);
        var markerCount = Directory.EnumerateFiles(shadowRoot, MarkerFileName, SearchOption.AllDirectories).Count();
        AssertSuccess(await RunMsbuildAsync(
            secondConsumerProject,
            scope.RootPath,
            "Clean",
            "-p:Configuration=Debug",
            "-p:Implementation=Second"));
        Assert.Equal(markerCount, Directory.EnumerateFiles(shadowRoot, MarkerFileName, SearchOption.AllDirectories).Count());
        Assert.True(File.Exists(taskAssembly));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task ConcurrentTaskProjectBuilds_StageAndLoadOneCoherentGeneration()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-source-race");
        var repositoryRoot = FindRepositoryRoot();
        var fixtureSource = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "TaskShadowCleanBuild");
        var fixture = scope.GetPath("fixture");
        CopyDirectory(fixtureSource, fixture);
        var taskProject = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var consumerProject = Path.Combine(fixture, "TaskShadowCleanBuild.proj");
        var targetsPath = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.targets");

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));

        var builds = Enumerable.Range(0, 8)
            .Select(index =>
            {
                return RunMsbuildAsync(
                    consumerProject,
                    fixture,
                    "Run",
                    $"-p:SunderPackageBuildTargetsPath={targetsPath}",
                    $"-p:ProbeOutput={scope.GetPath($"probe-{index}.txt")}",
                    "-p:ProbeValue=first",
                    "-p:ExpectedProbeValue=first",
                    $"-p:SunderPackageBuildTaskShadowStamp=generation-{index}",
                    "-p:BuildProjectReferences=false");
            })
            .ToArray();

        var results = await Task.WhenAll(builds);

        Assert.All(results, AssertSuccess);
        for (var index = 0; index < results.Length; index++)
        {
            Assert.Equal("first", (await File.ReadAllTextAsync(scope.GetPath($"probe-{index}.txt"))).Trim());
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task ParallelConsumerGraph_DoesNotStarveSourceBuild(int nodeCount, int configurationCount)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-consumer-graph");
        var repositoryRoot = FindRepositoryRoot();
        var fixture = scope.GetPath("fixture");
        CopyDirectory(
            Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "tests", "Sunder.Package.Build.TestFixtures", "TaskShadowCleanBuild"),
            fixture);
        var taskProject = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var consumerProject = Path.Combine(fixture, "TaskShadowCleanBuild.proj");
        var targetsPath = Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "packages", "Sunder.Package.Build", "build", "Sunder.Package.Build.targets");
        var consumers = Enumerable.Range(0, 8).Select(index => scope.WriteAllText($"Consumer{index}.proj", $$"""
            <Project>
              <PropertyGroup>
                <SunderPackageBuildTargetsPath>{{targetsPath}}</SunderPackageBuildTargetsPath>
                <TaskShadowUseDefaultTaskAssembly>true</TaskShadowUseDefaultTaskAssembly>
                <SunderPackageBuildTaskSourceGlobalProperties>DefineConstants</SunderPackageBuildTaskSourceGlobalProperties>
                <SunderPackageBuildTaskBootstrapConfiguration>Debug-{{index % configurationCount}}</SunderPackageBuildTaskBootstrapConfiguration>
                <SunderPackageBuildTaskShadowStamp>graph-{{index}}</SunderPackageBuildTaskShadowStamp>
                <ProbeOutput>{{scope.GetPath($"probe-{index}.txt")}}</ProbeOutput>
                <ExpectedProbeValue>first</ExpectedProbeValue>
              </PropertyGroup>
              <Import Project="{{consumerProject}}" />
            </Project>
            """)).ToArray();
        var driver = scope.WriteAllText("Graph.proj", $$"""
            <Project>
              <ItemGroup>
                {{string.Join(Environment.NewLine, consumers.Select((consumer, index) => $$"""
                <_Consumer Include="{{consumer}}">
                  <AdditionalProperties>SunderPackageId=consumer-{{index}};PackageVersion=2.0.{{index}};GeneratePackageOnBuild={{index % 2 == 0}};Configuration=Consumer-{{index}};TargetFramework=net9.0;BuildProjectReferences=false;UnrecognizedConsumerProperty=value-{{index}};DefineConstants=TASK_SHADOW_FIRST%3BANOTHER_SOURCE_SYMBOL</AdditionalProperties>
                </_Consumer>
                """))}}
              </ItemGroup>
              <Target Name="Run">
                <MSBuild Projects="@(_Consumer)" Targets="Run" BuildInParallel="true" />
              </Target>
            </Project>
            """);

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));
        var result = await RunMsbuildAsync(driver, scope.RootPath, "Run", $"-m:{nodeCount}", "-v:diagnostic");

        AssertSuccess(result);
        // Assert coalescing, not elapsed time: even a favorable schedule must not
        // create one yielded file-lock waiter per consumer in this build graph.
        Assert.Equal(configurationCount, Regex.Matches(result.StandardOutput + result.StandardError, "SUNDER_SOURCE_LOCK_ACQUIRED [0-9a-f]{32}").Count);
        var snapshots = Directory.GetDirectories(Path.Combine(fixture, "Sunder.Package.Build", "obj", "sunder-package-build-task-sources"));
        Assert.Equal(configurationCount, snapshots.Length);
        for (var index = 0; index < consumers.Length; index++)
        {
            Assert.Equal("first", (await File.ReadAllTextAsync(scope.GetPath($"probe-{index}.txt"))).Trim());
            var shadow = Path.Combine(fixture, "obj", "consumer", "task-shadow", $"graph-{index}");
            AssertPublishedSet(shadow, Path.Combine(fixture, "Sunder.Package.Build", "bin", $"Debug-{index % configurationCount}", "net10.0"));
            Assert.Contains(snapshots, snapshot => ReadFingerprint(snapshot) == ReadFingerprint(shadow));
        }
    }

    [Theory]
    [InlineData(false, "Build")]
    [InlineData(true, "Build")]
    [InlineData(false, "Rebuild")]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task FailedSourceBuild_ReleasesLeaseBeforeSameNodeRecoveryBuild(bool viaConsumer, string ownerTarget)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-source-lock-recovery");
        var repositoryRoot = FindRepositoryRoot();
        var fixtureSource = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "TaskShadowCleanBuild");
        var fixture = scope.GetPath("fixture");
        CopyDirectory(fixtureSource, fixture);
        var taskProject = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var sourceLockTargets = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.SourceLock.targets");
        var failedNodeIdentity = scope.GetPath("failed-node.txt");
        var recoveredNodeIdentity = scope.GetPath("recovered-node.txt");
        var buildProject = viaConsumer ? Path.Combine(fixture, "TaskShadowCleanBuild.proj") : taskProject;
        var buildTarget = viaConsumer ? "Run" : ownerTarget;
        var targetsPath = Path.Combine(Path.GetDirectoryName(sourceLockTargets)!, "Sunder.Package.Build.targets");
        var driverProject = scope.WriteAllText("SourceLockRecovery.proj", $$"""
            <Project>
              <Target Name="Run">
                <MSBuild Projects="{{buildProject}}"
                         Targets="{{buildTarget}}"
                         Properties="SunderPackageBuildSourceLockTargetsPath={{sourceLockTargets}};SunderPackageBuildTargetsPath={{targetsPath}};TaskShadowFailAfterSourceLockAcquisition=true;TaskShadowBuildNodeIdentityFile={{failedNodeIdentity}}"
                         ContinueOnError="WarnAndContinue" />
                <Error Condition="!Exists('{{failedNodeIdentity}}')" Text="The intentional failing build did not acquire the source lock." />
                <MSBuild Projects="{{buildProject}}"
                         Targets="{{buildTarget}}"
                         Properties="SunderPackageBuildSourceLockTargetsPath={{sourceLockTargets}};SunderPackageBuildTargetsPath={{targetsPath}};TaskShadowFailAfterSourceLockAcquisition=false;TaskShadowBuildNodeIdentityFile={{recoveredNodeIdentity}}" />
              </Target>
            </Project>
            """);

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));
        var result = await RunMsbuildAsync(driverProject, fixture, "Run", "-m:1");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Intentional task-shadow fixture failure", result.StandardOutput + result.StandardError);
        Assert.True(File.Exists(recoveredNodeIdentity));
        Assert.Equal(
            File.ReadAllText(failedNodeIdentity).Trim(),
            File.ReadAllText(recoveredNodeIdentity).Trim());
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task SourceLockOwnerImportBeforeSdkTargets_IsRejected()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-source-lock-import-order");
        var sourceLockTargets = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.SourceLock.targets");
        var project = scope.WriteAllText("PreSdkSourceLockOwner.csproj", $$"""
            <Project>
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <SunderPackageBuildTaskSourceLockOwner>true</SunderPackageBuildTaskSourceLockOwner>
              </PropertyGroup>
              <Import Project="{{sourceLockTargets}}" />
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
              <Target Name="Noop" />
            </Project>
            """);

        var result = await RunMsbuildAsync(project, scope.RootPath, "Noop");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "source-lock ownership must be imported after Sdk.targets",
            result.StandardOutput + result.StandardError,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Build;Pack", 2)]
    [InlineData("Pack;Build", 2)]
    [InlineData("Rebuild;Pack", 4)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task CompoundOwnerOperations_ReacquireSourceLeaseAndPackOnce(
        string targets,
        int expectedAcquisitions)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-owner-compound");
        var repositoryRoot = FindRepositoryRoot();
        var fixture = scope.GetPath("fixture");
        CopyDirectory(
            Path.Combine(
                repositoryRoot,
                "languages",
                "csharp",
                "dotnet",
                "tests",
                "Sunder.Package.Build.TestFixtures",
                "TaskShadowCleanBuild"),
            fixture);
        var taskProject = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var sourceLockTargets = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.SourceLock.targets");
        var packRecord = scope.GetPath("pack-record.txt");

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));
        var result = await RunMsbuildAsync(
            taskProject,
            fixture,
            targets,
            $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLockTargets}",
            "-p:GeneratePackageOnBuild=false",
            $"-p:TaskShadowPackRecordFile={packRecord}",
            "-v:diagnostic");

        AssertSuccess(result);
        Assert.Equal(
            expectedAcquisitions,
            Regex.Matches(result.StandardOutput + result.StandardError, "SUNDER_SOURCE_LOCK_ACQUIRED [0-9a-f]{32}").Count);
        Assert.Equal(["pack"], await File.ReadAllLinesAsync(packRecord));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task DirectOwnerClean_WaitsForConsumerStaging(bool useDefaultTaskAssembly)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-owner-clean-race");
        var repositoryRoot = FindRepositoryRoot();
        var fixture = scope.GetPath("fixture");
        CopyDirectory(
            Path.Combine(
                repositoryRoot,
                "languages",
                "csharp",
                "dotnet",
                "tests",
                "Sunder.Package.Build.TestFixtures",
                "TaskShadowCleanBuild"),
            fixture);
        var taskProject = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var consumerProject = Path.Combine(fixture, "TaskShadowCleanBuild.proj");
        var packageBuildRoot = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build");
        var targetsPath = Path.Combine(packageBuildRoot, "build", "Sunder.Package.Build.targets");
        var sourceLockTargets = Path.Combine(packageBuildRoot, "build", "Sunder.Package.Build.SourceLock.targets");
        var configuration = useDefaultTaskAssembly ? "Debug-sunder-bootstrap" : "Debug";
        var taskAssembly = Path.Combine(fixture, "Sunder.Package.Build", "bin", configuration, "net10.0", "Sunder.Package.Build.dll");
        var probeOutput = scope.GetPath("probe.txt");
        var holderStarted = scope.GetPath("holder-started.txt");
        var holderContinue = scope.GetPath("holder-continue.txt");
        var contenderAttempted = scope.GetPath("contender-attempted.txt");

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));
        var consumer = RunMsbuildAsync(
            consumerProject,
            fixture,
            "Run",
            $"-p:SunderPackageBuildTargetsPath={targetsPath}",
            $"-p:TaskShadowUseDefaultTaskAssembly={useDefaultTaskAssembly}",
            $"-p:ProbeOutput={probeOutput}",
            "-p:ProbeValue=first",
            "-p:ExpectedProbeValue=first",
            "-p:SunderPackageBuildTaskShadowStamp=owner-clean-race",
            $"-p:TaskShadowAfterBuildWaitStartedFile={holderStarted}",
            $"-p:TaskShadowAfterBuildContinueFile={holderContinue}");
        await WaitForFileAsync(holderStarted);
        var clean = RunMsbuildAsync(
            taskProject,
            fixture,
            "Clean",
            $"-p:Configuration={configuration}",
            $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLockTargets}",
            $"-p:_SunderPackageBuildTaskSourceLockAttemptSignalFile={contenderAttempted}");

        try
        {
            // Stop at the Build-to-stage handoff. Clean must actually contend here;
            // without a continuous lease it completes and deletes the staging inputs.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(contenderAttempted) && !clean.IsCompleted)
            {
                await Task.Delay(25, timeout.Token);
            }
        }
        finally
        {
            await File.WriteAllTextAsync(holderContinue, "continue");
        }

        var results = await Task.WhenAll(consumer, clean);

        Assert.All(results, AssertSuccess);
        Assert.Equal(
            Path.Combine(fixture, "Sunder.Package.Build", "obj", ".sunder-package-build-task-source.lock"),
            await File.ReadAllTextAsync(contenderAttempted));
        Assert.Equal("first", (await File.ReadAllTextAsync(probeOutput)).Trim());
        var shadow = Path.Combine(fixture, "obj", "consumer", "task-shadow", "owner-clean-race");
        Assert.True(File.Exists(Path.Combine(shadow, MarkerFileName)));
        Assert.All(TaskFileNames, name => Assert.True(File.Exists(Path.Combine(shadow, name)), name));
        Assert.False(File.Exists(taskAssembly));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task OwnerRebuild_CleansSharedReferencesBeforeBuildingThem(bool buildProjectReferences)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-rebuild-reference-order");
        var repositoryRoot = FindRepositoryRoot();
        var fixture = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(scope.GetPath("fixture"));
        CopyDirectory(
            Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "tests", "Sunder.Package.Build.TestFixtures", "TaskShadowCleanBuild"),
            fixture);
        var owner = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var dependency = Path.Combine(fixture, "Sunder.Package.Format", "Sunder.Package.Format.csproj");
        var reference = Path.Combine(fixture, "Sunder.Package.Format", "obj", "Debug", "net10.0", "ref", "Sunder.Package.Format.dll");
        var sourceLockTargets = Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "packages", "Sunder.Package.Build", "build", "Sunder.Package.Build.SourceLock.targets");
        // Force the unfavorable solution ordering in one graph. A late first Clean
        // must not delete a reference whose Build result is already cached.
        var driver = scope.WriteAllText("RebuildReferenceOrder.proj", $$"""
            <Project>
              <Target Name="Run">
                <MSBuild Projects="{{owner}}" Targets="Rebuild"
                         Properties="Configuration=Debug;GeneratePackageOnBuild=false;BuildProjectReferences={{buildProjectReferences}};SunderPackageBuildSourceLockTargetsPath={{sourceLockTargets}}" />
                <Error Condition="!Exists('{{reference}}')" Text="The owner did not produce its shared reference." />
                <MSBuild Projects="{{dependency}}" Targets="Rebuild" Properties="Configuration=Debug" />
                <Error Condition="!Exists('{{reference}}')" Text="A late Clean deleted the shared reference after its Build result was cached." />
              </Target>
            </Project>
            """);

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", owner, "--disable-parallel", "--nologo"));
        AssertSuccess(await RunMsbuildAsync(owner, fixture, "Build",
            "-p:Configuration=Debug", "-p:GeneratePackageOnBuild=false",
            $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLockTargets}"));
        var result = await RunMsbuildAsync(driver, fixture, "Run", "-m:2", "-v:diagnostic");
        AssertSuccess(result);
        var phases = Regex.Matches(result.StandardOutput,
            "Target \"(CoreClean|CoreCompile):[^\\r\\n]*from project \"[^\"]*Sunder.Package.Format.csproj\"");
        Assert.Equal(["CoreClean", "CoreCompile"], phases.Select(match => match.Groups[1].Value));
        Assert.True(File.Exists(reference));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task OwnerBuild_DoesNotHoldOutputLeaseWhileBuildingSharedDependencies()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-reference-lease-order");
        var root = FindRepositoryRoot();
        var fixture = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(scope.GetPath("fixture"));
        CopyDirectory(Path.Combine(root, "languages/csharp/dotnet/tests/Sunder.Package.Build.TestFixtures/TaskShadowCleanBuild"), fixture);
        var owner = Path.Combine(fixture, "Sunder.Package.Build/Sunder.Package.Build.csproj");
        var dependency = Path.Combine(fixture, "Sunder.Package.Format/Sunder.Package.Format.csproj");
        var lockPath = Path.Combine(fixture, "Sunder.Package.Build/obj/.sunder-package-build-task-source.lock");
        var sourceLock = Path.Combine(root, "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.SourceLock.targets");
        var document = XDocument.Load(dependency);
        document.Root!.Add(XElement.Parse($$"""
            <UsingTask TaskName="ProbeOwnerOutputLease" TaskFactory="RoslynCodeTaskFactory"
                       AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
              <Task><Code Type="Fragment" Language="cs"><![CDATA[
                using (var lease = new System.IO.FileStream(@"{{lockPath}}", System.IO.FileMode.OpenOrCreate,
                    System.IO.FileAccess.ReadWrite, System.IO.FileShare.None)) { }
              ]]></Code></Task>
            </UsingTask>
            """));
        document.Root.Add(XElement.Parse("""
            <Target Name="ProbeOwnerLeaseWhileResolvingReferences" BeforeTargets="CoreCompile">
              <ProbeOwnerOutputLease />
            </Target>
            """));
        document.Save(dependency);
        AssertSuccess(await RunDotnetAsync(fixture, "restore", owner, "--nologo"));
        AssertSuccess(await RunMsbuildAsync(owner, fixture, "Build", "-m:2", "-p:BuildProjectReferences=false",
            "-p:GeneratePackageOnBuild=false", $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLock}"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task OwnerRebuild_RecordsOuterCleanAndBuildForReferenceConsumers(bool generatePackageOnBuild, bool buildFirst)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-owner-reference-cache");
        var repositoryRoot = FindRepositoryRoot();
        var fixture = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(scope.GetPath("fixture"));
        CopyDirectory(
            Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "tests", "Sunder.Package.Build.TestFixtures", "TaskShadowCleanBuild"),
            fixture);
        var owner = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var reference = Path.Combine(fixture, "Sunder.Package.Build", "obj", "Debug", "net10.0", "ref", "Sunder.Package.Build.dll");
        var sourceLockTargets = Path.Combine(repositoryRoot, "languages", "csharp", "dotnet", "packages", "Sunder.Package.Build", "build", "Sunder.Package.Build.SourceLock.targets");
        var consumers = new[] { "First", "Second" }.Select(name =>
        {
            scope.WriteAllText($"{name}/Probe.cs", $"public sealed class {name}Probe;");
            return GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(scope.WriteAllText($"{name}/{name}.csproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsPackable>false</IsPackable>
                    <DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{owner}}" />
                  </ItemGroup>
                </Project>
                """));
        }).ToArray();
        var driver = scope.WriteAllText("ReferenceConsumers.proj", $$"""
            <Project>
              <Target Name="Run">
                <MSBuild Projects="{{owner}}" Targets="Build" Condition="'{{buildFirst}}' == 'True'" />
                <MSBuild Projects="{{owner}}" Targets="Rebuild" />
                <Error Condition="!Exists('{{reference}}')" Text="Owner Rebuild returned without its reference assembly." />
                <MSBuild Projects="{{consumers[0]}}" Targets="Build" />
                <MSBuild Projects="{{consumers[1]}}" Targets="Rebuild" />
                <Error Condition="!Exists('{{reference}}')" Text="A reference consumer's Clean invalidated the cached owner Build." />
              </Target>
            </Project>
            """);

        foreach (var consumer in consumers)
        {
            AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", consumer, "--disable-parallel", "--nologo"));
        }
        var result = await RunMsbuildAsync(driver, fixture, "Run", "-m:2", "-v:diagnostic",
            "-p:Configuration=Debug", $"-p:GeneratePackageOnBuild={generatePackageOnBuild}",
            $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLockTargets}");
        AssertSuccess(result);
        var phases = Regex.Matches(result.StandardOutput,
            "Target \"(CoreClean|CoreCompile):[^\\r\\n]*from project \"[^\"]*Sunder.Package.Build.csproj\"");
        Assert.Equal(buildFirst ? ["CoreCompile", "CoreClean", "CoreCompile"] : ["CoreClean", "CoreCompile"], phases.Select(match => match.Groups[1].Value));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task DirectOwnerClean_DoesNotCleanReferencedProjectOutputs()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-owner-clean-boundary");
        var repositoryRoot = FindRepositoryRoot();
        var fixture = scope.GetPath("fixture");
        CopyDirectory(
            Path.Combine(
                repositoryRoot,
                "languages",
                "csharp",
                "dotnet",
                "tests",
                "Sunder.Package.Build.TestFixtures",
                "TaskShadowCleanBuild"),
            fixture);
        var taskProject = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var sourceLockTargets = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.SourceLock.targets");
        var taskAssembly = Path.Combine(fixture, "Sunder.Package.Build", "bin", "Debug", "net10.0", "Sunder.Package.Build.dll");
        var dependencyAssembly = Path.Combine(fixture, "Sunder.Package.Format", "bin", "Debug", "net10.0", "Sunder.Package.Format.dll");
        var dependencyReferenceAssembly = Path.Combine(fixture, "Sunder.Package.Format", "obj", "Debug", "net10.0", "ref", "Sunder.Package.Format.dll");

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));
        AssertSuccess(await RunMsbuildAsync(
            taskProject,
            fixture,
            "Build",
            $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLockTargets}",
            "-p:GeneratePackageOnBuild=false"));
        Assert.True(File.Exists(taskAssembly));
        Assert.True(File.Exists(dependencyAssembly));
        Assert.True(File.Exists(dependencyReferenceAssembly));
        var dependencyHash = SHA256.HashData(await File.ReadAllBytesAsync(dependencyAssembly));
        var dependencyReferenceHash = SHA256.HashData(await File.ReadAllBytesAsync(dependencyReferenceAssembly));

        AssertSuccess(await RunMsbuildAsync(
            taskProject,
            fixture,
            "Clean",
            $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLockTargets}"));

        Assert.False(File.Exists(taskAssembly));
        Assert.Equal(dependencyHash, SHA256.HashData(await File.ReadAllBytesAsync(dependencyAssembly)));
        Assert.Equal(dependencyReferenceHash, SHA256.HashData(await File.ReadAllBytesAsync(dependencyReferenceAssembly)));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task DirectOwnerBuild_DoesNotPropagateOperationPropertiesToReferences()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-owner-reference-properties");
        var repositoryRoot = FindRepositoryRoot();
        var fixture = scope.GetPath("fixture");
        CopyDirectory(
            Path.Combine(
                repositoryRoot,
                "languages",
                "csharp",
                "dotnet",
                "tests",
                "Sunder.Package.Build.TestFixtures",
                "TaskShadowCleanBuild"),
            fixture);
        var taskProject = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var sourceLockTargets = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.SourceLock.targets");
        var propertyRecord = scope.GetPath("dependency-properties.txt");

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));
        AssertSuccess(await RunMsbuildAsync(
            taskProject,
            fixture,
            "Build",
            $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLockTargets}",
            "-p:GeneratePackageOnBuild=false",
            $"-p:TaskShadowDependencyPropertyRecordFile={propertyRecord}"));

        Assert.Equal(
            [
                "BuildProjectReferences=dependency-local",
                "GeneratePackageOnBuild=dependency-local",
                "SunderPackageBuildTaskSourceLockAlreadyHeld=dependency-local",
                "SunderPackageBuildTaskSourceLockPath=dependency-local",
                "SunderPackageBuildSourceLockTargetsPath=dependency-local",
            ],
            await File.ReadAllLinesAsync(propertyRecord));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task PrimaryAppBuild_CoalescesWithDirectBuildForSameFramework()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-primary-app-build-coalescing");
        var targetsPath = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.targets");
        var appProject = scope.WriteAllText("App.proj", """
            <Project>
              <PropertyGroup>
                <TargetFramework Condition="'$(TargetFramework)' == ''">net10.0</TargetFramework>
              </PropertyGroup>
              <Target Name="Build">
                <WriteLinesToFile File="$(InvocationRecord)" Lines="build" Overwrite="false" />
              </Target>
              <Target Name="Run" DependsOnTargets="Build" />
            </Project>
            """);
        var primaryProject = scope.WriteAllText("Primary.proj", $$"""
            <Project>
              <PropertyGroup>
                <Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>
                <TargetFramework>net10.0</TargetFramework>
                <SunderPackagePrimaryProject>true</SunderPackagePrimaryProject>
                <SunderPackageAppProject>{{appProject}}</SunderPackageAppProject>
                <SunderPackageComposeRuntimeIdentifiers>osx-arm64</SunderPackageComposeRuntimeIdentifiers>
              </PropertyGroup>
              <Import Project="{{targetsPath}}" />
              <Target Name="Run" DependsOnTargets="_BuildSunderPackageApp" />
            </Project>
            """);
        var invocationRecord = scope.GetPath("app-builds.txt");
        var driverProject = scope.WriteAllText("Driver.proj", $$"""
            <Project>
              <ItemGroup>
                <_BuildProject Include="{{appProject}}" />
                <_BuildProject Include="{{primaryProject}}" />
              </ItemGroup>
              <Target Name="Run">
                <MSBuild Projects="@(_BuildProject)"
                         Targets="Run"
                         Properties="Configuration=Debug;InvocationRecord={{invocationRecord}}"
                         BuildInParallel="true" />
              </Target>
            </Project>
            """);

        AssertSuccess(await RunMsbuildAsync(driverProject, scope.RootPath, "Run", "-m:2"));

        Assert.Equal(["build"], await File.ReadAllLinesAsync(invocationRecord));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task DirectOwnerClean_WaitsForPackAndRemovesItsPackage()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-owner-pack-clean-race");
        var repositoryRoot = FindRepositoryRoot();
        var fixture = scope.GetPath("fixture");
        CopyDirectory(
            Path.Combine(
                repositoryRoot,
                "languages",
                "csharp",
                "dotnet",
                "tests",
                "Sunder.Package.Build.TestFixtures",
                "TaskShadowCleanBuild"),
            fixture);
        var taskProjectDirectory = Path.Combine(fixture, "Sunder.Package.Build");
        var taskProject = Path.Combine(taskProjectDirectory, "Sunder.Package.Build.csproj");
        var sourceLockTargets = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.SourceLock.targets");
        var holderStarted = scope.GetPath("pack-holder-started.txt");
        var holderContinue = scope.GetPath("pack-holder-continue.txt");
        var contenderAttempted = scope.GetPath("clean-contender-attempted.txt");

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));
        AssertSuccess(await RunMsbuildAsync(
            taskProject,
            fixture,
            "Build",
            $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLockTargets}",
            "-p:GeneratePackageOnBuild=false"));

        var pack = RunMsbuildAsync(
            taskProject,
            fixture,
            "Pack",
            $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLockTargets}",
            "-p:GeneratePackageOnBuild=true",
            $"-p:TaskShadowBeforePackWaitStartedFile={holderStarted}",
            $"-p:TaskShadowBeforePackContinueFile={holderContinue}");
        await WaitForFileAsync(holderStarted);
        var clean = RunMsbuildAsync(
            taskProject,
            fixture,
            "Clean",
            $"-p:SunderPackageBuildSourceLockTargetsPath={sourceLockTargets}",
            "-p:GeneratePackageOnBuild=true",
            $"-p:_SunderPackageBuildTaskSourceLockAttemptSignalFile={contenderAttempted}");

        try
        {
            await WaitForFileAsync(contenderAttempted);
            Assert.False(clean.IsCompleted, "Clean completed while Pack still held the source lock.");
        }
        finally
        {
            await File.WriteAllTextAsync(holderContinue, "continue");
        }

        var results = await Task.WhenAll(pack, clean);

        Assert.All(results, AssertSuccess);
        Assert.Empty(Directory.EnumerateFiles(taskProjectDirectory, "*.nupkg", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(taskProjectDirectory, "bin", "Debug", "net10.0", "Sunder.Package.Build.dll")));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task FailedPack_ReleasesLeaseBeforeSameNodeRecoveryPack()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-pack-lock-recovery");
        var repositoryRoot = FindRepositoryRoot();
        var fixture = scope.GetPath("fixture");
        CopyDirectory(
            Path.Combine(
                repositoryRoot,
                "languages",
                "csharp",
                "dotnet",
                "tests",
                "Sunder.Package.Build.TestFixtures",
                "TaskShadowCleanBuild"),
            fixture);
        var taskProject = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var sourceLockTargets = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.SourceLock.targets");
        var failedNodeIdentity = scope.GetPath("failed-pack-node.txt");
        var recoveredNodeIdentity = scope.GetPath("recovered-pack-node.txt");
        var driverProject = scope.WriteAllText("PackSourceLockRecovery.proj", $$"""
            <Project>
              <Target Name="Run">
                <MSBuild Projects="{{taskProject}}"
                         Targets="Pack"
                         Properties="SunderPackageBuildSourceLockTargetsPath={{sourceLockTargets}};GeneratePackageOnBuild=false;TaskShadowFailPackAfterSourceLockAcquisition=true;TaskShadowBuildNodeIdentityFile={{failedNodeIdentity}}"
                         ContinueOnError="WarnAndContinue" />
                <Error Condition="!Exists('{{failedNodeIdentity}}')" Text="The intentional failing Pack did not acquire the source lock." />
                <MSBuild Projects="{{taskProject}}"
                         Targets="Pack"
                         Properties="SunderPackageBuildSourceLockTargetsPath={{sourceLockTargets}};GeneratePackageOnBuild=false;TaskShadowFailPackAfterSourceLockAcquisition=false;TaskShadowPackNodeIdentityFile={{recoveredNodeIdentity}}" />
              </Target>
            </Project>
            """);

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));
        var result = await RunMsbuildAsync(driverProject, fixture, "Run", "-m:1");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Intentional task-shadow fixture Pack failure", result.StandardOutput + result.StandardError);
        Assert.True(File.Exists(recoveredNodeIdentity));
        Assert.Equal(
            File.ReadAllText(failedNodeIdentity).Trim(),
            File.ReadAllText(recoveredNodeIdentity).Trim());
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task FailedOuterAfterPackTarget_ReleasesLeaseBeforeSameNodeRecoveryPack()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-after-pack-lock-recovery");
        var repositoryRoot = FindRepositoryRoot();
        var fixture = scope.GetPath("fixture");
        CopyDirectory(
            Path.Combine(
                repositoryRoot,
                "languages",
                "csharp",
                "dotnet",
                "tests",
                "Sunder.Package.Build.TestFixtures",
                "TaskShadowCleanBuild"),
            fixture);
        var taskProject = Path.Combine(fixture, "Sunder.Package.Build", "Sunder.Package.Build.csproj");
        var sourceLockTargets = Path.Combine(
            repositoryRoot,
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.SourceLock.targets");
        var failedNodeIdentity = scope.GetPath("failed-after-pack-node.txt");
        var recoveredNodeIdentity = scope.GetPath("recovered-after-pack-node.txt");
        var driverProject = scope.WriteAllText("AfterPackSourceLockRecovery.proj", $$"""
            <Project>
              <Target Name="Run">
                <MSBuild Projects="{{taskProject}}"
                         Targets="Pack"
                         Properties="SunderPackageBuildSourceLockTargetsPath={{sourceLockTargets}};GeneratePackageOnBuild=false;TaskShadowFailAfterOuterPack=true;TaskShadowBuildNodeIdentityFile={{failedNodeIdentity}}"
                         ContinueOnError="WarnAndContinue" />
                <Error Condition="!Exists('{{failedNodeIdentity}}')" Text="The intentional outer AfterTargets=Pack failure did not run." />
                <MSBuild Projects="{{taskProject}}"
                         Targets="Pack"
                         Properties="SunderPackageBuildSourceLockTargetsPath={{sourceLockTargets}};GeneratePackageOnBuild=false;TaskShadowFailAfterOuterPack=false;TaskShadowPackNodeIdentityFile={{recoveredNodeIdentity}}" />
              </Target>
            </Project>
            """);

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", taskProject, "--disable-parallel", "--nologo"));
        var result = await RunMsbuildAsync(driverProject, fixture, "Run", "-m:1");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Intentional task-shadow fixture outer Pack failure", result.StandardOutput + result.StandardError);
        Assert.True(File.Exists(recoveredNodeIdentity));
        Assert.Equal(
            File.ReadAllText(failedNodeIdentity).Trim(),
            File.ReadAllText(recoveredNodeIdentity).Trim());
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task ContendingSourceLockWaiter_YieldsNodeForHolderNestedBuild()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-source-lock-yield");
        var sourceLockTargets = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.SourceLock.targets");
        var lockPath = scope.GetPath("source.lock");
        var holderReady = scope.GetPath("holder-ready.txt");
        var contenderAttempted = scope.GetPath("contender-attempted.txt");
        var nestedCompleted = scope.GetPath("nested-completed.txt");
        var contenderCompleted = scope.GetPath("contender-completed.txt");
        var nestedProject = scope.WriteAllText("SourceLockNested.proj", """
            <Project>
              <Target Name="Run">
                <WriteLinesToFile File="$(NestedCompleted)" Lines="completed" Overwrite="true" />
              </Target>
            </Project>
            """);
        var participantProject = scope.WriteAllText("SourceLockParticipant.proj", $$"""
            <Project>
              <PropertyGroup>
                <_ParticipantTarget Condition="'$(Mode)' == 'holder'">HoldLease</_ParticipantTarget>
                <_ParticipantTarget Condition="'$(Mode)' == 'contender'">ContendForLease</_ParticipantTarget>
              </PropertyGroup>

              <UsingTask TaskName="_WaitForSourceLockSignal"
                         TaskFactory="RoslynCodeTaskFactory"
                         AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
                <ParameterGroup>
                  <File ParameterType="System.String" Required="true" />
                </ParameterGroup>
                <Task>
                  <Using Namespace="System" />
                  <Using Namespace="System.IO" />
                  <Using Namespace="System.Threading" />
                  <Code Type="Fragment" Language="cs"><![CDATA[
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (!System.IO.File.Exists(File))
                    {
                        if (DateTime.UtcNow >= deadline)
                        {
                            throw new TimeoutException($"Timed out waiting for '{File}'.");
                        }
                        Thread.Sleep(10);
                    }
                  ]]></Code>
                </Task>
              </UsingTask>

              <Import Project="{{sourceLockTargets}}" />

              <Target Name="Run" DependsOnTargets="$(_ParticipantTarget)" />

              <Target Name="HoldLease">
                <_SunderManagePackageBuildTaskSourceLock Action="Acquire"
                                                         LockPath="$(LockPath)"
                                                         Context="yield-test-holder">
                  <Output TaskParameter="LeaseId" PropertyName="_TestLeaseId" />
                </_SunderManagePackageBuildTaskSourceLock>
                <WriteLinesToFile File="$(HolderReady)" Lines="ready" Overwrite="true" />
                <_WaitForSourceLockSignal File="$(ContenderAttempted)" />
                <MSBuild Projects="$(NestedProject)"
                         Targets="Run"
                         BuildInParallel="false"
                         Properties="NestedCompleted=$(NestedCompleted)" />
                <_SunderManagePackageBuildTaskSourceLock Action="Release"
                                                         LockPath="$(LockPath)"
                                                         Context="yield-test-holder"
                                                         LeaseId="$(_TestLeaseId)" />
                <PropertyGroup>
                  <_TestLeaseId></_TestLeaseId>
                </PropertyGroup>
                <OnError ExecuteTargets="ReleaseLease" />
              </Target>

              <Target Name="ContendForLease">
                <_WaitForSourceLockSignal File="$(HolderReady)" />
                <_SunderManagePackageBuildTaskSourceLock Action="Acquire"
                                                         LockPath="$(LockPath)"
                                                         Context="yield-test-contender"
                                                         AttemptSignalFile="$(ContenderAttempted)">
                  <Output TaskParameter="LeaseId" PropertyName="_TestLeaseId" />
                </_SunderManagePackageBuildTaskSourceLock>
                <WriteLinesToFile File="$(ContenderCompleted)" Lines="completed" Overwrite="true" />
                <_SunderManagePackageBuildTaskSourceLock Action="Release"
                                                         LockPath="$(LockPath)"
                                                         Context="yield-test-contender"
                                                         LeaseId="$(_TestLeaseId)" />
                <PropertyGroup>
                  <_TestLeaseId></_TestLeaseId>
                </PropertyGroup>
                <OnError ExecuteTargets="ReleaseLease" />
              </Target>

              <Target Name="ReleaseLease" Condition="'$(_TestLeaseId)' != ''">
                <_SunderManagePackageBuildTaskSourceLock Action="Release"
                                                         LockPath="$(LockPath)"
                                                         Context="yield-test-release"
                                                         LeaseId="$(_TestLeaseId)" />
                <PropertyGroup>
                  <_TestLeaseId></_TestLeaseId>
                </PropertyGroup>
              </Target>
            </Project>
            """);
        var driverProject = scope.WriteAllText("SourceLockDriver.proj", $$"""
            <Project>
              <ItemGroup>
                <_Participant Include="{{participantProject}}">
                  <AdditionalProperties>Mode=holder;LockPath={{lockPath}};HolderReady={{holderReady}};ContenderAttempted={{contenderAttempted}};NestedProject={{nestedProject}};NestedCompleted={{nestedCompleted}}</AdditionalProperties>
                </_Participant>
                <_Participant Include="{{participantProject}}">
                  <AdditionalProperties>Mode=contender;LockPath={{lockPath}};HolderReady={{holderReady}};ContenderAttempted={{contenderAttempted}};ContenderCompleted={{contenderCompleted}}</AdditionalProperties>
                </_Participant>
              </ItemGroup>
              <Target Name="Run">
                <MSBuild Projects="@(_Participant)" Targets="Run" BuildInParallel="true" />
              </Target>
            </Project>
            """);

        var result = await RunMsbuildAsync(driverProject, scope.RootPath, "Run", "-m:2");

        AssertSuccess(result);
        Assert.True(File.Exists(nestedCompleted));
        Assert.True(File.Exists(contenderCompleted));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task RelativeOverrides_AreResolvedFromTheImportingProject()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-relative");
        var source = CopyCurrentTaskSet(scope);
        var launchDirectory = scope.CreateDirectory("launch", "nested");
        var targetDirectory = scope.CreateDirectory("target");
        var targetsPath = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.targets");
        var project = scope.WriteAllText("Relative.proj", $$"""
            <Project>
              <PropertyGroup>
                <Configuration>Debug</Configuration>
                <TargetFramework>net10.0</TargetFramework>
                <BaseIntermediateOutputPath>obj/</BaseIntermediateOutputPath>
                <TargetDir>{{targetDirectory}}{{Path.DirectorySeparatorChar}}</TargetDir>
                <SunderPackageBuildTaskSourceIsImmutable>false</SunderPackageBuildTaskSourceIsImmutable>
                <SunderPackageBuildTaskShadowStamp>relative</SunderPackageBuildTaskShadowStamp>
              </PropertyGroup>
              <Import Project="{{targetsPath}}" />
              <Target Name="Run" DependsOnTargets="_PrepareSunderPackageBuildTasks">
                <ValidateSunderDevOutputPathTask DevOutputPath="{{Path.Combine(targetDirectory, "sunder-dev")}}"
                                                  DiscoveryRoot="{{targetDirectory}}"
                                                  ProjectDirectory="{{scope.RootPath}}"
                                                  TargetDirectory="{{targetDirectory}}" />
              </Target>
            </Project>
            """);

        var result = await RunMsbuildAsync(
            project,
            launchDirectory,
            "Run",
            "-p:SunderPackageBuildProject=missing.csproj",
            "-p:SunderPackageBuildTaskAssembly=source/Sunder.Package.Build.dll",
            "-p:SunderPackageBuildTaskShadowRoot=relative-shadow",
            "-p:SunderPackageBuildTaskShadowDirectory=relative-shadow/global-child");

        AssertSuccess(result);
        AssertPublishedSet(Path.Combine(scope.RootPath, "relative-shadow", "global-child"), source);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task TaskProjectReference_IsCanonicalAndRemovesConsumerOutputGlobals()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-project-reference");
        var taskDirectory = scope.CreateDirectory("MutableTask");
        var taskProject = Path.Combine(taskDirectory, "MutableTask.csproj");
        var consumerProject = scope.GetPath("Consumer.csproj");
        var referenceOutput = scope.GetPath("references.txt");
        var targetsPath = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.targets");

        await File.WriteAllTextAsync(taskProject, "<Project />");
        await File.WriteAllTextAsync(consumerProject, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <SunderPackageProject>false</SunderPackageProject>
                <SunderPackageBuildProject>{{taskProject}}</SunderPackageBuildProject>
                <SunderPackageBuildTaskAssembly>{{Path.Combine(taskDirectory, "bin", "Debug", "net10.0", "Sunder.Package.Build.dll")}}</SunderPackageBuildTaskAssembly>
                <SunderPackageBuildTaskSourceIsImmutable>false</SunderPackageBuildTaskSourceIsImmutable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{{Path.GetRelativePath(scope.RootPath, taskProject)}}" ReferenceOutputAssembly="true" />
              </ItemGroup>
              <Import Project="{{targetsPath}}" />
              <Target Name="WriteReferences">
                <WriteLinesToFile File="$(ReferenceOutput)"
                                  Lines="@(ProjectReference->'%(FullPath)|%(ReferenceOutputAssembly)|%(BuildReference)|%(GlobalPropertiesToRemove)')"
                                  Overwrite="true" />
              </Target>
            </Project>
            """);

        AssertSuccess(await RunMsbuildAsync(
            consumerProject,
            scope.RootPath,
            "WriteReferences",
            $"-p:ReferenceOutput={referenceOutput}",
            "-p:RuntimeIdentifier=win-x64",
            "-p:BuildProjectReferences=false",
            $"-p:BaseOutputPath={scope.GetPath("redirected-bin")}{Path.DirectorySeparatorChar}",
            $"-p:OutDir={scope.GetPath("redirected-out")}{Path.DirectorySeparatorChar}"));

        var reference = Assert.Single(await File.ReadAllLinesAsync(referenceOutput));
        var fields = reference.Split('|', 4);
        Assert.Equal(Path.GetFullPath(taskProject), Path.GetFullPath(fields[0]));
        Assert.Equal("false", fields[1], ignoreCase: true);
        Assert.Equal("false", fields[2], ignoreCase: true);
        var removedProperties = fields[3].Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("TargetFramework", removedProperties);
        Assert.Contains("Platform", removedProperties);
        Assert.Contains("RuntimeIdentifier", removedProperties);
        Assert.Contains("BaseOutputPath", removedProperties);
        Assert.Contains("IntermediateOutputPath", removedProperties);
        Assert.Contains("OutDir", removedProperties);
        Assert.Contains("BuildProjectReferences", removedProperties);
    }

    private static string WriteMutableTaskSource(string result)
        => $$"""
            using System.IO;
            using Microsoft.Build.Framework;
            using Microsoft.Build.Utilities;

            namespace Sunder.Package.Build.Tasks;

            public sealed class ValidateSunderDevOutputPathTask : Task
            {
                [Required]
                public string DevOutputPath { get; set; } = string.Empty;

                public string DiscoveryRoot { get; set; } = string.Empty;
                public string ProjectDirectory { get; set; } = string.Empty;
                public string TargetDirectory { get; set; } = string.Empty;

                public override bool Execute()
                {
                    File.WriteAllText(DevOutputPath, "{{result}}");
                    return true;
                }
            }

            public sealed class CleanSunderRpcGeneratedOutputTask : Task
            {
                public string ProjectDirectory { get; set; } = string.Empty;
                public ITaskItem[] GeneratedDirectories { get; set; } = [];

                public override bool Execute() => true;
            }
            """;

    private static string CopyCurrentTaskSet(TemporaryDirectoryScope scope)
    {
        var source = scope.CreateDirectory("source");
        var currentDirectory = Path.GetDirectoryName(typeof(ValidateSunderDevOutputPathTask).Assembly.Location)!;
        foreach (var fileName in TaskFileNames)
        {
            var current = Path.Combine(currentDirectory, fileName);
            Assert.True(File.Exists(current), $"Test task dependency does not exist: {current}");
            File.Copy(current, Path.Combine(source, fileName));
        }
        return source;
    }

    private static string WriteStagingProject(
        TemporaryDirectoryScope scope,
        string source,
        string shadowRoot,
        string shadowStamp,
        string fileName,
        bool immutable = false)
    {
        var targetDirectory = scope.CreateDirectory("target");
        var targetsPath = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "packages",
            "Sunder.Package.Build",
            "build",
            "Sunder.Package.Build.targets");
        return scope.WriteAllText(fileName, $$"""
            <Project>
              <PropertyGroup>
                <Configuration>Debug</Configuration>
                <TargetFramework>net10.0</TargetFramework>
                <BaseIntermediateOutputPath>obj/</BaseIntermediateOutputPath>
                <TargetDir>{{targetDirectory}}{{Path.DirectorySeparatorChar}}</TargetDir>
                <SunderPackageBuildProject>{{scope.GetPath("missing.csproj")}}</SunderPackageBuildProject>
                <SunderPackageBuildTaskAssembly>{{Path.Combine(source, "Sunder.Package.Build.dll")}}</SunderPackageBuildTaskAssembly>
                <SunderPackageBuildTaskSourceIsImmutable>{{immutable.ToString().ToLowerInvariant()}}</SunderPackageBuildTaskSourceIsImmutable>
                <SunderPackageBuildTaskShadowRoot>{{shadowRoot}}</SunderPackageBuildTaskShadowRoot>
                <SunderPackageBuildTaskShadowStamp>{{shadowStamp}}</SunderPackageBuildTaskShadowStamp>
              </PropertyGroup>
              <Import Project="{{targetsPath}}" />
              <Target Name="Run" DependsOnTargets="_PrepareSunderPackageBuildTasks">
                <ValidateSunderDevOutputPathTask DevOutputPath="{{Path.Combine(targetDirectory, "sunder-dev")}}"
                                                  DiscoveryRoot="{{targetDirectory}}"
                                                  ProjectDirectory="{{scope.RootPath}}"
                                                  TargetDirectory="{{targetDirectory}}" />
              </Target>
            </Project>
            """);
    }

    private static string WriteCrashingPublisherProject(TemporaryDirectoryScope scope, string shadowRoot)
        => scope.WriteAllText("Crash.proj", $$"""
            <Project>
              <UsingTask TaskName="CrashTaskShadowPublisher"
                         TaskFactory="RoslynCodeTaskFactory"
                         AssemblyFile="$([System.IO.Path]::Combine('$(MSBuildToolsPath)', 'Microsoft.Build.Tasks.Core.dll'))">
                <ParameterGroup>
                  <ShadowRoot ParameterType="System.String" Required="true" />
                </ParameterGroup>
                <Task>
                  <Using Namespace="System" />
                  <Using Namespace="System.IO" />
                  <Code Type="Fragment" Language="cs"><![CDATA[
                    Directory.CreateDirectory(ShadowRoot);
                    using (var publishLock = new FileStream(
                        Path.Combine(ShadowRoot, ".sunder-task-set.lock"),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None))
                    {
                        var abandoned = Path.Combine(ShadowRoot, ".sunder-stage-abandoned");
                        Directory.CreateDirectory(abandoned);
                        File.WriteAllText(Path.Combine(abandoned, "Sunder.Package.Build.dll"), "truncated");
                        Environment.FailFast("Simulated task-shadow publisher crash.");
                    }
                    return true;
                  ]]></Code>
                </Task>
              </UsingTask>
              <Target Name="Run">
                <CrashTaskShadowPublisher ShadowRoot="{{shadowRoot}}" />
              </Target>
            </Project>
            """);

    private static void AssertPublishedSet(string shadow, string source)
    {
        Assert.True(Directory.Exists(shadow), $"Task shadow does not exist: {shadow}");
        Assert.Equal(
            TaskFileNames.Append(MarkerFileName).Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(shadow)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateDirectories(shadow));
        foreach (var fileName in TaskFileNames)
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(source, fileName)),
                File.ReadAllBytes(Path.Combine(shadow, fileName)));
        }
        Assert.Matches("^[0-9a-f]{64}$", ReadFingerprint(shadow));
    }

    private static string ReadFingerprint(string shadow)
    {
        var lines = File.ReadAllLines(Path.Combine(shadow, MarkerFileName));
        Assert.Equal(TaskFileNames.Length + 2, lines.Length);
        Assert.Equal("sunder-package-build-task-set-v1", lines[0]);
        Assert.StartsWith("sha256=", lines[1], StringComparison.Ordinal);
        return lines[1]["sha256=".Length..];
    }

    private static void AssertNoTemporaryPublishDirectories(string shadowRoot)
        => Assert.DoesNotContain(
            Directory.EnumerateDirectories(shadowRoot).Select(path => Path.GetFileName(path)!),
            name => name.StartsWith(".sunder-stage-", StringComparison.Ordinal));

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(path))
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task<ProcessResult> RunMsbuildAsync(
        string project,
        string workingDirectory,
        string target,
        params string[] additionalArguments)
    {
        var arguments = new List<string>
        {
            "msbuild",
            project,
            "-t:" + target,
            "-nologo",
            "-nodeReuse:false",
            "-v:minimal",
        };
        arguments.AddRange(additionalArguments);
        return await RunDotnetAsync(workingDirectory, arguments.ToArray());
    }

    private static async Task<ProcessResult> RunDotnetAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            return new ProcessResult(-1, await standardOutput, await standardError + "dotnet timed out.");
        }
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static void AssertSuccess(ProcessResult result)
        => Assert.True(
            result.ExitCode == 0,
            $"dotnet exited with {result.ExitCode}.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name is "bin" or "obj")
            {
                continue;
            }
            CopyDirectory(directory, Path.Combine(destination, name));
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
