using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;
using static Sunder.Package.Build.Tests.PackageManifestFixture;
using static Sunder.Package.Build.Tests.PackageOutputFixture;
using static Sunder.Package.Build.Tests.PackageOutputLockFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageArchiveTests
{
    [Theory]
    [InlineData("runtime")]
    [InlineData("app")]
    public async Task PackPackage_DescriptorFirstRpcRequiresExplicitCompleteSourceInputs(string role)
    {
        var root = CreateTempDirectory();
        var devPackage = CreateRpcLeaf(root, role, "linux-x64", includeProvider: role == "runtime");
        var descriptor = Path.Combine(Path.GetDirectoryName(devPackage)!, "minimal.rpc.json");
        var output = Path.Combine(root, "bin", "descriptor-first.sunderpkg");
        var intermediate = Path.Combine(root, "pack-read-only");
        var missing = CreatePack();
        Assert.False(missing.Execute());
        Assert.Contains(((TestBuildEngine)missing.BuildEngine).Errors, error => error.Contains("Artifact-only RPC packing is disabled", StringComparison.Ordinal));

        var explicitInputs = CreatePack();
        explicitInputs.ContractBundles = [ContractBundle(descriptor)];
        explicitInputs.UsesContracts = [ContractUse(role)];
        explicitInputs.RpcProviders = role == "runtime" ? [RpcProvider(role)] : [];
        Assert.True(explicitInputs.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)explicitInputs.BuildEngine).Errors));
        Assert.False(Directory.Exists(intermediate));
        var prior = await File.ReadAllBytesAsync(output);
        var incomplete = CreatePack();
        incomplete.ContractBundles = [ContractBundle(descriptor)];
        incomplete.RpcProviders = explicitInputs.RpcProviders;
        Assert.False(incomplete.Execute());
        Assert.Contains(((TestBuildEngine)incomplete.BuildEngine).Errors, error => error.Contains(
            role == "app" ? "App contract uses" : "runtime contract uses", StringComparison.Ordinal));
        Assert.Equal(prior, await File.ReadAllBytesAsync(output));
        Assert.False(File.Exists(Path.Combine(root, "sunder-contracts.owned.json")));
        var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(output, Path.Combine(root, "descriptor-validation"));
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));

        PackSunderPackageTask CreatePack() => new()
        {
            BuildEngine = new TestBuildEngine(), ProjectDirectory = root, DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = Path.GetDirectoryName(devPackage)!, PackageOutputPath = output, IntermediateOutputPath = intermediate,
        };
    }

    [Fact]
    public async Task PackPackage_DescriptorSourceIsRecheckedAfterWaitingForBaselineLease()
    {
        var root = CreateTempDirectory();
        var devPackage = CreateRpcLeaf(root, "runtime", "linux-x64");
        var descriptor = Path.Combine(Path.GetDirectoryName(devPackage)!, "minimal.rpc.json");
        var baseline = Path.Combine(root, "sunder-contracts.owned.json");
        var task = new PackSunderPackageTask
        {
            BuildEngine = new TestBuildEngine(), ProjectDirectory = root, DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = Path.GetDirectoryName(devPackage)!, PackageOutputPath = Path.Combine(root, "bin", "descriptor-race.sunderpkg"),
            ContractBundles = [ContractBundle(descriptor)], UsesContracts = [ContractUse("runtime")], RpcProviders = [RpcProvider("runtime")],
        };
        using var lease = GeneratedOutputLock.Acquire([baseline]);
        using var contender = OutputLockContenderBarrier<bool>.Start(baseline, task.Execute, () => descriptor);
        try
        {
            await contender.WaitForAttemptAsync(TimeSpan.FromSeconds(30));
            await contender.AssertContendedAsync();
            File.AppendAllText(descriptor, "\n");
        }
        finally
        {
            lease.Dispose();
            await contender.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.False(await contender.Completion);
        Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error => error.Contains("changed after it was read", StringComparison.Ordinal));
        Assert.False(File.Exists(task.PackageOutputPath));
        Assert.False(File.Exists(baseline));
    }

    [Fact]
    public async Task PackPackage_PreservesIndexedAssetNamedContentIndexJson()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var devPackage = Path.Combine(root, "sunder-dev");
        var manifestEngine = new TestBuildEngine();
        CreateValidDevPackage(devPackage, manifestEngine);
        Directory.CreateDirectory(Path.Combine(devPackage, "payload", "shared", "assets"));
        File.WriteAllText(Path.Combine(devPackage, "payload", "shared", "assets", "content-index.json"), "asset");
        PackageContentIndexer.Write(devPackage);
        var output = Path.Combine(project, "bin", "package.sunderpkg");
        var buildEngine = new TestBuildEngine();

        Assert.True(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));

        using var archive = ZipFile.OpenRead(output);
        Assert.Contains(archive.Entries, entry => entry.FullName == "payload/shared/assets/content-index.json");
        var indexEntry = Assert.Single(archive.Entries, entry => entry.FullName == "manifest/content-index.json");
        using var reader = new StreamReader(indexEntry.Open());
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        Assert.Contains(document.RootElement.GetProperty("files").EnumerateArray(), entry =>
            entry.GetProperty("path").GetString() == "payload/shared/assets/content-index.json");
        Assert.All(
            document.RootElement.GetProperty("files").EnumerateArray(),
            entry => Assert.False(entry.TryGetProperty("role", out _)));

        var validation = await SunderPackageArchiveInspector
            .ExtractAndValidateAsync(output, Path.Combine(root, "validated"));
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public void PackPackage_RejectsContentChangedAfterIndexing()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var devPackage = Path.Combine(root, "sunder-dev");
        CreateValidDevPackage(devPackage, new TestBuildEngine());
        var assemblyPath = Path.Combine(
            devPackage,
            "payload",
            "shared",
            "lib",
            Path.GetFileName(Assembly.GetExecutingAssembly().Location));
        File.AppendAllText(assemblyPath, "tampered-after-signing-and-indexing");
        var output = Path.Combine(project, "bin", "package.sunderpkg");
        var buildEngine = new TestBuildEngine();

        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute());

        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("SHA-256 mismatch", StringComparison.Ordinal));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PackPackage_RecoversAdjacentLockOwnedByDeadProcess()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var devPackage = Path.Combine(root, "sunder-dev");
        var output = Path.Combine(project, "bin", "recovered.sunderpkg");
        CreateValidDevPackage(devPackage, new TestBuildEngine());
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var lockPath = output + ".sunder-output.lock";
        using var deadOwnerMarker = CreateOwnerDirectory(
            lockPath,
            int.MaxValue,
            Environment.MachineName,
            DateTime.UtcNow.AddMinutes(-1));
        var buildEngine = new TestBuildEngine();

        Assert.True(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute(), string.Join(Environment.NewLine, buildEngine.Errors));
        Assert.True(File.Exists(output));
        Assert.False(Directory.Exists(lockPath));
    }

    [Fact]
    public void PackPackage_RejectsPackageThatFailsConsumerFormatValidation()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var devPackage = Path.Combine(root, "sunder-dev");
        Directory.CreateDirectory(Path.Combine(devPackage, "manifest"));
        Directory.CreateDirectory(Path.Combine(devPackage, "payload", "shared", "lib"));
        File.WriteAllText(Path.Combine(devPackage, "manifest", "sunder-package.json"), "{}");
        File.WriteAllText(Path.Combine(devPackage, "payload", "shared", "lib", "Package.dll"), "assembly");
        PackageContentIndexer.Write(devPackage);
        var output = Path.Combine(project, "bin", "package.sunderpkg");
        var buildEngine = new TestBuildEngine();

        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute());

        Assert.Contains(buildEngine.Errors, error =>
            error.Contains("manifestVersion", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void PackPackage_RejectsNonPackageExtensionWithoutOverwritingFile()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var output = Path.Combine(project, "bin", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "keep");
        var buildEngine = new TestBuildEngine();

        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = Path.Combine(root, "missing-dev-package"),
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute());

        Assert.Contains(buildEngine.Errors, error => error.Contains(".sunderpkg", StringComparison.Ordinal));
        Assert.Equal("keep", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("src")]
    [InlineData("Assets")]
    public void PackPackage_RejectsSourceTreeArchiveWithoutOverwritingFile(string sourceDirectory)
    {
        var project = CreateTempDirectory();
        var output = Path.Combine(project, sourceDirectory, "keep.sunderpkg");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "keep");
        var buildEngine = new TestBuildEngine();

        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = buildEngine,
            DevPackagePath = Path.Combine(project, "missing-dev-package"),
            DevPackageDiscoveryRoot = project,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute());

        Assert.Contains(buildEngine.Errors, error => error.Contains("source", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("keep", File.ReadAllText(output));
    }

    [Fact]
    public async Task PackTask_PreservesPriorValidatedArchiveWhenReplacementFails()
    {
        var root = CreateTempDirectory();
        var project = Path.Combine(root, "project");
        var devPackage = Path.Combine(root, "sunder-dev");
        var output = Path.Combine(project, "bin", "preserved.sunderpkg");
        var firstEngine = new TestBuildEngine();
        CreateValidDevPackage(devPackage, firstEngine);
        var first = new PackSunderPackageTask
        {
            BuildEngine = firstEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        };
        Assert.True(first.Execute(), string.Join(Environment.NewLine, firstEngine.Errors));
        var priorBytes = await File.ReadAllBytesAsync(output);

        File.WriteAllText(Path.Combine(devPackage, "manifest", "sunder-package.json"), "{}");
        var secondEngine = new TestBuildEngine();
        Assert.False(new PackSunderPackageTask
        {
            BuildEngine = secondEngine,
            DevPackagePath = devPackage,
            DevPackageDiscoveryRoot = root,
            PackageOutputPath = output,
            ProjectDirectory = project,
        }.Execute());

        Assert.Equal(priorBytes, await File.ReadAllBytesAsync(output));
        var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            output,
            Path.Combine(root, "preserved-validation"));
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
    }

    private static void CreateValidDevPackage(string devPackage, TestBuildEngine buildEngine)
    {
        Directory.CreateDirectory(Path.Combine(devPackage, "manifest"));
        Directory.CreateDirectory(Path.Combine(devPackage, "payload", "shared", "lib"));
        var manifestPath = Path.Combine(devPackage, "manifest", "sunder-package.json");
        var inputs = devPackage + "-inputs";
        Directory.CreateDirectory(inputs);
        var generation = CreateTask(Path.Combine(inputs, "sunder-package.json"), buildEngine);
        Assert.True(
            generation.Execute(),
            string.Join(Environment.NewLine, buildEngine.Errors));
        File.Copy(generation.ManifestOutputPath, manifestPath);
        foreach (var file in generation.ResolvedContractFiles)
        {
            var destination = Path.Combine(devPackage, "payload", "shared", file.GetMetadata("DescriptorPath"));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.ItemSpec, destination);
        }
        File.Copy(
            Assembly.GetExecutingAssembly().Location,
            Path.Combine(devPackage, "payload", "shared", "lib", Path.GetFileName(Assembly.GetExecutingAssembly().Location)));
        PackageContentIndexer.Write(devPackage);
    }
}
