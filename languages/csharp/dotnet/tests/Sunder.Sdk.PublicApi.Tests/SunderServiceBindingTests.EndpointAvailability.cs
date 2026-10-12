using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class SunderServiceBindingTests
{
    [Fact]
    public async Task ConcurrentEndpointUnavailabilityRetiresOnlyTheExactBindingAndKeepsDiscoveryHealthy()
    {
        var selected = ServiceResourceTestClient.Provider;
        var other = selected with { ContractId = "test.other", Endpoint = new("other-endpoint") };
        var rpc = new ServiceBindingTestClient(selected, other);
        await using var bindings = new SunderRpcServiceBindingLifetime(rpc);
        await bindings.DiscoverAsync(selected.ContractId);
        await bindings.DiscoverAsync(other.ContractId);
        var old = bindings.CreateProvider(selected, CreateService);
        var unaffected = bindings.CreateProvider(other, CreateService);
        var observer = await rpc.NextObservationAsync();
        var unavailable = new SunderRpcException(new(SunderRpcErrorKind.Unavailable, "test.lookup-failed", "Endpoint unavailable."));
        rpc.QueryFailure = unavailable;
        var lookups = rpc.Queries;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            Assert.Same(unavailable, await Assert.ThrowsAsync<SunderRpcException>(async () => await old.EnsureCurrentAsync())))));
        Assert.Equal(lookups + 16, rpc.Queries);
        Assert.True(old.RetirementToken.IsCancellationRequested);
        Assert.False(unaffected.RetirementToken.IsCancellationRequested);
        Assert.False(observer.Drained.Task.IsCompleted);
        Assert.Equal(1, rpc.Watches);

        var denied = new SunderRpcException(new(SunderRpcErrorKind.PermissionDenied, "test.lookup-failed", "Current permission denial."));
        rpc.QueryFailure = denied;
        var freshSnapshot = await bindings.DiscoverAsync(selected.ContractId);
        var fresh = bindings.CreateProvider(Assert.Single(freshSnapshot.Providers), CreateService);
        Assert.Same(denied, await Assert.ThrowsAsync<SunderRpcException>(async () => await fresh.EnsureCurrentAsync()));
        Assert.False(unaffected.RetirementToken.IsCancellationRequested);
        rpc.QueryFailure = null;
        await unaffected.EnsureCurrentAsync();
        await Assert.ThrowsAsync<SunderRpcException>(async () => await old.EnsureCurrentAsync());
        Assert.True(old.RetirementToken.IsCancellationRequested);
        var recoveredSnapshot = await bindings.DiscoverAsync(selected.ContractId);
        var recovered = bindings.CreateProvider(Assert.Single(recoveredSnapshot.Providers), CreateService);
        await recovered.EnsureCurrentAsync();
        Assert.NotSame(old, recovered);
        Assert.Equal(1, rpc.Watches);
    }
}
