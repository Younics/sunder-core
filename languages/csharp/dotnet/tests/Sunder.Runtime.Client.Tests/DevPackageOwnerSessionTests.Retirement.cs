using System.Net;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed partial class DevPackageOwnerSessionTests
{
    [Fact]
    public async Task StagedReleaseKeepsHeartbeatDuringCleanupAndReturnsOnlyAcknowledgedSnapshot()
    {
        var client = new OwnerClient(Guid.NewGuid()) { LeaseLifetime = TimeSpan.FromMilliseconds(90) };
        await using var session = CreateSession(client, options: new() { ReleaseTimeout = TimeSpan.FromSeconds(5), MinimumHeartbeatDelay = TimeSpan.FromMilliseconds(10), MaximumHeartbeatDelay = TimeSpan.FromMilliseconds(20) });
        var acquired = await session.AcquireAsync(["dev-a", "dev-b"], true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = session.ReleaseStagedAsync(async (stage, token) =>
        {
            entered.TrySetResult();
            Assert.Equal(client.RuntimeId, stage.BaseStamp!.RuntimeInstanceId);
            var quiet = client.Mutations[^1];
            Assert.All(quiet.Folders, folder => Assert.False(folder.Watch));
            Assert.Equal(acquired.Revision + 1, quiet.Revision);
            Assert.Equal(client.Mutations[0].Folders.Select(folder => folder.Folder), quiet.Folders.Select(folder => folder.Folder));
            Assert.Empty(client.Commits);
            await finish.Task.WaitAsync(token);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var heartbeats = client.HeartbeatCount;
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (client.HeartbeatCount <= heartbeats) await Task.Delay(1, wait.Token);
        Assert.Empty(client.Commits);
        finish.TrySetResult();
        var snapshot = await release;
        Assert.Equal(client.RuntimeId, snapshot!.RuntimeInstanceId);
        Assert.Single(client.Commits);
        Assert.Empty(client.Releases);
        Assert.Null(await session.ReleaseStagedAsync((_, _) => throw new InvalidOperationException("No lease to retire")));
    }

    [Fact]
    public async Task LostCommitResponseRetriesExactControlIdentityWithoutRepeatingLocalCleanup()
    {
        var client = new OwnerClient(Guid.NewGuid()) { LoseFirstCommitResponse = true };
        await using var session = CreateSession(client);
        await session.AcquireAsync(["dev-a"], true);
        var cleaned = 0;
        Task Retire(PackageLifecycleStageResult stage, CancellationToken token) { cleaned++; return Task.CompletedTask; }
        await Assert.ThrowsAsync<HttpRequestException>(() => session.ReleaseStagedAsync(Retire));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcquireAsync(["dev-b"], false));
        var snapshot = await session.ReleaseStagedAsync(Retire);
        Assert.NotNull(snapshot);
        Assert.Equal(1, cleaned);
        Assert.Equal(2, client.Commits.Count);
        Assert.Equal(client.Commits[0], client.Commits[1]);
        Assert.Equal(client.ReleaseStages[0], client.ReleaseStages[1]);
        Assert.Equal(2, client.Mutations.Count); // Initial owner plus one acknowledged watch withdrawal.
        Assert.Empty(client.Releases);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnacknowledgedWatchWithdrawalPinsExactMutationWithoutRetiringAppOrReacquiring(bool timeout, bool heartbeatRejects)
    {
        var client = new OwnerClient(Guid.NewGuid()) { FailMutationOnCall = timeout ? null : 2, BlockMutationOnCall = timeout ? 2 : null,
            RejectFirstHeartbeat = heartbeatRejects, LeaseLifetime = TimeSpan.FromMilliseconds(90) };
        // This case uniquely proves the explicit retry of an unacknowledged
        // control mutation. Existing heartbeat-reconciliation tests own the
        // independent case where a live heartbeat acknowledges it first.
        await using var session = CreateSession(client, options: new()
        {
            ReleaseTimeout = TimeSpan.FromMilliseconds(100),
            MinimumHeartbeatDelay = heartbeatRejects ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromMinutes(1),
            MaximumHeartbeatDelay = heartbeatRejects ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromMinutes(1),
        });
        var original = await session.AcquireAsync(["dev-a"], true);
        var cleaned = 0;
        Task Retire(PackageLifecycleStageResult _, CancellationToken token) { cleaned++; return Task.CompletedTask; }
        var failure = await Record.ExceptionAsync(() => session.ReleaseStagedAsync(Retire));
        if (timeout) Assert.IsType<TimeoutException>(failure);
        else Assert.IsType<HttpRequestException>(failure);
        Assert.Equal(0, cleaned);
        Assert.Empty(client.ReleaseStages);
        Assert.Empty(client.Commits);
        Assert.Equal(2, client.Mutations.Count);
        var pending = client.Mutations[1];
        Assert.Equal(original.RuntimeInstanceId, pending.RuntimeInstanceId);
        Assert.Equal(original.Revision + 1, pending.Revision);
        Assert.False(Assert.Single(pending.Folders).Watch);
        Assert.Equal(client.Mutations[0].OwnerToken, pending.OwnerToken);
        Assert.Equal(Assert.Single(client.Mutations[0].Folders).Folder, Assert.Single(pending.Folders).Folder);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcquireAsync(["unrelated"], true));
        if (heartbeatRejects)
        {
            // The next two heartbeat iterations prove the first rejected
            // heartbeat completed its reacquisition branch, rather than racing
            // the assertion before that branch could recreate the owner.
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (client.HeartbeatCount < 3) await Task.Delay(1, wait.Token);
            Assert.Equal(2, client.Mutations.Count);
        }
        Assert.NotNull(await session.ReleaseStagedAsync(Retire));
        Assert.Equal(1, cleaned);
        Assert.Equal(heartbeatRejects ? 2 : 3, client.Mutations.Count);
        if (!heartbeatRejects) Assert.Same(pending, client.Mutations[2]);
        Assert.Equal(pending.Revision, Assert.Single(client.ReleaseStages).Revision);
        Assert.Single(client.Commits);
        Assert.Empty(client.Releases);
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("conflict")]
    [InlineData("mismatched-response")]
    [InlineData("timeout")]
    public async Task UnprovenStagedReleasePropagatesFailureAndKeepsOwnership(string failure)
    {
        var client = new OwnerClient(Guid.NewGuid()) { RejectCommit = failure == "conflict", MismatchCommit = failure == "mismatched-response" };
        await using var session = CreateSession(client);
        var lease = await session.AcquireAsync(["dev-a"], false);
        Task? retirementCallback = null;
        async Task Retire(PackageLifecycleStageResult _, CancellationToken token)
        {
            if (failure == "cleanup") throw new InvalidOperationException("cleanup unproven");
            if (failure == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        var release = session.ReleaseStagedAsync((stage, token) => retirementCallback = Retire(stage, token));
        var error = await Record.ExceptionAsync(() => release);
        Assert.NotNull(error);
        if (failure == "timeout")
        {
            Assert.IsType<TimeoutException>(error);
            // A bounded release may return before cooperative cancellation has
            // actually drained the callback. Join the exact callback before
            // asserting reacquisition; the separate cancellation-ignoring case
            // requires exclusion while that work is still in flight.
            Assert.NotNull(retirementCallback);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retirementCallback!).WaitAsync(TimeSpan.FromSeconds(3));
        }
        if (failure is "cleanup" or "timeout") Assert.Empty(client.Commits);
        Assert.Empty(client.Releases);
        if (failure == "mismatched-response")
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcquireAsync(["dev-a"], false));
        else
            Assert.Equal(lease.MutationId, (await session.AcquireAsync(["dev-a"], false)).MutationId);
        Assert.Single(client.Mutations);
    }

    [Fact]
    public async Task AmbiguousReleaseNeverLetsHeartbeatRecreateTheWithdrawnOwner()
    {
        var client = new OwnerClient(Guid.NewGuid()) { LoseFirstCommitResponse = true, RejectHeartbeatAfterCommit = true,
            LeaseLifetime = TimeSpan.FromMilliseconds(90) };
        await using var session = CreateSession(client);
        await session.AcquireAsync(["dev-a"], false);
        await Assert.ThrowsAsync<HttpRequestException>(() => session.ReleaseStagedAsync((_, _) => Task.CompletedTask));
        await client.ReleasedHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await session.ReleaseStagedAsync((_, _) => throw new InvalidOperationException("Must query the existing receipt"));
        Assert.Single(client.Mutations);
        // An explicit later load gets a new lease and restarts normal maintenance.
        client.RejectHeartbeatAfterCommit = false;
        var lease = await session.AcquireAsync(["dev-b"], false);
        Assert.Equal(1, lease.Revision);
        Assert.Equal(2, client.Mutations.Count);
    }

    [Fact]
    public async Task CancellationIgnoringCleanupTimesOutWithoutOverlappingOrAcknowledgingWithdrawal()
    {
        var client = new OwnerClient(Guid.NewGuid());
        await using var session = CreateSession(client);
        await session.AcquireAsync(["dev-a"], false);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(() => session.ReleaseStagedAsync((_, _) => finish.Task));
        Assert.Empty(client.Commits);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcquireAsync(["dev-b"], false));
        finish.TrySetResult();
        Assert.NotNull(await session.ReleaseStagedAsync((_, _) => throw new InvalidOperationException("Already completed cleanup")));
        Assert.Single(client.Commits);
    }

    private sealed partial class OwnerClient
    {
        internal int HeartbeatCount => Volatile.Read(ref _heartbeatCount);
        internal List<DevPackageOwnerReleaseStageRequest> ReleaseStages { get; } = [];
        internal List<DevPackageOwnerReleaseCommitRequest> Commits { get; } = [];
        internal bool LoseFirstCommitResponse { get; init; }
        internal bool RejectCommit { get; init; }
        internal bool MismatchCommit { get; init; }
        internal bool RejectHeartbeatAfterCommit { get; set; }
        internal TaskCompletionSource ReleasedHeartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, string> _stageIds = [];
        public Task<PackageLifecycleStageResult> StageDevPackageOwnerReleaseAsync(string ownerId, DevPackageOwnerReleaseStageRequest request, CancellationToken token = default)
        {
            ReleaseStages.Add(request);
            if (!_stageIds.TryGetValue(request.MutationId, out var stage)) _stageIds.Add(request.MutationId, stage = Guid.NewGuid().ToString("N"));
            return Task.FromResult(new PackageLifecycleStageResult(stage, [], [], [], []) { BaseStamp = new(RuntimeId, request.Revision) });
        }
        public Task<DevPackageOwnerReleaseResult> CommitDevPackageOwnerReleaseAsync(string ownerId, DevPackageOwnerReleaseCommitRequest request, CancellationToken token = default)
        {
            Commits.Add(request);
            if (RejectCommit) throw new RuntimeClientException(HttpStatusCode.Conflict, "Stage stale", "Owner sources changed.");
            if (LoseFirstCommitResponse && Commits.Count == 1) throw new HttpRequestException("commit response lost");
            return Task.FromResult(new DevPackageOwnerReleaseResult(RuntimeId, ownerId, MismatchCommit ? "other-stage" : request.StageId,
                new(RuntimeId, 2, 1, RuntimeBootstrapState.Ready, [], [], [], [])));
        }
    }
}
