using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class AppProcessPolicySupervisorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuitKeepsProofThroughServiceCleanupAndDispatcherWaitButPolicyDenialStillExpires(bool policyDenial)
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        await fixture.WaitForHeartbeatIdleAsync();
        using var desktop = new ClassicDesktopStyleApplicationLifetime();
        var posted = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        var app = new Sunder.App.App(action =>
        {
            Interlocked.Increment(ref posts);
            posted.TrySetResult(action);
        }, (_, exitCode) => exited.TrySetResult(exitCode)) { ApplicationLifetime = desktop };
        var gateway = new RuntimeClientTransport(() => new(new Uri("http://127.0.0.1:5274/"), "fixture-only"));
        var transport = new AppRuntimeProcessTransport(gateway, _ => new(RuntimeIpcEndpoint.LogicalRuntimeUrl, "fixture-only")
        {
            IpcEndpoint = OperatingSystem.IsWindows()
                ? new(RuntimeIpcTransportKind.NamedPipe, "fixture-not-used")
                : new(RuntimeIpcTransportKind.UnixSocket, "/fixture/not-used"),
        });
        var policy = new AppProcessPolicyLifetime(gateway, transport, fixture.Supervisor);
        var cleanup = new QuitCleanup();
        var services = new ServiceCollection();
        services.AddSingleton(_ => cleanup);
        services.AddSingleton(_ => policy);
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<QuitCleanup>();
        _ = provider.GetRequiredService<AppProcessPolicyLifetime>();
        AppField("_serviceProvider").SetValue(app, provider);
        AppField("_processPolicy").SetValue(app, policy);
        var tasks = Assert.IsType<OwnedTaskObserver>(AppField("_tasks").GetValue(app));
        try
        {
            app.RequestDesktopShutdown(desktop);
            app.RequestDesktopShutdown(desktop);
            await cleanup.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.RequireLoadAsync(Snapshot(), default));
            Assert.Equal(PackagePolicyErrorCodes.AppProcessTerminationRequired, denied.Message);
            Assert.Equal(1, fixture.Connection.Permits);
            if (policyDenial)
            {
                fixture.Connection.HeartbeatState = RuntimeAppPolicySessionStates.ShutdownRequested;
                fixture.Connection.ShutdownRequired = true;
            }
            var previousProof = fixture.Now;
            for (var second = 1; second <= 6; second++)
            {
                fixture.Now = previousProof with { Ticks = previousProof.Ticks + previousProof.Frequency * second };
                if (second < RuntimePolicyTiming.AppHeartbeatGraceSeconds || !policyDenial)
                    await fixture.NextHeartbeatAsync();
                if (policyDenial && second == 1) app.RequestPolicyShutdown();
                fixture.Supervisor.CheckWatchdog();
                if (policyDenial && second >= RuntimePolicyTiming.AppHeartbeatGraceSeconds)
                    await fixture.FailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(policyDenial && second >= RuntimePolicyTiming.AppHeartbeatGraceSeconds ? 1 : 0, fixture.Failures);
            }
            Assert.False(posted.Task.IsCompleted);
            Assert.Equal(0, fixture.Connection.Shutdowns);
            // Container teardown has not disposed either private policy/RPC transport.
            _ = policy.PackageTransport.CreateRequestUri("probe");
            cleanup.Release.TrySetResult();
            var completeExit = await posted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (!policyDenial)
            {
                // A delayed dispatcher handoff must not start the five-second
                // final-exit clock while it is still queued behind ordinary UI work.
                for (var second = 0; second < 6; second++)
                {
                    fixture.Now = fixture.Now with { Ticks = fixture.Now.Ticks + fixture.Now.Frequency };
                    await fixture.NextHeartbeatAsync();
                    fixture.Supervisor.CheckWatchdog();
                }
                Assert.Equal(0, fixture.Failures);
            }
            Assert.Equal(0, fixture.Connection.Shutdowns);
            Assert.False(exited.Task.IsCompleted);
            completeExit();
            Assert.Equal(policyDenial ? 1 : 0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            var first = policy.StopForExitAsync();
            Assert.Same(first, policy.StopForExitAsync());
            await first;
            Assert.Equal(1, fixture.Connection.Shutdowns);
            Assert.Equal(1, cleanup.Calls);
            Assert.Equal(1, posts);
            Assert.Throws<ObjectDisposedException>(() => policy.PackageTransport.CreateRequestUri("probe"));
            if (!policyDenial)
            {
                // Finalization is not permission to disarm the process watchdog.
                fixture.Now = fixture.Now with { Ticks = fixture.Now.Ticks + fixture.Now.Frequency * RuntimePolicyTiming.AppHeartbeatGraceSeconds };
                fixture.Supervisor.CheckWatchdog();
                await fixture.FailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, fixture.Failures);
            }
        }
        finally
        {
            cleanup.Release.TrySetResult();
            await provider.DisposeAsync();
            await policy.StopForExitAsync();
            await tasks.StopAsync();
            tasks.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuitFencesAnInFlightPermitEvenBeforeFirstLoadIsArmed(bool sessionAlreadyOpen)
    {
        await using var fixture = new Fixture();
        if (sessionAlreadyOpen) await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connection.DuringPermit = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var admission = fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Supervisor.BeginShutdown();
            await fixture.StopSupervisorAsync();
        }
        finally { release.TrySetResult(); }
        var denial = await Assert.ThrowsAsync<InvalidOperationException>(() => admission);
        Assert.Equal(PackagePolicyErrorCodes.AppProcessTerminationRequired, denial.Message);
        Assert.Equal(1, fixture.Connection.Shutdowns);
        Assert.Contains(fixture.Diagnostics, message => message.Contains("event=cleanup-started;", StringComparison.Ordinal));
        Assert.Contains(fixture.Diagnostics, message => message.Contains("event=exit-handoff;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuitWhileOpeningCannotStartSupervisionAfterTheExitHandoff()
    {
        await using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connection.DuringOpen = async () => { entered.TrySetResult(); await release.Task; };
        var admission = fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fixture.Supervisor.BeginShutdown();
            await fixture.StopSupervisorAsync();
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => admission);
        Assert.Equal(0, fixture.Connection.Permits);
        Assert.DoesNotContain(fixture.Diagnostics, message => message.Contains("event=started;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnacknowledgedFinalShutdownIsBoundedAndNeverReplayed()
    {
        await using var fixture = new Fixture();
        await fixture.Supervisor.RequireLoadAsync(Snapshot(), default);
        fixture.Connection.ShutdownReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await fixture.StopSupervisorAsync().WaitAsync(TimeSpan.FromSeconds(4));
            await fixture.Supervisor.DisposeAsync();
            Assert.Equal(1, fixture.Connection.Shutdowns);
            Assert.Contains(fixture.Diagnostics, message => message.Contains("event=shutdown-request-failed;", StringComparison.Ordinal));
        }
        finally
        {
            fixture.Connection.ShutdownReply.TrySetResult(new(fixture.Connection.SessionId,
                RuntimeAppPolicySessionStates.ShutdownRequested, null, true, []));
        }
    }

    private static FieldInfo AppField(string name)
        => typeof(Sunder.App.App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class QuitCleanup : IAsyncDisposable
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task;
        }
    }
}
