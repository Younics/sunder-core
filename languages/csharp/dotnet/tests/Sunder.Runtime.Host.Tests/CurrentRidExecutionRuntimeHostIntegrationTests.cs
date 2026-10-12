using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class CurrentRidDotNetRuntimeHostIntegrationTests(
    OfficialRuntimeFixture executionRuntimes)
    : IClassFixture<OfficialRuntimeFixture>
{
    private const string ProviderPackageId = "test.worker.interop";
    private const string CallerPackageId = "sunder.interop.dotnet.self.caller";
    private const string CallerStateKey = "interop.dotnet-to-dotnet";

    [Fact]
    [Trait("Category", "CurrentRidExecutionRuntime")]
    public async Task OfficialCurrentRidDotNetRuntime_LaunchesThinWorkerAndRebindIsAtomic()
    {
        const string firstRuntimeVersion = "10.0.11";
        const string secondRuntimeVersion = "10.0.12";
        var firstReceipt = await executionRuntimes.EnsureDotNetAsync(firstRuntimeVersion);
        var root = CurrentRidRuntimeHostTestSupport.CreateRoot("dotnet");
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime-host"));
        var installedStore = new InstalledPackageStore(paths);
        var transfers = new RuntimeContentTransferStore(paths);
        var logs = new RecordingLogSink();
        var host = CurrentRidRuntimeHostTestSupport.CreateHost(
            paths,
            installedStore,
            transfers,
            executionRuntimes,
            logs.CreateLogger<RuntimePackageSessionTestHost>());
        string? archivePath = null;
        var initialized = false;
        try
        {
            var packageRoot = CurrentRidRuntimeHostTestSupport.CreatePackageVariant(
                CurrentRidRuntimeHostTestSupport.GetInteropWorkerDevPackage(),
                Path.Combine(root, "installed-provider-source"),
                ProviderPackageId);
            CurrentRidRuntimeHostTestSupport.AssertPackageContainsNoRuntime(packageRoot);
            archivePath = await CanonicalPackageTestBuilder.CreateArchiveAsync(packageRoot);

            await host.InitializeAsync();
            initialized = true;
            var install = await host.InstallPackageFromRuntimePathAsync(archivePath);
            Assert.True(install.Success, string.Join(Environment.NewLine, install.Errors));
            var installed = Assert.Single(await installedStore.ListAsync());
            var firstBinding = Assert.Single(installed.ExecutionRuntimeBindings);
            CurrentRidRuntimeHostTestSupport.AssertBindingMatches(firstBinding, firstReceipt);
            CurrentRidRuntimeHostTestSupport.AssertPackageContainsNoRuntime(installed.InstallPath);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap[ProviderPackageId],
                firstReceipt,
                executionRuntimes);

            var callerRoot = CurrentRidRuntimeHostTestSupport.CreatePackageVariant(
                CurrentRidRuntimeHostTestSupport.GetInteropWorkerDevPackage(),
                Path.Combine(root, "dotnet-caller"),
                CallerPackageId,
                includeProviders: false);
            CurrentRidRuntimeHostTestSupport.AssertPackageContainsNoRuntime(callerRoot);
            await host.GrantRuntimeRpcPermissionsAsync(callerRoot);
            var load = await host.LoadStartupDevPackagesAsync([callerRoot]);
            Assert.True(
                load.Success,
                string.Join(Environment.NewLine, load.Errors) + Environment.NewLine + CurrentRidRuntimeHostTestSupport.FormatLogs(logs));
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap[ProviderPackageId],
                firstReceipt,
                executionRuntimes);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap[CallerPackageId],
                firstReceipt,
                executionRuntimes);
            await CurrentRidRuntimeHostTestSupport.AssertInteropSummaryAsync(
                host,
                CallerPackageId,
                CallerStateKey,
                "native .NET unary",
                ["native .NET stream 1", "native .NET stream 2"]);

            Assert.True(await host.SetPackageStateAsync(ProviderPackageId, "rebind.preserved", "preserve-me"));
            var installedTree = CurrentRidRuntimeHostTestSupport.CaptureFileTree(installed.InstallPath);
            var transferTree = CurrentRidRuntimeHostTestSupport.CaptureFileTreeIfExists(paths.TransferRootPath);
            var sessionBeforeSuccessfulRebind = host.ActiveSession;
            var oldProcessIds = CurrentRidRuntimeHostTestSupport.GetActiveProcessIds(host);
            Assert.True(await host.DeletePackageStateAsync(CallerPackageId, CallerStateKey));

            var secondReceipt = await executionRuntimes.EnsureDotNetAsync(secondRuntimeVersion);
            var desiredBinding = OfficialRuntimeFixture.CreateBinding(secondReceipt);
            var successfulStage = await host.StageExecutionRuntimeRebindAsync(installed, [desiredBinding]);
            Assert.True(successfulStage.Success, string.Join(Environment.NewLine, successfulStage.Errors));
            Assert.Equal(transferTree, CurrentRidRuntimeHostTestSupport.CaptureFileTreeIfExists(paths.TransferRootPath));
            Assert.Same(sessionBeforeSuccessfulRebind, host.ActiveSession);

            var successfulCommit = await host.CommitRegistryPackageStoreStageAsync(
                successfulStage.StageId!,
                static (_, _) => Task.CompletedTask);
            Assert.True(successfulCommit.Success, string.Join(Environment.NewLine, successfulCommit.Errors));
            Assert.True(successfulCommit.RuntimeSessionApplied);
            Assert.NotSame(sessionBeforeSuccessfulRebind, host.ActiveSession);
            await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(oldProcessIds);
            var rebound = Assert.Single(await installedStore.ListAsync());
            Assert.Equal(installed.ContentIdentity, rebound.ContentIdentity);
            Assert.Equal(installed.InstallPath, rebound.InstallPath);
            Assert.Equal(installedTree, CurrentRidRuntimeHostTestSupport.CaptureFileTree(rebound.InstallPath));
            Assert.Equal(transferTree, CurrentRidRuntimeHostTestSupport.CaptureFileTreeIfExists(paths.TransferRootPath));
            Assert.Equal(desiredBinding, Assert.Single(rebound.ExecutionRuntimeBindings));
            Assert.Equal(
                "preserve-me",
                (await host.GetPackageStateAsync(ProviderPackageId, "rebind.preserved"))?.Value);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap[ProviderPackageId],
                secondReceipt,
                executionRuntimes);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap[CallerPackageId],
                secondReceipt,
                executionRuntimes);
            await CurrentRidRuntimeHostTestSupport.AssertInteropSummaryAsync(
                host,
                CallerPackageId,
                CallerStateKey,
                "native .NET unary",
                ["native .NET stream 1", "native .NET stream 2"]);

            var activeAfterSuccessfulRebind = host.ActiveSession;
            var activeProcessIds = CurrentRidRuntimeHostTestSupport.GetActiveProcessIds(host);
            var failedBinding = OfficialRuntimeFixture.CreateBinding(firstReceipt);
            var entryPointPath = CurrentRidRuntimeHostTestSupport.GetInstalledRuntimeEntryPointPath(rebound);
            var originalEntryPointBytes = await File.ReadAllBytesAsync(entryPointPath);
            try
            {
                await File.WriteAllTextAsync(entryPointPath, "not a managed assembly");
                var failedStage = await host.StageExecutionRuntimeRebindAsync(rebound, [failedBinding]);
                Assert.False(failedStage.Success);
                Assert.Null(failedStage.StageId);
            }
            finally
            {
                await File.WriteAllBytesAsync(entryPointPath, originalEntryPointBytes);
            }
            Assert.Same(activeAfterSuccessfulRebind, host.ActiveSession);
            Assert.Equal(activeProcessIds, CurrentRidRuntimeHostTestSupport.GetActiveProcessIds(host));
            Assert.All(activeProcessIds, static processId => Assert.True(
                CurrentRidRuntimeHostTestSupport.IsProcessAlive(processId),
                $"Active worker process {processId} exited during a failed runtime-only rebind."));
            Assert.Equal(
                desiredBinding,
                Assert.Single(Assert.Single(await installedStore.ListAsync()).ExecutionRuntimeBindings));
            Assert.Equal(installedTree, CurrentRidRuntimeHostTestSupport.CaptureFileTree(rebound.InstallPath));
            Assert.Equal(transferTree, CurrentRidRuntimeHostTestSupport.CaptureFileTreeIfExists(paths.TransferRootPath));
            Assert.Equal(
                "preserve-me",
                (await host.GetPackageStateAsync(ProviderPackageId, "rebind.preserved"))?.Value);

            await host.ShutdownAsync();
            initialized = false;
            await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(activeProcessIds);
        }
        finally
        {
            if (initialized)
            {
                await host.ShutdownAsync();
            }
            transfers.Dispose();
            if (archivePath is not null && File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
            CurrentRidRuntimeHostTestSupport.DeleteDirectory(root);
        }
    }
}

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class CurrentRidNodeRuntimeHostIntegrationTests(
    OfficialRuntimeFixture executionRuntimes)
    : IClassFixture<OfficialRuntimeFixture>
{
    [Node24Fact]
    [Trait("Category", "CurrentRidExecutionRuntime")]
    public async Task OfficialCurrentRidNode24Runtime_LaunchesThinWorkerAndRoutesCrossLanguageRpc()
    {
        var node = Node24Availability.Current;
        Assert.True(node.IsAvailable, node.UnavailableReason);
        var nodeReceipt = await executionRuntimes.EnsureNodeAsync("24.21.0");
        var dotnetReceipt = await executionRuntimes.EnsureDotNetAsync("10.0.12");
        var root = CurrentRidRuntimeHostTestSupport.CreateRoot("node24");
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime-host"));
        var installedStore = new InstalledPackageStore(paths);
        var transfers = new RuntimeContentTransferStore(paths);
        var logs = new RecordingLogSink();
        var host = CurrentRidRuntimeHostTestSupport.CreateHost(
            paths,
            installedStore,
            transfers,
            executionRuntimes,
            logs.CreateLogger<RuntimePackageSessionTestHost>());
        var initialized = false;
        var processIds = new List<int>();
        try
        {
            var nodePackage = await CurrentRidRuntimeHostTestSupport.BuildNodeDevPackageAsync(node);
            var nodeProvider = CurrentRidRuntimeHostTestSupport.CreatePackageVariant(
                nodePackage,
                Path.Combine(root, "node-provider"),
                "sunder.node.runtimebacked");
            var nodeCaller = await CurrentRidRuntimeHostTestSupport.CreateNodeCallerAsync(
                node,
                nodePackage,
                Path.Combine(root, "node-caller"));
            var dotnetProvider = CurrentRidRuntimeHostTestSupport.CreatePackageVariant(
                CurrentRidRuntimeHostTestSupport.GetInteropWorkerDevPackage(),
                Path.Combine(root, "dotnet-provider"),
                "test.worker.interop");
            var dotnetCaller = CurrentRidRuntimeHostTestSupport.CreatePackageVariant(
                CurrentRidRuntimeHostTestSupport.GetInteropWorkerDevPackage(),
                Path.Combine(root, "dotnet-caller"),
                "sunder.interop.dotnet.runtimebacked.caller",
                includeProviders: false);
            foreach (var packageRoot in new[] { nodeProvider, nodeCaller, dotnetProvider, dotnetCaller })
            {
                CurrentRidRuntimeHostTestSupport.AssertPackageContainsNoRuntime(packageRoot);
            }

            await host.InitializeAsync();
            initialized = true;
            await host.GrantRuntimeRpcPermissionsAsync(dotnetCaller);
            var load = await host.LoadStartupDevPackagesAsync([nodeProvider, dotnetCaller]);
            Assert.True(
                load.Success,
                string.Join(Environment.NewLine, load.Errors) + Environment.NewLine + CurrentRidRuntimeHostTestSupport.FormatLogs(logs));
            Assert.Equal(2, host.ActiveSession.LoadedPackageMap.Count);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap["sunder.node.runtimebacked"],
                nodeReceipt,
                executionRuntimes);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap["sunder.interop.dotnet.runtimebacked.caller"],
                dotnetReceipt,
                executionRuntimes);
            await CurrentRidRuntimeHostTestSupport.AssertInteropSummaryAsync(
                host,
                "sunder.interop.dotnet.runtimebacked.caller",
                "interop.dotnet-to-runtime-backed-node",
                "runtime-backed Node unary",
                ["runtime-backed Node stream 1", "runtime-backed Node stream 2"]);
            processIds.AddRange(CurrentRidRuntimeHostTestSupport.GetActiveProcessIds(host));

            var retainNodeProvider = await host.LoadStartupDevPackagesAsync([nodeProvider]);
            Assert.True(retainNodeProvider.Success, string.Join(Environment.NewLine, retainNodeProvider.Errors));
            processIds.AddRange(CurrentRidRuntimeHostTestSupport.GetActiveProcessIds(host));
            var unloadNodeProvider = await host.LoadStartupDevPackagesAsync([]);
            Assert.True(unloadNodeProvider.Success, string.Join(Environment.NewLine, unloadNodeProvider.Errors));
            await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(processIds);

            await host.GrantRuntimeRpcPermissionsAsync(nodeCaller);
            logs.Clear();
            load = await host.LoadStartupDevPackagesAsync([dotnetProvider, nodeCaller]);
            Assert.True(
                load.Success,
                string.Join(Environment.NewLine, load.Errors) + Environment.NewLine + CurrentRidRuntimeHostTestSupport.FormatLogs(logs)
                + Environment.NewLine + (File.Exists(Path.Combine(root, "node-caller-error.log"))
                    ? await File.ReadAllTextAsync(Path.Combine(root, "node-caller-error.log")) : string.Empty));
            Assert.Equal(2, host.ActiveSession.LoadedPackageMap.Count);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap["sunder.interop.node.caller"],
                nodeReceipt,
                executionRuntimes);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                host.ActiveSession.LoadedPackageMap["test.worker.interop"],
                dotnetReceipt,
                executionRuntimes);
            await CurrentRidRuntimeHostTestSupport.AssertInteropSummaryAsync(
                host,
                "sunder.interop.node.caller",
                "interop.node-to-dotnet",
                "native .NET unary",
                ["native .NET stream 1", "native .NET stream 2"]);
            processIds.AddRange(CurrentRidRuntimeHostTestSupport.GetActiveProcessIds(host));
        }
        finally
        {
            if (initialized)
            {
                await host.ShutdownAsync();
                await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(processIds);
            }
            transfers.Dispose();
            CurrentRidRuntimeHostTestSupport.DeleteDirectory(root);
        }
    }
}

