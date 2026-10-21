using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeRpcBrokerTests
{
    [Fact]
    public async Task AppCallerPolicyValidationDoesNotBlockConcurrentOpeningOrPolicyHeartbeat()
    {
        using var process = new PausedAppPolicyProcess(interruptValidation: true);
        await using var fixture = await RetiringRpcFixture.CreateAsync(new TestHandler(), process);
        var id = Guid.NewGuid();
        var permit = await fixture.PermitAsync(id);
        var session = await fixture.OpenAsync(id, permit);
        process.PauseNextProbe();
        var lookup = OnDedicatedThread(() => fixture.Manager.GetRequired(session.SessionId));
        Task<RuntimeRpcAppSessionDescriptor>? opening = null;
        try
        {
            await process.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // OpenAsync commits under PackageSessionState.ExecuteIfCurrent. A lookup
            // retaining the dictionary lock here would invert those two monitors.
            opening = OnDedicatedThread(() => fixture.OpenAsync(id, permit)).Unwrap();
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            var heartbeat = await OnDedicatedThread(() => fixture.Policies.HeartbeatAsync(fixture.Session, fixture.Peer, default))
                .Unwrap().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(heartbeat.ShutdownRequired);
        }
        finally
        {
            // Deliberately interrupt the paused validation before it requests the
            // package monitor. Even the old lock inversion can unwind, so a red
            // regression cannot leave deadlocked threads in the test runner.
            process.Resume();
            await Assert.ThrowsAsync<InterruptedPolicyProbeException>(() => lookup);
            if (opening is not null) await opening.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("close")]
    [InlineData("dispose")]
    [InlineData("retire")]
    public async Task AppCallerCannotEscapeInvalidationDuringOutsideLockPolicyValidation(string invalidation)
    {
        using var process = new PausedAppPolicyProcess();
        await using var fixture = await RetiringRpcFixture.CreateAsync(new TestHandler(), process);
        var id = Guid.NewGuid();
        var permit = await fixture.PermitAsync(id);
        var session = await fixture.OpenAsync(id, permit);
        var caller = fixture.Manager.GetRequired(session.SessionId);
        process.PauseNextProbe();
        var lookup = OnDedicatedThread(() => fixture.Manager.GetRequired(session.SessionId));
        Task? invalidating = null;
        try
        {
            await process.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            invalidating = OnDedicatedThread(() =>
            {
                if (invalidation == "close") fixture.Manager.Close(session.SessionId);
                else if (invalidation == "dispose") fixture.Manager.Dispose();
                else caller.Retire();
            });
            await invalidating.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { process.Resume(); }
        if (invalidating is not null) await invalidating.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => lookup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SunderRpcErrorKind.Unavailable, error.Error.Kind);
        Assert.Equal("rpc.app-session.retired", error.Error.Code);
        Assert.False(caller.TryAcquireCaller(1, out _));
    }

    private static Task<T> OnDedicatedThread<T>(Func<T> action)
        => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static Task OnDedicatedThread(Action action)
        => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private sealed class InterruptedPolicyProbeException : Exception { }

    private sealed class PausedAppPolicyProcess(bool interruptValidation = false) : IAppPolicyProcessController, IAppPolicyProcessHandle
    {
        private readonly ManualResetEventSlim _resume = new();
        private int _pause;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void PauseNextProbe() => Volatile.Write(ref _pause, 1);
        internal void Resume() => _resume.Set();
        public IAppPolicyProcessHandle Attach(RuntimeAppProcessIdentity identity, AppPolicyPeer peer, bool recovery) => this;
        public AppPolicyProcessState Probe()
        {
            if (Interlocked.Exchange(ref _pause, 0) == 1)
            {
                Entered.TrySetResult();
                if (!_resume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("The test did not resume policy validation.");
                if (interruptValidation) throw new InterruptedPolicyProbeException();
            }
            return AppPolicyProcessState.Alive;
        }
        public void Terminate() { }
        // The fixture and test both own this fake; leave the event usable until
        // paused work is joined even if policy disposal races that work.
        public void Dispose() => Resume();
    }
}
