using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed partial class OfficialProcessRuntimeTests(
    OfficialRuntimeFixture fixture)
    : IClassFixture<OfficialRuntimeFixture>
{
    [Fact]
    [Trait("Capability", "Process")]
    public async Task OfficialNodeProbe_RejectsPreloadEnvironmentBeforeNativeExecution()
    {
        using var temp = TemporaryDirectoryScope.Create("official-node-probe-preload");
        var marker = Path.Combine(temp.RootPath, "preload-executed");
        var preload = Path.Combine(temp.RootPath, "preload.cjs");
        await File.WriteAllTextAsync(preload, $"require('node:fs').writeFileSync({JsonSerializer.Serialize(marker)}, 'injected');");
        var start = new ProcessStartInfo(fixture.GetLauncherPath(fixture.NodeReceipt))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("--eval");
        start.ArgumentList.Add("process.stdout.write(process.version)");
        start.Environment["NODE_OPTIONS"] = $"--require \"{preload}\"";
        start.Environment["Node_Options"] = $"--require \"{preload}\"";
        start.Environment["openssl_conf"] = preload;
        OfficialRuntimePlatformProbe.ConfigureEnvironment(start.Environment, fixture.GetRuntimeRoot(fixture.NodeReceipt), WorkerProcessEnvironment.GetCurrentPlatform());
        using var process = new Process { StartInfo = start };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.True(process.Start());
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("v" + fixture.NodeReceipt.Identity.VendorVersion, await output);
            Assert.Equal("", await error);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    [Trait("Capability", "Process")]
    public async Task FixedRecipes_RunFromOfficialRootsWithoutAmbientLookupOrShell()
    {
        using var ambient = new AmbientRuntimePoison(fixture.RootPath);
        foreach (var kind in new[] { SyntheticRuntimeKind.DotNet, SyntheticRuntimeKind.Node })
        {
            var package = fixture.CreatePackage(
                kind,
                kind == SyntheticRuntimeKind.DotNet ? "official.dotnet.recipe" : "official.node.recipe",
                "v1-environment");
            var activated = await fixture.CreateWorkerAsync(package);
            try
            {
                var startInfo = activated.Worker.CreateStartInfo();
                var receipt = await fixture.Store.GetReceiptAsync(fixture.GetReceipt(kind).Identity)
                              ?? throw new InvalidDataException(
                                  "The installed synthetic execution-runtime receipt is missing.");
                Assert.False(startInfo.UseShellExecute);
                Assert.True(Path.IsPathFullyQualified(startInfo.FileName));
                Assert.Equal(fixture.Layout.GetContentRootPath(receipt.ContentIdentity), fixture.GetRuntimeRoot(receipt));
                Assert.Equal(fixture.GetLauncherPath(receipt), startInfo.FileName);
                Assert.Equal(receipt.LauncherSize, new FileInfo(startInfo.FileName).Length);
                Assert.Equal(receipt.LauncherSha256, await HashFileAsync(startInfo.FileName));
                ExecutionRuntimeRootFileSystem.VerifySealed(
                    fixture.GetRuntimeRoot(receipt),
                    fixture.GetLauncherPath(receipt));
                Assert.NotEqual(Path.GetFullPath(package.EntryAssemblyPath!), startInfo.FileName);
                Assert.Equal(
                    kind == SyntheticRuntimeKind.DotNet
                        ?
                        [
                            "exec",
                            Path.GetFullPath(package.EntryAssemblyPath!),
                        ]
                        : [Path.GetFullPath(package.EntryAssemblyPath!)],
                    startInfo.ArgumentList);
                Assert.DoesNotContain("PATH", startInfo.Environment.Keys);
                Assert.DoesNotContain("DOTNET_ROOT", startInfo.Environment.Keys);
                Assert.DoesNotContain("DOTNET_ROOT_X64", startInfo.Environment.Keys);
                Assert.DoesNotContain("DOTNET_ROOT_X86", startInfo.Environment.Keys);
                Assert.DoesNotContain("DOTNET_ROOT_ARM64", startInfo.Environment.Keys);
                Assert.DoesNotContain("NVM_BIN", startInfo.Environment.Keys);
                Assert.DoesNotContain("NVM_DIR", startInfo.Environment.Keys);
                Assert.DoesNotContain("NODE_PATH", startInfo.Environment.Keys);
                Assert.DoesNotContain(AmbientRuntimePoison.SentinelName, startInfo.Environment.Keys);
                Assert.Equal("1", startInfo.Environment["SUNDER_WORKER_OPERATIONAL_OUTCOMES_V1"]);
                Assert.Equal(
                    ExpectedWorkerEnvironmentNames(),
                    startInfo.Environment.Keys
                        .Select(NormalizeEnvironmentName)
                        .Order(StringComparer.Ordinal));

                await ActivateAsync(activated);
                using var status = await WaitForWorkerStatusAsync(
                    activated.Worker,
                    "v1-environment.json");
                var statusRoot = status.RootElement;
                Assert.Equal(
                    ExpectedWorkerEnvironmentNames(),
                    statusRoot.GetProperty("environmentNames")
                        .EnumerateArray()
                        .Select(static item => NormalizeEnvironmentName(item.GetString()!))
                        .Order(StringComparer.Ordinal));
                Assert.Equal(Path.GetFullPath(package.ShadowFolder), statusRoot.GetProperty("contentRoot").GetString());
                Assert.Equal(activated.Worker.WorkerTemporaryPath, statusRoot.GetProperty("temporaryRoot").GetString());
                var packageRoot = LocalPackageStorageContext.GetPackageRootPath(
                    package.PackageId,
                    fixture.Paths.PackageDataRootPath);
                Assert.Equal(
                    Path.Combine(packageRoot, "workspace"),
                    statusRoot.GetProperty("workspaceRoot").GetString());
                Assert.Equal(activated.Worker.WorkspaceRootPath, statusRoot.GetProperty("workspaceRoot").GetString());
                Assert.True(Directory.Exists(Path.Combine(packageRoot, "data")));
                Assert.Equal(JsonValueKind.Null, statusRoot.GetProperty("workspaceEnvironment").ValueKind);
                Assert.Equal(JsonValueKind.Null, statusRoot.GetProperty("legacyDataPath").ValueKind);
                Assert.False(WorkerReleaseMetrics.GetProcess(activated.Worker).HasExited);
                var lease = Assert.Single(await fixture.ReadLeasesAsync());
                Assert.True(lease.HasContainment);
                Assert.True(lease.IsAttached);
                Assert.Equal(receipt.ContentIdentity, lease.ContentIdentity);
                Assert.Equal(package.PackageId, lease.PackageId);

                await activated.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await activated.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await activated.Worker.DisposeAsync();
            }
            Assert.Empty(await fixture.ReadLeasesAsync());
        }
    }

    [Fact]
    public async Task OfficialStore_IsolatesVersionsAndAlternateRidWhileLaunchingOnlyCurrentRid()
    {
        Assert.Equal(
            fixture.DotNetReceipt.Identity.RuntimeId,
            fixture.AlternateDotNetReceipt.Identity.RuntimeId);
        Assert.NotEqual(fixture.DotNetReceipt.Identity, fixture.AlternateDotNetReceipt.Identity);
        Assert.NotEqual(fixture.DotNetReceipt.ContentIdentity, fixture.AlternateDotNetReceipt.ContentIdentity);
        Assert.NotEqual(fixture.DotNetReceipt.Identity, fixture.NodeReceipt.Identity);
        Assert.NotEqual(fixture.DotNetReceipt.ContentIdentity, fixture.NodeReceipt.ContentIdentity);
        Assert.NotEqual(
            fixture.GetRuntimeRoot(fixture.DotNetReceipt),
            fixture.GetRuntimeRoot(fixture.AlternateDotNetReceipt));
        Assert.NotEqual(
            fixture.GetRuntimeRoot(fixture.DotNetReceipt),
            fixture.GetRuntimeRoot(fixture.NodeReceipt));
        Assert.Equal(2, new[]
        {
            fixture.DotNetReceipt,
            fixture.AlternateDotNetReceipt,
        }.Select(receipt => fixture.Layout.GetReceiptPath(receipt.Identity)).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, new[]
        {
            fixture.DotNetReceipt,
            fixture.AlternateDotNetReceipt,
        }.Select(fixture.GetRuntimeRoot).Distinct(StringComparer.Ordinal).Count());
        Assert.True(File.Exists(fixture.GetLauncherPath(fixture.DotNetReceipt)));
        Assert.True(File.Exists(fixture.GetLauncherPath(fixture.AlternateDotNetReceipt)));
        Assert.True(File.Exists(fixture.GetLauncherPath(fixture.NodeReceipt)));
        Assert.Equal(
            fixture.DotNetReceipt.ContentIdentity,
            (await fixture.Store.GetReceiptAsync(fixture.DotNetReceipt.Identity))?.ContentIdentity);
        Assert.Equal(
            fixture.AlternateDotNetReceipt.ContentIdentity,
            (await fixture.Store.GetReceiptAsync(fixture.AlternateDotNetReceipt.Identity))?.ContentIdentity);
        Assert.Equal(
            fixture.NodeReceipt.ContentIdentity,
            (await fixture.Store.GetReceiptAsync(fixture.NodeReceipt.Identity))?.ContentIdentity);

        var dotNetPackage = fixture.CreatePackage(
            SyntheticRuntimeKind.DotNet,
            "official.sidebyside.dotnet",
            "normal");
        var alternatePackage = fixture.CreatePackage(
            SyntheticRuntimeKind.DotNet,
            "official.sidebyside.alternate",
            "normal",
            fixture.AlternateDotNetReceipt);
        var dotNetLaunch = await fixture.Resolver.ResolveAsync(
            dotNetPackage,
            Guid.NewGuid(),
            CancellationToken.None);
        var alternateLaunch = await fixture.Resolver.ResolveAsync(
            alternatePackage,
            Guid.NewGuid(),
            CancellationToken.None);
        try
        {
            Assert.Equal(fixture.DotNetReceipt.Identity, dotNetLaunch.Identity);
            Assert.Equal(fixture.AlternateDotNetReceipt.Identity, alternateLaunch.Identity);
            Assert.NotEqual(dotNetLaunch.RuntimeRootPath, alternateLaunch.RuntimeRootPath);
            Assert.NotEqual(dotNetLaunch.LauncherPath, alternateLaunch.LauncherPath);
            var leases = await fixture.ReadLeasesAsync();
            Assert.Equal(2, leases.Count);
        }
        finally
        {
            await dotNetLaunch.DisposeAsync();
            await alternateLaunch.DisposeAsync();
        }
        Assert.Empty(await fixture.ReadLeasesAsync());
        var mismatchedIdentity = fixture.DotNetReceipt.Identity with { Rid = MismatchedRuntimeIdentifier(fixture.DotNetReceipt.Identity.Rid) };
        Assert.Null(await fixture.Store.GetReceiptAsync(mismatchedIdentity));
        var mismatchedPackage = fixture.CreatePackage(SyntheticRuntimeKind.DotNet, "official.mismatched-rid", "normal");
        mismatchedPackage = mismatchedPackage with
        {
            ExecutionRuntimeBinding = mismatchedPackage.ExecutionRuntimeBinding! with
            {
                Runtime = mismatchedIdentity.ToContract(),
            },
        };
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Resolver.ResolveAsync(mismatchedPackage, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    [Trait("Capability", "Process")]
    public async Task MissingManagedEntryPoint_FailsWithoutAmbientRuntimeFallback()
    {
        var package = fixture.CreatePackage(SyntheticRuntimeKind.DotNet, "official.missing.entry", "normal");
        File.Delete(package.EntryAssemblyPath!);
        using var ambient = new AmbientRuntimePoison(fixture.RootPath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Resolver.ResolveAsync(package, Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(await fixture.ReadLeasesAsync());
    }

    [Fact]
    [Trait("Capability", "Process")]
    public async Task SharedOfficialRoot_UsesIndependentProcessesAndDurableLeases()
    {
        var firstPackage = fixture.CreatePackage(
            SyntheticRuntimeKind.DotNet,
            "official.shared.first",
            "normal");
        var secondPackage = fixture.CreatePackage(
            SyntheticRuntimeKind.DotNet,
            "official.shared.second",
            "normal");
        var first = await fixture.CreateWorkerAsync(firstPackage);
        var second = await fixture.CreateWorkerAsync(secondPackage);
        try
        {
            Assert.Equal(
                first.Worker.CreateStartInfo().FileName,
                second.Worker.CreateStartInfo().FileName);
            Assert.Equal(2, (await fixture.ReadLeasesAsync()).Count);

            await Task.WhenAll(ActivateAsync(first), ActivateAsync(second));
            var firstProcess = WorkerReleaseMetrics.GetProcess(first.Worker);
            var secondProcess = WorkerReleaseMetrics.GetProcess(second.Worker);
            Assert.NotEqual(firstProcess.Id, secondProcess.Id);
            var leases = await fixture.ReadLeasesAsync();
            Assert.Equal(2, leases.Count);
            Assert.All(leases, lease =>
            {
                Assert.True(lease.HasContainment);
                Assert.True(lease.IsAttached);
                Assert.Equal(fixture.DotNetReceipt.ContentIdentity, lease.ContentIdentity);
            });
            Assert.Equal(
                [firstPackage.PackageId, secondPackage.PackageId],
                leases.Select(static lease => lease.PackageId).Order(StringComparer.Ordinal));
            Assert.Equal(
                new[] { firstProcess.Id, secondProcess.Id }.Order(),
                leases.Select(static lease => lease.ProcessId!.Value).Order());

            await first.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await first.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            secondProcess.Refresh();
            Assert.False(secondProcess.HasExited);
            var remaining = Assert.Single(await fixture.ReadLeasesAsync());
            Assert.Equal(secondPackage.PackageId, remaining.PackageId);

            await second.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await second.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await DisposeIgnoringWorkerFaultAsync(first.Worker);
            await DisposeIgnoringWorkerFaultAsync(second.Worker);
        }
        Assert.Empty(await fixture.ReadLeasesAsync());
    }

    [Fact]
    [Trait("Capability", "Process")]
    public async Task OfficialProcessFailures_AreSupervisedAndReleaseDurableLeases()
    {
        await AssertStartupTimeoutAsync();
        await AssertStartupCancellationAsync();
        await AssertMalformedFrameAsync();
        await AssertCrashAsync();
        await AssertForcedShutdownAsync();
    }

    private async Task AssertStartupTimeoutAsync()
    {
        var package = fixture.CreatePackage(
            SyntheticRuntimeKind.DotNet,
            "official.failure.timeout",
            "startup-hang");
        var activated = await fixture.CreateWorkerAsync(
            package,
            new RuntimeProcessPolicyOptions { StartupTimeout = TimeSpan.FromMilliseconds(150) });
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await activated.Worker.ComposeAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await DisposeIgnoringWorkerFaultAsync(activated.Worker);
        }
        Assert.Empty(await fixture.ReadLeasesAsync());
    }

    private async Task AssertStartupCancellationAsync()
    {
        var package = fixture.CreatePackage(
            SyntheticRuntimeKind.DotNet,
            "official.failure.cancellation",
            "startup-hang");
        var activated = await fixture.CreateWorkerAsync(
            package,
            new RuntimeProcessPolicyOptions { StartupTimeout = TimeSpan.FromSeconds(10) });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await activated.Worker.ComposeAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await DisposeIgnoringWorkerFaultAsync(activated.Worker);
        }
        Assert.Empty(await fixture.ReadLeasesAsync());
    }

    private async Task AssertMalformedFrameAsync()
    {
        var package = fixture.CreatePackage(
            SyntheticRuntimeKind.DotNet,
            "official.failure.malformed",
            "malformed-frame");
        var activated = await fixture.CreateWorkerAsync(package);
        try
        {
            await StartThroughMigrationPreparationAsync(activated);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await activated.Worker.ActivateGenerationAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await activated.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await DisposeIgnoringWorkerFaultAsync(activated.Worker);
        }
        Assert.Empty(await fixture.ReadLeasesAsync());
    }

    private async Task AssertCrashAsync()
    {
        var package = fixture.CreatePackage(
            SyntheticRuntimeKind.DotNet,
            "official.failure.crash",
            "crash");
        var activated = await fixture.CreateWorkerAsync(package);
        try
        {
            await StartThroughMigrationPreparationAsync(activated);
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await activated.Worker.ActivateGenerationAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await activated.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await DisposeIgnoringWorkerFaultAsync(activated.Worker);
        }
        Assert.Empty(await fixture.ReadLeasesAsync());
    }

    private async Task AssertForcedShutdownAsync()
    {
        var package = fixture.CreatePackage(
            SyntheticRuntimeKind.DotNet,
            "official.failure.forcedstop",
            "shutdown-hang");
        var activated = await fixture.CreateWorkerAsync(
            package,
            new RuntimeProcessPolicyOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(150) });
        Process? process = null;
        try
        {
            await ActivateAsync(activated);
            process = WorkerReleaseMetrics.GetProcess(activated.Worker);
            await activated.Worker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await activated.Worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);
        }
        finally
        {
            await DisposeIgnoringWorkerFaultAsync(activated.Worker);
            process?.Dispose();
        }
        Assert.Empty(await fixture.ReadLeasesAsync());
    }

    private static async Task ActivateAsync(ActivatedSyntheticWorker activated)
    {
        await StartThroughMigrationPreparationAsync(activated);
        await activated.Worker.ActivateGenerationAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task StartThroughMigrationPreparationAsync(ActivatedSyntheticWorker activated)
    {
        await activated.Worker.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var generation = new RuntimePackageGeneration(activated.ActivationId, 1);
        await activated.Worker.CommitGenerationAsync(generation).WaitAsync(TimeSpan.FromSeconds(10));
        await activated.Worker.PrepareStorageMigrationsAsync(generation).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task DisposeIgnoringWorkerFaultAsync(ProcessRuntimeWorker worker)
    {
        try
        {
            await worker.DisposeAsync();
        }
        catch
        {
        }
    }

    private static async Task<JsonDocument> WaitForWorkerStatusAsync(
        ProcessRuntimeWorker worker,
        string fileName)
    {
        var path = Path.Combine(worker.WorkerTemporaryPath, fileName);
        var signal = FileSignal.WaitForAsync(
            path,
            TimeSpan.FromSeconds(10),
            () => $"generationCompleted={worker.GenerationCompletion.IsCompleted}; temporaryRoot={worker.WorkerTemporaryPath}");
        var completed = await Task.WhenAny(signal, worker.GenerationCompletion);
        if (completed == worker.GenerationCompletion && !File.Exists(path))
        {
            await worker.GenerationCompletion;
            throw new InvalidOperationException("The official synthetic Worker stopped before writing its environment status.");
        }
        await signal;
        return JsonDocument.Parse(await File.ReadAllBytesAsync(path));
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string[] ExpectedWorkerEnvironmentNames()
    {
        var names = new List<string>
        {
            "SUNDER_ACTIVATION_ID",
            "SUNDER_PACKAGE_CONTENT_ROOT",
            "SUNDER_PACKAGE_ID",
            "SUNDER_PACKAGE_VERSION",
            "SUNDER_SESSION_ID",
            "SUNDER_WORKER_OPERATIONAL_OUTCOMES_V1",
            "SUNDER_WORKER_PROTOCOL",
            "SUNDER_WORKER_TEMP_ROOT",
            "TEMP",
            "TMP",
            "TMPDIR",
        };
        if (OperatingSystem.IsWindows())
        {
            names.Add("SystemRoot");
            names.Add("WINDIR");
        }
        else if (OperatingSystem.IsMacOS())
        {
            names.Add("__CF_USER_TEXT_ENCODING");
        }
        return names
            .Select(NormalizeEnvironmentName)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeEnvironmentName(string name)
        => OperatingSystem.IsWindows() ? name.ToUpperInvariant() : name;

    private static string MismatchedRuntimeIdentifier(string current)
    {
        var platformPrefix = current[..(current.IndexOf('-', StringComparison.Ordinal) + 1)];
        return SunderPackageFormat.SupportedRuntimeIdentifiers.First(candidate =>
            candidate.StartsWith(platformPrefix, StringComparison.Ordinal)
            && !string.Equals(candidate, current, StringComparison.Ordinal));
    }

    private sealed class AmbientRuntimePoison : IDisposable
    {
        public const string SentinelName = "SUNDER_SIGNED_RUNTIME_GATE_AMBIENT_SENTINEL";
        private static readonly string[] VariableNames =
        [
            "PATH",
            "DOTNET_ROOT",
            "DOTNET_ROOT_X64",
            "DOTNET_ROOT_X86",
            "DOTNET_ROOT_ARM64",
            "DOTNET_HOST_PATH",
            "DOTNET_ADDITIONAL_DEPS",
            "DOTNET_SHARED_STORE",
            "DOTNET_STARTUP_HOOKS",
            "DOTNET_MULTILEVEL_LOOKUP",
            "CORECLR_ENABLE_PROFILING",
            "CORECLR_PROFILER",
            "CORECLR_PROFILER_PATH",
            "COR_ENABLE_PROFILING",
            "COR_PROFILER",
            "COR_PROFILER_PATH",
            "NODE",
            "NODE_OPTIONS",
            "NVM_BIN",
            "NVM_DIR",
            "NODE_PATH",
            "VOLTA_HOME",
            "HOME",
            "USERPROFILE",
            "LANG",
            "LC_ALL",
            "LC_CTYPE",
            "SUNDER_RUNTIME_BEARER_TOKEN",
            "SUNDER_RUNTIME_CONNECTION_FILE",
            "SUNDER_HOST_STATE_ROOT",
            "GITHUB_TOKEN",
            "OPENAI_API_KEY",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
            "AZURE_CLIENT_SECRET",
            "GOOGLE_APPLICATION_CREDENTIALS",
            SentinelName,
        ];
        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

        public AmbientRuntimePoison(string rootPath)
        {
            PoisonPath = Path.Combine(rootPath, "ambient-runtime-poison");
            Directory.CreateDirectory(PoisonPath);
            foreach (var name in VariableNames)
            {
                _original[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, PoisonPath);
            }
        }

        public string PoisonPath { get; }

        public void Dispose()
        {
            foreach (var (name, value) in _original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
