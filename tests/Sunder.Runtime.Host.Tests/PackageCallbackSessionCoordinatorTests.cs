using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageCallbackSessionCoordinatorTests
{
    [Fact]
    public async Task CompletingCallback_WinsAgainstExpiryUnloadAndShutdown()
    {
        var handler = new TestCallbackHandler(blockCompletion: true);
        var (state, callbacks) = await CreateCoordinatorAsync(handler);
        await using var server = CreateServer();
        PackageCallbackSessionResponse started;
        using (var startLease = state.AcquireLease())
        {
            started = (await callbacks.StartAsync(
                startLease,
                "test.package",
                handler.CallbackHandlerId,
                null,
                server))!;
        }
        Assert.Equal(server.GetCallbackUri(started.CallbackSessionId), handler.CallbackUri);
        using var completionLease = state.AcquireLease();

        var completion = callbacks.CompleteAsync(
            completionLease,
            started.CallbackSessionId,
            new Dictionary<string, string?> { ["code"] = "winner" });
        await handler.CompletionEntered.WaitAsync(TimeSpan.FromSeconds(5));

        callbacks.SweepExpired(DateTimeOffset.UtcNow + TimeSpan.FromDays(1));
        callbacks.RemovePackageSessions("test.package");
        callbacks.Clear();
        var shutdown = callbacks.ShutdownAsync();

        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, handler.CancellationCount);
        handler.ReleaseCompletion();

        Assert.True(await completion);
        await shutdown;
        Assert.Equal(0, handler.CancellationCount);
        Assert.Equal("winner", handler.CompletedCode);
        completionLease.Dispose();
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task Start_DoesNotPublishAfterGenerationRetirementBegins()
    {
        var handler = new TestCallbackHandler(blockStart: true, ignoreStartCancellation: true);
        var (state, callbacks) = await CreateCoordinatorAsync(handler);
        await using var server = CreateServer();
        var lease = state.AcquireLease();

        var start = callbacks.StartAsync(
            lease,
            "test.package",
            handler.CallbackHandlerId,
            null,
            server);
        await handler.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));
        var retirement = state.ClearActiveSessionAsync();
        handler.ReleaseStart();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, server.RegisteredHandlerCount);
        Assert.Equal(1, handler.CancellationCount);
        lease.Dispose();
        await retirement;
    }

    [Fact]
    public async Task Start_DoesNotPublishAfterShutdownWins()
    {
        var handler = new TestCallbackHandler(blockStart: true, ignoreStartCancellation: true);
        var (state, callbacks) = await CreateCoordinatorAsync(handler);
        await using var server = CreateServer();
        using var lease = state.AcquireLease();

        var start = callbacks.StartAsync(
            lease,
            "test.package",
            handler.CallbackHandlerId,
            null,
            server);
        await handler.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));

        await callbacks.ShutdownAsync();
        await handler.CancellationEntered.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, server.RegisteredHandlerCount);
        Assert.Equal(1, handler.CancellationCount);
        handler.ReleaseStart();
    }

    [Fact]
    public async Task CompletingCallback_IsCancelledByPackageSessionRetirement()
    {
        var handler = new TestCallbackHandler(blockCompletion: true);
        var (state, callbacks) = await CreateCoordinatorAsync(handler);
        await using var server = CreateServer();
        PackageCallbackSessionResponse started;
        using (var startLease = state.AcquireLease())
        {
            started = (await callbacks.StartAsync(
                startLease,
                "test.package",
                handler.CallbackHandlerId,
                null,
                server))!;
        }
        var completionLease = state.AcquireLease();
        var completion = callbacks.CompleteAsync(
            completionLease,
            started.CallbackSessionId,
            new Dictionary<string, string?> { ["code"] = "late" });
        await handler.CompletionEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var retirement = state.ClearActiveSessionAsync();

        Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(5)));
        completionLease.Dispose();
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(handler.CompletedCode);
        await callbacks.ShutdownAsync();
    }

    [Fact]
    public async Task CompletingCallback_IsCancelledWhenHostStops()
    {
        using var hostStopping = new CancellationTokenSource();
        var handler = new TestCallbackHandler(blockCompletion: true);
        var (state, callbacks) = await CreateCoordinatorAsync(handler, hostStopping.Token);
        await using var server = CreateServer();
        PackageCallbackSessionResponse started;
        using (var startLease = state.AcquireLease())
        {
            started = (await callbacks.StartAsync(
                startLease,
                "test.package",
                handler.CallbackHandlerId,
                null,
                server))!;
        }
        using var completionLease = state.AcquireLease();
        var completion = callbacks.CompleteAsync(
            completionLease,
            started.CallbackSessionId,
            new Dictionary<string, string?> { ["code"] = "late" });
        await handler.CompletionEntered.WaitAsync(TimeSpan.FromSeconds(5));

        hostStopping.Cancel();

        Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(handler.CompletedCode);
        await callbacks.ShutdownAsync();
        completionLease.Dispose();
        await state.ClearActiveSessionAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullOrFailedStart_DisposesTokensAndRegistrations(bool throwFromStart)
    {
        var handler = new TestCallbackHandler(returnNullFromStart: !throwFromStart, throwFromStart: throwFromStart);
        var (state, callbacks) = await CreateCoordinatorAsync(handler);
        await using var server = CreateServer();
        using var lease = state.AcquireLease();

        if (throwFromStart)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => callbacks.StartAsync(
                lease,
                "test.package",
                handler.CallbackHandlerId,
                null,
                server));
        }
        else
        {
            Assert.Null(await callbacks.StartAsync(
                lease,
                "test.package",
                handler.CallbackHandlerId,
                null,
                server));
        }

        Assert.Equal(0, server.RegisteredHandlerCount);
        Assert.Equal(1, handler.CancellationCount);
        Assert.Throws<ObjectDisposedException>(() => handler.StartToken.WaitHandle);
        await callbacks.ShutdownAsync();
    }

    [Fact]
    public async Task Cancel_PendingSessionRetainsTerminalStatusAndNotifiesHandler()
    {
        var handler = new TestCallbackHandler();
        var (state, callbacks) = await CreateCoordinatorAsync(handler);
        await using var server = CreateServer();
        using var lease = state.AcquireLease();
        var started = (await callbacks.StartAsync(
            lease,
            "test.package",
            handler.CallbackHandlerId,
            null,
            server))!;

        Assert.True(callbacks.Cancel(lease, "test.package", started.CallbackSessionId));
        await handler.CancellationEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var status = callbacks.GetStatus(lease, "test.package", started.CallbackSessionId);
        Assert.NotNull(status);
        Assert.Equal(Sunder.Runtime.Contracts.PackageCallbackSessionState.Cancelled, status.State);
        Assert.Equal(PackageCallbackCancellationReason.CallerRequested, handler.CancellationReason);
        Assert.False(callbacks.Cancel(lease, "test.package", started.CallbackSessionId));

        await callbacks.ShutdownAsync();
        lease.Dispose();
        await state.ClearActiveSessionAsync();
    }

    private static async Task<(PackageSessionState State, PackageCallbackSessionCoordinator Callbacks)> CreateCoordinatorAsync(
        TestCallbackHandler handler,
        CancellationToken hostStopping = default)
    {
        PackageCallbackSessionCoordinator? callbacks = null;
        var state = new PackageSessionState(
            NullLogger.Instance,
            () => callbacks?.Clear(),
            packageId => callbacks?.RemovePackageSessions(packageId));
        callbacks = new PackageCallbackSessionCoordinator(state, hostStopping: hostStopping);
        var packageId = "test.package";
        var loadedPackage = CreateLoadedPackage(handler);
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = new SessionPackageDescriptor(
                    packageId,
                    loadedPackage.Descriptor.DisplayName,
                    loadedPackage.Descriptor.Version,
                    loadedPackage.Descriptor.HostRoles,
                    loadedPackage.Descriptor.Icon,
                    IsEnabled: true,
                    PackageReadinessState.Ready,
                    loadedPackage.Descriptor.Views,
                    FailureOrigin: null,
                    LastError: null,
                    LastFailureAtUtc: null,
                    FailureCount: 0),
            });
        await state.PublishSessionAsync(session);
        return (state, callbacks);
    }

    private static ActiveLoadedPackage CreateLoadedPackage(TestCallbackHandler handler)
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "sunder-runtime-host-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var assemblyPath = typeof(PackageCallbackSessionCoordinator).Assembly.Location;
        return new ActiveLoadedPackage(
            new ActivePackageDescriptor("test.package", "Test Package", "1.0.0", PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready, []),
            new RuntimePackageSource("test.package", PackageSourceKind.Dev, tempDirectory),
            SettingsSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(tempDirectory, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(tempDirectory, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            AuthHandler: null,
            new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase)
            {
                [handler.CallbackHandlerId] = handler,
            },
            BackgroundServices: [],
            new ServiceCollection().BuildServiceProvider(),
            new RuntimePackageLoadContext(
                "test.package",
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])),
            EmptyTestPackageSettings.Instance);
    }

    private static PackageCallbackServer CreateServer()
        => new(NullLogger<PackageCallbackServer>.Instance, GetFreePort());

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, port: 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TestCallbackHandler(
        bool blockStart = false,
        bool ignoreStartCancellation = false,
        bool returnNullFromStart = false,
        bool throwFromStart = false,
        bool blockCompletion = false) : IPackageCallbackHandler
    {
        private readonly TaskCompletionSource _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellationCount;

        public string CallbackHandlerId => "test.callback";
        public Task StartEntered => _startEntered.Task;
        public Task CompletionEntered => _completionEntered.Task;
        public Task CancellationEntered => _cancellationEntered.Task;
        public CancellationToken StartToken { get; private set; }
        public int CancellationCount => Volatile.Read(ref _cancellationCount);
        public PackageCallbackCancellationReason? CancellationReason { get; private set; }
        public string? CompletedCode { get; private set; }
        public Uri? CallbackUri { get; private set; }

        public async Task<PackageCallbackStartResult?> StartCallbackAsync(
            PackageCallbackStartContext context,
            CancellationToken cancellationToken = default)
        {
            StartToken = cancellationToken;
            CallbackUri = context.CallbackUri;
            _startEntered.TrySetResult();
            if (blockStart)
            {
                if (ignoreStartCancellation)
                {
                    await _releaseStart.Task;
                }
                else
                {
                    await _releaseStart.Task.WaitAsync(cancellationToken);
                }
            }
            if (throwFromStart)
            {
                throw new InvalidOperationException("Start failed.");
            }
            if (returnNullFromStart)
            {
                return null;
            }
            return new PackageCallbackStartResult(
                "test.package",
                context.CallbackSessionId,
                PackageCallbackFlowKind.Browser,
                "https://login.example.test",
                "Continue in the browser.");
        }

        public async Task<PackageCallbackCompletionResult> CompleteCallbackAsync(
            PackageCallbackCompletionContext context,
            CancellationToken cancellationToken = default)
        {
            _completionEntered.TrySetResult();
            if (blockCompletion)
            {
                await _releaseCompletion.Task.WaitAsync(cancellationToken);
            }
            CompletedCode = context.QueryValues.TryGetValue("code", out var code) ? code : null;
            return new PackageCallbackCompletionResult(
                "test.package",
                context.CallbackSessionId,
                PackageCallbackCompletionState.Completed,
                "Completed.");
        }

        public Task CancelCallbackAsync(
            PackageCallbackCancellationContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _cancellationCount);
            CancellationReason = context.Reason;
            _cancellationEntered.TrySetResult();
            return Task.CompletedTask;
        }

        public void ReleaseStart() => _releaseStart.TrySetResult();
        public void ReleaseCompletion() => _releaseCompletion.TrySetResult();
    }
}
