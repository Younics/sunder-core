using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class ProcessRuntimeWorkerTests
{
    [Theory]
    [Trait("Capability", "Process")]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Worker_CallOwnershipSeparatesWatchDeadlineWithoutDroppingParentOrDepth(bool callOwnership, bool declareExtendedDeadline)
    {
        await using var fixture = await WorkerFixture.CreateAsync("nested-call-ownership",
            callOwnership: callOwnership, declareExtendedDeadline: declareExtendedDeadline);
        var activation = await fixture.PublishBrokerSessionAsync();
        await fixture.SetSubscribePermissionAsync(RuntimeRpcPermissionState.Granted);
        var watchDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
        await using var watch = activation.Client.SubscribeAsync(activation.Endpoint, "messages", "watch",
            JsonSerializer.SerializeToElement(new { message = "watch" }), new SunderRpcCallOptions(watchDeadline)).GetAsyncEnumerator();
        Assert.True(await watch.MoveNextAsync());

        var parentDeadline = DateTimeOffset.UtcNow.AddSeconds(declareExtendedDeadline ? 30 : 10);
        var result = await activation.Client.InvokeAsync(activation.Endpoint, "messages", "send",
            JsonSerializer.SerializeToElement(new { message = "provider" }), new SunderRpcCallOptions(parentDeadline));
        using var status = await fixture.WaitForWorkerStatusAsync("call-ownership.json");
        var named = status.RootElement.GetProperty("namedDeadline").GetDateTimeOffset();
        var root = status.RootElement.GetProperty("rootDeadline").GetDateTimeOffset();
        Assert.Equal(callOwnership, result.GetProperty("accepted").GetBoolean());
        Assert.Equal(3, status.RootElement.GetProperty("rootDepth").GetInt32());
        Assert.Equal("1", status.RootElement.GetProperty("lineageEnabled").GetString());
        if (callOwnership)
        {
            Assert.Equal(parentDeadline, named);
            Assert.True(root > named);
        }
        else
        {
            Assert.Equal(watchDeadline, named);
            Assert.Equal(watchDeadline, root);
        }
        var expired = await Assert.ThrowsAsync<SunderRpcException>(async () => await watch.MoveNextAsync());
        Assert.Equal(SunderRpcErrorKind.DeadlineExceeded, expired.Error.Kind);
    }
}
