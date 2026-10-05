using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Runtime.Host.Tests;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Services;

public sealed class PackageSessionLoadConcurrencyTests
{
    [Fact]
    public async Task InstalledPreparation_RespectsConfiguredConcurrencyAndReleasesOnCancellation()
    {
        await using var temporary = TemporaryDirectoryScope.Create("sunder-package-preparation-concurrency-tests");
        var paths = new RuntimePackagePaths(temporary.GetPath("state"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var packages = CreatePackages(paths, includeRuntimeBindings: false);
        var projections = new PackageProjectionCache(paths.RootPath);
        var resolver = new BlockingExecutionRuntimeResolver(
            expectedConcurrency: 2,
            blockPreparation: true);
        var loader = CreateLoader(paths, projections, resolver, preparationConcurrency: 2, startupConcurrency: 4);
        using var cancellation = new CancellationTokenSource();
        var load = loader.LoadInstalledAsync(packages, cancellationToken: cancellation.Token);

        try
        {
            await resolver.WaitUntilBlockedAsync();
            Assert.Equal(2, resolver.ActiveCount);
            Assert.Equal(2, resolver.MaximumActiveCount);
            Assert.Equal(2, resolver.CallCount);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await load.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.Equal(0, resolver.ActiveCount);
        var recovery = await PrepareProjectionCleanupAsync(projections);
        Assert.Equal(0, recovery.ActiveLeaseCount);
    }

    [Fact]
    public async Task WorkerStartup_RespectsConfiguredConcurrencyAndReleasesProjectionsOnCancellation()
    {
        await using var temporary = TemporaryDirectoryScope.Create("sunder-worker-startup-concurrency-tests");
        var paths = new RuntimePackagePaths(temporary.GetPath("state"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var packages = CreatePackages(paths, includeRuntimeBindings: true);
        var projections = new PackageProjectionCache(paths.RootPath);
        var resolver = new BlockingExecutionRuntimeResolver(
            expectedConcurrency: 2,
            blockPreparation: false);
        var events = new RuntimeEventStreamService();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, events);
        using var permissions = new RuntimeRpcPermissionStore(paths);
        var broker = new RuntimeRpcBroker(new RuntimeRpcCatalog(), permissions, owner.State, owner);
        var loader = CreateLoader(
            paths,
            projections,
            resolver,
            preparationConcurrency: 4,
            startupConcurrency: 2,
            rpcBroker: broker,
            packageSessionState: owner.State);
        using var cancellation = new CancellationTokenSource();
        var load = loader.LoadInstalledAsync(packages, cancellationToken: cancellation.Token);

        try
        {
            await resolver.WaitUntilBlockedAsync();
            Assert.Equal(2, resolver.ActiveCount);
            Assert.Equal(2, resolver.MaximumActiveCount);
            Assert.Equal(2, resolver.CallCount);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await load.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.Equal(0, resolver.ActiveCount);
        var recovery = await PrepareProjectionCleanupAsync(projections);
        Assert.Equal(0, recovery.ActiveLeaseCount);
    }

    [Fact]
    public async Task WorkerStartup_ReportsSameWaveFailuresInPackageOrder()
    {
        await using var temporary = TemporaryDirectoryScope.Create("sunder-worker-failure-order-tests");
        var paths = new RuntimePackagePaths(temporary.GetPath("state"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var packages = CreatePackages(paths, includeRuntimeBindings: true);
        var projections = new PackageProjectionCache(paths.RootPath);
        var resolver = new OrderedFailingExecutionRuntimeResolver(packages.Count);
        var events = new RuntimeEventStreamService();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, events);
        using var permissions = new RuntimeRpcPermissionStore(paths);
        var broker = new RuntimeRpcBroker(new RuntimeRpcCatalog(), permissions, owner.State, owner);
        var loader = new PackageSessionLoadService(
            NullLogger<PackageSessionLoadService>.Instance,
            paths,
            rpcBroker: broker,
            packageSessionState: owner.State,
            executionRuntimeLaunchResolver: resolver,
            projectionCache: projections,
            concurrency: new PackageSessionConcurrencyOptions
            {
                MaximumPreparationConcurrency = packages.Count,
                MaximumWorkerStartupConcurrency = packages.Count,
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var load = loader.LoadInstalledAsync(packages, cancellationToken: cancellation.Token);

        await resolver.WaitUntilAllStartedAsync();
        var reversePackageIds = packages.Select(static package => package.PackageId).Reverse().ToArray();
        foreach (var packageId in reversePackageIds)
        {
            await resolver.FailAsync(packageId);
        }

        var result = await load;
        Assert.Equal(reversePackageIds, resolver.CompletedPackageIds);
        Assert.Equal(
            packages.Select(package => $"Failed to activate package '{package.PackageId}': failure-{package.PackageId}"),
            result.Errors);
        Assert.NotNull(result.Session);
        await result.Session.DisposeAsync();

        var recovery = await PrepareProjectionCleanupAsync(projections);
        Assert.Equal(0, recovery.ActiveLeaseCount);
    }

    [Fact]
    public async Task WorkerStartup_FirstFailureCancelsPendingStartsAndReportsOwningFault()
    {
        await using var temporary = TemporaryDirectoryScope.Create("sunder-worker-fail-fast-tests");
        var paths = new RuntimePackagePaths(temporary.GetPath("state"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var packages = CreatePackages(paths, includeRuntimeBindings: true);
        var projections = new PackageProjectionCache(paths.RootPath);
        var resolver = new FailFastExecutionRuntimeResolver();
        var events = new RuntimeEventStreamService();
        var owner = new RuntimeSessionOwner(NullLogger<RuntimeSessionOwner>.Instance, events);
        using var permissions = new RuntimeRpcPermissionStore(paths);
        var broker = new RuntimeRpcBroker(new RuntimeRpcCatalog(), permissions, owner.State, owner);
        var loader = new PackageSessionLoadService(
            NullLogger<PackageSessionLoadService>.Instance,
            paths,
            rpcBroker: broker,
            packageSessionState: owner.State,
            executionRuntimeLaunchResolver: resolver,
            projectionCache: projections,
            concurrency: new PackageSessionConcurrencyOptions
            {
                MaximumPreparationConcurrency = packages.Count,
                MaximumWorkerStartupConcurrency = 2,
            });

        var result = await loader.LoadInstalledAsync(packages);

        var failedPackageId = packages[1].PackageId;
        Assert.Equal(
            [$"Failed to activate package '{failedPackageId}': failure-{failedPackageId}"],
            result.Errors);
        Assert.Equal(
            packages.Take(2).Select(static package => package.PackageId).Order(StringComparer.Ordinal),
            resolver.StartedPackageIds.Order(StringComparer.Ordinal));
        Assert.Equal([packages[0].PackageId], resolver.CanceledPackageIds);
        Assert.NotNull(result.Session);
        Assert.Equal(packages.Count, result.Session.SessionPackageMap.Count);
        Assert.Contains(
            failedPackageId,
            result.Session.SessionPackageMap[packages[2].PackageId].LastError,
            StringComparison.Ordinal);
        await result.Session.DisposeAsync();

        var recovery = await PrepareProjectionCleanupAsync(projections);
        Assert.Equal(0, recovery.ActiveLeaseCount);
    }

    private static async Task<PackageProjectionLeaseRecoveryResult> PrepareProjectionCleanupAsync(
        PackageProjectionCache projections)
    {
        await using var reset = await projections.AcquireOfflineResetScopeAsync();
        await reset.ValidateAsync();
        var recovery = await reset.PrepareForDeletionAsync();
        if (Directory.Exists(projections.RootPath))
        {
            Directory.Delete(projections.RootPath, recursive: true);
        }
        return recovery;
    }

    private static PackageSessionLoadService CreateLoader(
        RuntimePackagePaths paths,
        PackageProjectionCache projections,
        BlockingExecutionRuntimeResolver resolver,
        int preparationConcurrency,
        int startupConcurrency,
        RuntimeRpcBroker? rpcBroker = null,
        PackageSessionState? packageSessionState = null)
        => new(
            NullLogger<PackageSessionLoadService>.Instance,
            paths,
            rpcBroker: rpcBroker,
            packageSessionState: packageSessionState,
            executionRuntimeLaunchResolver: resolver,
            ephemeralExecutionRuntimeBindingResolver: resolver,
            projectionCache: projections,
            concurrency: new PackageSessionConcurrencyOptions
            {
                MaximumPreparationConcurrency = preparationConcurrency,
                MaximumWorkerStartupConcurrency = startupConcurrency,
            });

    private static IReadOnlyList<InstalledPackageRecord> CreatePackages(
        RuntimePackagePaths paths,
        bool includeRuntimeBindings)
        => Enumerable.Range(0, 4).Select(index =>
        {
            var packageId = $"test.concurrent.package{index}";
            var installPath = paths.GetInstalledPackagePath(packageId, "1.0.0");
            CanonicalPackageTestBuilder.WriteExplodedPackage(
                installPath,
                packageId,
                "1.0.0",
                typeof(PackageSessionLoadConcurrencyTests).Assembly.Location,
                roles: [SunderPackageFormat.RuntimeHostRole]);
            var package = CanonicalPackageTestBuilder.CreateInstalledRecord(
                installPath,
                packageId,
                "1.0.0");
            return includeRuntimeBindings
                ? package
                : package with { ExecutionRuntimeBindings = [] };
        }).ToArray();

    private sealed class BlockingExecutionRuntimeResolver(
        int expectedConcurrency,
        bool blockPreparation)
        : IExecutionRuntimeLaunchResolver, ITestOnlyInstalledExecutionRuntimeBindingResolver
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource<bool> _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCount;
        private int _callCount;
        private int _maximumActiveCount;

        public int ActiveCount
        {
            get
            {
                lock (_sync) return _activeCount;
            }
        }

        public int CallCount
        {
            get
            {
                lock (_sync) return _callCount;
            }
        }

        public int MaximumActiveCount
        {
            get
            {
                lock (_sync) return _maximumActiveCount;
            }
        }

        public async Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
            PreparedRuntimePackage package,
            Guid activationId,
            CancellationToken cancellationToken)
        {
            if (!blockPreparation)
            {
                await BlockAsync(cancellationToken);
                throw new InvalidOperationException("The blocking launch resolver should only complete through cancellation.");
            }

            return await TestExecutionRuntimeLaunchResolver.Instance.ResolveAsync(
                package,
                activationId,
                cancellationToken);
        }

        public async Task<ResolvedEphemeralExecutionRuntimeBinding> ResolveEphemeralBindingAsync(
            SunderPackageTargetKey targetKey,
            SunderPackageTargetManifest target,
            CancellationToken cancellationToken)
        {
            if (blockPreparation)
            {
                await BlockAsync(cancellationToken);
                throw new InvalidOperationException("The blocking preparation resolver should only complete through cancellation.");
            }

            return await TestExecutionRuntimeLaunchResolver.Instance.ResolveEphemeralBindingAsync(
                targetKey,
                target,
                cancellationToken);
        }

        public Task WaitUntilBlockedAsync() => _blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        private async Task BlockAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _activeCount++;
                _callCount++;
                _maximumActiveCount = Math.Max(_maximumActiveCount, _activeCount);
                if (_activeCount == expectedConcurrency)
                {
                    _blocked.TrySetResult(true);
                }
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                lock (_sync) _activeCount--;
            }
        }
    }

    private sealed class OrderedFailingExecutionRuntimeResolver(int expectedCount) : IExecutionRuntimeLaunchResolver
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource<bool> _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, PendingFailure> _pending = new(StringComparer.Ordinal);
        private readonly List<string> _completedPackageIds = [];

        public IReadOnlyList<string> CompletedPackageIds
        {
            get
            {
                lock (_sync) return _completedPackageIds.ToArray();
            }
        }

        public async Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
            PreparedRuntimePackage package,
            Guid activationId,
            CancellationToken cancellationToken)
        {
            var pending = new PendingFailure();
            lock (_sync)
            {
                _pending.Add(package.PackageId, pending);
                if (_pending.Count == expectedCount)
                {
                    _allStarted.TrySetResult(true);
                }
            }

            // Keep every in-flight failure observable even after the first failure cancels the wave.
            await pending.Release.Task;
            lock (_sync) _completedPackageIds.Add(package.PackageId);
            pending.Completed.TrySetResult(true);
            throw new InvalidOperationException($"failure-{package.PackageId}");
        }

        public Task WaitUntilAllStartedAsync() => _allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public async Task FailAsync(string packageId)
        {
            PendingFailure pending;
            lock (_sync) pending = _pending[packageId];
            pending.Release.TrySetResult(true);
            await pending.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private sealed class PendingFailure
        {
            public TaskCompletionSource<bool> Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<bool> Completed { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class FailFastExecutionRuntimeResolver : IExecutionRuntimeLaunchResolver
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource<bool> _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _startedPackageIds = [];
        private readonly List<string> _canceledPackageIds = [];

        public IReadOnlyList<string> StartedPackageIds
        {
            get
            {
                lock (_sync) return _startedPackageIds.ToArray();
            }
        }

        public IReadOnlyList<string> CanceledPackageIds
        {
            get
            {
                lock (_sync) return _canceledPackageIds.ToArray();
            }
        }

        public async Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
            PreparedRuntimePackage package,
            Guid activationId,
            CancellationToken cancellationToken)
        {
            lock (_sync) _startedPackageIds.Add(package.PackageId);
            if (package.PackageId.EndsWith("0", StringComparison.Ordinal))
            {
                _firstStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (_sync) _canceledPackageIds.Add(package.PackageId);
                    throw;
                }
            }

            if (package.PackageId.EndsWith("1", StringComparison.Ordinal))
            {
                await _firstStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException($"failure-{package.PackageId}");
            }

            throw new InvalidOperationException($"Unexpected pending launch for '{package.PackageId}'.");
        }
    }
}
