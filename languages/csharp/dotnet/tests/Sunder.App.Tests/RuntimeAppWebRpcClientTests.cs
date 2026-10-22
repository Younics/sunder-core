using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeAppWebRpcClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataReadFromRetiredCallerUsesOnlyAnAlreadyCommittedReplacement(bool providerLookup)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retirement = new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.caller-retired", "The caller package activation is retired."));
        var first = new CleanupProbeClient { MetadataRead = async _ => { entered.SetResult(); await failed.Task; throw retirement; } };
        var next = new CleanupProbeClient();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(first, (_, _) => Task.FromResult<ISunderRpcClient>(next), publication);
        var read = providerLookup ? (Task)client.GetProviderAsync(new("rpc1_provider")).AsTask() : client.DiscoverAsync("test.contract").AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var replacement = await client.PrepareAsync(RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, "test"), default);
        replacement.Commit();
        failed.SetResult();
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.MetadataReads);
        Assert.Equal(1, next.MetadataReads);
        if (!providerLookup) Assert.Equal(1, (await (Task<SunderRpcCatalogSnapshot>)read).Revision);
    }

    [Theory]
    [InlineData(false, SunderRpcErrorKind.StaleEndpoint, "rpc.caller-retired", true)]
    [InlineData(true, SunderRpcErrorKind.PermissionDenied, "rpc.permission-denied", true)]
    [InlineData(true, SunderRpcErrorKind.Protocol, "rpc.invalid-catalog", true)]
    [InlineData(true, SunderRpcErrorKind.Unavailable, "rpc.transport.unavailable", true)]
    [InlineData(true, SunderRpcErrorKind.StaleEndpoint, "rpc.caller-retired", false)]
    public async Task MetadataReadDoesNotInventReplacementOrRetrySecurityAndProtocolFailures(bool commit, SunderRpcErrorKind kind, string code, bool safe)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new SunderRpcException(new(kind, code, "Original failure.",
            safe ? SunderRpcDispatchState.NotDispatched : SunderRpcDispatchState.PossiblyDispatched,
            safe ? SunderRpcRetrySafety.Safe : SunderRpcRetrySafety.Unsafe));
        var first = new CleanupProbeClient { MetadataRead = async _ => { entered.SetResult(); await release.Task; throw failure; } };
        var next = new CleanupProbeClient();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(first, (_, _) => Task.FromResult<ISunderRpcClient>(next), publication);
        var read = client.DiscoverAsync("test.contract").AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var candidate = await client.PrepareAsync(RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, "test"), default);
        if (commit) candidate.Commit();
        release.SetResult();
        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => read));
        Assert.Equal(0, next.MetadataReads);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("cleanup")]
    [InlineData("cleanup-ended")]
    [InlineData("cleanup-begins")]
    [InlineData("revoke")]
    public async Task MetadataHandoffRechecksCallerCancellationAndNeverBorrowsCleanupAuthority(string boundary)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.caller-retired", "Retired."));
        var first = new CleanupProbeClient { MetadataRead = async _ => { entered.SetResult(); await release.Task; throw failure; } };
        var next = new CleanupProbeClient();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(first, (_, _) => Task.FromResult<ISunderRpcClient>(next), publication);
        using var authority = boundary is "cleanup" or "cleanup-ended" ? client.BeginCleanup() : null;
        var read = client.DiscoverAsync("test.contract", cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var candidate = await client.PrepareAsync(RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, "test"), default);
        candidate.Commit();
        if (boundary == "cleanup-ended") authority!.Dispose();
        using var laterAuthority = boundary == "cleanup-begins" ? client.BeginCleanup() : null;
        if (boundary == "cancel") cancellation.Cancel();
        if (boundary == "revoke") publication.Revoke();
        release.SetResult();
        if (boundary is "cleanup" or "cleanup-ended" or "cleanup-begins")
            Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => read));
        else if (boundary == "revoke") await Assert.ThrowsAsync<InvalidOperationException>(() => read);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0, next.MetadataReads);
    }

    [Fact]
    public async Task RetainedManagedActivationReopensHostSessionWithoutRetargetingExistingCallScope()
    {
        var firstHandler = new InvariantReportHandler();
        var nextHandler = new InvariantReportHandler();
        RuntimeAppWebRpcClient Client(string id, InvariantReportHandler handler) => new(
            new RuntimeManagementClient(() => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler), id);
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(Client("first", firstHandler),
            (_, _) => Task.FromResult<ISunderRpcClient>(Client("next", nextHandler)), publication);
        await using var scope = await client.CreateCallScopeAsync();
        var source = RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, "test");
        await using (var discarded = await client.PrepareAsync(source, default)) { }
        var endpoint = new SunderRpcEndpointReference("rpc1_exact_provider");
        Assert.True(await client.TryReportInvariantViolationAsync(endpoint, new Exception("before")));
        Assert.Equal("first", firstHandler.InvariantReport!.SessionId);
        await using var replacement = await client.PrepareAsync(source, default);
        replacement.Commit();
        Assert.True(await client.TryReportInvariantViolationAsync(endpoint, new Exception("after")));
        Assert.Equal("next", nextHandler.InvariantReport!.SessionId);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.TryReportInvariantViolationAsync(endpoint, new Exception("stale")).AsTask());
        publication.Revoke();
        Assert.Throws<InvalidOperationException>(() => client.DiscoverAsync("test.contract"));
    }

    [Fact]
    public async Task RevokedActivation_CanAcknowledgeOwnedCleanupBeforeRpcSessionCloses()
    {
        var handler = new InvariantReportHandler();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        var rpc = new AppPackageManagedRpcClient(new RuntimeAppWebRpcClient(new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler), "retiring-owner"),
            (_, _) => throw new InvalidOperationException("Cleanup must not reopen a session."), publication);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? lateCall = null;
        var services = new ServiceCollection().AddSingleton(_ => new CleanupOwner(async () =>
        {
            entered.SetResult();
            await release.Task;
            Assert.True(await rpc.TryReportInvariantViolationAsync(new("rpc1_exact_provider"), new Exception("cleanup acknowledgement")));
            lateCall = Task.Run(async () =>
            {
                await lateRelease.Task;
                await Assert.ThrowsAsync<ObjectDisposedException>(() => rpc.DiscoverAsync("test.contract").AsTask());
            });
        })).BuildServiceProvider();
        _ = services.GetRequiredService<CleanupOwner>();
        await using var runtimeWork = new BackgroundTaskQueueService();
        using var assemblies = new AppSharedAssemblyRegistry([]);
        var unload = new AppPackageUnloadCoordinator(new AppPackageViewRegistry(new InlineUiDispatcher()), new AppPackageRuntimeWorkStopper(runtimeWork),
            new AppPackageAssemblyTracker(), assemblies, _ => { }, _ => { });

        publication.Revoke();
        var retirement = unload.RollBackActivationAsync("test.package", null, services, null, rpc, stopRuntimeWork: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(handler.InvariantReport);
        Assert.Throws<InvalidOperationException>(() => rpc.CreateCallScopeAsync());
        Assert.Throws<InvalidOperationException>(() => rpc.InvokeAsync(new("rpc1_exact_provider"), "service", "method", default));
        release.SetResult();

        Assert.Equal(AppPackageUiCleanupOutcome.Proven, await retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("retiring-owner", handler.InvariantReport!.SessionId);
        Assert.Equal("retiring-owner", Assert.Single(handler.ClosedSessionIds));
        lateRelease.SetResult();
        await lateCall!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupAuthority_DoesNotEscapeOwnerCompletionOrDeadline(bool expire)
    {
        var handler = new InvariantReportHandler();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        AppPackageManagedRpcClient Client(string id) => new(new RuntimeAppWebRpcClient(new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler), id),
            (_, _) => throw new InvalidOperationException("No cleanup replacement."), publication);
        await using var client = Client("owner");
        await using var other = Client("other-owner");
        publication.Revoke();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task captured;
        using (client.BeginCleanup(expire ? TimeSpan.FromMilliseconds(50) : null))
        {
            Assert.Throws<InvalidOperationException>(() => other.DiscoverAsync("test.contract"));
            Assert.Throws<InvalidOperationException>(() => publication.RequireRuntimeAccess("storage"));
            captured = Task.Run(async () =>
            {
                await release.Task;
                Assert.Throws<InvalidOperationException>(() => client.DiscoverAsync("test.contract"));
            });
            if (expire)
            {
                await Task.Delay(100);
                release.SetResult();
                await captured.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        if (!expire)
        {
            release.SetResult();
            await captured.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Null(handler.InvariantReport);
    }

    [Fact]
    public async Task CleanupAuthority_PreservesHostRetirementRejection()
    {
        var handler = new InvariantReportHandler
        {
            CallScopeOpenError = new(RuntimeRpcErrorKind.Unavailable, "rpc.app-session.retired", "Retired.",
                RuntimeRpcDispatchState.NotDispatched, RuntimeRpcRetrySafety.Safe),
        };
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(new RuntimeAppWebRpcClient(new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler), "owner"),
            (_, _) => throw new InvalidOperationException("No cleanup replacement."), publication);
        publication.Revoke();
        using (client.BeginCleanup())
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => client.CreateCallScopeAsync().AsTask());
            Assert.Equal("rpc.app-session.retired", error.Error.Code);
        }
        Assert.Equal(1, handler.CallScopeOpenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupAuthority_BoundsIssuedScopesAndDeferredStreams(bool expire)
    {
        var raw = new CleanupProbeClient();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(raw, (_, _) => throw new InvalidOperationException("No reopen."), publication);
        await using var ordinary = await client.CreateCallScopeAsync();
        Assert.Same(raw.Scopes[0], ordinary); // Ordinary scopes keep their existing raw-client semantics.
        publication.Revoke();
        var budget = TimeSpan.FromMilliseconds(500);
        var latestDeadline = DateTimeOffset.UtcNow + budget;
        using var cleanup = client.BeginCleanup(budget);
        await using var scope = await client.CreateCallScopeAsync(new(DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.InRange(raw.ScopeOptions[^1]!.DeadlineUtc!.Value, latestDeadline - TimeSpan.FromSeconds(1), latestDeadline + TimeSpan.FromMilliseconds(50));
        Assert.Equal(raw.ScopeOptions[^1]!.DeadlineUtc, scope.DeadlineUtc);
        var earlier = DateTimeOffset.UtcNow.AddMilliseconds(100);
        await client.InvokeAsync(new("exact"), "service", "method", default, new(earlier));
        Assert.Equal(earlier, raw.InvokeOptions[^1]!.DeadlineUtc);
        var lazy = client.SubscribeAsync(new("exact"), "service", "method", default);
        var watch = scope.WatchAsync(0, 0);
        await using var active = client.SubscribeAsync(new("exact"), "service", "method", default,
            new(DateTimeOffset.UtcNow.AddMinutes(1))).GetAsyncEnumerator();
        Assert.True(await active.MoveNextAsync());
        Assert.True(raw.SubscribeOptions[^1]!.DeadlineUtc <= latestDeadline + TimeSpan.FromMilliseconds(50));
        if (expire) await Task.Delay(budget + TimeSpan.FromMilliseconds(50));
        else cleanup.Dispose();

        var dispatches = raw.StreamStarts;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.DiscoverAsync("contract").AsTask());
        await using (var iterator = lazy.GetAsyncEnumerator())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
        await using (var iterator = watch.GetAsyncEnumerator())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.MoveNextAsync().AsTask());
        Assert.Equal(dispatches, raw.StreamStarts);
        Assert.Equal(0, raw.Scopes[^1].Discoveries);
        Assert.Equal(1, (await ordinary.DiscoverAsync("contract")).Revision);
        await scope.DisposeAsync();
        Assert.Equal(1, raw.Scopes[^1].Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupAuthority_CancelsPendingUnaryAndFencesLateStreamResults(bool expire)
    {
        var raw = new CleanupProbeClient();
        using var stopProbe = new CancellationTokenSource();
        var unaryEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        raw.Invoke = async token =>
        {
            unaryEntered.SetResult(token);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopProbe.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            return default;
        };
        var streamEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamDisposed = false;
        raw.Subscribe = LateStream;
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(raw, (_, _) => throw new InvalidOperationException("No reopen."), publication);
        publication.Revoke();
        using var cleanup = client.BeginCleanup(TimeSpan.FromMilliseconds(500));
        var unary = client.InvokeAsync(new("exact"), "service", "method", default).AsTask();
        var lateReply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        raw.Invoke = _ => new(lateReply.Task);
        var lateUnary = client.InvokeAsync(new("exact"), "service", "late-reply", default).AsTask();
        await using var iterator = client.SubscribeAsync(new("exact"), "service", "method", default).GetAsyncEnumerator();
        var move = iterator.MoveNextAsync().AsTask();
        var unaryToken = await unaryEntered.Task;
        var streamToken = await streamEntered.Task;
        try
        {
            if (!expire) cleanup.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unary.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(unaryToken.IsCancellationRequested);
            Assert.True(streamToken.IsCancellationRequested);
            lateReply.SetResult(JsonSerializer.SerializeToElement(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lateUnary.WaitAsync(TimeSpan.FromSeconds(2)));
            releaseStream.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(streamDisposed);
        }
        finally
        {
            stopProbe.Cancel();
            lateReply.TrySetResult(default);
            releaseStream.TrySetResult();
            // Drain the controlled in-flight MoveNext even when a regression fails
            // earlier; concurrent iterator disposal would hide the original failure.
            try { await move; } catch (OperationCanceledException) { }
            try { await unary; } catch (OperationCanceledException) { }
            try { await lateUnary; } catch (OperationCanceledException) { }
        }

        async IAsyncEnumerable<JsonElement> LateStream([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                streamEntered.SetResult(token);
                await releaseStream.Task; // Deliberately models an already-buffered/non-cooperative result.
                yield return JsonSerializer.SerializeToElement(1);
            }
            finally { streamDisposed = true; }
        }
    }

    [Fact]
    public async Task CleanupAuthority_ClosesLateAcquiredScopeAndDrainsDisposalAfterRevocation()
    {
        var raw = new CleanupProbeClient();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(raw, (_, _) => throw new InvalidOperationException("No reopen."), publication);
        publication.Revoke();
        using var cleanup = client.BeginCleanup();
        var scope = await client.CreateCallScopeAsync();
        var owned = raw.Scopes.Single();
        var late = new CleanupProbeScope();
        var acquired = new TaskCompletionSource<ISunderRpcCallScope>(TaskCreationOptions.RunContinuationsAsynchronously);
        raw.OpenScope = _ => new(acquired.Task);
        var pending = client.CreateCallScopeAsync().AsTask();
        cleanup.Dispose();
        late.CloseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        acquired.SetResult(late);
        try
        {
            await Task.WhenAny(pending, late.CloseEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(pending.IsCompleted);
            late.CloseGate.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(1, late.Disposals);
        }
        finally
        {
            late.CloseGate.TrySetResult();
            try { await using var unexpected = await pending; } catch (OperationCanceledException) { }
        }

        owned.CloseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = scope.DisposeAsync().AsTask();
        Assert.Same(closing, scope.DisposeAsync().AsTask());
        Assert.False(closing.IsCompleted);
        owned.CloseGate.SetResult();
        await closing;
        Assert.Equal(1, owned.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupAuthority_PreservesCapturedFailuresWithoutRetargeting(bool foreignCancellation)
    {
        var raw = new CleanupProbeClient();
        var replacement = new CleanupProbeClient();
        using var foreign = new CancellationTokenSource();
        Exception failure = foreignCancellation ? new OperationCanceledException("Foreign cancellation", foreign.Token)
            : new InvalidDataException("Exact captured transport failure");
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        raw.Invoke = _ => new(reply.Task);
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(raw, (_, _) => throw new InvalidOperationException("No reopen."), publication);
        publication.Revoke();
        using var cleanup = client.BeginCleanup();
        var invocation = client.InvokeAsync(new("exact"), "service", "method", default).AsTask();
        client.Commit(replacement);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.DiscoverAsync("contract").AsTask());
        cleanup.Dispose();
        reply.SetException(failure);
        Assert.Same(failure, await Record.ExceptionAsync(() => invocation));
        Assert.Empty(replacement.InvokeOptions);
        Assert.Equal(0, replacement.Discoveries);
    }

    [Fact]
    public async Task CleanupAuthority_BoundsResponseContentAndPreservesCloseFailures()
    {
        var raw = new CleanupProbeClient();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(raw, (_, _) => throw new InvalidOperationException("No reopen."), publication);
        publication.Revoke();
        using var cleanup = client.BeginCleanup();
        var scope = await client.CreateCallScopeAsync();
        var reference = new SunderRpcContentReference("content", 1, new string('a', 64), "application/octet-stream", "payload.bin",
            DateTimeOffset.UtcNow.AddMinutes(1), SunderRpcContentRepeatability.Repeatable);
        var content = await scope.OpenContentAsync(reference);
        Assert.Equal(1, await content.ReadAsync(new byte[1]));
        cleanup.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => content.ReadAsync(new byte[1]).AsTask());
        await content.DisposeAsync();
        Assert.False(raw.Scopes.Single().Content.CanRead);
        var failure = new IOException("Exact close failure");
        raw.Scopes.Single().CloseFailure = failure;
        Assert.Same(failure, await Record.ExceptionAsync(() => scope.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task CleanupAuthority_AllowsExactOwnerObservationRecoveryWithoutAmbientBusinessAuthority()
    {
        var raw = new CleanupProbeClient();
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        await using var client = new AppPackageManagedRpcClient(raw, (_, _) => throw new InvalidOperationException("No reopen."), publication);
        await using var other = new AppPackageManagedRpcClient(new CleanupProbeClient(), (_, _) => throw new InvalidOperationException("No reopen."), publication);
        var firstStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var marker = new AsyncLocal<bool>();
        var observations = 0;
        ISunderRpcCallScope? issuedScope = null;
        raw.Observation = Observe;
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync("test.contract").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        publication.Revoke();
        // A pre-existing producer's rejected query invalidates the SDK's catalog.
        await Assert.ThrowsAsync<InvalidOperationException>(() => bindings.DiscoverAsync("test.contract").AsTask());
        await firstStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cleanup = client.BeginCleanup();
        await using var scope = issuedScope = await client.CreateCallScopeAsync();
        marker.Value = true;
        // This success requires the real SDK observer to refresh, subscribe and
        // resnapshot from its intentionally flow-suppressed ExecutionContext.
        await bindings.DiscoverAsync("test.contract").AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var observationToken = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.InvokeAsync(new("exact"), "service", "cancel-job", default);
        Assert.Single(raw.InvokeOptions);
        cleanup.Dispose();
        await recoveredStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(observationToken.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(() => client.DiscoverAsync("test.contract"));

        async IAsyncEnumerable<SunderRpcCatalogEvent> Observe([EnumeratorCancellation] CancellationToken token)
        {
            var current = Interlocked.Increment(ref observations);
            try
            {
                Assert.False(marker.Value);
                if (current > 1)
                {
                    Assert.Null(await client.GetProviderAsync(new("exact"), token));
                    Assert.Throws<InvalidOperationException>(() => client.CreateCallScopeAsync());
                    Assert.Throws<InvalidOperationException>(() => client.InvokeAsync(new("exact"), "service", "method", default));
                    Assert.Throws<InvalidOperationException>(() => client.TryReportInvariantViolationAsync(new("exact"), new Exception("denied")));
                    Assert.Throws<InvalidOperationException>(() => client.SubscribeAsync(new("exact"), "service", "stream", default));
                    Assert.Throws<InvalidOperationException>(() => other.DiscoverAsync("test.contract"));
                    Assert.Throws<InvalidOperationException>(() => publication.RequireRuntimeAccess("storage"));
                    Assert.Null(await issuedScope!.GetProviderAsync(new("exact"), token));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => issuedScope.InvokeAsync(new("exact"), "service", "method", default).AsTask());
                    recovered.TrySetResult(token);
                }
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                if (current == 1) firstStopped.TrySetResult();
                else recoveredStopped.TrySetResult();
            }
            yield break;
        }
    }

    [Fact]
    public async Task InvariantReport_UsesExactAppRouteAndSanitizedMessage()
    {
        var handler = new InvariantReportHandler();
        var connection = new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret");
        var management = new RuntimeManagementClient(() => connection, handler);
        await management.GetRuntimeHandshakeAsync();
        await using var client = new RuntimeAppWebRpcClient(management, "app-session");
        var endpoint = new SunderRpcEndpointReference("rpc1_exact_provider");

        var accepted = await client.TryReportInvariantViolationAsync(
            endpoint,
            new InvalidOperationException("line one\r\nline two " + new string('x', 600)));

        Assert.True(accepted);
        Assert.Equal("/api/v1/rpc/app/invariant-violation", handler.InvariantReportPath);
        var request = Assert.IsType<RuntimeRpcAppInvariantViolationRequest>(handler.InvariantReport);
        Assert.Equal("app-session", request.SessionId);
        Assert.Equal(endpoint.Value, request.EndpointReference);
        Assert.Null(request.CallScopeId);
        Assert.Equal(512, request.ExceptionMessage.Length);
        Assert.DoesNotContain('\r', request.ExceptionMessage);
        Assert.DoesNotContain('\n', request.ExceptionMessage);
        Assert.DoesNotContain(nameof(InvalidOperationException), request.ExceptionMessage, StringComparison.Ordinal);
        Assert.Null(client.ResourceProfile);
    }

    [Fact]
    public async Task ResourceProfile_ReflectsNegotiatedLimitsAndIsDelegatedByCallScope()
    {
        var handler = new InvariantReportHandler(RuntimeResourcePolicies.Large);
        var management = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);
        await management.GetRuntimeHandshakeAsync();
        await using var client = new RuntimeAppWebRpcClient(management, "app-session");

        await client.TryReportInvariantViolationAsync(
            new SunderRpcEndpointReference("rpc1_exact_provider"),
            new InvalidOperationException("failure"));

        Assert.True(client.ResourceLimits.IsRuntimeReported);
        Assert.Equal(
            RuntimeResourcePolicies.Large.MaximumRpcValueBytes,
            client.ResourceLimits.EffectivePolicy.MaximumRpcValueBytes);
        Assert.Equal(
            RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Large),
            client.ResourceLimits.Profile?.EffectiveProfileHash);
        var profile = Assert.IsType<SunderRpcResourceProfile>(client.ResourceProfile);
        await using var pendingActivation = new AppPackageManagedRpcClient(client,
            (_, _) => throw new InvalidOperationException("No replacement expected."), new AppPackageGenerationPublication());
        Assert.Same(profile, pendingActivation.ResourceProfile);
        Assert.Equal(
            RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Large),
            profile.EffectiveProfileHash);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumRpcValueBytes, profile.MaximumRpcValueBytes);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumValueDepth, profile.MaximumValueDepth);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumValueNodes, profile.MaximumValueNodes);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumObjectMembers, profile.MaximumObjectMembers);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumInlineDecodedBytes, profile.MaximumInlineDecodedBytes);
        Assert.Equal(RuntimeResourcePolicies.Large.MaximumContentReferenceBytes, profile.MaximumContentReferenceBytes);

        await using var scope = await client.CreateCallScopeAsync();
        Assert.Same(profile, scope.ResourceProfile);
    }

    [Fact]
    public async Task Invoke_PreservesRuntimeDomainErrorDetails()
    {
        var handler = new InvariantReportHandler();
        var management = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);
        await using var client = new RuntimeAppWebRpcClient(management, "app-session");

        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            "rpc1_exact_provider",
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }),
            null,
            CancellationToken.None).AsTask());

        Assert.Equal(SunderRpcErrorKind.Domain, failure.Error.Kind);
        Assert.Equal("duplicate", failure.Error.Details!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task FirstUsefulRpcTrace_RecordsOnlyTheFirstSuccessfulInvoke()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sunder-app-rpc-trace-{Guid.NewGuid():N}");
        var tracePath = Path.Combine(root, "app-startup.json");
        try
        {
            var traceSession = AppStartupTrace.CreateSession(
                tracePath,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                processId: 42);
            traceSession.RecordShellRevealed(elapsedMilliseconds: 0, openCoreShell: false);
            var traceOperation = Assert.IsType<AppStartupTraceOperation>(traceSession.BeginOperation(
                AppStartupTrace.FirstUsefulRpcStartedEvent,
                AppStartupTrace.FirstUsefulRpcCompletedEvent,
                new AppStartupTraceMetadata(
                    PackageId: "sunder.agent",
                    AppGenerationId: Guid.NewGuid().ToString("N"),
                    SessionGeneration: 7)));
            var handler = new InvariantReportHandler { InvokeSucceeds = true };
            var management = new RuntimeManagementClient(
                () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
                handler);

            await using (var client = new RuntimeAppWebRpcClient(
                             management,
                             "app-session",
                             traceOperation))
            {
                var request = JsonSerializer.SerializeToElement(new { message = "hello" });
                await client.InvokeAsync(
                    "rpc1_exact_provider",
                    "messages",
                    "send",
                    request,
                    null,
                    CancellationToken.None);
                await client.InvokeAsync(
                    "rpc1_exact_provider",
                    "messages",
                    "send",
                    request,
                    null,
                    CancellationToken.None);
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(tracePath));
            var completed = Assert.Single(
                document.RootElement.GetProperty("events").EnumerateArray(),
                item => string.Equals(
                    item.GetProperty("event").GetString(),
                    AppStartupTrace.FirstUsefulRpcCompletedEvent,
                    StringComparison.Ordinal));
            Assert.Equal("succeeded", completed.GetProperty("outcome").GetString());
            Assert.Equal("invoke", completed.GetProperty("operation").GetString());
            Assert.Equal("sunder.agent", completed.GetProperty("packageId").GetString());
            Assert.True(completed.GetProperty("activeDurationMilliseconds").GetDouble() >= 0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false, HttpStatusCode.NoContent, false)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(false, HttpStatusCode.InternalServerError, true)]
    [InlineData(true, HttpStatusCode.NoContent, false)]
    public async Task CallScopeDisposal_ReportsOnlyUnexpectedLiveSessionFailures(
        bool retireCaller,
        HttpStatusCode closeStatusCode,
        bool expectedFailure)
    {
        var handler = new InvariantReportHandler
        {
            CallScopeCloseStatusCode = closeStatusCode,
        };
        var management = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);
        var failureLogged = false;
        void Observe(AppSessionLogSnapshotEntry entry)
            => failureLogged |= string.Equals(
                entry.Message,
                "Failed to close a package App RPC call scope.",
                StringComparison.Ordinal);

        AppSessionLog.EntryWritten += Observe;
        try
        {
            await using var client = new RuntimeAppWebRpcClient(management, "app-session");
            var scope = await client.CreateCallScopeAsync();
            if (retireCaller)
            {
                await client.DisposeAsync();
                Assert.Equal(1, handler.SessionCloseCount);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.DiscoverAsync("test.contract").AsTask());
            }

            await scope.DisposeAsync();

            Assert.Equal(retireCaller ? 0 : 1, handler.CallScopeCloseCount);
            Assert.Equal(expectedFailure, failureLogged);
        }
        finally
        {
            AppSessionLog.EntryWritten -= Observe;
        }
    }

    [Fact]
    public async Task CreateCallScope_PreservesRetiredAppSessionRpcErrorWithoutRetry()
    {
        var handler = new InvariantReportHandler
        {
            CallScopeOpenError = new RuntimeRpcErrorDescriptor(
                RuntimeRpcErrorKind.Unavailable,
                "rpc.app-session.retired",
                "The App RPC caller session is unavailable or retired.",
                RuntimeRpcDispatchState.NotDispatched,
                RuntimeRpcRetrySafety.Safe),
        };
        var management = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"),
            handler);
        await using var client = new RuntimeAppWebRpcClient(management, "app-session");

        var exception = await Assert.ThrowsAsync<SunderRpcException>(() =>
            client.CreateCallScopeAsync().AsTask());

        Assert.Equal(SunderRpcErrorKind.Unavailable, exception.Error.Kind);
        Assert.Equal("rpc.app-session.retired", exception.Error.Code);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, exception.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, exception.Error.RetrySafety);
        Assert.Equal(1, handler.CallScopeOpenCount);
    }

    [Theory]
    [InlineData("discover")]
    [InlineData("provider")]
    [InlineData("call-scopes/open")]
    [InlineData("invoke")]
    [InlineData("watch")]
    [InlineData("subscribe")]
    public async Task TransportUnavailable_IsHostNeutralWithoutReplayingRpc(string operation)
    {
        var handler = new InvariantReportHandler { RpcStatusCode = HttpStatusCode.ServiceUnavailable };
        await using var client = new RuntimeAppWebRpcClient(new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler), "app-session");

        var exception = await Assert.ThrowsAsync<SunderRpcException>(() => SendAsync(operation));

        Assert.Equal(SunderRpcErrorKind.Unavailable, exception.Error.Kind);
        Assert.Equal("runtime.v1.unavailable", exception.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, exception.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, exception.Error.RetrySafety);
        Assert.Equal("publication-transition", exception.Data["RuntimeCorrelationId"]);
        Assert.Null(exception.Error.Details);
        Assert.Equal("/api/v1/rpc/app/" + operation, Assert.Single(handler.FailedRpcPaths));

        // The activation-owned observer decides when to reconnect. The same client can
        // make a new query after publication, but the failed request was never replayed.
        handler.RpcStatusCode = null;
        var snapshot = await client.DiscoverAsync("test.contract", CancellationToken.None);
        Assert.Equal(2, snapshot.Revision);
        Assert.Single(handler.FailedRpcPaths);

        async Task SendAsync(string route)
        {
            var endpoint = new SunderRpcEndpointReference("rpc1_exact_provider");
            var request = JsonSerializer.SerializeToElement(new { });
            switch (route)
            {
                case "discover": await client.DiscoverAsync("test.contract", CancellationToken.None); break;
                case "provider": await client.GetProviderAsync(endpoint); break;
                case "call-scopes/open": await using (await client.CreateCallScopeAsync()) { } break;
                case "invoke": await client.InvokeAsync(endpoint.Value, "messages", "send", request, null, CancellationToken.None); break;
                case "watch":
                    await using (var events = client.WatchAsync(0, 0, CancellationToken.None).GetAsyncEnumerator())
                        await events.MoveNextAsync();
                    break;
                case "subscribe":
                    await using (var events = client.SubscribeAsync(endpoint.Value, "messages", "watch", request, null, CancellationToken.None).GetAsyncEnumerator())
                        await events.MoveNextAsync();
                    break;
                default: throw new InvalidOperationException(route);
            }
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task TransportFailure_DoesNotReclassifyPermissionOrUnexpectedErrorsAsAvailability(HttpStatusCode status)
    {
        var handler = new InvariantReportHandler { RpcStatusCode = status };
        await using var client = new RuntimeAppWebRpcClient(new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://runtime.test/"), "secret"), handler), "app-session");

        var exception = await Assert.ThrowsAsync<RuntimeClientException>(() =>
            client.DiscoverAsync("test.contract", CancellationToken.None).AsTask());

        Assert.Equal(status, exception.StatusCode);
        Assert.Single(handler.FailedRpcPaths);
    }

    private sealed class InvariantReportHandler(RuntimeResourcePolicy? resourcePolicy = null) : HttpMessageHandler
    {
        public List<string> ClosedSessionIds { get; } = [];
        public HttpStatusCode? RpcStatusCode { get; set; }

        public List<string> FailedRpcPaths { get; } = [];

        public HttpStatusCode CallScopeCloseStatusCode { get; init; } = HttpStatusCode.NoContent;

        public RuntimeRpcErrorDescriptor? CallScopeOpenError { get; init; }

        public bool InvokeSucceeds { get; init; }

        public int CallScopeOpenCount { get; private set; }

        public int CallScopeCloseCount { get; private set; }

        public int SessionCloseCount { get; private set; }

        public string? InvariantReportPath { get; private set; }

        public RuntimeRpcAppInvariantViolationRequest? InvariantReport { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/handshake")
            {
                var features = new List<string>
                {
                    RuntimeProtocolFeatures.VersionedApiV1,
                    RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1,
                    RuntimeProtocolFeatures.AppWebRpcV1,
                };
                RuntimeResourceProfileDescriptor? profile = null;
                if (resourcePolicy is not null)
                {
                    features.Add(RuntimeProtocolFeatures.RuntimeResourcePolicyV1);
                    profile = new RuntimeResourceProfileDescriptor(
                        RuntimeResourcePolicyContract.GetEffectiveProfileHash(resourcePolicy),
                        resourcePolicy.MaximumRpcValueBytes,
                        resourcePolicy.MaximumValueDepth,
                        resourcePolicy.MaximumValueNodes,
                        resourcePolicy.MaximumObjectMembers,
                        resourcePolicy.MaximumInlineDecodedBytes,
                        resourcePolicy.MaximumContentReferenceBytes);
                }
                return Json(new RuntimeHandshakeResponse(
                    RuntimeProtocol.Identity,
                    RuntimeProtocol.CurrentRevision,
                    RuntimeProtocol.MinimumSupportedRevision,
                    RuntimeProtocol.MaximumSupportedRevision,
                    Guid.NewGuid(),
                    features,
                    new RuntimeProductVersionDiagnostics(
                        "Sunder.Runtime.Host",
                        "Development",
                        "Development"),
                    profile));
            }

            if (RpcStatusCode is { } status && request.RequestUri?.AbsolutePath is { } path
                && path.StartsWith("/api/v1/rpc/app/", StringComparison.Ordinal)
                && path != "/api/v1/rpc/app/call-scopes/close")
            {
                FailedRpcPaths.Add(path);
                return new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(new
                    {
                        title = "Runtime unavailable", code = "runtime.v1.unavailable",
                        detail = "Publication is in progress.", correlationId = "publication-transition",
                    }),
                };
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app/discover")
            {
                return Json(new RuntimeRpcAppDiscoverResponse(new RuntimeRpcCatalogSnapshot(2, 3, [], false), null));
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app/invariant-violation")
            {
                InvariantReportPath = request.RequestUri.AbsolutePath;
                InvariantReport = await request.Content!
                    .ReadFromJsonAsync<RuntimeRpcAppInvariantViolationRequest>(cancellationToken);
                return Json(new RuntimeRpcAppInvariantViolationResponse(true, null));
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app/call-scopes/open")
            {
                CallScopeOpenCount++;
                if (CallScopeOpenError is not null)
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = JsonContent.Create(CallScopeOpenError),
                    };
                }
                return Json(new RuntimeRpcAppCallScopeDescriptor(
                    "call-scope",
                    DateTimeOffset.UtcNow.AddMinutes(1)));
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app/call-scopes/close")
            {
                CallScopeCloseCount++;
                return new HttpResponseMessage(CallScopeCloseStatusCode);
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app/invoke")
            {
                if (InvokeSucceeds)
                {
                    return Json(new RuntimeRpcAppInvokeResponse(
                        JsonSerializer.SerializeToElement(new { accepted = true }),
                        null));
                }
                return Json(new RuntimeRpcAppInvokeResponse(
                    null,
                    new RuntimeRpcErrorDescriptor(
                        RuntimeRpcErrorKind.Domain,
                        "messages.rejected",
                        "Rejected.",
                        RuntimeRpcDispatchState.PossiblyDispatched,
                        RuntimeRpcRetrySafety.Unsafe,
                        JsonSerializer.SerializeToElement(new { reason = "duplicate" }))));
            }

            if (request.RequestUri?.AbsolutePath == "/api/v1/rpc/app-sessions/close")
            {
                SessionCloseCount++;
                ClosedSessionIds.Add((await request.Content!.ReadFromJsonAsync<RuntimeRpcAppSessionCloseRequest>(cancellationToken))!.SessionId);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value)
            => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private class CleanupProbeClient : ISunderRpcClient, IAsyncDisposable
    {
        internal List<CleanupProbeScope> Scopes { get; } = [];
        internal List<SunderRpcCallOptions?> ScopeOptions { get; } = [];
        internal List<SunderRpcCallOptions?> InvokeOptions { get; } = [];
        internal List<SunderRpcCallOptions?> SubscribeOptions { get; } = [];
        internal Func<CancellationToken, ValueTask<ISunderRpcCallScope>>? OpenScope { get; set; }
        internal Func<CancellationToken, ValueTask<JsonElement>>? Invoke { get; set; }
        internal Func<CancellationToken, IAsyncEnumerable<JsonElement>>? Subscribe { get; set; }
        internal Func<CancellationToken, IAsyncEnumerable<SunderRpcCatalogEvent>>? Observation { get; set; }
        internal Func<CancellationToken, Task>? MetadataRead { get; set; }
        internal int MetadataReads { get; private set; }
        internal int Discoveries { get; private set; }
        internal int StreamStarts { get; private set; }
        internal int Disposals { get; set; }
        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            ScopeOptions.Add(options);
            if (OpenScope is not null) return OpenScope(cancellationToken);
            var scope = new CleanupProbeScope();
            Scopes.Add(scope);
            return ValueTask.FromResult<ISunderRpcCallScope>(scope);
        }
        public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        {
            MetadataReads++;
            if (MetadataRead is not null) await MetadataRead(cancellationToken);
            return null;
        }
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
        public async ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposals > 0, this);
            Discoveries++;
            MetadataReads++;
            if (MetadataRead is not null) await MetadataRead(cancellationToken);
            return new SunderRpcCatalogSnapshot(1, 1, [], false);
        }
        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamStarts++;
            if (Observation is not null)
            {
                await foreach (var item in Observation(cancellationToken)) yield return item;
                yield break;
            }
            await Task.CompletedTask;
            yield return null!;
        }
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            InvokeOptions.Add(options);
            return Invoke?.Invoke(cancellationToken) ?? ValueTask.FromResult(JsonSerializer.SerializeToElement(1));
        }
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            SubscribeOptions.Add(options);
            return Subscribe?.Invoke(cancellationToken) ?? Events();
        }
        private async IAsyncEnumerable<JsonElement> Events()
        {
            StreamStarts++;
            await Task.CompletedTask;
            yield return JsonSerializer.SerializeToElement(1);
            yield return JsonSerializer.SerializeToElement(2);
        }
        public virtual ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class CleanupProbeScope : CleanupProbeClient, ISunderRpcCallScope
    {
        public DateTimeOffset DeadlineUtc { get; } = DateTimeOffset.UtcNow.AddMinutes(1);
        internal TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? CloseGate { get; set; }
        internal Exception? CloseFailure { get; set; }
        internal MemoryStream Content { get; } = new([1, 2]);
        public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Stream>(Content);
        public override async ValueTask DisposeAsync()
        {
            Disposals++;
            CloseEntered.TrySetResult();
            if (CloseGate is not null) await CloseGate.Task;
            if (CloseFailure is not null) throw CloseFailure;
        }
    }

    private sealed class CleanupOwner(Func<Task> cleanup) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(cleanup());
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
