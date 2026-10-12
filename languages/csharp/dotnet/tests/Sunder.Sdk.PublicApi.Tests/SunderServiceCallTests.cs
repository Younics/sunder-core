using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderServiceCallTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteCancellationUsesTheOriginalCallerTokenAndRetainsTheStructuredInnerError(bool enumerationOnly)
    {
        using var caller = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        using var retirement = new CancellationTokenSource();
        var remote = new SunderRpcException(new(SunderRpcErrorKind.Cancelled, "rpc.cancelled", "Cancelled by host.",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        (enumerationOnly ? enumeration : caller).Cancel();
        retirement.Cancel();
        var result = Assert.IsType<OperationCanceledException>(SunderRpcServiceCall.TranslateCancellation(
            remote, caller.Token, enumeration.Token, retirement.Token));
        Assert.Equal(enumerationOnly ? enumeration.Token : caller.Token, result.CancellationToken);
        Assert.Same(remote, result.InnerException);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, remote.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, remote.Error.RetrySafety);
    }

    [Fact]
    public void CallerWinsBothOriginalsAndLinkedCancellationIsNormalizedWithoutMaskingOtherFailures()
    {
        using var caller = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, enumeration.Token);
        caller.Cancel();
        enumeration.Cancel();
        var original = new OperationCanceledException(linked.Token);
        var translated = Assert.IsType<OperationCanceledException>(SunderRpcServiceCall.TranslateCancellation(
            original, caller.Token, enumeration.Token));
        Assert.Equal(caller.Token, translated.CancellationToken);
        Assert.Same(original, translated.InnerException);
        var denied = new SunderRpcException(new(SunderRpcErrorKind.PermissionDenied, "rpc.denied", "Denied."));
        Assert.Same(denied, SunderRpcServiceCall.TranslateCancellation(denied, caller.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetirementWithoutCallerCancellationRemainsAStructuredSdkFailure(bool remote)
    {
        using var retirement = new CancellationTokenSource();
        retirement.Cancel();
        Exception failure = remote
            ? new SunderRpcException(new(SunderRpcErrorKind.Cancelled, "rpc.cancelled", "Cancelled.",
                SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe))
            : new OperationCanceledException(retirement.Token);
        var translated = Assert.IsType<SunderRpcException>(SunderRpcServiceCall.TranslateCancellation(
            failure, CancellationToken.None, retirementToken: retirement.Token));
        Assert.Equal(SunderRpcErrorKind.Unavailable, translated.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, translated.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, translated.Error.RetrySafety);
        var stale = Assert.Throws<SunderRpcException>(() => SunderRpcServiceCall.ThrowIfRetired(retirement.Token));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        Assert.Same(failure, SunderRpcServiceCall.TranslateCancellation(failure, CancellationToken.None));
    }

    [Theory]
    [InlineData("packageId")]
    [InlineData("packageVersion")]
    [InlineData("providerId")]
    [InlineData("contractId")]
    [InlineData("contractVersion")]
    [InlineData("descriptorSha256")]
    [InlineData("abiSha256")]
    [InlineData("activationId")]
    [InlineData("activationEpoch")]
    [InlineData("generationUp")]
    [InlineData("generationDown")]
    [InlineData("endpoint")]
    [InlineData("inactive")]
    [InlineData("missing")]
    public async Task ScopedIdentityChangesAreRejectedBeforePeerInvocationDespiteValidGlobalMetadata(string change)
    {
        var provider = ServiceResourceTestClient.Provider;
        var rpc = new ServiceBindingTestClient(provider);
        await using var bindings = new SunderRpcServiceBindingLifetime(rpc);
        await bindings.DiscoverAsync(provider.ContractId);
        await bindings.CreateProvider(provider, static (_, _, _) => new object()).EnsureCurrentAsync();
        rpc.ScopedProviderTransform = _ => change switch
        {
            "packageId" => provider with { PackageId = "other.package" },
            "packageVersion" => provider with { PackageVersion = "2.0.0" },
            "providerId" => provider with { ProviderId = "other.provider" },
            "contractId" => provider with { ContractId = "other.contract" },
            "contractVersion" => provider with { ContractVersion = "2.0.0" },
            "descriptorSha256" => provider with { DescriptorSha256 = new string('a', 64) },
            "abiSha256" => provider with { AbiSha256 = new string('a', 64) },
            "activationId" => provider with { ActivationId = Guid.NewGuid() },
            "activationEpoch" => provider with { ActivationEpoch = provider.ActivationEpoch + 1 },
            "generationUp" => provider with { SessionGeneration = provider.SessionGeneration + 1 },
            "generationDown" => provider with { SessionGeneration = provider.SessionGeneration - 1 },
            "endpoint" => provider with { Endpoint = new("other-endpoint") },
            "inactive" => provider with { State = SunderRpcProviderState.Inactive },
            "missing" => null,
            _ => throw new InvalidOperationException(),
        };
        var calls = 0;
        rpc.Invoke = (request, _) => { calls++; return ValueTask.FromResult(request); };
        await using var scope = await rpc.CreateCallScopeAsync();
        var error = await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            await SunderRpcServiceCall.EnsureCurrentAsync(scope, provider);
            await scope.InvokeAsync(provider.Endpoint, "resource", "get", JsonSerializer.SerializeToElement(new { }));
        });
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, error.Error.Kind);
        Assert.Equal(0, calls);
        Assert.True(rpc.Queries > 0); // Global observer metadata remains intentional, not prohibited by this regression.
    }

    [Fact]
    public async Task ScopedAuthorizationFailureIsPreservedBeforeDispatch()
    {
        var provider = ServiceResourceTestClient.Provider;
        var rpc = new ServiceBindingTestClient(provider);
        var denied = new SunderRpcException(new(SunderRpcErrorKind.PermissionDenied, "rpc.denied", "Scoped visibility denied."));
        rpc.ScopedProviderTransform = _ => throw denied;
        var calls = 0;
        rpc.Invoke = (request, _) => { calls++; return ValueTask.FromResult(request); };
        await using var scope = await rpc.CreateCallScopeAsync();
        Assert.Same(denied, await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            await SunderRpcServiceCall.EnsureCurrentAsync(scope, provider);
            await scope.InvokeAsync(provider.Endpoint, "resource", "get", JsonSerializer.SerializeToElement(new { }));
        }));
        Assert.Equal(0, calls);
    }
}
