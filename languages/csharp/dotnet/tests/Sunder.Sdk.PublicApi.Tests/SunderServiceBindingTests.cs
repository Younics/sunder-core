using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class SunderServiceBindingTests
{
    [Fact]
    public async Task InjectionIsDormantAndFirstUseSharesOneObserverAndTheCompleteSnapshotCursor()
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(snapshot);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await using var resources = new SunderRpcResourceLifetime(bindings);
        Assert.Equal(0, client.Discoveries);
        Assert.Equal(0, client.Watches);
        Assert.Empty(client.Scopes);

        var catalog = await bindings.DiscoverAsync(snapshot.ContractId);
        var first = bindings.CreateProvider(Assert.Single(catalog.Providers), CreateService);
        var second = bindings.CreateProvider(snapshot, CreateService);
        var observer = await client.NextObservationAsync();
        Assert.Equal(11, observer.Revision);
        Assert.Equal(37, observer.Sequence);
        Assert.True(client.Discoveries >= 3); // initial snapshot, post-subscription resnapshot, actual List query
        Assert.Equal(1, client.Watches);
        Assert.Same(first, second);
        Assert.Equal(first.RetirementToken, first.Reference.RetirementToken);
        await first.EnsureCurrentAsync();
        Assert.Equal("ready", first.Service.Use());
        Assert.Empty(client.Scopes);
        await bindings.DisposeAsync();
        await observer.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.RetirementToken.IsCancellationRequested);
        Assert.True(first.Reference.RetirementToken.IsCancellationRequested);
    }

    [Fact]
    public async Task InitialResnapshotRetiresAnEntryWithdrawnWhileSubscriptionIsEstablished()
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(snapshot);
        client.WatchStarting = _ => client.ReplaceSilently();
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        var entry = new ServiceProvider<BoundService>(snapshot, new(CancellationToken.None), bindings);
        Assert.Equal(0, client.Discoveries);
        var retirement = entry.RetirementToken;
        var catalog = await bindings.DiscoverAsync(snapshot.ContractId);
        Assert.Empty(catalog.Providers);
        await Cancelled(retirement);
        var error = await Assert.ThrowsAsync<SunderRpcException>(async () => await entry.EnsureCurrentAsync());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, error.Error.Kind);
    }

    [Theory]
    [InlineData("withdrawal")]
    [InlineData("disconnect")]
    [InlineData("reset")]
    [InlineData("invalid")]
    public async Task RetirementIsPermanentAndRecoveryCreatesFreshBindings(string reason)
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(snapshot);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(snapshot.ContractId);
        var old = bindings.CreateProvider(snapshot, CreateService);
        var oldService = old.Service;
        var original = await client.NextObservationAsync();
        using var throwingCallback = old.RetirementToken.Register(() => throw new InvalidOperationException("business callback"));
        switch (reason)
        {
            case "withdrawal": client.Publish(SunderRpcCatalogEventKind.Removed, snapshot); break;
            case "disconnect": client.Disconnect(); break;
            case "reset": client.Publish(SunderRpcCatalogEventKind.ResetRequired); break;
            case "invalid": client.Publish(SunderRpcCatalogEventKind.Added, snapshot with { AbiSha256 = "invalid" }); break;
        }
        await Cancelled(old.RetirementToken);
        Assert.ThrowsAny<OperationCanceledException>(() => oldService.Use());
        Assert.Throws<SunderRpcException>(() => old.Service);
        Assert.Throws<SunderRpcException>(() => old.Reference.Service);
        client.ReplaceSilently(snapshot);
        if (reason != "withdrawal")
        {
            await original.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = await client.NextObservationAsync();
            Assert.True(replacement.Sequence >= original.Sequence);
        }
        var fresh = await DiscoverAfterRecovery(bindings, snapshot.ContractId);
        var current = bindings.CreateProvider(Assert.Single(fresh.Providers), CreateService);
        Assert.NotSame(old, current);
        Assert.NotSame(oldService, current.Service);
        Assert.True(old.RetirementToken.IsCancellationRequested);
        await Assert.ThrowsAsync<SunderRpcException>(async () => await old.Reference.EnsureCurrentAsync());
        await current.Reference.EnsureCurrentAsync();
        Assert.Equal("ready", current.Service.Use());
        Assert.Equal(1, client.MaximumConcurrentWatches);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task EnsureCurrentRejectsBothGenerationDirectionsAndNeverRevivesAnIssuedReference(int delta)
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(snapshot);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(snapshot.ContractId);
        var reference = bindings.CreateProvider(snapshot, CreateService).Reference;
        client.ReplaceSilently(snapshot with { SessionGeneration = snapshot.SessionGeneration + delta });
        var stale = await Assert.ThrowsAsync<SunderRpcException>(async () => await reference.EnsureCurrentAsync());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        await Cancelled(reference.RetirementToken);
        client.ReplaceSilently(snapshot);
        await Assert.ThrowsAsync<SunderRpcException>(async () => await reference.EnsureCurrentAsync());
    }

    [Fact]
    public async Task ResetRetiresIssuedReferencesBeforeWaitingForWatchCleanup()
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(snapshot);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(snapshot.ContractId);
        var entry = bindings.CreateProvider(snapshot, CreateService);
        var observer = await client.NextObservationAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.DisposalWait = release.Task;
        try
        {
            client.Publish(SunderRpcCatalogEventKind.ResetRequired);
            await Cancelled(entry.RetirementToken);
            Assert.False(observer.Drained.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await observer.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InvalidLookupRetiresTheObservationButContractPermissionDenialRemainsDistinct()
    {
        var first = ServiceResourceTestClient.Provider;
        var second = first with { ContractId = "test.other", Endpoint = new("other") };
        var client = new ServiceBindingTestClient(first, second);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(first.ContractId);
        await bindings.DiscoverAsync(second.ContractId);
        var denied = bindings.CreateProvider(first, CreateService);
        var unaffected = bindings.CreateProvider(second, CreateService);
        client.QueryFailure = new(new(SunderRpcErrorKind.PermissionDenied, "test.denied", "Denied."));
        Assert.Same(client.QueryFailure, await Assert.ThrowsAsync<SunderRpcException>(async () => await denied.EnsureCurrentAsync()));
        Assert.True(denied.RetirementToken.IsCancellationRequested);
        Assert.False(unaffected.RetirementToken.IsCancellationRequested);
        client.QueryFailure = null;
        await unaffected.EnsureCurrentAsync();

        await bindings.DiscoverAsync(first.ContractId);
        var reselected = bindings.CreateProvider(first, CreateService);
        client.ReplaceSilently(first with { DescriptorSha256 = "invalid" }, second);
        var malformed = await Assert.ThrowsAsync<SunderRpcException>(async () => await reselected.EnsureCurrentAsync());
        Assert.Equal(SunderRpcErrorKind.Protocol, malformed.Error.Kind);
        await Cancelled(unaffected.RetirementToken);
        Assert.Equal(1, client.MaximumConcurrentWatches);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.Unavailable)]
    [InlineData(SunderRpcErrorKind.Protocol)]
    public async Task LookupAndReportPreserveStructuredFailures(SunderRpcErrorKind kind)
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(snapshot);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(snapshot.ContractId);
        var reference = bindings.CreateProvider(snapshot, CreateService).Reference;
        var expected = new SunderRpcException(new(kind, "test.structured", "Original failure.",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        client.ReportFailure = expected;
        var actual = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await reference.TryReportInvariantViolationAsync(new InvalidOperationException("invalid graph")));
        Assert.Same(expected, actual);
        Assert.Same(expected.Error, actual.Error);
        await Cancelled(reference.RetirementToken);
        Assert.Single(client.Reports);

        var lookupClient = new ServiceBindingTestClient(snapshot);
        await using var lookupBindings = new SunderRpcServiceBindingLifetime(lookupClient);
        await lookupBindings.DiscoverAsync(snapshot.ContractId);
        var lookup = lookupBindings.CreateProvider(snapshot, CreateService);
        lookupClient.QueryFailure = expected;
        Assert.Same(expected, await Assert.ThrowsAsync<SunderRpcException>(async () => await lookup.EnsureCurrentAsync()));
        await Cancelled(lookup.RetirementToken);
    }

    [Fact]
    public async Task SemanticInvariantReportUsesOnlyTheValidatedExactActivation()
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(snapshot);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(snapshot.ContractId);
        var entry = bindings.CreateProvider(snapshot, CreateService);
        var error = new InvalidOperationException("invalid editor graph");
        Assert.True(await entry.TryReportInvariantViolationAsync(error));
        var report = Assert.Single(client.Reports);
        Assert.Equal(snapshot.Endpoint, report.Endpoint);
        Assert.Same(error, report.Error);
        await Cancelled(entry.RetirementToken);

        var other = snapshot with { ActivationId = Guid.NewGuid(), Endpoint = new("replacement") };
        client.ReplaceSilently(other);
        await bindings.DiscoverAsync(snapshot.ContractId);
        var reference = bindings.CreateProvider(other, CreateService).Reference;
        client.ReplaceSilently();
        Assert.False(await reference.TryReportInvariantViolationAsync(error));
        Assert.Single(client.Reports);
    }

    [Fact]
    public async Task DisposalDrainsSharedObservationAndInFlightValidation()
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(snapshot);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.QueryWait = async token => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(snapshot.ContractId);
        var entry = bindings.CreateProvider(snapshot, CreateService);
        var observer = await client.NextObservationAsync();
        var validating = entry.EnsureCurrentAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await bindings.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await observer.Drained.Task;
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => validating);
        Assert.Equal(SunderRpcErrorKind.Unavailable, error.Error.Kind);
        Assert.True(entry.RetirementToken.IsCancellationRequested);
    }

    private static BoundService CreateService(ISunderRpcClient client, SunderRpcProviderSnapshot snapshot, CancellationToken retirement)
        => new(retirement);

    private static async Task Cancelled(CancellationToken token)
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Task.Delay(Timeout.InfiniteTimeSpan, token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<SunderRpcCatalogSnapshot> DiscoverAfterRecovery(SunderRpcServiceBindingLifetime bindings, string contractId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try { return await bindings.DiscoverAsync(contractId, timeout.Token); }
            catch (SunderRpcException error) when (error.Error.Kind is SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.Protocol)
            { await Task.Delay(10, timeout.Token); }
        }
    }

    public sealed class BoundService(CancellationToken retirement)
    {
        public string Use() { retirement.ThrowIfCancellationRequested(); return "ready"; }
    }
}
