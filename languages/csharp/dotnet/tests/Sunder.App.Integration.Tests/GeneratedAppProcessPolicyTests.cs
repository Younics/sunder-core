using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.Host.TestSupport;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.App.Integration.Tests;

[Collection("App/Runtime integration")]
public sealed class GeneratedAppProcessPolicyTests(GeneratedOwnedRpcPackageFixture packageFixture) : IClassFixture<GeneratedOwnedRpcPackageFixture>
{
    [Theory]
    [Trait("Capability", "Process")]
    [InlineData("policy")]
    [InlineData("forced")]
    [InlineData("host-loss")]
    public async Task GeneratedAppCallsItsWorkerBeforeNativePolicyEnforcementProvesProcessExit(string failure)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-native-app-policy");
        using var policy = new ActivePolicyTestContext();
        policy.Clock.ReadOverride = new PolicyContinuousClock().Read;
        RuntimeLocalState.EnsureInitialized(policy.Paths.RootPath);
        var identity = new RegistryPackageVersionIdentity("example.generated", "7.6.5", PackageSessionPreparer.ComputeContentIdentity(packageFixture.PackagePath));
        await policy.RefreshAsync(packages: [identity]);
        var endpoint = OperatingSystem.IsWindows()
            ? new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "sunder-native-app-" + Guid.NewGuid().ToString("N"))
            : new RuntimeIpcEndpoint(RuntimeIpcTransportKind.UnixSocket, "/tmp/sna-" + Guid.NewGuid().ToString("N"));
        var controller = new RecordingProcessController();
        var builder = WebApplication.CreateSlimBuilder();
        RuntimeSupervisedTransport.Configure(builder.WebHost, endpoint);
        builder.Services.AddRuntimeHostServices(policy.Paths, new RuntimeBearerTokenValidator("native-app-policy-token"));
        builder.Services.UseTestExecutionRuntimeLaunchResolver();
        builder.Services.AddSingleton(policy.Cache);
        builder.Services.AddSingleton(policy.Client);
        builder.Services.AddSingleton<IPolicyContinuousClock>(policy.Clock);
        builder.Services.AddSingleton<IAppPolicyProcessController>(controller);
        await using var app = builder.Build();
        app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
        app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();
        app.MapRuntimeHandshakeEndpoint();
        app.MapGroup("/api/v1").MapInstalledPackageEndpoints().MapContentTransferEndpoints().MapPackageSessionEndpoints().MapRuntimeRpcEndpoints().MapAppPolicyEndpoints();
        await app.Services.GetRequiredService<InstalledPackageLifecycleService>().InitializeAsync();
        await app.StartAsync();
        RuntimeSupervisedTransport.RestrictEndpoint(endpoint);
        var connection = new RuntimeConnectionInfo(RuntimeIpcEndpoint.LogicalRuntimeUrl, "native-app-policy-token") { IpcEndpoint = endpoint };
        var connectionPath = temporary.GetPath("connection", "runtime.json");
        RuntimeConnectionInfoStore.Save(connection, connectionPath);
        using var transport = new RuntimeClientTransport(() => connection, RuntimeIpcHttpMessageHandlerFactory.Create(endpoint));
        using var client = new RuntimeManagementClient(transport);
        Process? child = null;
        Task<string>? stderr = null;
        PackageProjectionAcquisition? projection = null;
        var stopped = false;
        try
        {
            var archive = temporary.GetPath("generated.sunderpkg");
            ZipFile.CreateFromDirectory(packageFixture.PackagePath, archive);
            var upload = await client.UploadPackageAsync(archive);
            var stage = await client.StagePackageStoreChangesAsync(new([new(PackageStoreMutationKind.Install, UploadId: upload.UploadId)]));
            Assert.True(stage.Success, stage.OperationResult.Message);
            var commit = await client.CommitPackageStoreStageAsync(stage.StageId!, stage.OperationResult);
            Assert.True(commit.Success, string.Join(" | ", commit.Errors));
            var snapshot = Assert.Single(app.Services.GetRequiredService<RuntimePackageUiService>().GetActiveSnapshots(PackageTargetSelection.GetCurrentRuntimeIdentifier()));
            var source = Assert.Single(app.Services.GetRequiredService<PackageSessionState>().GetActivePackageSources());
            var key = new SunderPackageTargetKey("app", PackageTargetSelection.GetCurrentRuntimeIdentifier());
            Assert.True(SunderPackageTargetResolver.TryResolveTarget(source.Manifest!, key, out var target));
            projection = await app.Services.GetRequiredService<PackageProjectionCache>().GetOrBuildAndLeaseAsync(
                PackageProjectionRequestFactory.CreateApp(new(source, key, target!)), new("native-app-test", Guid.NewGuid(), source.PackageId, "probe"));
            Assert.DoesNotContain(Directory.EnumerateFiles(projection.RootPath, "*.dll", SearchOption.AllDirectories), path =>
                Path.GetFileName(path).Contains("Protocol", StringComparison.Ordinal) || Path.GetFileName(path) is "ImplementationFirstRpc.dll" or "Sunder.Sdk.Worker.dll");
            var configPath = temporary.GetPath("app.json");
            var resultPath = temporary.GetPath("app-result.json");
            await File.WriteAllBytesAsync(configPath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                connectionPath, snapshot, projectionPath = projection.RootPath, resultPath,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            child = StartApp(configPath);
            stderr = child.StandardError.ReadToEndAsync();
            _ = child.StandardOutput.ReadToEndAsync();
            var result = await WaitForResultAsync(child, resultPath, stderr);
            Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
            Assert.Equal("native-policy", result.GetProperty("response").GetString());
            Assert.DoesNotContain(result.GetProperty("assemblies").EnumerateArray(), value =>
                value.GetString()!.Contains("Protocol", StringComparison.Ordinal) || value.GetString() is "ImplementationFirstRpc" or "Sunder.Sdk.Worker");
            Assert.Single(app.Services.GetRequiredService<AppProcessPolicySessions>().CapturePolicySubjects());
            Assert.Equal(child.Id, controller.Peer!.ProcessId);

            if (failure == "host-loss")
            {
                await app.StopAsync();
                stopped = true;
            }
            else
            {
                if (failure == "forced") Suspend(child, controller.Peer);
                policy.Response = request => ActivePolicyTestContext.Allowed(request, 2) with
                { Packages = [new(identity, "SecurityBlocked", "Listed", "Denied", PackagePolicyErrorCodes.SecurityBlocked, "Blocked", [])] };
                await policy.RefreshAsync(packages: [identity]);
                await app.Services.GetRequiredService<AppProcessPolicySessions>().EnforceAsync(default);
            }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (failure == "forced") Assert.True(controller.Terminations > 0);
            if (!stopped) await app.Services.GetRequiredService<AppProcessPolicySessions>().EnforceAsync(default);
            using var recovery = new AppProcessPolicySessions(policy.Paths, new RuntimeProtocolDescriptor(),
                app.Services.GetRequiredService<IAppPolicySnapshotSource>(), new(policy.Cache), policy.Cache, policy.Clock, TimeProvider.System,
                new AppPolicyProcessController(), NullLogger<AppProcessPolicySessions>.Instance);
            await recovery.InitializeAsync(default);
            Assert.Empty(recovery.CapturePolicySubjects());
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(policy.Paths.RootPath, RuntimeV1StateDescriptor.AppPolicySessionsDirectory), "*.json"));
        }
        finally
        {
            if (child is not null && !child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            child?.Dispose();
            if (stderr is not null) await stderr;
            projection?.Dispose();
            await app.Services.GetRequiredService<InstalledPackageLifecycleService>().ShutdownAsync(app.Services.GetRequiredService<PackageSessionLifecycleService>(), default);
            if (!stopped) await app.StopAsync();
            if (!OperatingSystem.IsWindows()) File.Delete(endpoint.Address);
        }
    }

    private static Process StartApp(string configPath)
    {
        var assembly = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sunder-policy-app-target-path.txt")).Trim();
        var executable = Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "Sunder.App.exe" : "Sunder.App");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.Environment["DOTNET_STARTUP_HOOKS"] = Path.Combine(AppContext.BaseDirectory, "app-policy-hook", "Sunder.App.Tests.dll");
        start.Environment["SUNDER_TEST_APP_POLICY_CONFIG"] = configPath;
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        return Process.Start(start)!;
    }

    private static async Task<JsonElement> WaitForResultAsync(Process child, string path, Task<string> stderr)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!File.Exists(path))
        {
            if (child.HasExited) Assert.Fail("Native App exited before its generated RPC call: " + await stderr);
            await Task.Delay(25, timeout.Token);
        }
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, timeout.Token));
        return document.RootElement.Clone();
    }

    private static void Suspend(Process child, AppPolicyPeer peer)
    {
        if (OperatingSystem.IsWindows()) Assert.Equal(0, NtSuspendProcess(child.SafeHandle));
        else if (OperatingSystem.IsMacOS())
        {
            var audit = peer.AuditToken!.Value;
            Assert.Equal(0, proc_signal_with_audittoken(ref audit, 17));
        }
        else
        {
            using var fd = new SafeFileHandle((IntPtr)pidfd_open(child.Id, 0), ownsHandle: true);
            Assert.False(fd.IsInvalid);
            Assert.False(child.HasExited);
            Assert.Equal(0, pidfd_send_signal(fd, 19, IntPtr.Zero, 0));
        }
    }

    private sealed class RecordingProcessController : IAppPolicyProcessController
    {
        internal AppPolicyPeer? Peer;
        internal int Terminations;
        public IAppPolicyProcessHandle Attach(RuntimeAppProcessIdentity identity, AppPolicyPeer peer, bool recovery)
        {
            var handle = new AppPolicyProcessController().Attach(identity, peer, recovery);
            Peer = peer;
            return new RecordingHandle(handle, this);
        }
        private sealed class RecordingHandle(IAppPolicyProcessHandle inner, RecordingProcessController owner) : IAppPolicyProcessHandle
        {
            public AppPolicyProcessState Probe() => inner.Probe();
            public void Terminate() { Interlocked.Increment(ref owner.Terminations); inner.Terminate(); }
            public void Dispose() => inner.Dispose();
        }
    }

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(SafeProcessHandle process);
    [DllImport("libproc.dylib", SetLastError = true)] private static extern int proc_signal_with_audittoken(ref RuntimeAuditToken token, int signal);
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_open(int pid, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_send_signal(SafeFileHandle fd, int signal, IntPtr info, uint flags);
}

public sealed class GeneratedOwnedRpcPackageFixture : IAsyncLifetime
{
    internal string PackagePath { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        var marker = Path.Combine(AppContext.BaseDirectory, "sunder-policy-package-path.txt");
        Assert.True(File.Exists(marker), "Build Sunder.App.Integration.Tests before running its generated package behavior tests.");
        PackagePath = (await File.ReadAllTextAsync(marker)).Trim();
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(PackagePath);
        Assert.True(validation.Success, string.Join(" | ", validation.Errors));
        Assert.Single(validation.Manifest!.Provides!);
        Assert.Single(validation.Manifest.UsesContracts!);
    }
    public Task DisposeAsync() => Task.CompletedTask;
}
