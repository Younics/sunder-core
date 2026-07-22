using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ActivePackageSessionTests
{
    [Fact]
    public void MarkPackageFailed_DisablesSessionPackageAndReturnsLoadedPackageForDeactivation()
    {
        var loadedPackage = CreateLoadedPackage("test.package");
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            });

        var marked = session.MarkPackageFailed(
            "test.package",
            PackageFailureOrigin.RuntimeActivation,
            "Activation failed.",
            out var packageToDeactivate);

        Assert.True(marked);
        Assert.Same(loadedPackage, packageToDeactivate);
        Assert.Empty(session.GetActivePackages());
        Assert.False(session.TryGetLoadedPackage("test.package", out _));

        var failedPackage = Assert.Single(session.GetSessionPackages());
        Assert.False(failedPackage.IsEnabled);
        Assert.Equal(PackageReadinessState.Failed, failedPackage.Readiness);
        Assert.Equal(PackageFailureOrigin.RuntimeActivation, failedPackage.FailureOrigin);
        Assert.Equal("Activation failed.", failedPackage.LastError);
        Assert.Equal(1, failedPackage.FailureCount);
        Assert.NotNull(failedPackage.LastFailureAtUtc);
    }

    [Fact]
    public void MarkPackageFailed_RemovesPackageContributionsFromRuntimeCatalog()
    {
        var extensionPoint = new PackageExtensionPoint<ITestContribution>("test:contribution");
        var extensionCatalog = new RuntimePackageExtensionCatalog();
        extensionCatalog.Add("test.package", extensionPoint, new TestContribution("test"));
        extensionCatalog.Add("other.package", extensionPoint, new TestContribution("other"));
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateLoadedPackage("test.package"),
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            },
            extensionCatalog);

        session.MarkPackageFailed(
            "TEST.PACKAGE",
            PackageFailureOrigin.RuntimeActivation,
            "Activation failed.",
            out _);

        var contribution = Assert.Single(extensionCatalog.GetExtensions(extensionPoint));
        Assert.Equal("other", contribution.Name);
    }

    [Fact]
    public void DisableInstalledPackage_DisablesPackageAndReturnsLoadedPackageForDeactivation()
    {
        var loadedPackage = CreateLoadedPackage("test.package");
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            });

        var disabled = session.DisableInstalledPackage("test.package", out var packageToDeactivate);

        Assert.True(disabled);
        Assert.Same(loadedPackage, packageToDeactivate);
        var sessionPackage = Assert.Single(session.GetSessionPackages());
        Assert.False(sessionPackage.IsEnabled);
        Assert.Equal(PackageReadinessState.Disabled, sessionPackage.Readiness);
        Assert.False(session.TryGetLoadedPackage("test.package", out _));
    }

    [Fact]
    public void RemovePackage_RemovesSessionAndLoadedPackage()
    {
        var loadedPackage = CreateLoadedPackage("test.package");
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            });

        var removed = session.RemovePackage("test.package", out var packageToDeactivate);

        Assert.True(removed);
        Assert.Same(loadedPackage, packageToDeactivate);
        Assert.Empty(session.GetSessionPackages());
        Assert.False(session.TryGetLoadedPackage("test.package", out _));
    }

    [Fact]
    public async Task DisposeAsync_ReleasesSessionFolder()
    {
        var sessionFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionFolder);
        try
        {
            var session = new ActivePackageSession(
                sessionFolder,
                new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase));

            await session.DisposeAsync();

            Assert.False(Directory.Exists(sessionFolder));
        }
        finally
        {
            TryDeleteDirectory(sessionFolder);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_CommitsGenerationOnlyAfterBackgroundServicesStart()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        var events = new RuntimeEventStreamService();
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            events,
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var publisher = new PackageSessionPublisher(owner, ui, NullLogger<PackageSessionPublisher>.Instance);
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backgroundService = new TestBackgroundService(
            () => owner.Generation,
            startEntered,
            allowStart.Task);
        var loadedPackage = CreateLoadedPackage("test.package", backgroundService);
        var session = new ActivePackageSession(
            sessionFolder: null,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test.package"] = CreateSessionPackage("test.package", isEnabled: true),
            },
            backgroundServicesStarted: false);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            Assert.Equal(0, backgroundService.StartCount);
            Assert.Equal(0, owner.Generation);

            var commit = publisher.CommitAsync(pending);
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, owner.Generation);
            allowStart.TrySetResult();
            var committed = await commit;

            Assert.Equal(1, committed.Stamp.SessionGeneration);
            Assert.Equal(1, backgroundService.StartCount);
            Assert.Equal(0, backgroundService.GenerationObservedAtStart);
        }
        finally
        {
            allowStart.TrySetResult();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_StartupTimeoutDoesNotPublishAndStopsAttemptedService()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageBackgroundServiceStartupTimeout = TimeSpan.FromMilliseconds(50),
            PackageBackgroundServiceCleanupTimeout = TimeSpan.FromMilliseconds(200),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            ui,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var backgroundService = new HangingStartBackgroundService();
        var session = CreateSession("test.package", backgroundService);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            var exception = await Assert.ThrowsAsync<TimeoutException>(() => publisher.CommitAsync(pending));

            Assert.Contains("did not start", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, owner.Generation);
            Assert.Equal(1, backgroundService.StopCount);
            using var lease = owner.State.AcquireLease();
            Assert.Equal(0, lease.Generation);
        }
        finally
        {
            backgroundService.Release();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_StartupTimeoutQuarantinesSessionUntilIgnoredStartCompletes()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var sessionFolder = Path.Combine(rootPath, "candidate-session");
        Directory.CreateDirectory(sessionFolder);
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageBackgroundServiceStartupTimeout = TimeSpan.FromMilliseconds(40),
            PackageBackgroundServiceCleanupTimeout = TimeSpan.FromMilliseconds(40),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            ui,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var backgroundService = new CancellationIgnoringStartBackgroundService();
        var serviceProvider = new TrackingServiceProvider();
        var session = CreateSession(
            "test.package",
            sessionFolder,
            serviceProvider,
            false,
            backgroundService);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            await Assert.ThrowsAsync<TimeoutException>(() => publisher.CommitAsync(pending));

            Assert.Equal(0, owner.Generation);
            Assert.Equal(1, backgroundService.StopCount);
            Assert.True(Directory.Exists(sessionFolder));
            Assert.False(serviceProvider.IsDisposed);

            backgroundService.ReleaseStart();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));

            Assert.True(serviceProvider.IsDisposed);
            Assert.False(Directory.Exists(sessionFolder));
        }
        finally
        {
            backgroundService.ReleaseStart();
            await session.DisposeAsync(TimeSpan.FromSeconds(2));
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task PublishSessionAsync_StopTimeoutQuarantinesRetiredSessionUntilIgnoredStopCompletes()
    {
        var state = new PackageSessionState(
            NullLogger.Instance,
            static () => { },
            static _ => { },
            TimeSpan.FromMilliseconds(50));
        var oldFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var newFolder = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(oldFolder);
        Directory.CreateDirectory(newFolder);
        var backgroundService = new CancellationIgnoringStopBackgroundService();
        var serviceProvider = new TrackingServiceProvider();
        var oldSession = CreateSession(
            "old.package",
            oldFolder,
            serviceProvider,
            true,
            backgroundService);
        var newSession = new ActivePackageSession(
            newFolder,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase));

        try
        {
            await state.PublishSessionAsync(oldSession);

            var publication = await state.PublishSessionAsync(newSession);

            Assert.Contains(publication.Warnings, warning => warning.Contains("quarantined", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, backgroundService.StopCount);
            Assert.True(Directory.Exists(oldFolder));
            Assert.False(serviceProvider.IsDisposed);

            backgroundService.ReleaseStop();
            await oldSession.DisposeAsync(TimeSpan.FromSeconds(2));

            Assert.True(serviceProvider.IsDisposed);
            Assert.False(Directory.Exists(oldFolder));
        }
        finally
        {
            backgroundService.ReleaseStop();
            await oldSession.DisposeAsync(TimeSpan.FromSeconds(2));
            await state.ClearActiveSessionAsync();
            TryDeleteDirectory(oldFolder);
            TryDeleteDirectory(newFolder);
        }
    }

    [Fact]
    public async Task PackageSessionPublisher_FailingServiceCleanupAttemptsAllServicesAndIsBounded()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(rootPath);
        using var snapshots = new PackageUiSnapshotStore(paths);
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            new RuntimeEventStreamService(),
            uiSnapshots: snapshots);
        var ui = new RuntimePackageUiService(owner, snapshots, new InstalledPackageStore(paths));
        var policy = new RuntimeLifecyclePolicyOptions
        {
            PackageBackgroundServiceStartupTimeout = TimeSpan.FromSeconds(1),
            PackageBackgroundServiceCleanupTimeout = TimeSpan.FromMilliseconds(50),
        };
        var publisher = new PackageSessionPublisher(
            owner,
            ui,
            NullLogger<PackageSessionPublisher>.Instance,
            policy);
        var startedService = new TestBackgroundService();
        var failingService = new FailingStartBackgroundService();
        var session = CreateSession("test.package", startedService, failingService);

        try
        {
            var candidate = publisher.Prepare(session, owner.Sources.Snapshot(), [], [], baseGeneration: 0);
            var pending = await publisher.BeginPublishAsync(candidate, CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => publisher.CommitAsync(pending).WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(0, owner.Generation);
            Assert.Equal(1, startedService.StopCount);
            Assert.Equal(1, failingService.StopCount);
        }
        finally
        {
            failingService.Release();
            await owner.State.ClearActiveSessionAsync();
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public void CleanupStaleSessions_RemovesFoldersWithoutRunningOwner()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        var activeFolder = Path.Combine(rootPath, $"20260511190000-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var staleFolder = Path.Combine(rootPath, $"20260511190001-{int.MaxValue}-{Guid.NewGuid():N}");
        var legacyFolder = Path.Combine(rootPath, $"20260511190002-{Guid.NewGuid():N}");
        Directory.CreateDirectory(activeFolder);
        Directory.CreateDirectory(staleFolder);
        Directory.CreateDirectory(legacyFolder);
        try
        {
            RuntimePackageSessionDirectories.CleanupStaleSessions(rootPath);

            Assert.True(Directory.Exists(activeFolder));
            Assert.False(Directory.Exists(staleFolder));
            Assert.False(Directory.Exists(legacyFolder));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    private static ActivePackageSession CreateSession(
        string packageId,
        params IPackageBackgroundService[] backgroundServices)
        => CreateSession(
            packageId,
            sessionFolder: null,
            serviceProvider: null,
            backgroundServicesStarted: false,
            backgroundServices: backgroundServices);

    private static ActivePackageSession CreateSession(
        string packageId,
        string? sessionFolder,
        IServiceProvider? serviceProvider,
        bool backgroundServicesStarted,
        params IPackageBackgroundService[] backgroundServices)
    {
        var loadedPackage = CreateLoadedPackageCore(packageId, sessionFolder, serviceProvider, backgroundServices);
        return new ActivePackageSession(
            sessionFolder,
            new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = loadedPackage,
            },
            new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                [packageId] = CreateSessionPackage(packageId, isEnabled: true),
            },
            backgroundServicesStarted: backgroundServicesStarted);
    }

    private static ActiveLoadedPackage CreateLoadedPackage(
        string packageId,
        params IPackageBackgroundService[] backgroundServices)
        => CreateLoadedPackageCore(
            packageId,
            sessionFolder: null,
            serviceProvider: null,
            backgroundServices: backgroundServices);

    private static ActiveLoadedPackage CreateLoadedPackageCore(
        string packageId,
        string? sessionFolder,
        IServiceProvider? serviceProvider,
        IReadOnlyList<IPackageBackgroundService> backgroundServices)
    {
        var tempDirectory = sessionFolder is null
            ? Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"))
            : Path.Combine(sessionFolder, "package-content");
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(Path.Combine(tempDirectory, "sunder-package.json"), "{}");
        var assemblyPath = typeof(PackageLoadPlanner).Assembly.Location;
        serviceProvider ??= new ServiceCollection().BuildServiceProvider();

        return new ActiveLoadedPackage(
            CreateActivePackage(packageId, isEnabled: true, PackageReadinessState.Ready),
            new RuntimePackageSource(packageId, PackageSourceKind.Dev, tempDirectory),
            SettingsSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(tempDirectory, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(tempDirectory, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            AuthHandler: null,
            CallbackHandlers: new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
            BackgroundServices: backgroundServices.Count == 0
                ? [new TestBackgroundService()]
                : backgroundServices,
            serviceProvider,
            new RuntimePackageLoadContext(
                packageId,
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])),
            EmptyTestPackageSettings.Instance);
    }

    private static ActivePackageDescriptor CreateActivePackage(
        string packageId,
        bool isEnabled,
        PackageReadinessState readiness)
        => new(packageId, packageId, "1.0.0", PackageHostRoles.Runtime, Icon: null, isEnabled, readiness, Views: []);

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static SessionPackageDescriptor CreateSessionPackage(string packageId, bool isEnabled)
        => new(
            packageId,
            packageId,
            "1.0.0",
            PackageHostRoles.Runtime,
            Icon: null,
            isEnabled,
            isEnabled ? PackageReadinessState.Ready : PackageReadinessState.Failed,
            Views: [],
            FailureOrigin: null,
            LastError: null,
            LastFailureAtUtc: null,
            FailureCount: 0);

    private sealed class TestBackgroundService(
        Func<long>? getGeneration = null,
        TaskCompletionSource? startEntered = null,
        Task? allowStart = null) : IPackageBackgroundService
    {
        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public long? GenerationObservedAtStart { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            GenerationObservedAtStart = getGeneration?.Invoke();
            startEntered?.TrySetResult();
            if (allowStart is not null)
            {
                await allowStart.WaitAsync(cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class HangingStartBackgroundService : IPackageBackgroundService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => _release.Task;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            _release.TrySetResult();
            return Task.CompletedTask;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class CancellationIgnoringStartBackgroundService : IPackageBackgroundService
    {
        private readonly TaskCompletionSource _startRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => _startRelease.Task;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void ReleaseStart() => _startRelease.TrySetResult();
    }

    private sealed class CancellationIgnoringStopBackgroundService : IPackageBackgroundService
    {
        private readonly TaskCompletionSource _stopRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return _stopRelease.Task;
        }

        public void ReleaseStop() => _stopRelease.TrySetResult();
    }

    private sealed class TrackingServiceProvider : IServiceProvider, IAsyncDisposable
    {
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public object? GetService(Type serviceType) => null;

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingStartBackgroundService : IPackageBackgroundService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Background service startup failed.");

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            await _release.Task;
        }

        public void Release() => _release.TrySetResult();
    }

    private interface ITestContribution
    {
        string Name { get; }
    }

    private sealed record TestContribution(string Name) : ITestContribution;
}
