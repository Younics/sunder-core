using System.Globalization;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class ProcessRuntimeWorkerTests
{
    [Theory]
    [Trait("Capability", "Process")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_CausalNestingGuardIsIndependentOfWatchesSiblingsAndRootsAndCannotBeResetByScope(bool customLimit)
    {
        var rpcPolicy = new RuntimeRpcPolicyOptions
        {
            MaxConcurrentCallsPerCaller = 144,
            MaxConcurrentCallsPerCallee = 144,
        };
        if (customLimit) rpcPolicy = rpcPolicy with { MaxNestedCallDepth = 12 };
        await using var fixture = await WorkerFixture.CreateAsync("nested-call-nesting",
            policy: new RuntimeProcessPolicyOptions { MaxOutstandingHostCalls = 144, MaxOutstandingWorkerCalls = 144 },
            callOwnership: true, rpcPolicy: rpcPolicy);
        var activation = await fixture.PublishBrokerSessionAsync();
        await fixture.SetSubscribePermissionAsync(RuntimeRpcPermissionState.Granted);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        var result = await activation.Client.InvokeAsync(activation.Endpoint, "messages", "send",
            JsonSerializer.SerializeToElement(new { message = rpcPolicy.MaxNestedCallDepth.ToString(CultureInfo.InvariantCulture) }),
            new SunderRpcCallOptions(deadline));
        Assert.True(result.GetProperty("accepted").GetBoolean());
        using var status = await fixture.WaitForWorkerStatusAsync("call-nesting.json");
        var root = status.RootElement;
        Assert.Equal(rpcPolicy.MaxNestedCallDepth, root.GetProperty("reachedDepth").GetInt32());
        Assert.Equal(12, root.GetProperty("watchCount").GetInt32());
        Assert.Equal(2, root.GetProperty("siblingDepth").GetInt32());
        Assert.Equal(1, root.GetProperty("independentDepth").GetInt32());
        Assert.True(root.GetProperty("independentDeadline").GetDateTimeOffset() > deadline);
        foreach (var field in new[] { "rejection", "scopeRejection" })
        {
            var error = root.GetProperty(field);
            Assert.Equal("resource-exhausted", error.GetProperty("kind").GetString());
            Assert.Equal("rpc.call.depth-limit", error.GetProperty("code").GetString());
            Assert.Equal("not-dispatched", error.GetProperty("dispatchState").GetString());
            Assert.Equal("safe", error.GetProperty("retrySafety").GetString());
            var diagnostic = error.GetProperty("message").GetString()!;
            Assert.Contains($"depth {rpcPolicy.MaxNestedCallDepth + 1}", diagnostic);
            Assert.Contains($"limit is {rpcPolicy.MaxNestedCallDepth}", diagnostic);
            Assert.Contains("process.test/messages.send -> process.test/messages.send", diagnostic);
            Assert.InRange(diagnostic.Length, 1, 512);
        }

        // The child refusal must release its capacity/scopes without retiring the
        // provider or poisoning the next independent invocation.
        var health = await activation.Client.InvokeAsync(activation.Endpoint, "messages", "send",
            JsonSerializer.SerializeToElement(new { message = "health" }));
        Assert.True(health.GetProperty("accepted").GetBoolean());
        using var healthStatus = await fixture.WaitForWorkerStatusAsync("call-nesting-health.json");
        Assert.Equal(1, healthStatus.RootElement.GetProperty("depth").GetInt32());
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }
}
