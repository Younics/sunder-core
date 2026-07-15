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
    public async Task PackageSessionPublisher_StartsBackgroundServicesOnlyAfterGenerationCommit()
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
        var backgroundService = new TestBackgroundService(() => owner.Generation);
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

            var committed = await publisher.CommitAsync(pending);

            Assert.Equal(1, committed.Stamp.SessionGeneration);
            Assert.Equal(1, backgroundService.StartCount);
            Assert.Equal(1, backgroundService.GenerationObservedAtStart);
        }
        finally
        {
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

    private static ActiveLoadedPackage CreateLoadedPackage(
        string packageId,
        TestBackgroundService? backgroundService = null)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "sunder-runtime-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(Path.Combine(tempDirectory, "sunder-package.json"), "{}");
        var assemblyPath = typeof(PackageLoadPlanner).Assembly.Location;
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

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
            BackgroundServices: [backgroundService ?? new TestBackgroundService()],
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

    private sealed class TestBackgroundService(Func<long>? getGeneration = null) : IPackageBackgroundService
    {
        public int StartCount { get; private set; }

        public long? GenerationObservedAtStart { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            GenerationObservedAtStart = getGeneration?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private interface ITestContribution
    {
        string Name { get; }
    }

    private sealed record TestContribution(string Name) : ITestContribution;
}
