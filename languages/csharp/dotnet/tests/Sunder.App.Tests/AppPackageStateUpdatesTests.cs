using Sunder.App.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageStateUpdatesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedDebounceIsFlushedUnderOriginalAuthorityBeforeHandoffOrShutdown(bool shutdown)
    {
        var publication = Published();
        var updates = new AppPackageStateUpdates(publication);
        var entered = Signal();
        var stored = false;
        var work = updates.RunAsync("bookmarks", async context =>
        {
            entered.SetResult();
            await WaitForSettlementAsync(context);
            await publication.RunDataMutationAsync(() => { stored = true; return Task.CompletedTask; }, context.CancellationToken);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (shutdown) Assert.True(await publication.SettleAndRevokeAsync());
        else
        {
            using var handoff = publication.BeginDataHandoff();
            await handoff.Drained.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Throws<InvalidOperationException>(() => { _ = updates.RunAsync("bookmarks", _ => Task.CompletedTask); });
            handoff.Commit();
        }
        await work;
        Assert.True(stored);
        Assert.True(publication.IsRevoked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => publication.RunDataMutationAsync(() => Task.CompletedTask));
        Assert.True(await publication.CompleteDataMutationsAsync());
    }

    [Fact]
    public async Task FailedLogicalSaveIsVisibleAndOnlySuccessfulSameKeyRetrySupersedesIt()
    {
        var publication = Published();
        var updates = new AppPackageStateUpdates(publication);
        var error = new InvalidDataException("Validation failed before a storage mutation was sent.");
        Assert.Same(error, await Assert.ThrowsAsync<InvalidDataException>(() => updates.RunAsync("bookmarks", _ => Task.FromException(error))));
        await updates.RunAsync("other", _ => Task.CompletedTask);
        using (var rejected = publication.BeginDataHandoff())
            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.Drained);
        Assert.True(publication.IsPublished);
        await updates.RunAsync("bookmarks", _ => Task.CompletedTask);
        using var accepted = publication.BeginDataHandoff();
        await accepted.Drained;
        accepted.Commit();
    }

    [Fact]
    public async Task CapturedContextLosesSettlementAuthorityWhenItsAcceptedCallbackCompletes()
    {
        var publication = Published();
        var updates = new AppPackageStateUpdates(publication);
        var release = Signal();
        Task? late = null;
        var invoked = false;
        await updates.RunAsync("bookmarks", _ =>
        {
            late = Task.Run(async () =>
            {
                await release.Task;
                await publication.RunDataMutationAsync(() => { invoked = true; return Task.CompletedTask; });
            });
            return Task.CompletedTask;
        });
        using var handoff = publication.BeginDataHandoff();
        await handoff.Drained;
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => late!);
        Assert.False(invoked);
        Assert.True(publication.IsPublished);
        handoff.Commit();
    }

    [Fact]
    public async Task SecurityRevocationDoesNotWaitForOrAuthorizeACancellationIgnoringCallback()
    {
        var publication = Published();
        var updates = new AppPackageStateUpdates(publication);
        var entered = Signal();
        var release = Signal();
        var invoked = false;
        var work = updates.RunAsync("bookmarks", async _ =>
        {
            entered.SetResult();
            await release.Task;
            await publication.RunDataMutationAsync(() => { invoked = true; return Task.CompletedTask; });
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        publication.Revoke();
        var drain = publication.CompleteDataMutationsAsync();
        try { Assert.True(publication.IsRevoked); Assert.False(drain.IsCompleted); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => work);
        Assert.False(await drain);
        Assert.False(invoked);
    }

    [Fact]
    public async Task ExpiredNormalSettlementRevokesImmediatelyButStillJoinsTheActualLateCallback()
    {
        var publication = Published();
        var entered = Signal();
        var release = Signal();
        var invoked = false;
        var work = new AppPackageStateUpdates(publication).RunAsync("bookmarks", async _ =>
        {
            entered.SetResult();
            await release.Task;
            await publication.RunDataMutationAsync(() => { invoked = true; return Task.CompletedTask; });
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task<bool>? drain = null;
        try
        {
            Assert.False(await publication.SettleAndRevokeAsync().WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.True(publication.IsRevoked);
            drain = publication.CompleteDataMutationsAsync();
            Assert.False(drain.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => work);
        if (drain is not null) Assert.False(await drain);
        Assert.False(invoked);
    }

    [Fact]
    public async Task SettlementSignalCallbackFailureCannotSkipTheActualAcceptedWorkOrClaimSuccessfulSettlement()
    {
        var publication = Published();
        var entered = Signal();
        var signalled = Signal();
        var release = Signal();
        var work = new AppPackageStateUpdates(publication).RunAsync("bookmarks", async context =>
        {
            using var callback = context.SettlementRequested.Register(() =>
            {
                signalled.TrySetResult();
                throw new IOException("Settlement cancellation callback failed.");
            });
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var handoff = publication.BeginDataHandoff();
        try
        {
            await signalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(handoff.Drained.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<AggregateException>(() => work);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handoff.Drained);
        publication.Revoke();
        Assert.False(await publication.CompleteDataMutationsAsync());
    }

    [Fact]
    public async Task AbortedHandoffReopensAdmissionButTheNextHandoffStillWaitsForOldAcceptedWork()
    {
        var publication = Published();
        var updates = new AppPackageStateUpdates(publication);
        var signalled = Signal();
        var release = Signal();
        var work = updates.RunAsync("bookmarks", async context =>
        {
            await WaitForSettlementAsync(context);
            signalled.SetResult();
            await release.Task;
            await publication.RunDataMutationAsync(() => Task.CompletedTask);
        });
        using (var aborted = publication.BeginDataHandoff())
        {
            await signalled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(aborted.Drained.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = updates.RunAsync("bookmarks", _ => Task.CompletedTask); });
        }
        await updates.RunAsync("other", _ => Task.CompletedTask);
        using var next = publication.BeginDataHandoff();
        try { Assert.False(next.Drained.IsCompleted); }
        finally { release.TrySetResult(); }
        await work;
        await next.Drained;
        next.Commit();
    }

    [Fact]
    public async Task AcceptedWorkCannotUseAnotherActivationsClosedStorageAuthority()
    {
        var original = Published();
        var replacement = Published();
        using var handoff = replacement.BeginDataHandoff();
        var invoked = false;
        await new AppPackageStateUpdates(original).RunAsync("bookmarks", async _ =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.RunDataMutationAsync(() =>
            {
                invoked = true;
                return Task.CompletedTask;
            })));
        Assert.False(invoked);
    }

    [Fact]
    public async Task AdmissionIsBoundedAndCapacityReturnsOnlyAfterActualCompletion()
    {
        var publication = Published();
        var updates = new AppPackageStateUpdates(publication);
        var release = Signal();
        var tasks = Enumerable.Range(0, 128).Select(_ => updates.RunAsync("bookmarks", _ => release.Task)).ToArray();
        try { Assert.Throws<InvalidOperationException>(() => { _ = updates.RunAsync("bookmarks", _ => Task.CompletedTask); }); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(tasks);
        await updates.RunAsync("bookmarks", _ => Task.CompletedTask);
        using var handoff = publication.BeginDataHandoff();
        await handoff.Drained;
    }

    private static AppPackageGenerationPublication Published()
    {
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        return publication;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WaitForSettlementAsync(PackageAppStateUpdateContext context)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, context.SettlementRequested); }
        catch (OperationCanceledException) when (context.SettlementRequested.IsCancellationRequested) { }
        context.CancellationToken.ThrowIfCancellationRequested();
    }
}
