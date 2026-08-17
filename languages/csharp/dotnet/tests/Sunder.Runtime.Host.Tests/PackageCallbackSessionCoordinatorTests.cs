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
    public async Task BrowserCallback_AcknowledgesBeforePackageCompletionFinishes()
    {
        var handler = new TestCallbackHandler(
            blockCompletion: true,
            callbackHandlerId: PackageCallbackHandlerIds.Authentication);
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
        using var statusLease = state.AcquireLease();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var response = await httpClient.GetStringAsync(
            new Uri($"{handler.CallbackUri}?state={started.CallbackSessionId}&code=winner"));

        Assert.Contains("Authorization received", response, StringComparison.Ordinal);
        await handler.CompletionEntered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            Sunder.Runtime.Contracts.PackageCallbackSessionState.Pending,
            callbacks.GetStatus(statusLease, "test.package", started.CallbackSessionId)?.State);

        handler.ReleaseCompletion();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        PackageCallbackSessionResponse? status;
        do
        {
            status = callbacks.GetStatus(statusLease, "test.package", started.CallbackSessionId);
            if (status?.State == Sunder.Runtime.Contracts.PackageCallbackSessionState.Completed)
            {
                break;
            }
            await Task.Delay(10);
        } while (DateTimeOffset.UtcNow < deadline);

        Assert.Equal(Sunder.Runtime.Contracts.PackageCallbackSessionState.Completed, status?.State);
        Assert.Equal("winner", handler.CompletedCode);
        await callbacks.ShutdownAsync();
        statusLease.Dispose();
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task BrowserCallback_FailedCompletionIsReportedThroughSessionStatus()
    {
        var handler = new TestCallbackHandler(
            throwFromCompletion: true,
            callbackHandlerId: PackageCallbackHandlerIds.Authentication);
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
        var statusLease = state.AcquireLease();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await httpClient.GetAsync(
            new Uri($"{handler.CallbackUri}?state={started.CallbackSessionId}&code=rejected"));

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        PackageCallbackSessionResponse? status;
        do
        {
            status = callbacks.GetStatus(statusLease, "test.package", started.CallbackSessionId);
            if (status?.State == Sunder.Runtime.Contracts.PackageCallbackSessionState.Failed)
            {
                break;
            }
            await Task.Delay(10);
        } while (DateTimeOffset.UtcNow < deadline);

        Assert.Equal(Sunder.Runtime.Contracts.PackageCallbackSessionState.Failed, status?.State);
        Assert.Contains("Completion failed", status?.Message, StringComparison.Ordinal);
        await callbacks.ShutdownAsync();
        statusLease.Dispose();
        await state.ClearActiveSessionAsync();
    }

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
    public async Task CompletingCallback_IsCancelledBySiblingGenerationAdvanceAndFreshStartSucceeds()
    {
        var handler = new TestCallbackHandler(blockCompletion: true);
        var (state, callbacks) = await CreateCoordinatorAsync(
            handler,
            includeFaultSibling: true);
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
            new Dictionary<string, string?> { ["code"] = "stale" });
        await handler.CompletionEntered.WaitAsync(TimeSpan.FromSeconds(5));
        PackageActivationIdentity faultIdentity;
        long faultGeneration;
        using (var faultLease = state.AcquireLease())
        {
            var faultPackage = Assert.IsType<ActiveLoadedPackage>(
                state.GetLoadedPackage(faultLease, "fault.package"));
            faultIdentity = faultLease.GetPackageActivationIdentity(faultPackage);
            faultGeneration = faultLease.Generation;
        }

        Assert.True(state.HandlePackageFault(
            "fault.package",
            faultIdentity,
            faultGeneration,
            PackageFailureOrigin.RuntimeBackgroundService,
            new InvalidOperationException("sibling failed"),
            "run sibling package"));

        Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(completionLease.GenerationRetirementToken.IsCancellationRequested);
        Assert.Null(handler.CompletedCode);
        using (var freshLease = state.AcquireLease())
        {
            Assert.Equal(faultGeneration + 1, freshLease.Generation);
            Assert.Null(callbacks.GetStatus(
                freshLease,
                "test.package",
                started.CallbackSessionId));
            var fresh = await callbacks.StartAsync(
                freshLease,
                "test.package",
                handler.CallbackHandlerId,
                null,
                server);
            Assert.NotNull(fresh);
        }

        completionLease.Dispose();
        await callbacks.ShutdownAsync();
        await state.ClearActiveSessionAsync();
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

    [Fact]
    public async Task Cancel_CompletingSessionCancelsInFlightHandler()
    {
        var handler = new TestCallbackHandler(blockCompletion: true);
        var (state, callbacks) = await CreateCoordinatorAsync(handler);
        await using var server = CreateServer();
        using var lease = state.AcquireLease();
        var started = (await callbacks.StartAsync(
            lease,
            "test.package",
            handler.CallbackHandlerId,
            null,
            server))!;
        var completion = callbacks.CompleteAsync(
            lease,
            started.CallbackSessionId,
            new Dictionary<string, string?> { ["code"] = "late" });
        await handler.CompletionEntered.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(callbacks.Cancel(lease, "test.package", started.CallbackSessionId));

        Assert.False(await completion.WaitAsync(TimeSpan.FromSeconds(5)));
        var status = callbacks.GetStatus(lease, "test.package", started.CallbackSessionId);
        Assert.Equal(Sunder.Runtime.Contracts.PackageCallbackSessionState.Cancelled, status?.State);
        Assert.Null(handler.CompletedCode);
        Assert.Equal(0, handler.CancellationCount);
        await callbacks.ShutdownAsync();
        lease.Dispose();
        await state.ClearActiveSessionAsync();
    }

    private static async Task<(PackageSessionState State, PackageCallbackSessionCoordinator Callbacks)> CreateCoordinatorAsync(
        TestCallbackHandler handler,
        CancellationToken hostStopping = default,
        bool includeFaultSibling = false)
    {
        PackageCallbackSessionCoordinator? callbacks = null;
        var state = new PackageSessionState(
            NullLogger.Instance,
            () => callbacks?.Clear(),
            packageId => callbacks?.RemovePackageSessions(packageId));
        callbacks = new PackageCallbackSessionCoordinator(state, hostStopping: hostStopping);
        var packageId = "test.package";
        var loadedPackage = CreateLoadedPackage(handler);
        var loadedPackages = new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
        {
            [packageId] = loadedPackage,
        };
        if (includeFaultSibling)
        {
            loadedPackages.Add(
                "fault.package",
                CreateLoadedPackage(handler, "fault.package", includeCallbackHandler: false));
        }
        var session = new ActivePackageSession(
            sessionFolder: null,
            loadedPackages,
            loadedPackages.ToDictionary(
                static pair => pair.Key,
                static pair => new SessionPackageDescriptor(
                    pair.Key,
                    pair.Value.Descriptor.DisplayName,
                    pair.Value.Descriptor.Version,
                    pair.Value.Descriptor.HostRoles,
                    pair.Value.Descriptor.Icon,
                    IsEnabled: true,
                    PackageReadinessState.Ready,
                    pair.Value.Descriptor.Views,
                    FailureOrigin: null,
                    LastError: null,
                    LastFailureAtUtc: null,
                    FailureCount: 0),
                StringComparer.OrdinalIgnoreCase));
        await state.PublishSessionAsync(session);
        return (state, callbacks);
    }

    private static ActiveLoadedPackage CreateLoadedPackage(
        TestCallbackHandler handler,
        string packageId = "test.package",
        bool includeCallbackHandler = true)
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "sunder-runtime-host-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        return new ActiveLoadedPackage(
            new ActivePackageDescriptor(packageId, "Test Package", "1.0.0", PackageHostRoles.Runtime, null, true, PackageReadinessState.Ready, []),
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, tempDirectory),
            SettingsSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(tempDirectory, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(tempDirectory, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            new LocalPackageFileStore(Path.Combine(tempDirectory, "files")),
            EmptyTestPackageSettings.Instance,
            new TestRuntimePackageWorker(
                callbackHandlers: includeCallbackHandler
                    ? new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase)
                    {
                        [handler.CallbackHandlerId] = handler,
                    }
                    : null));
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
        bool blockCompletion = false,
        bool throwFromCompletion = false,
        string callbackHandlerId = "test.callback") : IPackageCallbackHandler
    {
        private readonly TaskCompletionSource _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellationCount;

        public string CallbackHandlerId { get; } = callbackHandlerId;
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
            if (throwFromCompletion)
            {
                throw new InvalidOperationException("Completion failed.");
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
