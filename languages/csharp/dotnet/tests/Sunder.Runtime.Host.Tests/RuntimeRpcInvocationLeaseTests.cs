using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;
using static Sunder.Runtime.Host.Tests.RuntimeRpcTestPackages;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcInvocationLeaseTests
{
    [Fact]
    public async Task CallAcquisition_UnaryAndStreamPreservePermissionAndShapeFailureParity()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler());
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single(fixture.Catalog.GetSnapshot().Providers).Endpoint;
        var request = JsonSerializer.SerializeToElement(new { message = "hello" });

        var unaryDenied = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            endpoint,
            "missing",
            "missing",
            request).AsTask());
        await using var deniedSubscription = client.SubscribeAsync(
            endpoint,
            "missing",
            "missing",
            request).GetAsyncEnumerator();
        var streamDenied = await Assert.ThrowsAsync<SunderRpcException>(() =>
            deniedSubscription.MoveNextAsync().AsTask());

        AssertSetupParity(unaryDenied, streamDenied, SunderRpcErrorKind.PermissionDenied, "rpc.permission.denied");
        Assert.Contains("'invoke'", unaryDenied.Error.Message, StringComparison.Ordinal);
        Assert.Contains("'subscribe'", streamDenied.Error.Message, StringComparison.Ordinal);

        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        await fixture.GrantAsync(SunderRpcProtocol.SubscribeAction);
        var unaryShape = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            endpoint,
            "messages",
            "watch",
            request).AsTask());
        await using var shapeSubscription = client.SubscribeAsync(
            endpoint,
            "messages",
            "send",
            request).GetAsyncEnumerator();
        var streamShape = await Assert.ThrowsAsync<SunderRpcException>(() =>
            shapeSubscription.MoveNextAsync().AsTask());

        AssertSetupParity(unaryShape, streamShape, SunderRpcErrorKind.Validation, "rpc.method.kind-mismatch");
        Assert.Equal(unaryShape.Error.Message, streamShape.Error.Message);

        var expiredOptions = new SunderRpcCallOptions(DateTimeOffset.UnixEpoch);
        var unaryDeadline = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            request,
            expiredOptions).AsTask());
        await using var deadlineSubscription = client.SubscribeAsync(
            endpoint,
            "messages",
            "watch",
            request,
            expiredOptions).GetAsyncEnumerator();
        var streamDeadline = await Assert.ThrowsAsync<SunderRpcException>(() =>
            deadlineSubscription.MoveNextAsync().AsTask());

        AssertSetupParity(
            unaryDeadline,
            streamDeadline,
            SunderRpcErrorKind.DeadlineExceeded,
            "rpc.call.deadline-exceeded");
        Assert.Equal(unaryDeadline.Error.Message, streamDeadline.Error.Message);

        static void AssertSetupParity(
            SunderRpcException unary,
            SunderRpcException stream,
            SunderRpcErrorKind expectedKind,
            string expectedCode)
        {
            Assert.Equal(expectedKind, unary.Error.Kind);
            Assert.Equal(unary.Error.Kind, stream.Error.Kind);
            Assert.Equal(expectedCode, unary.Error.Code);
            Assert.Equal(unary.Error.Code, stream.Error.Code);
            Assert.Equal(SunderRpcDispatchState.NotDispatched, unary.Error.DispatchState);
            Assert.Equal(unary.Error.DispatchState, stream.Error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Safe, unary.Error.RetrySafety);
            Assert.Equal(unary.Error.RetrySafety, stream.Error.RetrySafety);
        }
    }

    [Fact]
    public async Task CallLease_DisposesAuthorityThenCalleeCallerAndSessionLeases()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler());
        var snapshot = Assert.Single(fixture.Catalog.GetSnapshot().Providers);
        Assert.True(fixture.Catalog.TryGetActiveEndpoint(snapshot.Endpoint, out var foundProvider, out _));
        var provider = Assert.IsType<RuntimeRpcProviderActivation>(foundProvider);
        var contract = Contract();
        var method = contract.FindService("messages")!.FindMethod("send")!;
        var order = new List<string>();
        var linked = new CancellationTokenSource();
        var deadline = new CancellationTokenSource();
        var authority = new RecordingInvocationAuthority(order);
        var sessionLease = new PackageSessionLease(
            activation.Session,
            snapshot.SessionGeneration,
            1,
            new object(),
            new object(),
            CancellationToken.None,
            CancellationToken.None,
            () => order.Add("session"));
        var call = new RuntimeRpcCallLease(
            sessionLease,
            CancellationToken.None,
            new RuntimeRpcHostCallerActivation(contract, CancellationToken.None),
            new RuntimeRpcActivationLease(CancellationToken.None, () => order.Add("caller")),
            new RuntimeRpcActivationLease(CancellationToken.None, () => order.Add("callee")),
            null,
            linked,
            deadline,
            provider,
            "messages",
            method,
            contract,
            new RuntimeRpcCallFrame(1, DateTimeOffset.UtcNow.AddMinutes(1)),
            new SunderRpcInvocationContext(
                "caller.package",
                "1.0.0",
                snapshot,
                DateTimeOffset.UtcNow.AddMinutes(1),
                1,
                linked.Token,
                authority));

        call.Dispose();

        Assert.Equal(new[] { "authority", "callee", "caller", "session" }, order);
    }

    [Fact]
    public async Task Broker_ExtendsRequestedDeadlineOnlyForOptedInTargetAndClampsIt()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new RpcFixture(
            new RuntimeRpcPolicyOptions
            {
                DefaultDeadline = TimeSpan.FromMinutes(5),
                MaximumExtendedDeadline = TimeSpan.FromMinutes(35),
            },
            clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = await fixture.PublishAsync(
            new TestHandler
            {
                Unary = async (_, cancellationToken) =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return JsonSerializer.SerializeToElement(new { accepted = true });
                },
            },
            providerRequiredHostCapabilities: [SunderSdkCapabilities.RpcExtendedDeadlineV1]);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var invocation = client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "wait" }),
            new SunderRpcCallOptions(clock.GetUtcNow().AddHours(1))).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromMinutes(6));
        await Task.Yield();
        Assert.False(invocation.IsCompleted);

        clock.Advance(TimeSpan.FromMinutes(29));
        var timeout = await Assert.ThrowsAsync<SunderRpcException>(async () => await invocation);
        Assert.Equal(SunderRpcErrorKind.DeadlineExceeded, timeout.Error.Kind);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    [Fact]
    public async Task CatalogWatch_ConsumesOnlyItsBoundedObservationBudgetAndReleasesIt()
    {
        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
        {
            MaxConcurrentCallsPerCaller = 1,
            MaxConcurrentObservationsPerCaller = 1,
        });
        var activation = await fixture.PublishAsync(new TestHandler());
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var snapshot = await client.DiscoverAsync("example.rpc");
        var endpoint = Assert.Single(snapshot.Providers).Endpoint;

        await using (var watch = client.WatchAsync(
                         snapshot.Revision + 1,
                         snapshot.Sequence).GetAsyncEnumerator())
        {
            Assert.True(await watch.MoveNextAsync());
            Assert.Equal(SunderRpcCatalogEventKind.ResetRequired, watch.Current.Kind);
            await using var excess = client.WatchAsync(snapshot.Revision + 1, snapshot.Sequence).GetAsyncEnumerator();
            var limited = await Assert.ThrowsAsync<SunderRpcException>(() => excess.MoveNextAsync().AsTask());
            Assert.Equal(SunderRpcErrorKind.ResourceExhausted, limited.Error.Kind);
            Assert.Equal("rpc.caller.observation-limit", limited.Error.Code);
            Assert.True((await client.InvokeAsync(endpoint, "messages", "send",
                JsonSerializer.SerializeToElement(new { message = "finite-headroom" }))).GetProperty("accepted").GetBoolean());
        }

        await using var replacement = client.WatchAsync(snapshot.Revision + 1, snapshot.Sequence).GetAsyncEnumerator();
        Assert.True(await replacement.MoveNextAsync());

        var response = await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "released" }));
        Assert.True(response.GetProperty("accepted").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerStreamsDoNotConsumeFiniteCallerOrProviderCapacity(bool appCaller)
    {
        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
        {
            MaxConcurrentCallsPerCaller = 1, MaxConcurrentCallsPerCallee = 1,
            MaxConcurrentObservationsPerCaller = 1, MaxConcurrentObservationsPerCallee = 1,
        });
        var activation = await fixture.PublishAsync(new TestHandler(), webCaller: appCaller, callerSourceKind: PackageSourceKind.Dev);
        var client = fixture.CreateClient(activation.Caller);
        var appSession = appCaller ? (await fixture.OpenAppCallerAsync()).Session.SessionId : null;
        var snapshot = appSession is null ? await client.DiscoverAsync("example.rpc")
            : await fixture.Broker.DiscoverAppAsync(appSession, "example.rpc", default);
        var endpoint = Assert.Single(snapshot.Providers).Endpoint;
        var request = JsonSerializer.SerializeToElement(new { message = "ordinary workload" });
        IAsyncEnumerable<JsonElement> Subscribe() => appSession is null
            ? client.SubscribeAsync(endpoint, "messages", "watch", request)
            : fixture.Broker.SubscribeAppAsync(appSession, endpoint, "messages", "watch", request, null, default);
        ValueTask<JsonElement> Invoke() => appSession is null
            ? client.InvokeAsync(endpoint, "messages", "send", request)
            : fixture.Broker.InvokeAppAsync(appSession, endpoint, "messages", "send", request, null, default);

        await using (var stream = Subscribe().GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.True((await Invoke()).GetProperty("accepted").GetBoolean());
            await using var excess = Subscribe().GetAsyncEnumerator();
            var pressure = await Assert.ThrowsAsync<SunderRpcException>(() => excess.MoveNextAsync().AsTask());
            Assert.Equal(SunderRpcErrorKind.ResourceExhausted, pressure.Error.Kind);
            Assert.Equal(SunderRpcDispatchState.NotDispatched, pressure.Error.DispatchState);
        }
        await using var replacement = Subscribe().GetAsyncEnumerator();
        Assert.True(await replacement.MoveNextAsync());
        Assert.True((await Invoke()).GetProperty("accepted").GetBoolean());
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    private sealed class RecordingInvocationAuthority(List<string> order) : ISunderRpcInvocationAuthority
    {
        public CancellationToken RevocationToken => CancellationToken.None;

        public ValueTask<SunderRpcContentReference> RegisterContentAsync(
            Stream source,
            SunderRpcContentRegistrationOptions options,
            CancellationToken cancellationToken)
            => ValueTask.FromException<SunderRpcContentReference>(new NotSupportedException());

        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
            string filePath,
            SunderRpcContentRegistrationOptions options,
            CancellationToken cancellationToken)
            => ValueTask.FromException<SunderRpcContentReference>(new NotSupportedException());

        public ValueTask<Stream> OpenContentAsync(
            SunderRpcContentReference reference,
            CancellationToken cancellationToken)
            => ValueTask.FromException<Stream>(new NotSupportedException());

        public void Revoke() => order.Add("authority");
    }
}
