using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Host.Client;
using Sunder.Runtime.Client;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeHostStartupIntegrationTests
{
    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task ManagedHost_RealLaunchdStartupPublishesMatchingDeploymentIdentity()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-host-startup-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "bin",
            BuildConfiguration,
            "net10.0",
            "RuntimeHost");
        var payloadRoot = Path.Combine(root, "payloads");
        var connectionPath = Path.Combine(root, "connection", "host.json");
        var hostStateRoot = Path.Combine(root, "host-state");
        var runtimeStateRoot = Path.Combine(root, "runtime-state");
        var runtimeUrl = new Uri($"http://127.0.0.1:{ReservePort()}/");
        var label = $"dev.sunder.host.tests.{Guid.NewGuid():N}";
        var launchd = new MacOsSessionRuntimeLauncher(
            legacyLaunchAgentPath: Path.Combine(root, "legacy.plist"),
            launchAgentPath: Path.Combine(root, "launchd", $"{label}.plist"),
            label: label);
        var serviceManager = new EnvironmentInjectingHostServiceManager(
            launchd,
            hostStateRoot,
            runtimeStateRoot);
        var store = new UserHostPayloadStore(source, payloadRoot, "integration");
        var expectedPayload = store.Prepare();
        var expectedIdentity = expectedPayload.DeploymentIdentity;
        expectedPayload.Dispose();
        using var manager = new RuntimeHostProcessManager(
            new AppStartupOptions { RuntimeUrl = runtimeUrl },
            new RuntimeConnectionState(runtimeUrl),
            connectionInfoPath: connectionPath,
            isRuntimeLeaseAvailable: () => RuntimeLocalState.IsLeaseAvailable(runtimeStateRoot),
            userHostPayloadStore: store,
            isHostInstanceLockAvailable: static () => true,
            hostServiceManager: serviceManager);
        var commandRunner = new HostServiceCommandRunner();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.EnsureStartedAsync(runtimeUrl, timeout.Token);
            var connection = RuntimeConnectionInfoStore.Load(connectionPath);
            Assert.NotNull(connection);
            using var client = new HostManagementClient(() => connection);

            var handshake = await client.GetHandshakeAsync(timeout.Token);

            Assert.Equal(expectedIdentity, handshake.DeploymentIdentity);
            await client.ShutdownHostAsync(timeout.Token);
            await WaitForFileDeletionAsync(connectionPath, timeout.Token);
        }
        finally
        {
            await commandRunner.RunAsync(
                "/bin/launchctl",
                ["bootout", launchd.ServiceTarget],
                TimeSpan.FromSeconds(15));
            TryDeleteDirectory(root);
        }
    }

    private static async Task WaitForFileDeletionAsync(string path, CancellationToken cancellationToken)
    {
        while (File.Exists(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken);
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the Sunder core repository root.");
    }

    private static string BuildConfiguration
    {
        get
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }
    }

    private static void TryDeleteDirectory(string path)
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
    }

    private sealed class EnvironmentInjectingHostServiceManager(
        IHostServiceManager inner,
        string hostStateRoot,
        string runtimeStateRoot) : IHostServiceManager
    {
        public Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
            ProcessStartInfo startInfo,
            bool replaceExisting,
            CancellationToken cancellationToken)
        {
            startInfo.Environment["SUNDER_HOST_STATE_ROOT"] = hostStateRoot;
            startInfo.Environment["SUNDER_RUNTIME_STATE_ROOT"] = runtimeStateRoot;
            return inner.ReconcileAndLaunchAsync(startInfo, replaceExisting, cancellationToken);
        }

        public Task<HostServiceObservation> ObserveAsync(
            HostServiceLaunchReceipt receipt,
            CancellationToken cancellationToken)
            => inner.ObserveAsync(receipt, cancellationToken);
    }
}
