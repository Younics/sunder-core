using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageDevelopmentServiceTests
{
    [Theory]
    [InlineData(PackageReadinessState.Ready, "Activated", true, true, true)]
    [InlineData(PackageReadinessState.Degraded, "Activated", true, true, true)]
    [InlineData(PackageReadinessState.Ready, "Pending", true, true, false)]
    [InlineData(PackageReadinessState.Ready, "Quarantined", true, true, false)]
    [InlineData(PackageReadinessState.Ready, "Activated", false, true, false)]
    [InlineData(PackageReadinessState.Failed, "Activated", true, true, false)]
    [InlineData(PackageReadinessState.Ready, "Pending", false, false, true)]
    public async Task AppliedRevisionRequiresReadyRuntimeAndMatchingPublishedAppSource(PackageReadinessState state,
        string appReadiness, bool sameAppSource, bool hasApp, bool applied)
    {
        var identity = new string('a', 64);
        var backend = new Backend { HasApp = hasApp, State = state, AppReadiness = appReadiness, SourceContentIdentity = identity,
            AppliedContentIdentity = sameAppSource ? identity : new string('b', 64) };
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await using var session = await service.LoadAsync("first", true);
        var status = await session.GetStatusAsync();
        var expected = new PackageDevelopmentRevision(backend.RuntimeId, 4, identity);
        Assert.Equal(expected, status.RuntimeRevision);
        Assert.Equal(applied ? expected : null, status.AppliedRevision);
        backend.SnapshotRuntimeId = Guid.NewGuid();
        status = await session.GetStatusAsync();
        Assert.Null(status.RuntimeRevision);
        Assert.Null(status.AppliedRevision);
    }

    [Fact]
    public async Task MissingOldHostRevisionMetadataIsUnknownButMalformedNonNullMetadataIsRejected()
    {
        var backend = new Backend { HasApp = true, State = PackageReadinessState.Ready, AppReadiness = "Activated" };
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await using var session = await service.LoadAsync("first", true);
        var status = await session.GetStatusAsync();
        Assert.Equal("Activated", status.AppReadiness);
        Assert.Null(status.RuntimeRevision);
        Assert.Null(status.AppliedRevision);
        backend.SourceContentIdentity = "not-a-content-identity";
        await Assert.ThrowsAsync<InvalidDataException>(() => session.GetStatusAsync());
        backend.SourceContentIdentity = new string('a', 64);
        backend.AppliedContentIdentity = "invalid-app-source";
        await Assert.ThrowsAsync<InvalidDataException>(() => session.GetStatusAsync());
    }

    [Fact]
    public async Task PublishedActivationOwnsOneLeasePerFolderAndRevocationDisposesAllRemainingOwners()
    {
        var backend = new Backend();
        var publication = new AppPackageGenerationPublication();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadAsync("first", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenPackageLogsAsync("demo"));
        Assert.Empty(backend.Owners);
        publication.Publish();
        var first = await service.LoadAsync("first", true);
        Assert.Same(first, await service.LoadAsync(Path.GetFullPath("first") + Path.DirectorySeparatorChar, false));
        Assert.False(Assert.Single(backend.Owners).Watch);
        var second = await service.LoadAsync("second", true);
        await first.DisposeAsync();
        Assert.Equal(1, backend.Owners[0].Disposals);
        Assert.Equal(0, backend.Owners[1].Disposals);
        publication.Revoke();
        // Publication fences new work immediately. The accepted lease stays alive
        // until the owning container has drained its jobs and disposes this service.
        Assert.Equal(0, backend.Owners[1].Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second.GetStatusAsync());
        await service.DisposeAsync();
        Assert.All(backend.Owners, owner => Assert.Equal(1, owner.Disposals));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => second.GetStatusAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.OpenPackageLogsAsync("demo"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrRevokedAcquisitionReleasesEvenWhenHostAlreadyCommitted(bool revoke)
    {
        var backend = new Backend { BlockAcquire = true };
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var load = service.LoadAsync("first", true, cancellation.Token);
        await backend.Acquiring.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (revoke) publication.Revoke(); else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.Equal(1, Assert.Single(backend.Owners).Disposals);
    }

    [Fact]
    public async Task StatusObservesHostFailuresAndRejectsSnapshotFromDifferentRuntime()
    {
        var backend = new Backend();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await using var session = await service.LoadAsync("first", true);
        var status = await session.GetStatusAsync();
        Assert.True(status.IsLoaded);
        Assert.True(status.OverridesInstalledPackage);
        Assert.Equal("Runtime:Failed", status.Readiness);
        Assert.Equal("Worker failed", status.ErrorMessage);
        backend.SnapshotRuntimeId = Guid.NewGuid();
        status = await session.GetStatusAsync();
        Assert.False(status.IsLoaded);
        Assert.False(status.OverridesInstalledPackage);
        Assert.Equal("Runtime:Reconnecting", status.Readiness);
    }

    [Fact]
    public async Task StatusSeparatesRequestedWatchEffectiveWatchAndPendingAppActivation()
    {
        var backend = new Backend { HasApp = true };
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await using var session = await service.LoadAsync("first", false);
        var status = await session.GetStatusAsync();
        Assert.True(status.WatchEnabled);
        Assert.False(status.RequestedWatch);
        Assert.Equal("Pending", status.AppReadiness);
    }

    [Fact]
    public async Task PrecancelledLoadDoesNotPrepareOrAcquire()
    {
        var backend = new Backend();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync("first", true, new CancellationToken(true)));
        Assert.Equal(0, backend.Preparations);
        Assert.Empty(backend.Owners);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAcknowledgedReleaseKeepsExactOwnerForRetry(bool activationDisposal)
    {
        var backend = new Backend { RejectRelease = true };
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        var session = await service.LoadAsync("first", true);
        var owner = Assert.Single(backend.Owners);
        if (activationDisposal)
        {
            publication.Revoke();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DisposeAsync().AsTask());
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.LoadAsync("first", false));
            Assert.Same(owner, Assert.Single(backend.Owners));
        }
        Assert.Equal(0, owner.Disposals);
        backend.RejectRelease = false;
        if (activationDisposal) await service.DisposeAsync();
        else await session.DisposeAsync();
        Assert.Equal(1, owner.Disposals);
    }

    [Theory]
    [InlineData("snapshot", "publication")]
    [InlineData("readiness", "publication")]
    [InlineData("throw", "publication")]
    [InlineData("snapshot", "caller")]
    [InlineData("readiness", "caller")]
    [InlineData("throw", "caller")]
    [InlineData("readiness", "session")]
    [InlineData("readiness", "service")]
    [InlineData("readiness", "both")]
    public async Task OwnedStatusCancellationReportsCallerStopOrRetiredService(string boundary, string stop)
    {
        var backend = new Backend { HasApp = true };
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await using var session = await service.LoadAsync("first", true);
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeStatusAsync = async (point, token) =>
        {
            if (point != (boundary == "snapshot" ? "snapshot" : "readiness")) return;
            entered.TrySetResult(token);
            // A non-cooperative reply exercises the final post-backend cancellation
            // check; "throw" exercises an awaited backend's own cancellation instead.
            await resume.Task;
            if (boundary == "throw") token.ThrowIfCancellationRequested();
        };
        var status = session.GetStatusAsync(caller.Token);
        try
        {
            var operationToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(caller.Token, operationToken);
            Task? retirement = null;
            if (stop is "publication" or "both") publication.Revoke();
            if (stop is "caller" or "both") caller.Cancel();
            if (stop == "session") retirement = session.DisposeAsync().AsTask();
            if (stop == "service") retirement = service.DisposeAsync().AsTask();
            Assert.True(operationToken.IsCancellationRequested);
            Assert.False(status.IsCompleted);
            Assert.Equal(0, Assert.Single(backend.Owners).Disposals);
            resume.SetResult();

            if (stop is "caller" or "both")
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => status.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(caller.Token, error.CancellationToken);
                Assert.True(error.CancellationToken.IsCancellationRequested);
                Assert.Equal(operationToken, Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException).CancellationToken);
            }
            else
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() => status.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.False(caller.IsCancellationRequested);
                // The monitor's public stop happens separately, after retirement.
                caller.Cancel();
            }
            if (retirement is not null) await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            else Assert.Equal(0, Assert.Single(backend.Owners).Disposals);
        }
        finally { resume.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusCancellationWhileWaitingForGateKeepsCallerIdentityAndOwnerLease(bool cancelCaller)
    {
        var backend = new Backend();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await using var session = await service.LoadAsync("first", true);
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeStatusAsync = async (_, _) => { entered.TrySetResult(); await resume.Task; };
        var holdingGate = session.GetStatusAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiting = session.GetStatusAsync(caller.Token);
            Assert.False(waiting.IsCompleted);
            if (cancelCaller) caller.Cancel(); else publication.Revoke();
            if (cancelCaller)
            {
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(caller.Token, error.CancellationToken);
            }
            else
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.False(caller.IsCancellationRequested);
            }
            Assert.False(holdingGate.IsCompleted);
            Assert.Equal(0, Assert.Single(backend.Owners).Disposals);
            resume.SetResult();
            if (cancelCaller) Assert.True((await holdingGate.WaitAsync(TimeSpan.FromSeconds(5))).IsLoaded);
            else await Assert.ThrowsAsync<ObjectDisposedException>(() => holdingGate.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { resume.TrySetResult(); }
    }

    [Theory]
    [InlineData("foreign", false)]
    [InlineData("foreign", true)]
    [InlineData("tokenless", false)]
    [InlineData("tokenless", true)]
    [InlineData("fault", false)]
    [InlineData("fault", true)]
    public async Task StatusRetirementPreservesUnrelatedBackendFailureEvenWhenCallerAlsoStops(string failure, bool cancelCaller)
    {
        var backend = new Backend();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await using var session = await service.LoadAsync("first", true);
        using var caller = new CancellationTokenSource();
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        Exception expected = failure switch
        {
            "foreign" => new OperationCanceledException("Unrelated backend stop.", foreign.Token),
            "tokenless" => new OperationCanceledException("Unattributed backend stop."),
            _ => new InvalidDataException("Invalid backend snapshot.")
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeStatusAsync = async (_, _) => { entered.TrySetResult(); await resume.Task; throw expected; };
        var status = session.GetStatusAsync(caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            publication.Revoke();
            if (cancelCaller) caller.Cancel();
            resume.SetResult();
            Assert.Same(expected, await Record.ExceptionAsync(() => status.WaitAsync(TimeSpan.FromSeconds(5))));
            Assert.Equal(cancelCaller, caller.IsCancellationRequested);
            Assert.Equal(0, Assert.Single(backend.Owners).Disposals);
        }
        finally { resume.TrySetResult(); }
    }

    [Fact]
    public async Task StatusUncancelledLinkedTokenDoesNotAuthorizeCancellationTranslation()
    {
        var backend = new Backend();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var service = new AppPackageDevelopmentService(backend, publication, Guid.NewGuid());
        await using var session = await service.LoadAsync("first", true);
        using var caller = new CancellationTokenSource();
        OperationCanceledException? expected = null;
        backend.BeforeStatusAsync = (_, token) => Task.FromException(expected = new OperationCanceledException("Backend did not cancel the operation.", token));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetStatusAsync(caller.Token));
        Assert.Same(expected, error);
        Assert.False(error.CancellationToken.IsCancellationRequested);
        Assert.NotEqual(caller.Token, error.CancellationToken);
        Assert.Equal(0, Assert.Single(backend.Owners).Disposals);
    }

    private sealed class Backend : IAppPackageDevelopmentBackend
    {
        internal Guid RuntimeId { get; } = Guid.NewGuid();
        internal Guid? SnapshotRuntimeId { get; set; }
        internal List<Owner> Owners { get; } = [];
        internal bool BlockAcquire { get; init; }
        internal bool HasApp { get; init; }
        internal PackageReadinessState State { get; init; } = PackageReadinessState.Failed;
        internal string AppReadiness { get; init; } = "Pending";
        internal string? SourceContentIdentity { get; set; }
        internal string? AppliedContentIdentity { get; set; }
        internal bool RejectRelease { get; set; }
        internal Func<string, CancellationToken, Task>? BeforeStatusAsync { get; set; }
        internal int Preparations { get; private set; }
        internal TaskCompletionSource Acquiring { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDevPackageOwnerSession CreateOwner(string ownerPrefix)
        {
            Assert.StartsWith("app-development-", ownerPrefix);
            var owner = new Owner(this);
            Owners.Add(owner);
            return owner;
        }
        public Task<string> PrepareAsync(string folder, CancellationToken token) { Preparations++; return Task.FromResult("demo"); }
        public Task<bool> IsInstalledAsync(string packageId, CancellationToken token) => Task.FromResult(true);
        public async Task<string> GetAppReadinessAsync(string packageId, RuntimePackageStamp stamp, CancellationToken token)
        {
            if (BeforeStatusAsync is not null) await BeforeStatusAsync("readiness", token);
            return AppReadiness;
        }
        public async Task<AppDevelopmentApplicationObservation> GetAppObservationAsync(string packageId, RuntimePackageStamp stamp,
            string? sourceContentIdentity, CancellationToken token)
            => new(await GetAppReadinessAsync(packageId, stamp, token), AppliedContentIdentity);
        public async Task<RuntimePackageSnapshot> GetSnapshotAsync(CancellationToken token)
        {
            if (BeforeStatusAsync is not null) await BeforeStatusAsync("snapshot", token);
            return new(SnapshotRuntimeId ?? RuntimeId, 4, 1, RuntimeBootstrapState.Ready, [],
                [new("demo", "Demo", "1.0.0", HasApp ? PackageHostRoles.App | PackageHostRoles.Runtime : PackageHostRoles.Runtime, null, true, State, [],
                    State == PackageReadinessState.Failed ? PackageFailureOrigin.RuntimeProcess : null,
                    State == PackageReadinessState.Failed ? "Worker failed" : null, null,
                    State == PackageReadinessState.Failed ? 1 : 0) { SourceContentIdentity = SourceContentIdentity }], [], []);
        }
        public Task OpenLogsAsync(string packageId, AppPackageGenerationPublication publication, CancellationToken token) => Task.CompletedTask;
        public Task ReleaseOwnerAsync(IDevPackageOwnerSession owner, CancellationToken token)
            => RejectRelease ? Task.FromException(new InvalidOperationException("Activation retirement was not acknowledged.")) : owner.DisposeAsync().AsTask();
    }

    private sealed class Owner(Backend backend) : IDevPackageOwnerSession
    {
        internal int Disposals { get; private set; }
        internal bool Watch { get; private set; }
        public async Task<DevPackageOwnerLeaseResponse> AcquireAsync(IReadOnlyList<string> folders, bool watch, CancellationToken cancellationToken = default)
        {
            Assert.Single(folders);
            Watch = watch;
            backend.Acquiring.TrySetResult();
            if (backend.BlockAcquire) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(backend.RuntimeId, "owner", "mutation", 1, DateTimeOffset.UtcNow.AddMinutes(1), 3, [new("demo", watch) { EffectiveWatch = true }]);
        }
        public Task ReleaseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
