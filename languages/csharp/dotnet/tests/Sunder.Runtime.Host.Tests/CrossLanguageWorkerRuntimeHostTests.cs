using System.Diagnostics;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class CrossLanguageWorkerRuntimeHostTests(
    SignedCurrentRidExecutionRuntimeFixture executionRuntimes)
    : IClassFixture<SignedCurrentRidExecutionRuntimeFixture>
{
    [ExternalArtifactFact(
        "SUNDER_REQUIRE_NODE_MODULE_INTEROP",
        "SUNDER_NODE_MODULE_PACKAGE_PATH",
        "SUNDER_NODE_MODULE_LEAF_PATH")]
    [Trait("Category", "ExternalArtifact")]
    public async Task DotNetToNode_RealRuntimeHostRunsRuntimeBackedModuleUnaryStreamDataFaultAndShutdown()
    {
        var configured = GetNodeModuleInputs();
        Assert.NotNull(configured);
        var (archivePath, _) = configured.Value;
        var node = Node24Availability.Current;
        Assert.True(node.IsAvailable, node.UnavailableReason);
        var nodeReceipt = await executionRuntimes.EnsureNodeAsync(
            node.Version!,
            packagingRevision: 1,
            node.ExecutablePath!);
        await executionRuntimes.ObserveSupportedAsync(nodeReceipt);
        var dotnetReceipt = await executionRuntimes.EnsureDotNetAsync(
            CurrentRidRuntimeHostTestSupport.GetCurrentDotNetVersion(),
            packagingRevision: 1);
        await executionRuntimes.ObserveSupportedAsync(dotnetReceipt);
        using var corpus = ReadCorpus();
        var interop = corpus.RootElement.GetProperty("interop");
        var nodePackageId = interop.GetProperty("nodeProviderPackageId").GetString()!;
        var dotnetCallerPackageId = interop.GetProperty("dotnetCallerPackageId").GetString()!;
        var root = CurrentRidRuntimeHostTestSupport.CreateRoot("cross-language-dotnet-to-node");
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        var transfers = new RuntimeContentTransferStore(paths);
        var logs = new RecordingLogSink();
        var host = CurrentRidRuntimeHostTestSupport.CreateHost(
            paths,
            new InstalledPackageStore(paths),
            transfers,
            executionRuntimes,
            logs.CreateLogger<RuntimePackageSessionTestHost>());
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
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap[nodePackageId],
                nodeReceipt,
                executionRuntimes);
            var nodeProcess = WorkerReleaseMetrics.GetProcess(nodeWorker);
            await WorkerReleaseMetrics.RecordAsync(
                "nodeModule",
                PackageTargetSelection.GetCurrentRuntimeIdentifier(),
                archivePath,
                WorkerReleaseMetrics.DirectoryBytes(paths.GetInstalledPackagePath(nodePackageId, installed.Version)),
                startup.Elapsed,
                workerProcessCount: 1,
                nodeProcess,
                Environment.GetEnvironmentVariable("SUNDER_WORKER_RELEASE_METRICS_PATH"));

            var dotnetCaller = CurrentRidRuntimeHostTestSupport.CreatePackageVariant(
                GetInteropWorkerDevPackage(),
                Path.Combine(root, "dotnet-caller"),
                dotnetCallerPackageId,
                includeProviders: false);
            await host.GrantRuntimeRpcPermissionsAsync(dotnetCaller);
            var load = await host.LoadStartupDevPackagesAsync([dotnetCaller]);
            Assert.True(
                load.Success,
                string.Join(Environment.NewLine, load.Errors) + Environment.NewLine
                + CurrentRidRuntimeHostTestSupport.FormatLogs(logs));
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap[dotnetCallerPackageId],
                dotnetReceipt,
                executionRuntimes);
            var state = await host.GetPackageStateAsync(
                dotnetCallerPackageId,
                interop.GetProperty("dotnetToNodeStateKey").GetString()!);
            Assert.True(state is { Found: true, Value: not null });
            using var summary = JsonDocument.Parse(state!.Value!);
            Assert.Equal("runtime-backed Node unary", summary.RootElement.GetProperty("unary").GetString());
            Assert.Equal(
                ["runtime-backed Node stream 1", "runtime-backed Node stream 2"],
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
            transfers.Dispose();
            CurrentRidRuntimeHostTestSupport.DeleteDirectory(root);
        }
    }

    [ExternalArtifactFact(
        "SUNDER_REQUIRE_NODE_MODULE_INTEROP",
        "SUNDER_NODE_MODULE_PACKAGE_PATH",
        "SUNDER_NODE_MODULE_LEAF_PATH")]
    [Trait("Category", "ExternalArtifact")]
    public async Task NodeToDotNet_RealRuntimeHostRoutesRpcToNativeDotNetWorker()
    {
        var configured = GetNodeModuleInputs();
        Assert.NotNull(configured);
        var (_, leafPath) = configured.Value;
        var node = Node24Availability.Current;
        Assert.True(node.IsAvailable, node.UnavailableReason);
        var nodeReceipt = await executionRuntimes.EnsureNodeAsync(
            node.Version!,
            packagingRevision: 1,
            node.ExecutablePath!);
        await executionRuntimes.ObserveSupportedAsync(nodeReceipt);
        var dotnetReceipt = await executionRuntimes.EnsureDotNetAsync(
            CurrentRidRuntimeHostTestSupport.GetCurrentDotNetVersion(),
            packagingRevision: 1);
        await executionRuntimes.ObserveSupportedAsync(dotnetReceipt);
        using var corpus = ReadCorpus();
        var interop = corpus.RootElement.GetProperty("interop");
        var nodeCallerPackageId = interop.GetProperty("nodeCallerPackageId").GetString()!;
        var dotnetProviderPackageId = interop.GetProperty("dotnetProviderPackageId").GetString()!;
        var root = CurrentRidRuntimeHostTestSupport.CreateRoot("cross-language-node-to-dotnet");
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        var transfers = new RuntimeContentTransferStore(paths);
        var logs = new RecordingLogSink();
        var host = CurrentRidRuntimeHostTestSupport.CreateHost(
            paths,
            new InstalledPackageStore(paths),
            transfers,
            executionRuntimes,
            logs.CreateLogger<RuntimePackageSessionTestHost>());
        var workerProcessIds = new List<int>();
        var workerTemporaryPaths = new List<string>();
        try
        {
            await host.InitializeAsync();
            var nodeCaller = CurrentRidRuntimeHostTestSupport.CreatePackageVariant(
                leafPath,
                Path.Combine(root, "node-caller"),
                nodeCallerPackageId,
                includeProviders: false,
                consumePublicContracts: true);
            var dotnetProvider = GetInteropWorkerDevPackage();
            await host.GrantRuntimeRpcPermissionsAsync(nodeCaller);
            var load = await host.LoadStartupDevPackagesAsync([dotnetProvider, nodeCaller]);
            Assert.True(
                load.Success,
                string.Join(Environment.NewLine, load.Errors) + Environment.NewLine
                + CurrentRidRuntimeHostTestSupport.FormatLogs(logs));
            Assert.Contains(
                host.GetActivePackages(),
                package => package.PackageId == dotnetProviderPackageId);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap[nodeCallerPackageId],
                nodeReceipt,
                executionRuntimes);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap[dotnetProviderPackageId],
                dotnetReceipt,
                executionRuntimes);
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
            transfers.Dispose();
            CurrentRidRuntimeHostTestSupport.DeleteDirectory(root);
        }
    }

    private static (string ArchivePath, string LeafPath)? GetNodeModuleInputs()
    {
        var archivePath = Environment.GetEnvironmentVariable("SUNDER_NODE_MODULE_PACKAGE_PATH");
        var leafPath = Environment.GetEnvironmentVariable("SUNDER_NODE_MODULE_LEAF_PATH");
        if (string.IsNullOrWhiteSpace(archivePath) && string.IsNullOrWhiteSpace(leafPath)) return null;
        Assert.False(string.IsNullOrWhiteSpace(archivePath));
        Assert.False(string.IsNullOrWhiteSpace(leafPath));
        archivePath = Path.GetFullPath(archivePath!);
        leafPath = Path.GetFullPath(leafPath!);
        Assert.True(File.Exists(archivePath), $"Node module archive does not exist: {archivePath}");
        Assert.True(Directory.Exists(leafPath), $"Node module leaf does not exist: {leafPath}");
        return (archivePath, leafPath);
    }

    private static JsonDocument ReadCorpus()
        => JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "WorkerV1Conformance", "corpus.json")));

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

}