internal static class CurrentRidRuntimeHostTestSupport
{
    private const string PackageDataValue = "shared Worker V1 package data";

    public static RuntimePackageSessionTestHost CreateHost(
        RuntimePackagePaths paths,
        InstalledPackageStore installedStore,
        RuntimeContentTransferStore transfers,
        OfficialRuntimeFixture executionRuntimes,
        ILogger<RuntimePackageSessionTestHost>? logger = null)
        => new(
            logger ?? NullLogger<RuntimePackageSessionTestHost>.Instance,
            installedStore,
            new SunderPackageArchiveInstaller(paths),
            transferStore: transfers,
            lifecyclePolicy: new RuntimeLifecyclePolicyOptions
            {
                PackageWorkerStartupTimeout = TimeSpan.FromSeconds(45),
                PackageRuntimeGenerationActivationTimeout = TimeSpan.FromSeconds(45),
            },
            executionRuntimeLaunchResolver: executionRuntimes.LaunchResolver,
            localExecutionRuntimeBindingResolver: executionRuntimes.LocalBindingResolver,
             processPolicy: new RuntimeProcessPolicyOptions
             {
                 StartupTimeout = TimeSpan.FromSeconds(30),
                 ShutdownTimeout = TimeSpan.FromSeconds(10),
             },
             projectionCache: new PackageProjectionCache(
                 paths.RootPath,
                 executionRuntimeLeaseRootPath: executionRuntimes.Layout.LeaseRootPath));

