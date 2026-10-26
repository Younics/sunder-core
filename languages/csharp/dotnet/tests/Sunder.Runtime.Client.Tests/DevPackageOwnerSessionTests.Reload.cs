using System.Net;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.Runtime.Client.Tests;

public sealed partial class DevPackageOwnerSessionTests
{
    [Fact]
    public async Task LostStageReplyAndRetirementLongerThanReleaseCleanupUseTheOriginalReloadBudget()
    {
        var client = new OwnerClient(Guid.NewGuid()) { LoseReloadStage = true };
        await using var session = CreateSession(client, options: new()
        {
            ReleaseTimeout = TimeSpan.FromMilliseconds(10), ReloadTimeout = TimeSpan.FromSeconds(2),
            MinimumHeartbeatDelay = TimeSpan.FromHours(1), MaximumHeartbeatDelay = TimeSpan.FromHours(1),
        });
        await session.AcquireAsync(["dev-a"], false);
        var retirements = 0;
        var result = await session.ReloadStagedAsync("original", "dev-a", new('c', 64), async (_, token) =>
        {
            retirements++;
            await Task.Delay(50, token);
        });
        Assert.Equal(2, client.ReloadStages.Count);
        Assert.Single(client.ReloadStages.Distinct());
        Assert.Single(client.ReloadCommits);
        Assert.Single(client.Mutations);
        Assert.Equal(1, retirements);
        Assert.Equal(new string('c', 64), result.SourceContentIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitReloadWaitsForRetirementAndResolvesLostCommitWithoutRebuildingOrReacquiring(bool lostReply)
    {
        var client = new OwnerClient(Guid.NewGuid()) { LoseReloadCommit = lostReply };
        await using var session = CreateSession(client);
        await session.AcquireAsync(["dev-a"], false);
        var cleanups = 0;
        Task Retire(PackageLifecycleStageResult stage, CancellationToken token)
        { cleanups++; Assert.Empty(client.ReloadCommits); return Task.CompletedTask; }
        var identity = new string('c', 64);
        var result = await session.ReloadStagedAsync("operation", "dev-a", identity, Retire);
        Assert.Same(result, await session.ReloadStagedAsync("operation", "dev-a", identity, (_, _) => throw new InvalidOperationException("Do not retire twice.")));
        Assert.Equal(1, cleanups);
        Assert.Single(client.Mutations);
        Assert.Equal(lostReply ? 2 : 1, client.ReloadCommits.Count);
        Assert.All(client.ReloadCommits, request => Assert.Equal(client.ReloadCommits[0], request));
        Assert.Empty(client.ReloadDiscards);
        Assert.Empty(client.Releases);
        Assert.Equal(identity, result.SourceContentIdentity);
        Assert.Equal(2, result.Snapshot.SessionGeneration);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("output")]
    [InlineData("operation")]
    public async Task UncertainReloadKeepsItsOriginalBindingAndCannotRetargetOrStartAnotherMutation(string change)
    {
        var client = new OwnerClient(Guid.NewGuid()) { LoseAllReloadCommits = true };
        await using var session = CreateSession(client);
        await session.AcquireAsync(["dev-a"], false);
        var identity = new string('c', 64);
        await Assert.ThrowsAsync<TimeoutException>(() => session.ReloadStagedAsync("operation", "dev-a", identity, (_, _) => Task.CompletedTask));
        if (change == "runtime") client.RuntimeId = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReloadStagedAsync(change == "operation" ? "different" : "operation",
            "dev-a", change == "output" ? new('d', 64) : identity, (_, _) => throw new InvalidOperationException("Must not dispatch cleanup again.")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcquireAsync(["dev-b"], false));
        Assert.Single(client.Mutations);
        Assert.NotEmpty(client.ReloadCommits);
        Assert.Single(client.ReloadCommits.Distinct());
        Assert.Single(client.ReloadStages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservationDuringUncertainReloadNeverAcquiresOrResetsItsOriginalBinding(bool replaceRuntime)
    {
        var client = new OwnerClient(Guid.NewGuid()) { LoseAllReloadCommits = true };
        await using var session = CreateSession(client);
        var lease = await session.AcquireAsync(["dev-a"], false);
        await Assert.ThrowsAsync<TimeoutException>(() => session.ReloadStagedAsync("operation", "dev-a", new('c', 64), (_, _) => Task.CompletedTask));
        var commits = client.ReloadCommits.Count;
        if (replaceRuntime) client.RuntimeId = Guid.NewGuid();

        var observed = await session.ObserveAsync();

        if (replaceRuntime) Assert.Null(observed);
        else Assert.Equal(lease.MutationId, observed!.MutationId);
        Assert.Single(client.Mutations);
        Assert.Equal(commits, client.ReloadCommits.Count);
        Assert.Empty(client.ReloadDiscards);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcquireAsync(["dev-a"], false));
        Assert.Contains("reload", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrecommitReloadFailureDiscardsOnlyAfterItsExactCleanupStopsAndNeverCommits(bool nonCooperative)
    {
        var client = new OwnerClient(Guid.NewGuid());
        await using var session = CreateSession(client);
        await session.AcquireAsync(["dev-a"], false);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new string('c', 64);
        if (nonCooperative)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => session.ReloadStagedAsync("operation", "dev-a", identity, (_, _) => finish.Task));
            Assert.Empty(client.ReloadDiscards);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReleaseStagedAsync((_, _) => Task.CompletedTask));
            Assert.Empty(client.ReleaseStages);
            finish.SetResult();
            await session.ReloadStagedAsync("operation", "dev-a", identity, (_, _) => throw new InvalidOperationException("Already drained."));
            Assert.Single(client.ReloadCommits);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => session.ReloadStagedAsync("operation", "dev-a", identity, (_, _) => throw new IOException("Cleanup refused.")));
            Assert.Single(client.ReloadDiscards);
            Assert.Empty(client.ReloadCommits);
            await Assert.ThrowsAsync<IOException>(() => session.ReloadStagedAsync("operation", "dev-a", identity, (_, _) => Task.CompletedTask));
            Assert.Single(client.ReloadStages);
        }
        Assert.Single(client.Mutations);
    }

    [Fact]
    public async Task ReloadRejectsWatchingOrUnownedSessionsBeforeAnyStageOrCleanup()
    {
        var client = new OwnerClient(Guid.NewGuid());
        await using var session = CreateSession(client);
        var identity = new string('c', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReloadStagedAsync("operation", "dev-a", identity, (_, _) => Task.CompletedTask));
        await session.AcquireAsync(["dev-a"], true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReloadStagedAsync("operation", "dev-a", identity, (_, _) => Task.CompletedTask));
        Assert.Empty(client.ReloadStages);
    }

    private sealed partial class OwnerClient
    {
        internal bool LoseReloadCommit { get; init; }
        internal bool LoseReloadStage { get; init; }
        internal bool LoseAllReloadCommits { get; init; }
        internal List<DevPackageOwnerReloadStageRequest> ReloadStages { get; } = [];
        internal List<DevPackageOwnerReloadCommitRequest> ReloadCommits { get; } = [];
        internal List<DevPackageOwnerReloadCommitRequest> ReloadDiscards { get; } = [];
        public Task<DevPackageOwnerReloadStageResult> StageDevPackageOwnerReloadAsync(string ownerId, DevPackageOwnerReloadStageRequest request, CancellationToken token = default)
        {
            ReloadStages.Add(request);
            if (LoseReloadStage && ReloadStages.Count == 1) throw new HttpRequestException("Reload stage response lost.");
            return Task.FromResult(new DevPackageOwnerReloadStageResult(request.PackageId, request.SourceContentIdentity,
                new("reload-stage", [], [], [], []) { BaseStamp = new(RuntimeId, 1) }));
        }
        public Task<DevPackageOwnerReloadResult> CommitDevPackageOwnerReloadAsync(string ownerId, DevPackageOwnerReloadCommitRequest request, CancellationToken token = default)
        {
            ReloadCommits.Add(request);
            if (LoseAllReloadCommits || LoseReloadCommit && ReloadCommits.Count == 1) throw new HttpRequestException("Reload commit response lost.");
            var selected = ReloadStages[0];
            return Task.FromResult(new DevPackageOwnerReloadResult(RuntimeId, ownerId, request.StageId, selected.PackageId, selected.SourceContentIdentity,
                new(RuntimeId, 2, 1, RuntimeBootstrapState.Ready, [], [new(selected.PackageId, selected.PackageId, "1.0.0", PackageHostRoles.Runtime,
                    null, true, PackageReadinessState.Ready, [], null, null, null, 0) { SourceContentIdentity = selected.SourceContentIdentity }], [], [])));
        }
        public Task DiscardDevPackageOwnerReloadAsync(string ownerId, DevPackageOwnerReloadCommitRequest request, CancellationToken token = default)
        { ReloadDiscards.Add(request); return Task.CompletedTask; }
    }
}
