using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class SunderServiceBindingTests
{
    [Fact]
    public async Task RequestReferenceRetirementCancelsSourceBeforeSourceCallsItsExecutionTarget()
    {
        var execution = ServiceResourceTestClient.Provider;
        var source = execution with { ProviderId = "test.toolsource", Endpoint = new("tool-source") };
        var client = new ServiceBindingTestClient(execution, source);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowTarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetCalls = 0;
        client.Invoke = async (request, token) =>
        {
            entered.TrySetResult();
            await allowTarget.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref targetCalls);
            return request;
        };
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(execution.ContractId);
        var selected = bindings.CreateProvider(execution, CreateService).Reference;
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var operation = await resources.CreateCallAsync(client, source.Endpoint);
        Configure(operation, bindings);
        var request = await operation.SerializeAsync(new[] { selected, selected },
            static async (context, references, token) =>
            {
                foreach (var reference in references) await context.RegisterServiceReferenceAsync(reference, token);
            });
        var invoking = operation.Client!.InvokeAsync(source.Endpoint, "resource", "get", request,
            cancellationToken: operation.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Publish(SunderRpcCatalogEventKind.Removed, execution);
        await Cancelled(operation.CancellationToken);
        allowTarget.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoking);
        Assert.Equal(0, targetCalls);
        await Assert.Single(client.Scopes).Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, client.Scopes[0].Disposals);
        Assert.Equal(1, client.Watches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParticipantsAndExactTargetRetirementOutliveTheOperationWithRetainedContent(bool targetItself)
    {
        var selected = ServiceResourceTestClient.Provider;
        var source = selected with { ProviderId = "test.source", Endpoint = new("source") };
        var client = new ServiceBindingTestClient(selected, source);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(selected.ContractId);
        var reference = bindings.CreateProvider(selected, CreateService).Reference;
        await using var resources = new SunderRpcResourceLifetime(bindings);
        var operation = await resources.CreateCallAsync(client, targetItself ? selected.Endpoint : source.Endpoint,
            providerRetirementToken: targetItself ? reference.RetirementToken : default);
        if (!targetItself)
        {
            Configure(operation, bindings);
            await operation.SerializeAsync(reference,
                static (context, value, token) => context.RegisterServiceReferenceAsync(value, token));
        }
        await using var content = await operation.DeserializeAsync<SunderContent>(ServiceResourceTestClient.ContentJson());
        await operation.DisposeAsync();
        var scope = Assert.Single(client.Scopes);
        Assert.Equal(0, scope.Disposals);
        client.Publish(SunderRpcCatalogEventKind.Deactivated, selected);
        await scope.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await content.OpenReadAsync());
        Assert.Equal(1, scope.Disposals);
    }

    [Fact]
    public async Task IncomingReferenceParticipantCancelsProviderDomainTokenWithoutAnAuthoredLease()
    {
        var selected = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(selected);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var sending = await resources.CreateCallAsync(client, selected.Endpoint);
        Configure(sending, bindings);
        await bindings.DiscoverAsync(selected.ContractId);
        var reference = bindings.CreateProvider(selected, CreateService).Reference;
        var wire = await sending.SerializeAsync(reference,
            static (context, value, token) => context.RegisterServiceReferenceAsync(value, token));
        using var invocationCancellation = new CancellationTokenSource();
        var invocation = new SunderRpcInvocationContext("caller.package", "1.0.0", selected,
            DateTimeOffset.UtcNow.AddMinutes(5), 1, invocationCancellation.Token);
        await using var receiving = SunderRpcMarshallingContext.ForInvocation(invocation, client, bindings);
        Configure(receiving, bindings);
        var decoded = await receiving.DeserializeAsync<ServiceReference<BoundService>>(wire,
            static (context, value, token) => context.PrepareServiceReferenceAsync<BoundService>(value, token));
        Assert.False(receiving.CancellationToken.IsCancellationRequested);
        client.Publish(SunderRpcCatalogEventKind.Faulted, selected);
        await Cancelled(receiving.CancellationToken);
        Assert.True(decoded.RetirementToken.IsCancellationRequested);
        Assert.False(invocationCancellation.IsCancellationRequested);
        Assert.Equal(1, client.Watches);
    }

    [Fact]
    public async Task AlreadyRetiredRequestReferenceCannotBeReboundOrSentAfterRecovery()
    {
        var selected = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(selected);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(selected.ContractId);
        var reference = bindings.CreateProvider(selected, CreateService).Reference;
        client.Disconnect();
        await Cancelled(reference.RetirementToken);
        await DiscoverAfterRecovery(bindings, selected.ContractId);
        await using var resources = new SunderRpcResourceLifetime(bindings);
        await using var operation = await resources.CreateCallAsync(client, selected.Endpoint);
        Configure(operation, bindings);
        await Assert.ThrowsAsync<SunderRpcException>(async () => await operation.SerializeAsync(reference,
            static (context, value, token) => context.RegisterServiceReferenceAsync(value, token)));
        Assert.True(reference.RetirementToken.IsCancellationRequested);
    }

    [Fact]
    public async Task WithdrawnBindingsAreRemovedSoRepeatedActivationsDoNotFillTheBoundedCache()
    {
        var selected = ServiceResourceTestClient.Provider;
        var client = new ServiceBindingTestClient(selected);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        await bindings.DiscoverAsync(selected.ContractId);
        for (var index = 0; index < 4100; index++)
        {
            var current = selected with { ActivationId = Guid.NewGuid(), ActivationEpoch = index + 1,
                Endpoint = new("activation-" + index) };
            client.ReplaceSilently(current);
            await bindings.DiscoverAsync(current.ContractId);
            var entry = bindings.CreateProvider(current, CreateService);
            client.Publish(SunderRpcCatalogEventKind.Removed, current);
            await Cancelled(entry.RetirementToken);
        }
        Assert.Equal(1, client.Watches);
        Assert.Equal(1, client.MaximumConcurrentWatches);
    }

    private static void Configure(SunderRpcMarshallingContext context, SunderRpcServiceBindingLifetime bindings)
        => context.AddServiceReference(ServiceResourceTestClient.Contract, "resource", bindings, CreateService);
}
