using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Protocol;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageAuthSessionCoordinatorTests
{
    [Fact]
    public async Task StartPackageAuthAsync_ReusesLatestPendingSession()
    {
        var authHandler = new TestPackageAuthHandler();
        var loadedPackage = CreateLoadedPackage(authHandler);
        using var callbackServer = new PackageAuthCallbackServer(
            NullLogger<PackageAuthCallbackServer>.Instance,
            GetFreePort());
        var coordinator = new PackageAuthSessionCoordinator(
            packageId => string.Equals(packageId, "test.package", StringComparison.OrdinalIgnoreCase) ? loadedPackage : null,
            (_, _, _, _) => throw new InvalidOperationException("Unexpected package fault."));

        var first = await coordinator.StartPackageAuthAsync("test.package", callbackServer);
        var second = await coordinator.StartPackageAuthAsync("test.package", callbackServer);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.AuthSessionId, second.AuthSessionId);
        Assert.Equal(1, authHandler.StartCount);
        Assert.Equal(Sunder.Protocol.PackageAuthFlowKind.Browser, first.Flow);
        Assert.Equal("https://login.example.test", first.LaunchUrl);
    }

    [Fact]
    public async Task CompletePackageAuthSessionAsync_UpdatesSessionStatus()
    {
        var authHandler = new TestPackageAuthHandler();
        var loadedPackage = CreateLoadedPackage(authHandler);
        using var callbackServer = new PackageAuthCallbackServer(
            NullLogger<PackageAuthCallbackServer>.Instance,
            GetFreePort());
        var coordinator = new PackageAuthSessionCoordinator(
            _ => loadedPackage,
            (_, _, _, _) => throw new InvalidOperationException("Unexpected package fault."));
        var started = await coordinator.StartPackageAuthAsync("test.package", callbackServer);

        var completed = await coordinator.CompletePackageAuthSessionAsync(
            started!.AuthSessionId,
            new Dictionary<string, string?> { ["code"] = "abc" });
        var status = coordinator.GetPackageAuthSessionStatus("test.package", started.AuthSessionId);

        Assert.True(completed);
        Assert.NotNull(status);
        Assert.Equal(Sunder.Protocol.PackageAuthSessionState.Connected, status.State);
        Assert.Equal("Connected.", status.Message);
        Assert.Equal("abc", authHandler.CompletedCode);
    }

    private static ActiveLoadedPackage CreateLoadedPackage(IPackageAuthHandler authHandler)
    {
        var tempDirectory = CreateTempDirectory();
        var assemblyPath = typeof(PackageAuthSessionCoordinator).Assembly.Location;

        return new ActiveLoadedPackage(
            new ActivePackageDescriptor("test.package", "Test Package", "1.0.0", Icon: null, IsEnabled: true, PackageReadinessState.Ready, Views: []),
            new PackageSourceDescriptor("test.package", PackageSourceKind.Dev, tempDirectory),
            ConfigurationSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(tempDirectory, "state.json")),
            new JsonPackageSecretsStore(Path.Combine(tempDirectory, "secrets.json")),
            authHandler,
            new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase)
            {
                [((IPackageCallbackHandler)authHandler).CallbackHandlerId] = (IPackageCallbackHandler)authHandler,
            },
            BackgroundServices: [],
            new ServiceCollection().BuildServiceProvider(),
            new RuntimePackageLoadContext(
                "test.package",
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, port: 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TestPackageAuthHandler : IPackageAuthHandler
    {
        private bool _connected;

        public int StartCount { get; private set; }

        public string? CompletedCode { get; private set; }

        public ValueTask<PackageAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new PackageAuthStatus(
                "test.package",
                _connected
                    ? Sunder.Sdk.Authentication.PackageAuthStatusKind.Connected
                    : Sunder.Sdk.Authentication.PackageAuthStatusKind.NotConnected,
                _connected ? "Connected." : "Not connected.",
                CanAuthorize: !_connected,
                CanDisconnect: _connected));

        public Task<PackageAuthSessionStartResult?> StartAuthorizationAsync(
            PackageAuthSessionStartContext context,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.FromResult<PackageAuthSessionStartResult?>(new PackageAuthSessionStartResult(
                "test.package",
                context.AuthSessionId,
                Sunder.Sdk.Authentication.PackageAuthFlowKind.Browser,
                "https://login.example.test",
                "Open the browser."));
        }

        public Task<PackageAuthStatus> CompleteAuthorizationAsync(
            PackageAuthSessionCompletionContext context,
            CancellationToken cancellationToken = default)
        {
            CompletedCode = context.QueryValues.TryGetValue("code", out var code) ? code : null;
            _connected = true;
            return Task.FromResult(new PackageAuthStatus(
                "test.package",
                Sunder.Sdk.Authentication.PackageAuthStatusKind.Connected,
                "Connected.",
                CanAuthorize: false,
                CanDisconnect: true));
        }

        public Task<PackageAuthStatus> DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;
            return Task.FromResult(new PackageAuthStatus(
                    "test.package",
                    Sunder.Sdk.Authentication.PackageAuthStatusKind.NotConnected,
                    "Disconnected.",
                    CanAuthorize: true,
                    CanDisconnect: false));
        }
    }
}
