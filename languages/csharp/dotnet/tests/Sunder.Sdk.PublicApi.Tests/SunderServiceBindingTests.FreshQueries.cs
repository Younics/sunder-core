using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class SunderServiceBindingTests
{
    [Theory]
    [InlineData("discover")]
    [InlineData("ensure")]
    [InlineData("resolve")]
    public async Task FiniteResourcePressurePreservesExactBindingsAndIndependentCatalogObservation(string operation)
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var other = snapshot with { ContractId = "test.other", Endpoint = new("other-endpoint") };
        var client = new ServiceBindingTestClient(snapshot, other);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(snapshot.ContractId);
        await bindings.DiscoverAsync(other.ContractId);
        var selected = bindings.CreateProvider(snapshot, CreateService);
        var unrelated = bindings.CreateProvider(other, CreateService);
        var observer = await client.NextObservationAsync();
        var pressure = new SunderRpcException(new(SunderRpcErrorKind.ResourceExhausted,
            "rpc.worker.call-limit", "The worker is busy."));
        client.QueryFailure = pressure;
        client.DiscoveryFailure = _ => pressure;

        Assert.Same(pressure, await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            if (operation == "discover") await bindings.DiscoverAsync(snapshot.ContractId);
            else if (operation == "ensure") await selected.EnsureCurrentAsync();
            else await bindings.ResolveAsync(snapshot, CreateService);
        }));
        Assert.False(selected.RetirementToken.IsCancellationRequested);
        Assert.False(unrelated.RetirementToken.IsCancellationRequested);
        Assert.False(observer.DisposalStarted.Task.IsCompleted);
        Assert.Equal(1, client.Watches);

        client.QueryFailure = null;
        client.DiscoveryFailure = null;
        await selected.EnsureCurrentAsync();
        await unrelated.EnsureCurrentAsync();
        Assert.Same(selected, bindings.CreateProvider(snapshot, CreateService));
        Assert.Equal(1, client.MaximumConcurrentWatches);
    }

    [Fact]
    public async Task SuccessfulIndependentDiscoveryAfterTransportReopenWaitsForObservationAndNeverRevivesIssuedReference()
    {
        var provider = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(provider);
        await using var lifetime = new SunderRpcServiceBindingLifetime(client);
        await lifetime.DiscoverAsync(provider.ContractId);
        var original = lifetime.CreateProvider(provider, CreateService);
        var observer = await client.NextObservationAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.DisposalWait = release.Task;
        try
        {
            client.Publish(SunderRpcCatalogEventKind.ResetRequired);
            await observer.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var next = lifetime.DiscoverAsync(provider.ContractId).AsTask();
            Assert.False(next.IsCompleted); // Fresh probe succeeded; old observer is still draining.
            Assert.True(original.RetirementToken.IsCancellationRequested);
            release.TrySetResult();
            var discovered = await next.WaitAsync(TimeSpan.FromSeconds(5));
            var fresh = lifetime.CreateProvider(Assert.Single(discovered.Providers), CreateService);
            await fresh.EnsureCurrentAsync();
            Assert.NotSame(original, fresh);
            await Assert.ThrowsAsync<SunderRpcException>(async () => await original.EnsureCurrentAsync());
            Assert.Equal(1, client.MaximumConcurrentWatches);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("discover")]
    [InlineData("ensure")]
    [InlineData("resolve")]
    public async Task ExplicitFreshLookupSurfacesPermissionDenialWhileObserverRecoveryIsBlocked(string operation)
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var other = snapshot with { ContractId = "test.other", Endpoint = new("other-endpoint") };
        var client = new ServiceBindingTestClient(snapshot, other);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(snapshot.ContractId);
        await bindings.DiscoverAsync(other.ContractId);
        var selected = bindings.CreateProvider(snapshot, CreateService);
        var observer = await client.NextObservationAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.DisposalWait = release.Task;
        var unavailable = new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "test.offline", "Temporarily unavailable."));
        var denied = new SunderRpcException(new(SunderRpcErrorKind.PermissionDenied, "test.denied", "Current permission denial.",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        try
        {
            client.QueryFailure = unavailable;
            Assert.Same(unavailable, await Assert.ThrowsAsync<SunderRpcException>(async () => await selected.EnsureCurrentAsync()));
            Assert.True(selected.RetirementToken.IsCancellationRequested);
            // An endpoint outage alone must no longer poison a healthy catalog. Introduce a real
            // observer reset to retain this test's separate blocked-recovery coverage.
            client.Publish(SunderRpcCatalogEventKind.ResetRequired);
            await observer.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.QueryFailure = denied;
            client.DiscoveryFailure = contract => contract == snapshot.ContractId ? denied : null;
            var count = operation == "discover" ? client.Discoveries : client.Queries;

            // Recovery cannot finish while its enumerator cleanup is held. The explicit read itself
            // must hit the client; no sleep or 250 ms watcher retry makes this assertion pass.
            var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            {
                if (operation == "ensure") await selected.EnsureCurrentAsync();
                else if (operation == "resolve") await bindings.ResolveAsync(snapshot, CreateService);
                else await bindings.DiscoverAsync(snapshot.ContractId);
            });
            Assert.Same(denied, failure);
            Assert.Equal(count + 1, operation == "discover" ? client.Discoveries : client.Queries);
            Assert.Equal(1, client.Watches);
            Assert.False(observer.Drained.Task.IsCompleted);
            Assert.True(selected.RetirementToken.IsCancellationRequested);
        }
        finally { release.TrySetResult(); }

        client.QueryFailure = null;
        var recovered = await DiscoverAfterRecovery(bindings, other.ContractId);
        await bindings.CreateProvider(Assert.Single(recovered.Providers), CreateService).EnsureCurrentAsync();
        Assert.Same(denied, await Assert.ThrowsAsync<SunderRpcException>(async () => await bindings.DiscoverAsync(snapshot.ContractId)));
        client.DiscoveryFailure = null;
        await bindings.DiscoverAsync(snapshot.ContractId);
        // Even a fresh successful metadata lookup cannot revive the retired selection.
        await Assert.ThrowsAsync<SunderRpcException>(async () => await selected.EnsureCurrentAsync());
        Assert.True(selected.RetirementToken.IsCancellationRequested);
        Assert.Equal(1, client.MaximumConcurrentWatches);
    }

    [Fact]
    public async Task ConcurrentExplicitProbesDoNotRestartOrMutateTheRecoveringObservation()
    {
        var snapshot = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(snapshot);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(snapshot.ContractId);
        var selected = bindings.CreateProvider(snapshot, CreateService);
        var observer = await client.NextObservationAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.DisposalWait = release.Task;
        var unavailable = new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "test.offline", "Offline."));
        try
        {
            client.QueryFailure = unavailable;
            await Assert.ThrowsAsync<SunderRpcException>(async () => await selected.EnsureCurrentAsync());
            client.Publish(SunderRpcCatalogEventKind.ResetRequired);
            await observer.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.DiscoveryFailure = _ => unavailable;
            var before = client.Discoveries;
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
                Assert.Same(unavailable, await Assert.ThrowsAsync<SunderRpcException>(async () =>
                    await bindings.DiscoverAsync(snapshot.ContractId))))));
            Assert.Equal(before + 16, client.Discoveries);
            Assert.Equal(1, client.Watches);
            Assert.False(observer.Drained.Task.IsCompleted);
        }
        finally
        {
            client.QueryFailure = null;
            client.DiscoveryFailure = null;
            release.TrySetResult();
        }
        var catalog = await DiscoverAfterRecovery(bindings, snapshot.ContractId);
        await bindings.CreateProvider(Assert.Single(catalog.Providers), CreateService).EnsureCurrentAsync();
        Assert.True(selected.RetirementToken.IsCancellationRequested);
        Assert.Equal(2, client.Watches);
        Assert.Equal(1, client.MaximumConcurrentWatches);
    }
}
