using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class AppProcessPolicySessionsTests
{
    [Fact]
    public async Task HealthyHeartbeatAndStatusDoNotWaitForLifecycleWorkOrGrantProofAfterPolicyExpiry()
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync();
        var session = await fixture.OpenAsync();
        await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Process.NextProbe = () =>
        {
            entered.TrySetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Lifecycle work was not resumed by the test.");
        };
        var enforcing = Task.Factory.StartNew(() => fixture.Sessions.EnforceAsync(default), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = fixture.Session(session);
            var heartbeat = await fixture.Sessions.HeartbeatAsync(request, fixture.Peer, default).WaitAsync(TimeSpan.FromSeconds(2));
            var status = await fixture.Sessions.GetStatusAsync(session.SessionId, fixture.Peer, default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(RuntimeAppPolicySessionStates.Active, heartbeat.State);
            Assert.False(heartbeat.ShutdownRequired);
            Assert.Equal(heartbeat.LoadedPackages, status.LoadedPackages);
            await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.HeartbeatAsync(
                request with { RuntimeInstanceId = Guid.NewGuid() }, fixture.Peer, default));
            await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.GetStatusAsync(session.SessionId, new(999, null), default));

            // This is a fresh native-process/policy observation, not cached active
            // proof. Expired policy must not grant a successful heartbeat even if
            // lifecycle work still owns the mutation gate.
            fixture.Policy.Clock.Advance(TimeSpan.FromHours(24));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Sessions.HeartbeatAsync(request, fixture.Peer, cancellation.Token));
        }
        finally
        {
            resume.Set();
            await enforcing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True((await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default)).ShutdownRequired);
        fixture.Policy.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Sessions.EnforceAsync(default);
        Assert.Equal(1, fixture.Process.Terminations);
    }

    [Fact]
    public async Task HeartbeatCannotReturnActiveRecordSupersededDuringProcessValidation()
    {
        using var fixture = new Fixture();
        await fixture.Policy.RefreshAsync();
        var session = await fixture.OpenAsync();
        await fixture.Sessions.PermitAsync(fixture.Request(session), fixture.Peer, default);
        fixture.Process.NextProbe = () => fixture.Sessions.ShutdownAsync(fixture.Session(session), fixture.Peer, default).GetAwaiter().GetResult();
        var status = await fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default);
        Assert.True(status.ShutdownRequired);
        Assert.Equal(RuntimeAppPolicySessionStates.ShutdownRequested, status.State);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, cancelled.Token));
    }

    [Fact]
    public async Task HeartbeatAndStatusCannotGrantProofAfterPolicySupervisorDisposal()
    {
        using var fixture = new Fixture();
        var session = await fixture.OpenAsync();
        fixture.Sessions.Dispose();
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.HeartbeatAsync(fixture.Session(session), fixture.Peer, default));
        await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => fixture.Sessions.GetStatusAsync(session.SessionId, fixture.Peer, default));
    }
}
