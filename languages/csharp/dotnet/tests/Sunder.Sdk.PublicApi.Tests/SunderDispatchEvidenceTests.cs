using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderDispatchEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyAnAuthenticatedInvokeThroughTheOwnedTrustedScopeCanIssueHostEvidence(bool trustedScope)
    {
        var rpc = new Probe(trustedScope) { InvokeFailure = HostFailure() };
        await using var bindings = new SunderRpcServiceBindingLifetime(rpc);
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => call.Client!.InvokeAsync(Probe.Provider.Endpoint,
            "builder", "start", JsonSerializer.SerializeToElement(new { })).AsTask());
        Assert.Equal(trustedScope, SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.Equal(trustedScope ? SunderRpcDispatchState.NotDispatched : SunderRpcDispatchState.PossiblyDispatched, error.Error.DispatchState);
        Assert.Equal(1, rpc.InvokeAttempts);
        Assert.Same(rpc, bindings.Client);
        // Friend access above models Host authentication only as an SDK unit probe;
        // the App transport tests exercise actual JSON validation/authentication.
    }

    [Fact]
    public async Task AuthenticatedWorkerInvokeKeepsItsWireDecisionWithoutAppObservationProof()
    {
        var rpc = new Probe(false) { IsWorkerScope = true, InvokeFailure = HostFailure() };
        await using var resources = new SunderRpcResourceLifetime();
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => call.Client!.InvokeAsync(Probe.Provider.Endpoint,
            "builder", "start", default).AsTask());
        Assert.Same(rpc.InvokeFailure, error);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, error.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, error.Error.RetrySafety);
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.Equal(1, rpc.InvokeAttempts);
    }

    [Fact]
    public async Task StreamErrorsRetainIdentityWithoutBorrowingAnotherUnaryOperationsProof()
    {
        var rpc = new Probe(true) { InvokeFailure = HostFailure() };
        await using var resources = new SunderRpcResourceLifetime();
        await using var unary = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        var genuine = await Assert.ThrowsAsync<SunderRpcException>(() => unary.Client!.InvokeAsync(Probe.Provider.Endpoint,
            "builder", "start", default).AsTask());
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(genuine));

        rpc.StreamFailure = new(new(SunderRpcErrorKind.Unavailable, "rpc.stream.failure", "Unavailable."));
        await using (var stream = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint))
        await using (var cursor = stream.Client!.SubscribeAsync(Probe.Provider.Endpoint, "builder", "watch", default).GetAsyncEnumerator())
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => cursor.MoveNextAsync().AsTask());
            Assert.Same(rpc.StreamFailure, error);
            Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        }

        rpc.StreamFailure = genuine;
        await using (var stream = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint))
        await using (var cursor = stream.Client!.SubscribeAsync(Probe.Provider.Endpoint, "builder", "watch", default).GetAsyncEnumerator())
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => cursor.MoveNextAsync().AsTask());
            Assert.NotSame(genuine, error);
            Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
            Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(genuine));
        }
        Assert.Equal(1, rpc.InvokeAttempts);
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("domain")]
    [InlineData("invalid-kind")]
    [InlineData("invalid-dispatch")]
    [InlineData("invalid-retry")]
    public async Task PublicFlagsDomainAndInvalidEnumsNeverBecomePositive(string fault)
    {
        var kind = fault == "domain" ? SunderRpcErrorKind.Domain : fault == "invalid-kind" ? (SunderRpcErrorKind)999 : SunderRpcErrorKind.Unavailable;
        var value = new SunderRpcError(kind, "rpc.failure", "Failure.",
            fault == "invalid-dispatch" ? (SunderRpcDispatchState)999 : SunderRpcDispatchState.NotDispatched,
            fault == "invalid-retry" ? (SunderRpcRetrySafety)999 : SunderRpcRetrySafety.Safe);
        var failure = fault == "forged" ? new SunderRpcException(value) : new SunderRpcException(value, hostAuthenticated: true);
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(failure)); // Authentication alone is not an operation proof.
        var rpc = new Probe(true) { InvokeFailure = failure };
        await using var resources = new SunderRpcResourceLifetime();
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => call.Client!.InvokeAsync(Probe.Provider.Endpoint,
            "builder", "start", default).AsTask());
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
    }

    [Fact]
    public async Task AuthenticatedMetadataAfterOneSuccessfulInvokeCannotDescribeThatInvokeAsNotDispatched()
    {
        var rpc = new Probe(true);
        await using var resources = new SunderRpcResourceLifetime();
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        await SunderRpcServiceCall.EnsureCurrentAsync((ISunderRpcCallScope)call.Client!, Probe.Provider);
        await call.Client!.InvokeAsync(Probe.Provider.Endpoint, "builder", "start", default);
        rpc.MetadataFailure = HostFailure();
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => call.Client!.GetProviderAsync(Probe.Provider.Endpoint).AsTask());
        Assert.Equal(1, rpc.InvokeAttempts);
        AssertUnsafe(error);
    }

    [Theory]
    [InlineData("decode")]
    [InlineData("dispose")]
    [InlineData("cancel")]
    [InlineData("cancel-dispose")]
    public async Task DecodeDisposalAndCancellationAfterInvokeRemainNegative(string stage)
    {
        var rpc = new Probe(true);
        await using var resources = new SunderRpcResourceLifetime();
        var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        await call.Client!.InvokeAsync(Probe.Provider.Endpoint, "builder", "start", default);
        try
        {
            if (stage == "decode")
            {
                var error = await Assert.ThrowsAsync<SunderRpcException>(() => call.DeserializeAsync<int>(JsonSerializer.SerializeToElement(1),
                    static (_, _, _) => throw HostFailure()).AsTask());
                AssertUnsafe(error);
            }
            else if (stage == "dispose")
            {
                rpc.DisposeFailure = HostFailure();
                AssertUnsafe(await Assert.ThrowsAsync<SunderRpcException>(() => call.DisposeAsync().AsTask()));
            }
            else
            {
                using var caller = new CancellationTokenSource();
                caller.Cancel();
                var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.DeserializeAsync<int>(
                    JsonSerializer.SerializeToElement(1), cancellationToken: caller.Token).AsTask());
                Exception failure = cancelled;
                if (stage == "cancel-dispose")
                {
                    rpc.DisposeFailure = HostFailure(SunderRpcErrorKind.Cancelled);
                    var cleanup = await Assert.ThrowsAsync<SunderRpcException>(() => call.DisposeAsync().AsTask());
                    AssertUnsafe(cleanup);
                    failure = cleanup;
                }
                var error = Assert.IsType<OperationCanceledException>(SunderRpcServiceCall.TranslateCancellation(failure, caller.Token));
                Assert.Equal(caller.Token, error.CancellationToken);
                Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
            }
            Assert.Equal(1, rpc.InvokeAttempts);
        }
        finally { try { await call.DisposeAsync(); } catch (SunderRpcException) when (stage is "dispose" or "cancel-dispose") { } }
    }

    [Fact]
    public async Task LocalPreInvokeComparisonIsTrustedOnlyAtTheSdkOwnedBoundary()
    {
        var rpc = new Probe(false) { Current = Probe.Provider with { State = SunderRpcProviderState.Inactive } };
        await using var resources = new SunderRpcResourceLifetime();
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => SunderRpcServiceCall.EnsureCurrentAsync(
            (ISunderRpcCallScope)call.Client!, Probe.Provider).AsTask());
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.Equal(0, rpc.InvokeAttempts);
        await using var raw = await rpc.CreateCallScopeAsync();
        var unowned = await Assert.ThrowsAsync<SunderRpcException>(() => SunderRpcServiceCall.EnsureCurrentAsync(raw, Probe.Provider).AsTask());
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(unowned));
        using var retired = new CancellationTokenSource(); retired.Cancel();
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(Assert.Throws<SunderRpcException>(() => SunderRpcServiceCall.ThrowIfRetired(retired.Token))));
        var localRetirement = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resources.CreateCallAsync(rpc,
            Probe.Provider.Endpoint, providerRetirementToken: retired.Token).AsTask());
        var translated = Assert.IsType<SunderRpcException>(SunderRpcServiceCall.TranslateCancellation(localRetirement,
            default, retirementToken: retired.Token));
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(translated));
        Assert.Equal(SunderRpcErrorKind.Unavailable, translated.Error.Kind);

        rpc.MetadataFailure = new(new(SunderRpcErrorKind.StaleEndpoint, "rpc.forged", "A public client rejected a lookup."));
        var fake = await Assert.ThrowsAsync<SunderRpcException>(() => SunderRpcServiceCall.EnsureCurrentAsync(
            (ISunderRpcCallScope)call.Client!, Probe.Provider).AsTask());
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(fake));
        Assert.Equal(0, rpc.InvokeAttempts);
    }

    [Fact]
    public async Task SdkPreInvokeCancellationAndRemoteCancellationKeepOriginalTokensWithoutInnerExceptionInference()
    {
        var rpc = new Probe(true) { InvokeFailure = HostFailure(SunderRpcErrorKind.Cancelled) };
        await using var resources = new SunderRpcResourceLifetime();
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        var remote = await Assert.ThrowsAsync<SunderRpcException>(() => call.Client!.InvokeAsync(Probe.Provider.Endpoint,
            "builder", "start", default).AsTask());
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var translated = Assert.IsType<OperationCanceledException>(SunderRpcServiceCall.TranslateCancellation(remote, caller.Token));
        Assert.Equal(caller.Token, translated.CancellationToken);
        Assert.Same(remote, translated.InnerException);
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(translated));
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(new OperationCanceledException("Untrusted wrapper", remote, caller.Token)));
        var local = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resources.CreateCallAsync(rpc, Probe.Provider.Endpoint,
            cancellationToken: caller.Token).AsTask());
        var normalized = Assert.IsType<OperationCanceledException>(SunderRpcServiceCall.TranslateCancellation(local, caller.Token));
        Assert.Equal(caller.Token, normalized.CancellationToken);
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(normalized));
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(new OperationCanceledException(caller.Token)));
    }

    private static void AssertUnsafe(SunderRpcException error)
    {
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(error));
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, error.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, error.Error.RetrySafety);
    }

    [Fact]
    public async Task DisposalCannotReuseAnEarlierPositiveInvokeException()
    {
        var rpc = new Probe(true) { InvokeFailure = HostFailure() };
        await using var resources = new SunderRpcResourceLifetime();
        var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        var rejected = await Assert.ThrowsAsync<SunderRpcException>(() => call.Client!.InvokeAsync(Probe.Provider.Endpoint,
            "builder", "start", default).AsTask());
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(rejected));
        rpc.DisposeFailure = rejected;
        AssertUnsafe(await Assert.ThrowsAsync<SunderRpcException>(() => call.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task ARejectionForAnotherEndpointCannotAuthorizeThisOperation()
    {
        var rpc = new Probe(true) { InvokeFailure = HostFailure() };
        await using var resources = new SunderRpcResourceLifetime();
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        AssertUnsafe(await Assert.ThrowsAsync<SunderRpcException>(() => call.Client!.InvokeAsync(new("different-endpoint"),
            "builder", "start", default).AsTask()));
    }

    [Fact]
    public async Task APublicScopeFactoryCannotRecycleAnotherOperationsPositiveException()
    {
        var rpc = new Probe(true) { InvokeFailure = HostFailure() };
        await using var resources = new SunderRpcResourceLifetime();
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        var rejected = await Assert.ThrowsAsync<SunderRpcException>(() => call.Client!.InvokeAsync(Probe.Provider.Endpoint,
            "builder", "start", default).AsTask());
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(rejected));
        rpc.CreationFailure = rejected;
        var reused = await Assert.ThrowsAsync<SunderRpcException>(() => resources.CreateCallAsync(rpc, Probe.Provider.Endpoint).AsTask());
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(reused));
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(rejected));
        Assert.Equal(1, rpc.InvokeAttempts);
    }

    [Fact]
    public async Task CancellationDetectedByAnOwnedPreparationGuardKeepsOnlyItsOwnOperationProof()
    {
        using var caller = new CancellationTokenSource();
        var rpc = new Probe(false);
        await using var resources = new SunderRpcResourceLifetime();
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint, cancellationToken: caller.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.SerializeAsync(1,
            (context, _, _) =>
            {
                caller.Cancel();
                return context.RegisterContentAsync(null);
            }).AsTask());
        var translated = Assert.IsType<OperationCanceledException>(SunderRpcServiceCall.TranslateCancellation(error, caller.Token));
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(translated));
        Assert.Equal(caller.Token, translated.CancellationToken);
        Assert.Equal(0, rpc.InvokeAttempts);
    }

    [Fact]
    public async Task AnOwnedPrecheckProofCannotSurviveALaterInvokeOnThatSameContext()
    {
        var rpc = new Probe(true) { Current = Probe.Provider with { State = SunderRpcProviderState.Inactive } };
        await using var resources = new SunderRpcResourceLifetime();
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        var before = await Assert.ThrowsAsync<SunderRpcException>(() => SunderRpcServiceCall.EnsureCurrentAsync(
            (ISunderRpcCallScope)call.Client!, Probe.Provider).AsTask());
        Assert.True(SunderRpcException.HasTrustedNoDispatchEvidence(before));
        rpc.Current = Probe.Provider;
        await call.Client!.InvokeAsync(Probe.Provider.Endpoint, "builder", "start", default);
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(before));
        Assert.Equal(1, rpc.InvokeAttempts);
    }

    [Fact]
    public async Task ConcurrentSecondInvokePreventsTheFirstRejectionFromAuthenticatingTheWholeOperation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = new Probe(true)
        {
            InvokeBehavior = async attempt =>
            {
                if (attempt == 1) { entered.TrySetResult(); await release.Task; throw HostFailure(); }
                return JsonSerializer.SerializeToElement(1);
            },
        };
        await using var resources = new SunderRpcResourceLifetime();
        await using var call = await resources.CreateCallAsync(rpc, Probe.Provider.Endpoint);
        var first = call.Client!.InvokeAsync(Probe.Provider.Endpoint, "builder", "start", default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await call.Client.InvokeAsync(Probe.Provider.Endpoint, "builder", "start", default);
        }
        finally { release.TrySetResult(); }
        AssertUnsafe(await Assert.ThrowsAsync<SunderRpcException>(() => first));
        Assert.Equal(2, rpc.InvokeAttempts);
    }
    private static SunderRpcException HostFailure(SunderRpcErrorKind kind = SunderRpcErrorKind.Unavailable)
        => SunderRpcException.Infrastructure(kind, "rpc.test.rejected", "Rejected before dispatch.");

    private sealed class Probe(bool trusted) : ISunderRpcClient
    {
        internal static readonly SunderRpcProviderSnapshot Provider = new("test.package", "1.0.0", "test.provider", "test.contract", "1.0.0",
            new('a', 64), new('b', 64), Guid.NewGuid(), 2, 3, new("rpc1_exact"), 4, SunderRpcProviderState.Active);
        internal SunderRpcProviderSnapshot? Current = Provider;
        internal SunderRpcException? InvokeFailure;
        internal SunderRpcException? MetadataFailure;
        internal SunderRpcException? DisposeFailure;
        internal SunderRpcException? CreationFailure;
        internal SunderRpcException? StreamFailure;
        internal Func<int, ValueTask<JsonElement>>? InvokeBehavior;
        internal int InvokeAttempts;
        internal bool IsWorkerScope;
        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => CreationFailure is { } failure ? ValueTask.FromException<ISunderRpcCallScope>(failure)
                : ValueTask.FromResult<ISunderRpcCallScope>(trusted ? new Trusted(this) : IsWorkerScope ? new Worker(this) : new Scope(this));
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            => MetadataFailure is { } failure ? ValueTask.FromException<SunderRpcProviderSnapshot?>(failure) : ValueTask.FromResult(Current);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SunderRpcCatalogSnapshot(4, 1, Current is { } provider ? [provider] : []));
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref InvokeAttempts);
            return InvokeBehavior is { } behavior ? behavior(attempt)
                : InvokeFailure is { } failure ? ValueTask.FromException<JsonElement>(failure) : ValueTask.FromResult(JsonSerializer.SerializeToElement(1));
        }
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => Stream(cancellationToken);
        private async IAsyncEnumerable<JsonElement> Stream([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            if (StreamFailure is { } error) throw error;
            yield return JsonSerializer.SerializeToElement(1);
        }
        private sealed class Trusted(Probe owner) : Scope(owner), ISunderRpcTrustedTransportScope { }
        private sealed class Worker(Probe owner) : Scope(owner), ISunderRpcTrustedDispatchMetadataScope { }
        private class Scope(Probe owner) : ISunderRpcCallScope
        {
            public DateTimeOffset DeadlineUtc => DateTimeOffset.UtcNow.AddMinutes(1);
            public ValueTask DisposeAsync() => owner.DisposeFailure is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
            public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default) => owner.GetProviderAsync(endpoint, cancellationToken);
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => owner.DiscoverAsync(contractId, cancellationToken);
            public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
                SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => owner.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
                SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => owner.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
