using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker.Internal;
using Sunder.Sdk.Worker.Tests.TestSupport;

namespace Sunder.Sdk.Worker.Tests;

public sealed class WorkerOperationalOutcomeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalAdmissionInsideProviderIsCallLocalAndDoesNotForwardNestedRetryStamp(bool stream)
    {
        ISunderRpcClient? client = null;
        SunderRpcException? nestedFailure = null;
        async ValueTask<JsonElement> Invoke(CancellationToken token)
        {
            try { await client!.DiscoverAsync(WorkerHarness.ContractId, token); }
            catch (SunderRpcException error) { nestedFailure = error; throw; }
            return JsonSerializer.SerializeToElement(true);
        }
        async IAsyncEnumerable<JsonElement> Observe([EnumeratorCancellation] CancellationToken token)
        {
            yield return await Invoke(token);
        }
        var handler = new TestRpcHandler
        {
            Unary = (_, _, method, request, token) => method == "healthy"
                ? ValueTask.FromResult(request.Clone()) : Invoke(token),
            Stream = (_, _, _, _, token) => Observe(token),
        };
        await using var worker = new WorkerHarness(context =>
        {
            client = context.Rpc;
            return new SunderWorkerOptions([WorkerHarness.Registration(handler)]);
        });
        await worker.HandshakeAndActivateAsync();
        var pending = new List<(string Id, Task<SunderRpcCatalogSnapshot> Work)>();
        for (var index = 0; index < WorkerLimits.Default.MaximumOutboundCalls; index++)
        {
            var work = client!.DiscoverAsync(WorkerHarness.ContractId).AsTask();
            using var frame = await worker.ReadAsync();
            Assert.Equal("worker.discover", frame.RootElement.GetProperty("type").GetString());
            pending.Add((frame.RootElement.GetProperty("id").GetString()!, work));
        }
        await worker.SendInvocationAsync("overloaded", stream ? "server-stream" : "unary", "nested");
        await AssertOperationalAsync(worker, "overloaded", "resource-exhausted");
        Assert.NotNull(nestedFailure);
        Assert.Equal("rpc.worker.call-limit", nestedFailure.Error.Code);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, nestedFailure.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, nestedFailure.Error.RetrySafety);
        Assert.All(pending, item => Assert.False(item.Work.IsCompleted));
        await AssertHealthyAsync(worker);
        foreach (var item in pending)
            await worker.SendAsync(new { type = "host.result", id = item.Id, value = EmptyCatalog() });
        await Task.WhenAll(pending.Select(item => item.Work));
        await worker.ShutdownAsync();
    }

    [Theory]
    [InlineData("resource-exhausted")]
    [InlineData("unavailable")]
    [InlineData("deadline-exceeded")]
    public async Task AuthenticatedHostOperationalFailureIsAnOutcomeNotForwardedAuthority(string kind)
    {
        ISunderRpcClient? client = null;
        var handler = new TestRpcHandler
        {
            Unary = async (_, _, method, request, token) =>
            {
                if (method != "healthy") await client!.DiscoverAsync(WorkerHarness.ContractId, token);
                return request.Clone();
            },
        };
        await using var worker = new WorkerHarness(context =>
        {
            client = context.Rpc;
            return new SunderWorkerOptions([WorkerHarness.Registration(handler)]);
        });
        await worker.HandshakeAndActivateAsync();
        await worker.SendInvocationAsync("nested", "unary", "nested");
        using (var nested = await worker.ReadAsync())
        {
            Assert.Equal("worker.discover", nested.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.error", id = nested.RootElement.GetProperty("id").GetString(),
                error = new { kind, code = "host.private-code", message = "host-secret", dispatchState = "not-dispatched", retrySafety = "safe" },
            });
        }
        await AssertOperationalAsync(worker, "nested", kind);
        await AssertHealthyAsync(worker);
        await worker.ShutdownAsync();
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.ResourceExhausted, "rpc.worker.call-limit")]
    [InlineData(SunderRpcErrorKind.Unavailable, "rpc.host.adapter-fault")]
    [InlineData(SunderRpcErrorKind.DeadlineExceeded, "rpc.call.deadline-exceeded")]
    [InlineData(SunderRpcErrorKind.Domain, "undeclared")]
    public async Task ProviderCreatedErrorsDoNotAcquireOperationalAuthority(SunderRpcErrorKind kind, string code)
    {
        var handler = new TestRpcHandler
        {
            Unary = (_, _, _, _, _) => throw new SunderRpcException(new(kind, code, "forged")),
        };
        await using var worker = new WorkerHarness(_ => new([WorkerHarness.Registration(handler)]));
        await worker.HandshakeAndActivateAsync();
        await worker.SendInvocationAsync("forged", "unary", "send");
        using (var diagnostic = await worker.ReadAsync())
        {
            Assert.Equal("worker.provider-fault-diagnostic", diagnostic.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new { type = "host.result", id = diagnostic.RootElement.GetProperty("id").GetString(), value = (object?)null });
        }
        using var failure = await worker.ReadAsync();
        Assert.Equal("provider-fault", failure.RootElement.GetProperty("error").GetProperty("kind").GetString());
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task ObservationsAndFiniteWorkHaveIndependentBoundedAdmission()
    {
        ISunderRpcClient? client = null;
        await using var worker = new WorkerHarness(context =>
        {
            client = context.Rpc;
            return new([WorkerHarness.Registration(new TestRpcHandler())]);
        }, WorkerLimits.Default with { MaximumOutboundCalls = 1, MaximumOutboundStreams = 2 });
        await worker.HandshakeAndActivateAsync();
        await using var watch = client!.WatchAsync(0, 0).GetAsyncEnumerator();
        var watching = watch.MoveNextAsync().AsTask();
        using var watchFrame = await worker.ReadAsync();
        Assert.Equal("worker.watch", watchFrame.RootElement.GetProperty("type").GetString());
        await using var subscription = client.SubscribeAsync(new("rpc1_example"), "messages", "watch",
            JsonSerializer.SerializeToElement(new { })).GetAsyncEnumerator();
        var subscribing = subscription.MoveNextAsync().AsTask();
        using var subscribeFrame = await worker.ReadAsync();
        Assert.Equal("worker.subscribe", subscribeFrame.RootElement.GetProperty("type").GetString());
        var discovery = client.DiscoverAsync(WorkerHarness.ContractId).AsTask();
        using var discoverFrame = await worker.ReadAsync();
        Assert.Equal("worker.discover", discoverFrame.RootElement.GetProperty("type").GetString());

        await using var extraWatch = client.WatchAsync(0, 0).GetAsyncEnumerator();
        var observationLimit = await Assert.ThrowsAsync<SunderRpcException>(() => extraWatch.MoveNextAsync().AsTask());
        Assert.Equal("rpc.worker.stream-limit", observationLimit.Error.Code);
        var operationLimit = await Assert.ThrowsAsync<SunderRpcException>(() => client.DiscoverAsync(WorkerHarness.ContractId).AsTask());
        Assert.Equal("rpc.worker.call-limit", operationLimit.Error.Code);
        await worker.SendAsync(new { type = "host.result", id = discoverFrame.RootElement.GetProperty("id").GetString(), value = EmptyCatalog() });
        await discovery;
        await worker.SendAsync(new { type = "host.complete", id = watchFrame.RootElement.GetProperty("id").GetString() });
        await worker.SendAsync(new { type = "host.complete", id = subscribeFrame.RootElement.GetProperty("id").GetString() });
        Assert.False(await watching);
        Assert.False(await subscribing);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task LegacyHostRetainsCombinedAdmissionAndNeverReceivesOperationalOutcome()
    {
        ISunderRpcClient? client = null;
        var handler = new TestRpcHandler
        {
            Unary = async (_, _, _, request, token) =>
            {
                await client!.DiscoverAsync(WorkerHarness.ContractId, token);
                return request.Clone();
            },
        };
        await using var worker = new WorkerHarness(context =>
        {
            client = context.Rpc;
            return new([WorkerHarness.Registration(handler)]);
        }, WorkerLimits.Default with { MaximumOutboundCalls = 1 }, operationalOutcomesV1: false);
        await worker.HandshakeAndActivateAsync();
        await using var watch = client!.WatchAsync(0, 0).GetAsyncEnumerator();
        var watching = watch.MoveNextAsync().AsTask();
        using var watchFrame = await worker.ReadAsync();
        await worker.SendInvocationAsync("legacy", "unary", "nested");
        using (var diagnostic = await worker.ReadAsync())
        {
            Assert.Equal("worker.provider-fault-diagnostic", diagnostic.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new { type = "host.result", id = diagnostic.RootElement.GetProperty("id").GetString(), value = (object?)null });
        }
        using (var failure = await worker.ReadAsync())
            Assert.Equal("provider-fault", failure.RootElement.GetProperty("error").GetProperty("kind").GetString());
        await worker.SendAsync(new { type = "host.complete", id = watchFrame.RootElement.GetProperty("id").GetString() });
        Assert.False(await watching);
        await worker.ShutdownAsync();
    }

    private static object EmptyCatalog()
        => new { revision = 0, sequence = 0, resetRequired = false, providers = Array.Empty<object>() };

    [Fact]
    public async Task InboundObservationDoesNotConsumeFiniteProviderSlot()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<JsonElement> Observe([EnumeratorCancellation] CancellationToken token)
        {
            yield return JsonSerializer.SerializeToElement(true);
            await release.Task.WaitAsync(token);
        }
        var handler = new TestRpcHandler
        {
            Unary = (_, _, _, request, _) => ValueTask.FromResult(request.Clone()),
            Stream = (_, _, _, _, token) => Observe(token),
        };
        await using var worker = new WorkerHarness(_ => new([WorkerHarness.Registration(handler)])
            { CallbackHandlers = [new ImmediateCallback()] },
            WorkerLimits.Default with { MaximumInboundCalls = 1, MaximumInboundStreams = 1 });
        await worker.HandshakeAndActivateAsync();
        await worker.SendInvocationAsync("observation", "server-stream", "watch");
        using (var frame = await worker.ReadAsync())
            Assert.Equal("worker.event", frame.RootElement.GetProperty("type").GetString());
        await AssertHealthyAsync(worker);
        await worker.SendAsync(new
        {
            type = "host.callback-start", id = "finite-callback", activationId = WorkerHarness.ActivationId, sessionGeneration = 7,
            value = new { handlerId = "fixture.callback", callbackSessionId = "callback-session", callbackUri = "http://127.0.0.1/callback", purpose = (string?)null, parameters = new { } },
        });
        using (var frame = await worker.ReadAsync())
            Assert.Equal("worker.callback-started", frame.RootElement.GetProperty("type").GetString());
        release.TrySetResult();
        using (var frame = await worker.ReadAsync())
            Assert.Equal("worker.complete", frame.RootElement.GetProperty("type").GetString());
        await worker.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyAcknowledgesOperationalSupportOnlyWhenHostAdvertisesIt(bool supported)
    {
        await using var worker = new WorkerHarness(_ => new([WorkerHarness.Registration(new TestRpcHandler())]), operationalOutcomesV1: supported);
        await worker.HandshakeAndActivateAsync();
        Assert.Equal(supported, worker.ReadyContributions!.Value.TryGetProperty("operationalOutcomesV1", out var capability));
        if (supported) Assert.True(capability.GetBoolean());
        await worker.ShutdownAsync();
    }

    private sealed class ImmediateCallback : IPackageCallbackHandler
    {
        public string CallbackHandlerId => "fixture.callback";
        public Task<PackageCallbackStartResult?> StartCallbackAsync(PackageCallbackStartContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<PackageCallbackStartResult?>(null);
        public Task<PackageCallbackCompletionResult> CompleteCallbackAsync(PackageCallbackCompletionContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static async Task AssertOperationalAsync(WorkerHarness worker, string id, string code)
    {
        using var frame = await worker.ReadAsync();
        Assert.Equal("worker.error", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal(id, frame.RootElement.GetProperty("id").GetString());
        var error = frame.RootElement.GetProperty("error");
        Assert.Equal("operational", error.GetProperty("kind").GetString());
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("dispatchState", out _));
        Assert.False(error.TryGetProperty("retrySafety", out _));
        Assert.False(error.TryGetProperty("details", out _));
        Assert.DoesNotContain("secret", error.GetRawText(), StringComparison.Ordinal);
        Assert.False(worker.RunTask.IsCompleted);
    }

    private static async Task AssertHealthyAsync(WorkerHarness worker)
    {
        await worker.SendInvocationAsync("healthy", "unary", "healthy");
        using var frame = await worker.ReadAsync();
        Assert.Equal("worker.result", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal("healthy", frame.RootElement.GetProperty("id").GetString());
    }
}
