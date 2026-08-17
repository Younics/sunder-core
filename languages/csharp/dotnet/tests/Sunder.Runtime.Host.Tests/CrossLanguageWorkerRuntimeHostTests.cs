using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class CrossLanguageWorkerRuntimeHostTests
{
    [Fact]
    public async Task DotNetToNode_RealRuntimeHostRunsNativeSeaUnaryStreamDataFaultAndShutdown()
    {
        var configured = GetNodeSeaInputs();
        if (configured is null) return;
        var (archivePath, leafPath) = configured.Value;
        using var corpus = ReadCorpus();
        var interop = corpus.RootElement.GetProperty("interop");
        var nodePackageId = interop.GetProperty("nodeProviderPackageId").GetString()!;
        var dotnetCallerPackageId = interop.GetProperty("dotnetCallerPackageId").GetString()!;
        var root = CreateRoot();
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        var host = CreateHost(paths);
        var workerProcessIds = new List<int>();
        var workerTemporaryPaths = new List<string>();
        try
        {
            await host.InitializeAsync();
            var startup = Stopwatch.StartNew();
            var install = await host.InstallPackageFromRuntimePathAsync(archivePath);
            startup.Stop();
            Assert.True(install.Success, string.Join(Environment.NewLine, install.Errors));
            var installed = Assert.Single(await host.GetInstalledPackagesAsync());
            Assert.Equal(nodePackageId, installed.PackageId);
            var nodeWorker = Assert.IsType<ProcessRuntimeWorker>(
                host.ActiveSession.LoadedPackageMap[nodePackageId].Worker);
            var nodeProcess = WorkerReleaseMetrics.GetProcess(nodeWorker);
            await WorkerReleaseMetrics.RecordAsync(
                "nodeSea",
                PackageTargetSelection.GetCurrentRuntimeIdentifier(),
                archivePath,
                WorkerReleaseMetrics.DirectoryBytes(paths.GetInstalledPackagePath(nodePackageId, installed.Version)),
                startup.Elapsed,
                workerProcessCount: 1,
                nodeProcess,
                Environment.GetEnvironmentVariable("SUNDER_WORKER_RELEASE_METRICS_PATH"));

            var dotnetCaller = CreatePackageVariant(
                GetInteropWorkerDevPackage(),
                Path.Combine(root, "dotnet-caller"),
                dotnetCallerPackageId);
            await host.GrantRuntimeRpcPermissionsAsync(dotnetCaller);
            var load = await host.LoadStartupDevPackagesAsync([dotnetCaller]);
            Assert.True(load.Success, string.Join(Environment.NewLine, load.Errors));
            var state = await host.GetPackageStateAsync(
                dotnetCallerPackageId,
                interop.GetProperty("dotnetToNodeStateKey").GetString()!);
            Assert.True(state is { Found: true, Value: not null });
            using var summary = JsonDocument.Parse(state!.Value!);
            Assert.Equal("native SEA unary", summary.RootElement.GetProperty("unary").GetString());
            Assert.Equal(
                ["native SEA stream 1", "native SEA stream 2"],
                summary.RootElement.GetProperty("stream").EnumerateArray().Select(static item => item.GetString()));
            Assert.Equal(
                corpus.RootElement.GetProperty("packageData").GetProperty("value").GetString(),
                summary.RootElement.GetProperty("data").GetString());
            Assert.Equal("example.rejected", summary.RootElement.GetProperty("faultCode").GetString());
            Assert.True(JsonElement.DeepEquals(
                corpus.RootElement.GetProperty("rpc").GetProperty("domainFault").GetProperty("error").GetProperty("details"),
                summary.RootElement.GetProperty("faultDetails")));
            CaptureWorkers(host, workerProcessIds, workerTemporaryPaths);
        }
        finally
        {
            await host.ShutdownAsync();
            await AssertWorkersStoppedAsync(workerProcessIds, workerTemporaryPaths);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task NodeToDotNet_RealRuntimeHostRoutesRpcToNativeDotNetWorker()
    {
        var configured = GetNodeSeaInputs();
        if (configured is null) return;
        var (_, leafPath) = configured.Value;
        using var corpus = ReadCorpus();
        var interop = corpus.RootElement.GetProperty("interop");
        var nodeCallerPackageId = interop.GetProperty("nodeCallerPackageId").GetString()!;
        var dotnetProviderPackageId = interop.GetProperty("dotnetProviderPackageId").GetString()!;
        var root = CreateRoot();
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        var host = CreateHost(paths);
        var workerProcessIds = new List<int>();
        var workerTemporaryPaths = new List<string>();
        try
        {
            await host.InitializeAsync();
            var nodeCaller = CreatePackageVariant(
                leafPath,
                Path.Combine(root, "node-caller"),
                nodeCallerPackageId);
            var dotnetProvider = GetInteropWorkerDevPackage();
            await host.GrantRuntimeRpcPermissionsAsync(nodeCaller);
            var load = await host.LoadStartupDevPackagesAsync([dotnetProvider, nodeCaller]);
            Assert.True(load.Success, string.Join(Environment.NewLine, load.Errors));
            Assert.Contains(
                host.GetActivePackages(),
                package => package.PackageId == dotnetProviderPackageId);
            var state = await host.GetPackageStateAsync(
                nodeCallerPackageId,
                interop.GetProperty("nodeToDotnetStateKey").GetString()!);
            Assert.True(state is { Found: true, Value: not null });
            using var summary = JsonDocument.Parse(state!.Value!);
            Assert.Equal("native .NET unary", summary.RootElement.GetProperty("unary").GetString());
            Assert.Equal(
                ["native .NET stream 1", "native .NET stream 2"],
                summary.RootElement.GetProperty("stream").EnumerateArray().Select(static item => item.GetString()));
            Assert.Equal(
                corpus.RootElement.GetProperty("packageData").GetProperty("value").GetString(),
                summary.RootElement.GetProperty("data").GetString());
            Assert.Equal("example.rejected", summary.RootElement.GetProperty("faultCode").GetString());
            Assert.True(JsonElement.DeepEquals(
                corpus.RootElement.GetProperty("rpc").GetProperty("domainFault").GetProperty("error").GetProperty("details"),
                summary.RootElement.GetProperty("faultDetails")));
            CaptureWorkers(host, workerProcessIds, workerTemporaryPaths);
            Assert.Equal(2, workerProcessIds.Count);
        }
        finally
        {
            await host.ShutdownAsync();
            await AssertWorkersStoppedAsync(workerProcessIds, workerTemporaryPaths);
            DeleteDirectory(root);
        }
    }

    private static RuntimePackageSessionTestHost CreateHost(RuntimePackagePaths paths)
        => new(
            NullLogger<RuntimePackageSessionTestHost>.Instance,
            new InstalledPackageStore(paths),
            new SunderPackageArchiveInstaller(paths),
            lifecyclePolicy: new RuntimeLifecyclePolicyOptions
            {
                PackageWorkerStartupTimeout = TimeSpan.FromSeconds(30),
                PackageRuntimeGenerationActivationTimeout = TimeSpan.FromSeconds(30),
            });

    private static (string ArchivePath, string LeafPath)? GetNodeSeaInputs()
    {
        var archivePath = Environment.GetEnvironmentVariable("SUNDER_NODE_SEA_PACKAGE_PATH");
        var leafPath = Environment.GetEnvironmentVariable("SUNDER_NODE_SEA_LEAF_PATH");
        if (string.IsNullOrWhiteSpace(archivePath) && string.IsNullOrWhiteSpace(leafPath)) return null;
        Assert.False(string.IsNullOrWhiteSpace(archivePath));
        Assert.False(string.IsNullOrWhiteSpace(leafPath));
        archivePath = Path.GetFullPath(archivePath!);
        leafPath = Path.GetFullPath(leafPath!);
        Assert.True(File.Exists(archivePath), $"Node SEA archive does not exist: {archivePath}");
        Assert.True(Directory.Exists(leafPath), $"Node SEA leaf does not exist: {leafPath}");
        return (archivePath, leafPath);
    }

    private static JsonDocument ReadCorpus()
        => JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "WorkerV3Conformance", "corpus.json")));

    private static string GetInteropWorkerDevPackage()
    {
        var path = Path.Combine(
            WorkerReleaseMetrics.FindRepositoryRoot(),
            "languages", "csharp", "dotnet", "tests", "Sunder.Runtime.Host.InteropWorker",
            "bin", ResolveConfiguration(), "net10.0",
            PackageTargetSelection.GetCurrentRuntimeIdentifier(),
            "sunder-dev");
        Assert.True(Directory.Exists(path), $"Native .NET interop worker package does not exist: {path}");
        return path;
    }

    private static string ResolveConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))
            .Parent?.Name
           ?? "Debug";

    private static string CreatePackageVariant(string sourceRoot, string destinationRoot, string packageId)
    {
        CopyDirectory(sourceRoot, destinationRoot);
        var manifestPath = Path.Combine(destinationRoot, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
                       ?? throw new InvalidDataException("Interop package manifest was empty.");
        manifest["id"] = packageId;
        manifest["name"] = packageId;
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        CanonicalPackageTestBuilder.WriteContentIndex(destinationRoot);
        return destinationRoot;
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
        }
        Directory.CreateDirectory(destinationRoot);
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
    }

    private static void CaptureWorkers(
        RuntimePackageSessionTestHost host,
        ICollection<int> processIds,
        ICollection<string> temporaryPaths)
    {
        foreach (var worker in host.ActiveSession.LoadedPackageMap.Values
                     .Select(static package => package.Worker)
                     .OfType<ProcessRuntimeWorker>())
        {
            processIds.Add(WorkerReleaseMetrics.GetProcess(worker).Id);
            temporaryPaths.Add(worker.WorkerTemporaryPath);
        }
    }

    private static async Task AssertWorkersStoppedAsync(
        IReadOnlyCollection<int> processIds,
        IReadOnlyCollection<string> temporaryPaths)
    {
        foreach (var processId in processIds.Distinct())
        {
            var deadline = Stopwatch.StartNew();
            while (IsProcessAlive(processId) && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50);
            }
            Assert.False(IsProcessAlive(processId), $"Worker process {processId} survived Runtime Host shutdown.");
        }
        Assert.All(temporaryPaths, path => Assert.False(
            Directory.Exists(path),
            $"Worker temporary directory survived Runtime Host shutdown: {path}"));
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string CreateRoot()
        => Path.Combine(Path.GetTempPath(), "sunder-cross-language-worker-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
