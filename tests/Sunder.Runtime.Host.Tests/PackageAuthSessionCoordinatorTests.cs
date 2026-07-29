using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageAuthSessionCoordinatorTests
{
    [Fact]
    public async Task StartPackageAuthAsync_ReusesLatestPendingSession()
    {
        var authHandler = new TestPackageAuthHandler();
        var loadedPackage = CreateLoadedPackage(authHandler);
        using var callbackServer = new PackageCallbackServer(
            NullLogger<PackageCallbackServer>.Instance,
            GetFreePort());
        var state = await CreateSessionStateAsync(loadedPackage);
        var callbacks = new PackageCallbackSessionCoordinator(state);
        var coordinator = new PackageAuthSessionCoordinator(
            state, callbacks,
            (_, _, _, _, _) => throw new InvalidOperationException("Unexpected package fault."));

        PackageAuthSessionStartResponse? first;
        PackageAuthSessionStartResponse? second;
        using (var lease = state.AcquireLease())
        {
            first = await coordinator.StartPackageAuthAsync(lease, "test.package", callbackServer);
        }
        using (var lease = state.AcquireLease())
        {
            second = await coordinator.StartPackageAuthAsync(lease, "test.package", callbackServer);
        }

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.AuthSessionId, second.AuthSessionId);
        Assert.Equal(1, authHandler.StartCount);
        Assert.Equal(Sunder.Runtime.Contracts.PackageAuthFlowKind.Browser, first.Flow);
        Assert.Equal("https://login.example.test", first.LaunchUrl);
        Assert.Equal(callbackServer.GetAuthenticationCallbackUri(), authHandler.CallbackUri);
        Assert.Equal("/auth/callback", authHandler.CallbackUri?.AbsolutePath);
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task CompletePackageAuthSessionAsync_UpdatesSessionStatus()
    {
        var authHandler = new TestPackageAuthHandler();
        var loadedPackage = CreateLoadedPackage(authHandler);
        using var callbackServer = new PackageCallbackServer(
            NullLogger<PackageCallbackServer>.Instance,
            GetFreePort());
        var state = await CreateSessionStateAsync(loadedPackage);
        var callbacks = new PackageCallbackSessionCoordinator(state);
        var coordinator = new PackageAuthSessionCoordinator(
            state, callbacks,
            (_, _, _, _, _) => throw new InvalidOperationException("Unexpected package fault."));
        PackageAuthSessionStartResponse? started;
        using (var lease = state.AcquireLease())
        {
            started = await coordinator.StartPackageAuthAsync(lease, "test.package", callbackServer);
        }

        bool completed;
        using (var lease = state.AcquireLease())
        {
            completed = await coordinator.CompletePackageAuthSessionAsync(
                lease,
                started!.AuthSessionId,
                new Dictionary<string, string?> { ["code"] = "abc" });
        }
        PackageAuthSessionStatusResponse? status;
        using (var lease = state.AcquireLease())
        {
            status = coordinator.GetPackageAuthSessionStatus(lease, "test.package", started.AuthSessionId);
        }

        Assert.True(completed);
        Assert.NotNull(status);
        Assert.Equal(Sunder.Runtime.Contracts.PackageAuthSessionState.Connected, status.State);
        Assert.Equal("Connected.", status.Message);
        Assert.Equal("abc", authHandler.CompletedCode);
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task GetPackageAuthStatusAsync_BlockedFailuresFromTwoPackagesDisableBothActivations()
    {
        var firstAuthHandler = new TestPackageAuthHandler(
            "first.package",
            blockStatus: true,
            statusFailure: new InvalidOperationException("First auth status failed."));
        var secondAuthHandler = new TestPackageAuthHandler(
            "second.package",
            blockStatus: true,
            statusFailure: new InvalidOperationException("Second auth status failed."));
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        await owner.PublishAsync(
            CreateSession(
                CreateLoadedPackage(firstAuthHandler, "first.package"),
                CreateLoadedPackage(secondAuthHandler, "second.package")),
            owner.Sources.Snapshot(),
            [],
            [],
            [],
            expectedGeneration: 0);
        var initialGeneration = owner.Generation;
        var firstLease = owner.State.AcquireLease();
        var secondLease = owner.State.AcquireLease();

        try
        {
            var firstStatusTask = owner.Auth.GetPackageAuthStatusAsync(firstLease, "first.package");
            var secondStatusTask = owner.Auth.GetPackageAuthStatusAsync(secondLease, "second.package");
            await Task.WhenAll(firstAuthHandler.StatusEntered, secondAuthHandler.StatusEntered)
                .WaitAsync(TimeSpan.FromSeconds(5));

            firstAuthHandler.ReleaseStatus();
            var firstStatus = await firstStatusTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(Sunder.Runtime.Contracts.PackageAuthStatusKind.Failed, firstStatus?.Status);
            Assert.Equal(initialGeneration + 1, owner.Generation);
            Assert.False(owner.State.GetSessionPackage("first.package")!.IsEnabled);
            Assert.True(owner.State.GetSessionPackage("second.package")!.IsEnabled);
            Assert.Equal(initialGeneration, secondLease.Generation);

            secondAuthHandler.ReleaseStatus();
            var secondStatus = await secondStatusTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(Sunder.Runtime.Contracts.PackageAuthStatusKind.Failed, secondStatus?.Status);
            Assert.Equal(initialGeneration + 2, owner.Generation);
            Assert.False(owner.State.GetSessionPackage("second.package")!.IsEnabled);
            Assert.Empty(owner.State.GetActivePackages());
        }
        finally
        {
            firstAuthHandler.ReleaseStatus();
            secondAuthHandler.ReleaseStatus();
            firstLease.Dispose();
            secondLease.Dispose();
            await owner.State.ClearActiveSessionAsync();
        }
    }

    [Fact]
    public async Task GetPackageAuthStatusAsync_RetiredActivationReportCannotFaultReplacement()
    {
        var staleException = new InvalidOperationException("Retired auth status failed.");
        var stalePackage = CreateLoadedPackage(
            new TestPackageAuthHandler(statusFailure: staleException));
        var replacementPackage = CreateLoadedPackage(new TestPackageAuthHandler());
        Assert.NotEqual(stalePackage.RuntimeActivationId, replacementPackage.RuntimeActivationId);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService());
        await owner.PublishAsync(
            CreateSession(stalePackage),
            owner.Sources.Snapshot(),
            [],
            [],
            [],
            expectedGeneration: 0);
        var staleLease = owner.State.AcquireLease();
        Func<bool>? applyStaleReport = null;
        var coordinator = new PackageAuthSessionCoordinator(
            owner.State,
            owner.Callbacks,
            (packageId, activationIdentity, origin, exception, action) =>
            {
                Assert.Same(staleLease.Identity, activationIdentity.SessionIdentity);
                Assert.Equal(stalePackage.RuntimeActivationId, activationIdentity.RuntimeActivationId);
                applyStaleReport = () => owner.State.HandlePackageFault(
                    packageId,
                    activationIdentity,
                    owner.State.Generation,
                    origin,
                    exception,
                    action);
            });

        try
        {
            var status = await coordinator.GetPackageAuthStatusAsync(staleLease, "test.package");
            Assert.Equal(Sunder.Runtime.Contracts.PackageAuthStatusKind.Failed, status?.Status);
            Assert.NotNull(applyStaleReport);

            staleLease.Dispose();
            await owner.PublishAsync(
                CreateSession(replacementPackage),
                owner.Sources.Snapshot(),
                [],
                [],
                [],
                expectedGeneration: 1);
            var replacementGeneration = owner.Generation;

            Assert.False(applyStaleReport!());
            Assert.Equal(replacementGeneration, owner.Generation);
            var replacement = owner.State.GetSessionPackage("test.package");
            Assert.True(replacement?.IsEnabled);
            Assert.Equal(PackageReadinessState.Ready, replacement?.Readiness);
            using (var replacementLease = owner.State.AcquireLease())
            {
                Assert.Same(
                    replacementPackage,
                    owner.State.GetLoadedPackage(replacementLease, "test.package"));
            }
        }
        finally
        {
            staleLease.Dispose();
            await owner.State.ClearActiveSessionAsync();
        }
    }

    [Fact]
    public async Task StartPackageAuthAsync_ParallelCallsShareOneProviderStart()
    {
        var authHandler = new TestPackageAuthHandler(blockStart: true);
        var loadedPackage = CreateLoadedPackage(authHandler);
        using var callbackServer = new PackageCallbackServer(NullLogger<PackageCallbackServer>.Instance, GetFreePort());
        var state = await CreateSessionStateAsync(loadedPackage);
        var callbacks = new PackageCallbackSessionCoordinator(state);
        var coordinator = new PackageAuthSessionCoordinator(state, callbacks, (_, _, _, _, _) => throw new InvalidOperationException("Unexpected package fault."));
        using var firstLease = state.AcquireLease();
        using var secondLease = state.AcquireLease();

        var firstTask = coordinator.StartPackageAuthAsync(firstLease, "test.package", callbackServer);
        await authHandler.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = coordinator.StartPackageAuthAsync(secondLease, "TEST.PACKAGE", callbackServer);
        Assert.Equal(1, authHandler.StartCount);

        authHandler.ReleaseStart();
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, authHandler.StartCount);
        Assert.Equal(results[0]!.AuthSessionId, results[1]!.AuthSessionId);
        firstLease.Dispose();
        secondLease.Dispose();
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task CompletePackageAuthSessionAsync_ParallelCallsClaimCompletionOnce()
    {
        var authHandler = new TestPackageAuthHandler(blockCompletion: true);
        var loadedPackage = CreateLoadedPackage(authHandler);
        using var callbackServer = new PackageCallbackServer(NullLogger<PackageCallbackServer>.Instance, GetFreePort());
        var state = await CreateSessionStateAsync(loadedPackage);
        var callbacks = new PackageCallbackSessionCoordinator(state);
        var coordinator = new PackageAuthSessionCoordinator(state, callbacks, (_, _, _, _, _) => throw new InvalidOperationException("Unexpected package fault."));
        PackageAuthSessionStartResponse started;
        using (var lease = state.AcquireLease())
        {
            started = (await coordinator.StartPackageAuthAsync(lease, "test.package", callbackServer))!;
        }
        using var firstLease = state.AcquireLease();
        using var secondLease = state.AcquireLease();

        var firstTask = coordinator.CompletePackageAuthSessionAsync(firstLease, started.AuthSessionId, new Dictionary<string, string?> { ["code"] = "first" });
        await authHandler.CompletionEntered.WaitAsync(TimeSpan.FromSeconds(5));
        var secondResult = await coordinator.CompletePackageAuthSessionAsync(secondLease, started.AuthSessionId, new Dictionary<string, string?> { ["code"] = "second" });
        authHandler.ReleaseCompletion();

        Assert.True(await firstTask);
        Assert.False(secondResult);
        Assert.Equal(1, authHandler.CompletionCount);
        Assert.Equal("first", authHandler.CompletedCode);
        firstLease.Dispose();
        secondLease.Dispose();
        await state.ClearActiveSessionAsync();
    }

    [Fact]
    public async Task ExpiredAndRemovedSessionsUnregisterCallbackHandlers()
    {
        var authHandler = new TestPackageAuthHandler();
        var loadedPackage = CreateLoadedPackage(authHandler);
        using var callbackServer = new PackageCallbackServer(NullLogger<PackageCallbackServer>.Instance, GetFreePort());
        var state = await CreateSessionStateAsync(loadedPackage);
        var callbacks = new PackageCallbackSessionCoordinator(state);
        var coordinator = new PackageAuthSessionCoordinator(state, callbacks, (_, _, _, _, _) => throw new InvalidOperationException("Unexpected package fault."));
        PackageAuthSessionStartResponse started;
        using (var lease = state.AcquireLease())
        {
            started = (await coordinator.StartPackageAuthAsync(lease, "test.package", callbackServer))!;
        }
        Assert.Equal(1, callbackServer.RegisteredHandlerCount);

        callbacks.SweepExpired(DateTimeOffset.UtcNow + new RuntimeAuthPolicyOptions().SessionLifetime + TimeSpan.FromSeconds(1));

        await authHandler.WaitForCancellationAsync();
        Assert.Equal(0, callbackServer.RegisteredHandlerCount);
        Assert.Contains(PackageCallbackCancellationReason.Expired, authHandler.CancellationReasons);
        using (var lease = state.AcquireLease())
        {
            Assert.Equal(
                Sunder.Runtime.Contracts.PackageAuthSessionState.Cancelled,
                coordinator.GetPackageAuthSessionStatus(lease, "test.package", started.AuthSessionId)?.State);
        }

        using (var lease = state.AcquireLease())
        {
            await coordinator.StartPackageAuthAsync(lease, "test.package", callbackServer);
        }
        callbacks.RemovePackageSessions("TEST.PACKAGE");
        await authHandler.WaitForCancellationAsync();
        Assert.Equal(0, callbackServer.RegisteredHandlerCount);
        Assert.Contains(PackageCallbackCancellationReason.PackageUnloaded, authHandler.CancellationReasons);

        using (var lease = state.AcquireLease())
        {
            await coordinator.StartPackageAuthAsync(lease, "test.package", callbackServer);
        }
        callbacks.Clear();
        await authHandler.WaitForCancellationAsync();
        Assert.Equal(0, callbackServer.RegisteredHandlerCount);
        await state.ClearActiveSessionAsync();
    }

    private static async Task<PackageSessionState> CreateSessionStateAsync(ActiveLoadedPackage loadedPackage)
    {
        var state = new PackageSessionState(NullLogger.Instance, static () => { }, static _ => { });
        await state.PublishSessionAsync(CreateSession(loadedPackage));
        return state;
    }

    private static ActivePackageSession CreateSession(params ActiveLoadedPackage[] loadedPackages)
        => new(
            sessionFolder: null,
            loadedPackages.ToDictionary(
                static package => package.Descriptor.PackageId,
                StringComparer.OrdinalIgnoreCase),
            loadedPackages.ToDictionary(
                static package => package.Descriptor.PackageId,
                static package => new SessionPackageDescriptor(
                    package.Descriptor.PackageId,
                    package.Descriptor.DisplayName,
                    package.Descriptor.Version,
                    package.Descriptor.HostRoles,
                    package.Descriptor.Icon,
                    IsEnabled: true,
                    PackageReadinessState.Ready,
                    package.Descriptor.Views,
                    FailureOrigin: null,
                    LastError: null,
                    LastFailureAtUtc: null,
                    FailureCount: 0),
                StringComparer.OrdinalIgnoreCase));

    private static ActiveLoadedPackage CreateLoadedPackage(
        IPackageAuthHandler authHandler,
        string packageId = "test.package")
    {
        var tempDirectory = CreateTempDirectory();
        var assemblyPath = typeof(PackageAuthSessionCoordinator).Assembly.Location;

        return new ActiveLoadedPackage(
            new ActivePackageDescriptor(packageId, "Test Package", "1.0.0", PackageHostRoles.Runtime, Icon: null, IsEnabled: true, PackageReadinessState.Ready, Views: []),
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, tempDirectory),
            SettingsSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(tempDirectory, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(tempDirectory, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            authHandler,
            new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase)
            {
                [((IPackageCallbackHandler)authHandler).CallbackHandlerId] = (IPackageCallbackHandler)authHandler,
            },
            BackgroundServices: [],
            new ServiceCollection().BuildServiceProvider(),
            new RuntimePackageLoadContext(
                packageId,
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])),
            EmptyTestPackageSettings.Instance);
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

    private sealed class TestPackageAuthHandler(
        string packageId = "test.package",
        bool blockStart = false,
        bool blockCompletion = false,
        bool blockStatus = false,
        Exception? statusFailure = null) : IPackageAuthHandler
    {
        private bool _connected;
        private readonly TaskCompletionSource _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _statusEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStatus = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startCount;
        private int _completionCount;
        private readonly List<PackageCallbackCancellationReason> _cancellationReasons = [];
        private readonly SemaphoreSlim _cancellationSignal = new(0);

        public int StartCount => Volatile.Read(ref _startCount);

        public int CompletionCount => Volatile.Read(ref _completionCount);

        public Task StartEntered => _startEntered.Task;

        public Task CompletionEntered => _completionEntered.Task;

        public Task StatusEntered => _statusEntered.Task;

        public string? CompletedCode { get; private set; }

        public Uri? CallbackUri { get; private set; }

        public IReadOnlyList<PackageCallbackCancellationReason> CancellationReasons
        {
            get
            {
                lock (_cancellationReasons)
                {
                    return _cancellationReasons.ToArray();
                }
            }
        }

        public async ValueTask<PackageAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            _statusEntered.TrySetResult();
            if (blockStatus)
            {
                await _releaseStatus.Task.WaitAsync(cancellationToken);
            }

            if (statusFailure is not null)
            {
                throw statusFailure;
            }

            return new PackageAuthStatus(
                packageId,
                _connected
                    ? Sunder.Sdk.Authentication.PackageAuthStatusKind.Connected
                    : Sunder.Sdk.Authentication.PackageAuthStatusKind.NotConnected,
                _connected ? "Connected." : "Not connected.",
                CanAuthorize: !_connected,
                CanDisconnect: _connected);
        }

        public async Task<PackageAuthSessionStartResult?> StartAuthorizationAsync(
            PackageAuthSessionStartContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            CallbackUri = context.CallbackUri;
            _startEntered.TrySetResult();
            if (blockStart)
            {
                await _releaseStart.Task.WaitAsync(cancellationToken);
            }
            return new PackageAuthSessionStartResult(
                packageId,
                context.AuthSessionId,
                Sunder.Sdk.Authentication.PackageAuthFlowKind.Browser,
                "https://login.example.test",
                "Open the browser.");
        }

        public async Task<PackageAuthStatus> CompleteAuthorizationAsync(
            PackageAuthSessionCompletionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _completionCount);
            _completionEntered.TrySetResult();
            if (blockCompletion)
            {
                await _releaseCompletion.Task.WaitAsync(cancellationToken);
            }
            CompletedCode = context.QueryValues.TryGetValue("code", out var code) ? code : null;
            _connected = true;
            return new PackageAuthStatus(
                packageId,
                Sunder.Sdk.Authentication.PackageAuthStatusKind.Connected,
                "Connected.",
                CanAuthorize: false,
                CanDisconnect: true);
        }

        public void ReleaseStart() => _releaseStart.TrySetResult();

        public void ReleaseCompletion() => _releaseCompletion.TrySetResult();

        public void ReleaseStatus() => _releaseStatus.TrySetResult();

        public async Task WaitForCancellationAsync()
        {
            if (!await _cancellationSignal.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The callback cancellation did not run.");
            }
        }

        public Task CancelCallbackAsync(
            PackageCallbackCancellationContext context,
            CancellationToken cancellationToken = default)
        {
            lock (_cancellationReasons)
            {
                _cancellationReasons.Add(context.Reason);
            }
            _cancellationSignal.Release();
            _releaseStart.TrySetCanceled(cancellationToken);
            _releaseCompletion.TrySetCanceled(cancellationToken);
            return Task.CompletedTask;
        }

        public Task<PackageAuthStatus> DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;
            return Task.FromResult(new PackageAuthStatus(
                    packageId,
                    Sunder.Sdk.Authentication.PackageAuthStatusKind.NotConnected,
                    "Disconnected.",
                    CanAuthorize: true,
                    CanDisconnect: false));
        }
    }
}
