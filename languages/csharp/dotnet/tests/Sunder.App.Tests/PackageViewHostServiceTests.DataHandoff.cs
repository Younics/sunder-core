using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class PackageViewHostServiceTests
{
    [Theory]
    [InlineData("shutdown")]
    [InlineData("replacement")]
    [InlineData("owner-release")]
    public async Task NormalLifecycleSettlesAcceptedDeferredStateBeforeRevocationAndProviderDrain(string operation)
    {
        using var temporary = TemporaryDirectoryScope.Create("app-accepted-state-lifecycle");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
        {
            ActivationFingerprint = new('a', 64), PolicySubject = new("agent", "1.0.0", new('b', 64)),
        };
        var policy = new ExactRetentionPolicy(source);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = "initial";
        using var handler = new RetentionRpcHandler(policy)
        {
            DataRequest = async (request, token) =>
            {
                var update = (await request.Content!.ReadFromJsonAsync<SetPackageDataValueRequest>(token))!;
                if (update.Value == "old-final")
                {
                    writeEntered.TrySetResult();
                    await writeRelease.Task.WaitAsync(token);
                }
                value = update.Value;
                return new(HttpStatusCode.NoContent);
            },
        };
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        await host.WaitForRetirementsAsync();
        var original = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
        var group = Assert.Single(RetentionGeneration(host).ActivationGroups, item => item.OwnedActivationIds.Contains("agent"));
        var view = host.GetOrCreateView("agent.chat");
        var state = original.ServiceProvider!.GetRequiredService<IPackageContext>().Storage.State;
        var updates = original.ServiceProvider!.GetRequiredService<IPackageAppStateUpdates>();
        var work = updates.RunAsync("bookmarks", async context =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.SettlementRequested); }
            catch (OperationCanceledException error) when (error.CancellationToken == context.SettlementRequested
                && context.SettlementRequested.IsCancellationRequested) { }
            context.CancellationToken.ThrowIfCancellationRequested();
            await state.SetValueAsync("bookmark", "old-final", context.CancellationToken);
        });
        Task change;
        if (operation == "shutdown") change = host.DisposeAsync().AsTask();
        else if (operation == "owner-release")
        {
            var admission = original.LoadAdmission!;
            change = host.RetireActivationsAsync([new("test-policy", admission.ActivationId, admission.PermitId!, source.PolicySubject!)], default);
        }
        else
        {
            var next = source with { SessionGeneration = 2, SnapshotId = Guid.NewGuid().ToString("N"), ActivationFingerprint = new('c', 64) };
            policy.Expected = next;
            change = host.ApplyPackageDeltaAsync([package], [next]);
        }
        try
        {
            await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(change.IsCompleted);
            Assert.True(group.Composition.ServicesPublished);
            Assert.False(GetIsDisposed(view!));
            await Assert.ThrowsAsync<InvalidOperationException>(() => state.SetValueAsync("bookmark", "not-accepted"));
        }
        finally { writeRelease.TrySetResult(); }
        await work.WaitAsync(TimeSpan.FromSeconds(3));
        await change.WaitAsync(TimeSpan.FromSeconds(3));
        await host.WaitForRetirementsAsync();
        Assert.Equal("old-final", value);
        Assert.False(group.Composition.ServicesPublished);
        Assert.True(GetIsDisposed(view!));
        Assert.Equal(original.LoadAdmission!.ActivationId, Assert.Single(policy.RetiredActivationIds));
        Assert.Equal(0, host.RetirementFailureCount);
        if (operation == "replacement")
        {
            var current = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
            await current.ServiceProvider!.GetRequiredService<IPackageContext>().Storage.State.SetValueAsync("bookmark", "replacement");
            Assert.Equal("replacement", value);
        }
    }

    [Fact]
    public async Task ReplacementRevokesExactPredecessorEvenWhenOlderPresentationRetirementStillHoldsItsReference()
    {
        using var temporary = TemporaryDirectoryScope.Create("app-state-handoff-retirement-backlog");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
        {
            ActivationFingerprint = new('a', 64), PolicySubject = new("agent", "1.0.0", new('b', 64)),
        };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy)
        {
            DataRequest = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
        };
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        await host.WaitForRetirementsAsync();
        var original = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
        var oldState = original.ServiceProvider!.GetRequiredService<IPackageContext>().Storage.State;
        var blocker = new BlockedRetirementLifetime(null);
        RetentionGeneration(host).State.TrackOwnedDisposable(blocker);
        try
        {
            for (var generation = 2; generation <= 4; generation++)
            {
                source = source with
                {
                    SessionGeneration = generation, SnapshotId = Guid.NewGuid().ToString("N"),
                    ActivationFingerprint = generation == 4 ? new('c', 64) : source.ActivationFingerprint,
                };
                policy.Expected = source;
                await host.ApplyPackageDeltaAsync([package], [source]);
                if (generation == 2) await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            // The retirement queue still owns an older presentation reference to
            // this activation. Reference count is not current publication authority.
            Assert.False(blocker.Release.Task.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => oldState.SetValueAsync("bookmark", "late-old"));
            var current = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
            Assert.NotSame(original.ServiceProvider, current.ServiceProvider);
            await current.ServiceProvider!.GetRequiredService<IPackageContext>().Storage.State.SetValueAsync("bookmark", "replacement");
        }
        finally { blocker.Release.TrySetResult(); }
        await host.WaitForRetirementsAsync();
        Assert.Equal(0, host.RetirementFailureCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateHandoffWaitsForAcceptedWriteBeforeReplacementButDoesNotPauseRetainedActivation(bool retained)
    {
        using var temporary = TemporaryDirectoryScope.Create("app-state-handoff");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
        {
            ActivationFingerprint = new('a', 64), PolicySubject = new("agent", "1.0.0", new('b', 64)),
        };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy);
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var original = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
        var state = original.ServiceProvider!.GetRequiredService<IPackageContext>().Storage.State;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = "initial";
        handler.DataRequest = async (request, token) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            var update = (await request.Content!.ReadFromJsonAsync<SetPackageDataValueRequest>(token))!;
            if (update.Value == "old-final")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            value = update.Value;
            return new(HttpStatusCode.NoContent);
        };
        var write = state.SetValueAsync("bookmark", "old-final");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var next = source with
        {
            SessionGeneration = 2, SnapshotId = Guid.NewGuid().ToString("N"),
            ActivationFingerprint = retained ? source.ActivationFingerprint : new('c', 64),
        };
        policy.Expected = next;
        var apply = host.ApplyPackageGenerationAsync(CreateRuntimeSnapshot(package, next, 2), [next], null,
            (_, _, _) =>
            {
                if (!retained) Assert.Equal("old-final", value);
                return Task.FromResult<Action?>(null);
            }, default);
        try
        {
            if (retained)
            {
                await apply.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Same(original.ServiceProvider, RetentionGeneration(host).State.GetLoadedPackage("agent")!.ServiceProvider);
                await state.SetValueAsync("other", "still-live");
                Assert.False(write.IsCompleted);
            }
            else
            {
                await Eventually.AssertPendingAsync(apply, () => "Candidate preparation crossed an accepted predecessor write.");
                Assert.Single(handler.Opens);
                Assert.Same(original.ServiceProvider, RetentionGeneration(host).State.GetLoadedPackage("agent")!.ServiceProvider);
            }
        }
        finally { release.TrySetResult(); }
        await write.WaitAsync(TimeSpan.FromSeconds(3));
        await apply.WaitAsync(TimeSpan.FromSeconds(3));
        await host.WaitForRetirementsAsync();
        var current = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
        if (!retained)
        {
            Assert.NotSame(original.ServiceProvider, current.ServiceProvider);
            await Assert.ThrowsAsync<InvalidOperationException>(() => state.SetValueAsync("bookmark", "late-old"));
            await current.ServiceProvider!.GetRequiredService<IPackageContext>().Storage.State.SetValueAsync("bookmark", "replacement");
            Assert.Equal("replacement", value);
        }
        Assert.Equal(0, host.RetirementFailureCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedStateHandoffRestoresCurrentWriteAdmissionWithoutWaitingForCandidateResourceDisposal(bool cancelled)
    {
        using var temporary = TemporaryDirectoryScope.Create("app-state-handoff-abort");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
        {
            ActivationFingerprint = new('a', 64), PolicySubject = new("agent", "1.0.0", new('b', 64)),
        };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy)
        {
            DataRequest = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
        };
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var original = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
        var state = original.ServiceProvider!.GetRequiredService<IPackageContext>().Storage.State;
        var next = source with { SessionGeneration = 2, SnapshotId = Guid.NewGuid().ToString("N"), ActivationFingerprint = new('c', 64) };
        policy.Expected = next;
        using var cancellation = new CancellationTokenSource();
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        policy.BeforeRetire = () =>
        {
            cleanupEntered.TrySetResult();
            cleanupRelease.Task.GetAwaiter().GetResult();
        };
        var apply = host.ApplyPackageGenerationAsync(CreateRuntimeSnapshot(package, next, 2), [next], null,
            async (_, _, _) =>
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => state.SetValueAsync("bookmark", "closed"));
                // Keep rejected candidate cleanup pending to prove rollback releases
                // the predecessor fence, rather than waiting for the retirement queue.
                if (cancelled) cancellation.Cancel();
                return () => throw new InvalidOperationException("Rejected presentation.");
            }, cancellation.Token);
        try
        {
            if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
            else await Assert.ThrowsAsync<InvalidOperationException>(() => apply);
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await state.SetValueAsync("bookmark", "recovered").WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Same(original.ServiceProvider, RetentionGeneration(host).State.GetLoadedPackage("agent")!.ServiceProvider);
        }
        finally { cleanupRelease.TrySetResult(); }
        await host.WaitForRetirementsAsync();
        Assert.Equal(0, host.RetirementFailureCount);
    }

    [Fact]
    public async Task FailedDataSettlementStillDisposesLocalResourcesButCannotAcknowledgeRetirementOrReplacement()
    {
        using var temporary = TemporaryDirectoryScope.Create("app-state-settlement-failure");
        var folder = CreateAppPackageSource(temporary.RootPath, "agent");
        var package = CreateActiveAgentPackage();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, folder) with
        {
            ActivationFingerprint = new('a', 64), PolicySubject = new("agent", "1.0.0", new('b', 64)),
        };
        var policy = new ExactRetentionPolicy(source);
        using var handler = new RetentionRpcHandler(policy)
        {
            DataRequest = (_, _) => Task.FromException<HttpResponseMessage>(new IOException("Uncertain storage transport.")),
        };
        using var transport = new RuntimeClientTransport(() => new(new Uri("http://runtime.test/"), "test-token"), handler);
        await using var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], temporary.GetPath("session"),
            getRuntimeConnectionInfo: transport.GetConnectionInfo, uiDispatcher: TestUiDispatcher, packagePolicy: policy,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync);
        await host.ApplyPackageDeltaAsync([package], [source]);
        var view = host.GetOrCreateView("agent.chat");
        var original = RetentionGeneration(host).State.GetLoadedPackage("agent")!;
        var state = original.ServiceProvider!.GetRequiredService<IPackageContext>().Storage.State;
        await Assert.ThrowsAsync<IOException>(() => state.SetValueAsync("bookmark", "uncertain"));
        handler.DataRequest = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        await state.SetValueAsync("bookmark", "retry-succeeded");
        var replacement = source with { SnapshotId = Guid.NewGuid().ToString("N"), ActivationFingerprint = new('c', 64) };
        policy.Expected = replacement;
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ApplyPackageDeltaAsync([package], [replacement]));
        await host.DisposeAsync();
        Assert.True(GetIsDisposed(view!));
        Assert.Single(handler.ClosedSessions);
        Assert.Empty(policy.RetiredActivationIds);
        Assert.Equal(1, host.RetirementFailureCount);
    }
}
