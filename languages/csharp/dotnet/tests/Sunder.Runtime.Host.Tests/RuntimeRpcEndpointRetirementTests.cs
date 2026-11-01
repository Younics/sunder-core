using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcEndpointRetirementTests
{
    [Fact]
    public async Task Replacement_UsesNewEndpointAndOldReferenceNeverInvokesReplacement()
    {
        await using var fixture = new RpcFixture();
        var first = await fixture.PublishAsync(new TestHandler());
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var firstClient = fixture.CreateClient(first.Caller);
        var oldEndpoint = Assert.Single((await firstClient.DiscoverAsync("example.rpc")).Providers).Endpoint;

        var second = await fixture.PublishAsync(new TestHandler());
        var secondClient = fixture.CreateClient(second.Caller);
        var newEndpoint = Assert.Single((await secondClient.DiscoverAsync("example.rpc")).Providers).Endpoint;

        Assert.NotEqual(oldEndpoint, newEndpoint);
        var stale = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await secondClient.InvokeAsync(
                oldEndpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        Assert.True((await secondClient.InvokeAsync(
            newEndpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }))).GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task SessionRetirement_CancelsCallAndDrainsBeforeReplacementDisposal()
    {
        await using var fixture = new RpcFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await fixture.PublishAsync(new TestHandler
        {
            Unary = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    stopped.TrySetResult();
                }
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(first.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var call = client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "wait" })).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var replacement = fixture.PublishAsync(new TestHandler());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await replacement;
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, failure.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, failure.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, failure.Error.RetrySafety);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerAndCalleeRetirement_CancelExactActiveCall(bool retireCaller)
    {
        await using var fixture = new RpcFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Unary = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var call = client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "wait" })).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stamp = retireCaller ? activation.Caller : activation.Provider;
        Assert.True(fixture.Catalog.DeactivatePackage(
            stamp.PackageId,
            stamp.ActivationId,
            faulted: false));
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        Assert.Equal(
            retireCaller ? SunderRpcErrorKind.Cancelled : SunderRpcErrorKind.StaleEndpoint,
            failure.Error.Kind);
    }
}
