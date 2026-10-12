using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class SunderServiceBindingTests
{
    public sealed record OptionalTarget([property: JsonRequired] ServiceReference<BoundService>? Target);

    [Fact]
    public async Task OptionalReference_FreshPermissionFailureWinsOverEarlierRetirement()
    {
        var target = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(target);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(target.ContractId);
        var reference = bindings.CreateProvider(target, CreateService).Reference;
        client.Publish(SunderRpcCatalogEventKind.Removed, target);
        await Cancelled(reference.RetirementToken);
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var sending = await resources.CreateCallAsync(client, new("source"));
        Configure(sending, bindings);
        var denied = new SunderRpcException(new(SunderRpcErrorKind.PermissionDenied, "rpc.denied", "fresh denial"), hostAuthenticated: true);
        client.QueryFailure = denied;
        Assert.Same(denied, await Assert.ThrowsAsync<SunderRpcException>(async () => await sending.SerializeAsync(reference,
            static (context, value, token) => context.RegisterOptionalServiceReferenceAsync(value, token))));
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("write")]
    [InlineData("decode")]
    public async Task OptionalReference_RetirementRacesCannotDeliverAStaleCallableReference(string phase)
    {
        var target = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(target);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(target.ContractId);
        var reference = bindings.CreateProvider(target, CreateService).Reference;
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var sending = await resources.CreateCallAsync(client, new("outer-source"));
        Configure(sending, bindings);
        async Task Retire()
        {
            client.Publish(SunderRpcCatalogEventKind.Removed, target);
            await Cancelled(reference.RetirementToken);
        }
        if (phase == "lookup") client.QueryWait = async _ => await Retire();
        var wire = await sending.SerializeAsync(new OptionalTarget(reference), async (context, value, token) =>
        {
            await context.RegisterOptionalServiceReferenceAsync(value.Target, token);
            if (phase == "write") await Retire();
        });
        client.QueryWait = null;
        if (phase != "decode") Assert.Equal(JsonValueKind.Null, wire.GetProperty("target").ValueKind);
        var invocation = new SunderRpcInvocationContext("caller.package", "1.0.0", target, DateTimeOffset.UtcNow.AddMinutes(1), 1, CancellationToken.None);
        await using var receiving = SunderRpcMarshallingContext.ForInvocation(invocation, client, bindings);
        Configure(receiving, bindings);
        var decoded = await receiving.DeserializeAsync<OptionalTarget>(wire, async (context, value, token) =>
        {
            await context.PrepareOptionalServiceReferenceAsync<BoundService>(value.GetProperty("target"), token);
            if (phase == "decode") await Retire();
        });
        Assert.Null(decoded.Target);
        Assert.False(sending.CancellationToken.IsCancellationRequested);
        Assert.False(receiving.CancellationToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.StaleEndpoint)]
    [InlineData(SunderRpcErrorKind.Unavailable)]
    public async Task OptionalReference_SafeLookupAbsenceDoesNotRetireAnUnrelatedMainCallee(SunderRpcErrorKind kind)
    {
        var target = ServiceResourceTestClient.Provider;
        var source = target with { ProviderId = "test.source", Endpoint = new("source") };
        var client = new ServiceBindingTestClient(target, source);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(target.ContractId);
        var dependency = bindings.CreateProvider(target, CreateService).Reference;
        var callee = bindings.CreateProvider(source, CreateService).Reference;
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var sending = await resources.CreateCallAsync(client, source.Endpoint, providerRetirementToken: callee.RetirementToken);
        Configure(sending, bindings);
        client.QueryFailure = new(new(kind, "rpc.test.absent", "absent"), hostAuthenticated: true);
        var wire = await sending.SerializeAsync(new OptionalTarget(dependency),
            static (context, value, token) => context.RegisterOptionalServiceReferenceAsync(value.Target, token));
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("target").ValueKind);
        Assert.False(callee.RetirementToken.IsCancellationRequested);
        Assert.False(sending.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, Assert.Single(client.Scopes).Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalReference_BeforeDeliveryBecomesExplicitNullWithoutRetargeting(bool receiverRace)
    {
        var target = ServiceResourceTestClient.Provider;
        var source = target with { ProviderId = "test.source", Endpoint = new("source") };
        var client = new ServiceBindingTestClient(target, source);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(target.ContractId);
        var reference = bindings.CreateProvider(target, CreateService).Reference;
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var sending = await resources.CreateCallAsync(client, source.Endpoint);
        Configure(sending, bindings);
        JsonElement wire = default;
        if (receiverRace) wire = await sending.SerializeAsync(new OptionalTarget(reference),
            static (context, value, token) => context.RegisterOptionalServiceReferenceAsync(value.Target, token));
        client.Publish(SunderRpcCatalogEventKind.Removed, target);
        await Cancelled(reference.RetirementToken);
        client.Publish(SunderRpcCatalogEventKind.Added, target with { ActivationId = Guid.NewGuid(), ActivationEpoch = 4 });
        if (!receiverRace)
        {
            wire = await sending.SerializeAsync(new OptionalTarget(reference),
                static (context, value, token) => context.RegisterOptionalServiceReferenceAsync(value.Target, token));
            Assert.Equal(JsonValueKind.Null, wire.GetProperty("target").ValueKind);
        }
        var invocation = new SunderRpcInvocationContext("caller.package", "1.0.0", source, DateTimeOffset.UtcNow.AddMinutes(1), 1, sending.CancellationToken);
        await using var receiving = SunderRpcMarshallingContext.ForInvocation(invocation, client, bindings);
        Configure(receiving, bindings);
        var result = await receiving.DeserializeAsync<OptionalTarget>(wire,
            static (context, value, token) => context.PrepareOptionalServiceReferenceAsync<BoundService>(value.GetProperty("target"), token));
        Assert.Null(result.Target);
        Assert.False(sending.CancellationToken.IsCancellationRequested);
        Assert.False(receiving.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, Assert.Single(client.Scopes).Disposals);
        Assert.Throws<SunderRpcException>(() => reference.Service);
    }

    [Fact]
    public async Task OptionalReference_MidflightRetirementLeavesOuterScopeAndProviderTokenUsable()
    {
        var target = ServiceResourceTestClient.Provider;
        var source = target with { ProviderId = "test.source", Endpoint = new("source") };
        var client = new ServiceBindingTestClient(target, source);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(target.ContractId);
        var reference = bindings.CreateProvider(target, CreateService).Reference;
        var sourceReference = bindings.CreateProvider(source, CreateService).Reference;
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var sending = await resources.CreateCallAsync(client, source.Endpoint, providerRetirementToken: sourceReference.RetirementToken);
        Configure(sending, bindings);
        var wire = await sending.SerializeAsync(reference,
            static (context, value, token) => context.RegisterOptionalServiceReferenceAsync(value, token));
        var invocation = new SunderRpcInvocationContext("caller.package", "1.0.0", source, DateTimeOffset.UtcNow.AddMinutes(1), 1, sending.CancellationToken);
        await using var receiving = SunderRpcMarshallingContext.ForInvocation(invocation, client, bindings);
        Configure(receiving, bindings);
        var decoded = await receiving.DeserializeAsync<ServiceReference<BoundService>>(wire,
            static (context, value, token) => context.PrepareOptionalServiceReferenceAsync<BoundService>(value, token));
        var captured = decoded.Service;
        client.Publish(SunderRpcCatalogEventKind.Removed, target);
        await Cancelled(decoded.RetirementToken);
        Assert.ThrowsAny<OperationCanceledException>(() => captured.Use());
        Assert.Throws<SunderRpcException>(() => decoded.Service);
        Assert.False(sending.CancellationToken.IsCancellationRequested);
        Assert.False(receiving.CancellationToken.IsCancellationRequested);
        var fallback = await receiving.SerializeAsync("package-unavailable");
        Assert.Equal("package-unavailable", (await sending.Client!.InvokeAsync(source.Endpoint, "resource", "get", fallback,
            cancellationToken: sending.CancellationToken)).GetString());
        Assert.Equal(0, Assert.Single(client.Scopes).Disposals);
        client.Publish(SunderRpcCatalogEventKind.Removed, source);
        await Cancelled(sending.CancellationToken);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task OptionalReference_StrictOccurrenceWinsInEitherOrderForSameLocator(bool optionalFirst, bool distinctWrapper)
    {
        var target = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(target);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(target.ContractId);
        var entry = bindings.CreateProvider(target, CreateService);
        var first = entry.Reference;
        var second = distinctWrapper ? entry.Reference : first;
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var sending = await resources.CreateCallAsync(client, new("outer-source"));
        Configure(sending, bindings);
        var wire = await sending.SerializeAsync(new[] { first, second }, async (context, value, token) =>
        {
            if (optionalFirst) await context.RegisterOptionalServiceReferenceAsync(value[0], token);
            else await context.RegisterServiceReferenceAsync(value[0], token);
            if (optionalFirst) await context.RegisterServiceReferenceAsync(value[1], token);
            else await context.RegisterOptionalServiceReferenceAsync(value[1], token);
        });
        var invocation = new SunderRpcInvocationContext("caller.package", "1.0.0", target, DateTimeOffset.UtcNow.AddMinutes(1), 1, CancellationToken.None);
        await using var receiving = SunderRpcMarshallingContext.ForInvocation(invocation, client, bindings);
        Configure(receiving, bindings);
        await receiving.DeserializeAsync<ServiceReference<BoundService>[]>(wire, async (context, value, token) =>
        {
            if (optionalFirst) await context.PrepareOptionalServiceReferenceAsync<BoundService>(value[0], token);
            else await context.PrepareServiceReferenceAsync<BoundService>(value[0], token);
            if (optionalFirst) await context.PrepareServiceReferenceAsync<BoundService>(value[1], token);
            else await context.PrepareOptionalServiceReferenceAsync<BoundService>(value[1], token);
        });
        client.Publish(SunderRpcCatalogEventKind.Removed, target);
        await Cancelled(sending.CancellationToken);
        await Cancelled(receiving.CancellationToken);
        await Assert.Single(client.Scopes).Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.Unavailable, SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Safe)]
    [InlineData(SunderRpcErrorKind.StaleEndpoint, SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Unsafe)]
    [InlineData(SunderRpcErrorKind.PermissionDenied, SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe)]
    [InlineData(SunderRpcErrorKind.Protocol, SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe)]
    [InlineData(SunderRpcErrorKind.Validation, SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe)]
    [InlineData(SunderRpcErrorKind.DeadlineExceeded, SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe)]
    [InlineData(SunderRpcErrorKind.ProviderFaulted, SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe)]
    public async Task OptionalReference_DoesNotNormalizeUnsafeOrUnrelatedLookupFailure(SunderRpcErrorKind kind, SunderRpcDispatchState dispatch, SunderRpcRetrySafety retry)
    {
        var target = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(target);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(target.ContractId);
        var reference = bindings.CreateProvider(target, CreateService).Reference;
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var sending = await resources.CreateCallAsync(client, new("source"));
        Configure(sending, bindings);
        var failure = new SunderRpcException(new(kind, "rpc.test.lookup", "original", dispatch, retry), hostAuthenticated: true);
        client.QueryFailure = failure;
        var actual = await Assert.ThrowsAsync<SunderRpcException>(async () => await sending.SerializeAsync(reference,
            static (context, value, token) => context.RegisterOptionalServiceReferenceAsync(value, token)));
        Assert.Same(failure, actual);
        Assert.Same(failure.Error, actual.Error);
    }
}