    public static string CreateRoot(string runtime)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sunder-current-rid-host-integration",
            runtime,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static string GetCurrentDotNetVersion()
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory()));

    public static string GetInteropWorkerDevPackage()
    {
        var path = Path.Combine(
            WorkerReleaseMetrics.FindRepositoryRoot(),
            "languages", "csharp", "dotnet", "tests", "Sunder.Runtime.Host.InteropWorker",
            "bin", ResolveConfiguration(), "net10.0",
            PackageTargetSelection.GetCurrentRuntimeIdentifier(),
            "sunder-dev");
        Assert.True(Directory.Exists(path), $"Thin .NET Worker V1 package does not exist: {path}");
        return path;
    }

    public static async Task<string> BuildNodeDevPackageAsync(Node24Probe node)
    {
        var nodeRoot = Path.Combine(
            WorkerReleaseMetrics.FindRepositoryRoot(),
            "languages", "typescript", "node");
        var packageTool = Path.Combine(nodeRoot, "packages", "package-tool", "dist", "cli.js");
        if (!File.Exists(packageTool))
        {
            var compiler = Path.Combine(nodeRoot, "node_modules", "typescript", "bin", "tsc");
            Assert.True(
                File.Exists(compiler),
                $"TypeScript workspace dependencies are required for the Node 24 Host lane: {compiler}");
            await RunCheckedAsync(node.ExecutablePath!, [compiler, "-b"], nodeRoot, TimeSpan.FromMinutes(2));
        }
        Assert.True(File.Exists(packageTool), $"Sunder TypeScript package tool was not built: {packageTool}");
        var projectRoot = Path.Combine(nodeRoot, "fixtures", "runtime-backed-worker");
        await RunCheckedAsync(
            node.ExecutablePath!,
            [packageTool, "dev", "--project", projectRoot],
            nodeRoot,
            TimeSpan.FromMinutes(2));
        var output = Path.Combine(projectRoot, "dist", "sunder-dev");
        Assert.True(Directory.Exists(output), $"Thin Node Worker V1 package was not generated: {output}");
        return output;
    }

    public static string CreatePackageVariant(
        string sourceRoot,
        string destinationRoot,
        string packageId,
        bool includeProviders = true,
        bool consumePublicContracts = false)
    {
        CopyDirectory(sourceRoot, destinationRoot);
        var manifestPath = Path.Combine(destinationRoot, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
                       ?? throw new InvalidDataException("Thin Worker V1 package manifest was empty.");
        var originalId = manifest["id"]!.GetValue<string>();
        foreach (var bundle in manifest["contractBundles"]?.AsArray() ?? new JsonArray())
        {
            if (bundle?["source"]?.GetValue<string>() != "owned") continue;
            Assert.Equal(originalId, bundle["authorityId"]!.GetValue<string>());
            bundle["authorityId"] = packageId;
        }
        manifest["id"] = packageId;
        manifest["name"] = packageId;
        if (!includeProviders)
        {
            manifest["provides"] = new JsonArray();
            if (consumePublicContracts)
            {
                // This is an independently permissioned low-level interoperability
                // probe, not an authoring path: providers never grant consumer uses.
                manifest["usesContracts"] = new JsonArray(manifest["contractBundles"]!.AsArray()
                    .Where(bundle => bundle!["exposure"]!.GetValue<string>() == "public")
                    .Select(bundle => (JsonNode)new JsonObject
                    {
                        ["role"] = "runtime",
                        ["contractId"] = bundle!["contractId"]!.DeepClone(),
                        ["contractVersion"] = bundle["version"]!.DeepClone(),
                        ["abiSha256"] = bundle["abiSha256"]!.DeepClone(),
                        ["required"] = true,
                        ["actions"] = new JsonArray("discover", "invoke", "subscribe"),
                    }).ToArray());
            }
        }
        else
        {
            // Provider-only variants do not consume the public API. Required uses
            // belong to the separately permissioned caller variant.
            manifest["usesContracts"] = new JsonArray();
        }
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        // This low-level activation probe changes only the fixture package identity.
        // Retain every source-generated contract projection; missing producer output fails.
        var projectionPath = Path.Combine(destinationRoot, SunderPackageFormat.RpcModelProjectionPath);
        var projection = SunderRpcModelProjectionDocument.Parse(File.ReadAllBytes(projectionPath));
        Assert.Equal(originalId, projection.PackageId);
        File.WriteAllBytes(projectionPath, new SunderRpcModelProjectionDocument(
            projection.SchemaVersion, packageId, projection.PackageVersion, projection.Contracts).ToCanonicalUtf8());
        CanonicalPackageTestBuilder.WriteContentIndex(destinationRoot);
        return destinationRoot;
    }

    public static async Task<string> CreateNodeCallerAsync(Node24Probe node, string sourceRoot, string destinationRoot)
    {
        CreatePackageVariant(sourceRoot, destinationRoot, "sunder.interop.node.caller", includeProviders: false, consumePublicContracts: true);
        var nodeRoot = Path.Combine(WorkerReleaseMetrics.FindRepositoryRoot(), "languages", "typescript", "node");
        var sdk = Path.Combine(nodeRoot, "packages", "sdk", "dist", "index.js");
        var configure = Path.Combine(nodeRoot, "fixtures", "runtime-backed-worker", "src", "worker.ts");
        var entry = Path.Combine(Path.GetDirectoryName(destinationRoot)!, "node-caller-entry.mjs");
        var errorLog = Path.Combine(Path.GetDirectoryName(destinationRoot)!, "node-caller-error.log");
        var output = Path.Combine(destinationRoot, "payload", "runtime", "shared", "bin", "worker.cjs");
        Assert.True(File.Exists(output), "The runtime-backed fixture's shared module layout changed.");
        // The generated provider entry binds Self to its authored package id. Rebranding that
        // binary as a caller must fail. This independently permissioned low-level RPC probe
        // uses its own Worker entry, without a provider's generated Self token or registration.
        await File.WriteAllTextAsync(entry, $$"""
            import { runWorker } from {{JsonSerializer.Serialize(sdk)}};
            import { configure } from {{JsonSerializer.Serialize(configure)}};
            import { writeFile } from "node:fs/promises";
            void runWorker(context => ({ ...configure(context), providers: [] })).catch(async error => {
              await writeFile({{JsonSerializer.Serialize(errorLog)}}, String(error?.stack ?? error).slice(0, 16384));
              process.exitCode = 70;
            });
            """);
        await RunCheckedAsync(node.ExecutablePath!,
            ["--input-type=module", "-e", "import { build } from 'esbuild'; await build({ entryPoints: [process.argv[1]], outfile: process.argv[2], bundle: true, platform: 'node', format: 'cjs', target: 'node24' });", entry, output],
            nodeRoot, TimeSpan.FromMinutes(1));
        CanonicalPackageTestBuilder.WriteContentIndex(destinationRoot);
        return destinationRoot;
    }

    public static void AssertPackageContainsNoRuntime(string packageRoot)
    {
        var forbidden = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(packageRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => string.Equals(Path.GetFileName(path), "dotnet", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Path.GetFileName(path), "node", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Path.GetFileName(path), "node.exe", StringComparison.OrdinalIgnoreCase)
                           || path.Contains("host/fxr/", StringComparison.OrdinalIgnoreCase)
                           || path.Contains("shared/Microsoft.NETCore.App/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(forbidden);
    }

    public static void AssertBindingMatches(
        InstalledPackageExecutionRuntimeBindingRecord actual,
        ExecutionRuntimeReceiptV1 receipt)
    {
        var expected = OfficialRuntimeFixture.CreateBinding(receipt) with
        {
            ResolvedAtUtc = actual.ResolvedAtUtc,
        };
        Assert.Equal(expected, actual);
    }

    public static string GetInstalledRuntimeEntryPointPath(InstalledPackageRecord package)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(package.ManifestPath))?.AsObject()
                       ?? throw new InvalidDataException("Installed package manifest was empty.");
        var targets = manifest["targets"]?.AsArray()
                      ?? throw new InvalidDataException("Installed package manifest has no targets.");
        var target = targets
            .OfType<JsonObject>()
            .Single(item => item["role"]?.GetValue<string>() == SunderPackageFormat.RuntimeHostRole
                && item["rid"]?.GetValue<string>() == PackageTargetSelection.GetCurrentRuntimeIdentifier());
        var entryPoint = target["entryPoint"]?.GetValue<string>()
                         ?? throw new InvalidDataException("Installed runtime target has no entry point.");
        var path = Path.Combine([package.InstallPath, "payload", SunderPackageFormat.RuntimeHostRole,
            PackageTargetSelection.GetCurrentRuntimeIdentifier(), .. entryPoint.Split('/')]);
        Assert.True(File.Exists(path), $"Installed runtime entry point does not exist: {path}");
        return path;
    }

    public static void AssertRuntimeLaunch(
        ActiveLoadedPackage package,
        ExecutionRuntimeReceiptV1 receipt,
        OfficialRuntimeFixture executionRuntimes)
    {
        Assert.Equal(receipt.Identity, package.ExecutionRuntimeBinding?.Identity);
        var worker = Assert.IsType<ProcessRuntimeWorker>(package.Worker);
        var process = WorkerReleaseMetrics.GetProcess(worker);
        Assert.False(process.HasExited);
        var executablePath = process.MainModule?.FileName;
        Assert.False(string.IsNullOrWhiteSpace(executablePath));
        Assert.Equal(
            Path.GetFullPath(executionRuntimes.GetLauncherPath(receipt)),
            Path.GetFullPath(executablePath!));
        Assert.True(
            Path.GetFullPath(executablePath!).StartsWith(
                Path.GetFullPath(executionRuntimes.GetRuntimeRoot(receipt)) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal),
            "Worker process did not launch from the official execution-runtime store.");
        Assert.False(
            Path.GetFullPath(executablePath!).StartsWith(
                Path.GetFullPath(package.Source.SourceFolder) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal),
            "Worker process launched a runtime bundled inside the package.");
    }

    public static async Task AssertInteropSummaryAsync(
        RuntimePackageSessionTestHost host,
        string packageId,
        string stateKey,
        string expectedUnary,
        IReadOnlyList<string> expectedStream)
    {
        var state = await host.GetPackageStateAsync(packageId, stateKey);
        Assert.True(state is { Found: true, Value: not null });
        using var summary = JsonDocument.Parse(state!.Value!);
        Assert.Equal(expectedUnary, summary.RootElement.GetProperty("unary").GetString());
        Assert.Equal(
            expectedStream,
            summary.RootElement.GetProperty("stream").EnumerateArray()
                .Select(static item => item.GetString() ?? throw new InvalidDataException("RPC stream item was null."))
                .ToArray());
        Assert.Equal(PackageDataValue, summary.RootElement.GetProperty("data").GetString());
        Assert.Equal("example.rejected", summary.RootElement.GetProperty("faultCode").GetString());
        var details = summary.RootElement.GetProperty("faultDetails");
        Assert.Equal("conformance rejection", details.GetProperty("reason").GetString());
        Assert.False(details.GetProperty("retryable").GetBoolean());
    }

    public static int[] GetActiveProcessIds(RuntimePackageSessionTestHost host)
        => host.ActiveSession.LoadedPackageMap.Values
            .Select(static package => WorkerReleaseMetrics.GetProcess(
                Assert.IsType<ProcessRuntimeWorker>(package.Worker)).Id)
            .Order()
            .ToArray();

    public static async Task AssertProcessesStoppedAsync(IReadOnlyCollection<int> processIds)
    {
        foreach (var processId in processIds.Distinct())
        {
            var deadline = Stopwatch.StartNew();
            while (IsProcessAlive(processId) && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50);
            }
            Assert.False(IsProcessAlive(processId), $"Worker process {processId} survived lifecycle shutdown.");
        }
    }

    public static async Task AssertDirectoriesRemovedAsync(IReadOnlyCollection<string> paths)
    {
        foreach (var path in paths)
        {
            var deadline = Stopwatch.StartNew();
            while (Directory.Exists(path) && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50);
            }
            Assert.False(Directory.Exists(path), $"Candidate worker directory survived failed cutover: {path}");
        }
    }

    public static bool IsProcessAlive(int processId)
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

    public static IReadOnlyDictionary<string, string> CaptureFileTree(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                static path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> CaptureFileTreeIfExists(string root)
        => Directory.Exists(root)
            ? CaptureFileTree(root)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    public static string FormatLogs(RecordingLogSink sink)
        => string.Join(
            Environment.NewLine,
            sink.Entries.Select(static entry => entry.Exception is null
                ? entry.Message
                : entry.Message + Environment.NewLine + entry.Exception));

    public static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task RunCheckedAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Could not start '{executable}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"'{executable}' did not complete within {timeout}.");
        }
        var output = await standardOutput;
        var error = await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"'{executable}' exited with {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
        }
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
            }
        }
    }

    private static string ResolveConfiguration()
        => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))
            .Parent?.Name
           ?? "Debug";
}
