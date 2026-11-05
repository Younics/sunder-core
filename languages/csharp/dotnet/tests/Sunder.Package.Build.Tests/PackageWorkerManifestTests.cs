using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;
using Xunit;
using MSBuildTaskItem = Microsoft.Build.Utilities.TaskItem;
using static Sunder.Package.Build.Tests.PackageManifestFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageWorkerManifestTests
{
    [Fact]
    public void GenerateManifest_AuthorsOneExactFrameworkDependentWorkerTarget()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var target = new MSBuildTaskItem("runtime/linux-arm64");
        target.SetMetadata("Kind", SunderPackageFormat.WorkerTargetKind);
        task.PackageTargets = [target];
        task.RuntimeIdentifier = "linux-arm64";
        task.OutputType = "Exe";
        task.SelfContained = false;
        task.UseAppHost = false;
        task.PublishSingleFile = false;
        task.PublishTrimmed = false;
        task.SdkCapabilities = [new MSBuildTaskItem("worker-protocol.v1")];

        Assert.True(task.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.True(task.HasWorkerTarget);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var generated = Assert.Single(document.RootElement.GetProperty("targets").EnumerateArray());
        Assert.Equal(SunderPackageFormat.WorkerTargetKind, generated.GetProperty("kind").GetString());
        Assert.Equal("lib/ManifestPackage.dll", generated.GetProperty("entryPoint").GetString());
        Assert.Equal("ManifestPackage.dll", task.WorkerEntryPointRelativePath);
    }

    [Fact]
    public void GenerateManifest_RejectsCustomCSharpWorkerModuleName()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var target = new MSBuildTaskItem("runtime/linux-x64");
        target.SetMetadata("EntryPoint", "lib/custom-worker");
        ConfigureWorkerTask(task, "linux-x64", target);

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("official managed module 'lib/ManifestPackage.dll'", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsOldWorkerV3Capability()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        ConfigureWorkerTask(task, "linux-x64");
        task.SdkCapabilities =
        [
            new MSBuildTaskItem("worker-protocol.v1"),
            new MSBuildTaskItem("worker-protocol.v3"),
        ];

        Assert.False(task.Execute());
        Assert.Contains(
            buildEngine.Errors,
            error => error.Contains(
                "cannot use unsupported worker protocol capability 'worker-protocol.v3'",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SelfContained")]
    [InlineData("UseAppHost")]
    [InlineData("PublishSingleFile")]
    [InlineData("PublishTrimmed")]
    public void GenerateManifest_RejectsUnsupportedWorkerPublishShape(string property)
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        ConfigureWorkerTask(task, "linux-x64");
        switch (property)
        {
            case "SelfContained": task.SelfContained = true; break;
            case "UseAppHost": task.UseAppHost = true; break;
            case "PublishSingleFile": task.PublishSingleFile = true; break;
            case "PublishTrimmed": task.PublishTrimmed = true; break;
        }

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error => error.Contains(property, StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateManifest_RejectsFreshLegacyProcessTargetWithWorkerGuidance()
    {
        var manifestPath = Path.Combine(CreateTempDirectory(), "sunder-package.json");
        var buildEngine = new TestBuildEngine();
        var task = CreateTask(manifestPath, buildEngine);
        var target = new MSBuildTaskItem("runtime/linux-x64");
        target.SetMetadata("Kind", "process");
        task.PackageTargets = [target];

        Assert.False(task.Execute());
        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("unsupported Runtime target kind 'process'", StringComparison.Ordinal)
            && error.Contains("must use 'worker'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerV1ExecutableFixture_InfersExactProtocolCapability()
    {
        var root = Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "tests",
            "Sunder.Package.Build.TestFixtures",
            "WorkerV1Executable",
            "bin",
            ResolveConfiguration(),
            "net10.0",
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            "sunder-dev");

        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(root);

        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var target = Assert.Single(validation.Manifest!.Targets!)!;
        Assert.Equal(SunderPackageFormat.WorkerTargetKind, target.Kind);
        Assert.Equal(
            [
                SunderSdkCapabilities.AuthV1,
                SunderSdkCapabilities.BackgroundServicesV1,
                SunderSdkCapabilities.CallbacksV1,
                SunderSdkCapabilities.ContributionsV1,
                SunderSdkCapabilities.CoreV1,
                SunderPackageFormat.ExecutionRuntimeV1Capability,
                SunderSdkCapabilities.LoggingV1,
                SunderSdkCapabilities.PackagingV1,
                SunderSdkCapabilities.RpcV1,
                SunderSdkCapabilities.RuntimeGenerationsV1,
                SunderSdkCapabilities.SecretsV1,
                SunderSdkCapabilities.SettingsSchemaV1,
                SunderSdkCapabilities.SettingsV1,
                SunderSdkCapabilities.StorageKeyMigrationV1,
                SunderSdkCapabilities.StorageV1,
                "worker-protocol.v1",
            ],
            target.RequiredHostCapabilities);
        const string moduleName = "WorkerV1Executable.dll";
        Assert.Equal($"lib/{moduleName}", target.EntryPoint);
        Assert.True(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", moduleName)));
        Assert.True(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", "WorkerV1Executable.runtimeconfig.json")));
        Assert.True(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", "WorkerV1Executable.deps.json")));
        Assert.False(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", "WorkerV1Executable")));
        Assert.False(File.Exists(Path.Combine(root, "payload", "runtime", target.Rid!, "lib", "WorkerV1Executable.exe")));
        Assert.False(Directory.EnumerateFiles(
            Path.Combine(root, "payload", "runtime", target.Rid!, "lib"),
            "*.pdb",
            SearchOption.AllDirectories).Any());
    }
}
