using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker.Internal;
using Sunder.Sdk.Worker.Tests.TestSupport;

namespace Sunder.Sdk.Worker.Tests;

public sealed class WorkerRuntimeTests
{
    [Fact]
    public async Task Worker_HandshakesDispatchesUnaryAndStreamCancelsAndShutsDown()
    {
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activated = new TaskCompletionSource<ISunderRpcClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestRpcHandler
        {
            Unary = async (_, _, methodId, request, cancellationToken) =>
            {
                if (methodId == "wait")
                {
                    waitStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return request.Clone();
            },
            Stream = static (_, _, _, _, cancellationToken) => Events(cancellationToken),
        };
        var options = new SunderWorkerOptions([WorkerHarness.Registration(handler)])
        {
            OnActivated = (client, _) =>
            {
                activated.TrySetResult(client);
                return ValueTask.CompletedTask;
            },
            OnShutdown = (context, _) =>
            {
                shutdown.TrySetResult(context.Reason);
                return ValueTask.CompletedTask;
            },
        };
        await using var worker = new WorkerHarness(options);
        await worker.HandshakeAndActivateAsync();
        Assert.NotNull(await activated.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await worker.SendInvocationAsync("unary1", "unary", "echo");
        using (var result = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
            Assert.Equal("hello", result.RootElement.GetProperty("value").GetProperty("message").GetString());
        }

        await worker.SendInvocationAsync("stream1", "server-stream", "watch");
        using (var first = await worker.ReadAsync())
        using (var second = await worker.ReadAsync())
        using (var complete = await worker.ReadAsync())
        {
            Assert.Equal(1, first.RootElement.GetProperty("value").GetProperty("sequence").GetInt32());
            Assert.Equal(2, second.RootElement.GetProperty("value").GetProperty("sequence").GetInt32());
            Assert.Equal("worker.complete", complete.RootElement.GetProperty("type").GetString());
        }

        await worker.SendInvocationAsync("cancel1", "unary", "wait");
        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.SendAsync(new { type = "host.cancel", id = "cancel1" });
        using (var cancelled = await worker.ReadAsync())
        {
            Assert.Equal("worker.error", cancelled.RootElement.GetProperty("type").GetString());
            Assert.Equal("cancelled", cancelled.RootElement.GetProperty("error").GetProperty("kind").GetString());
        }

        await worker.ShutdownAsync("activation-retired");
        Assert.Equal("activation-retired", await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(string.Empty, worker.Diagnostics.ToString());
    }

    [Fact]
    public async Task Worker_MapsOnlyDomainErrorsAndBoundsProviderDiagnostics()
    {
        var handler = new TestRpcHandler
        {
            Unary = static (_, _, methodId, _, _) => methodId switch
            {
                "domain" => ValueTask.FromException<JsonElement>(new SunderRpcException(
                    new SunderRpcError(SunderRpcErrorKind.Domain, "example.rejected", "Request rejected."))),
                _ => ValueTask.FromException<JsonElement>(new InvalidOperationException(
                    "failure\n" + new string('x', 512) + "\u0001")),
            },
        };
        var limits = WorkerLimits.Default with { MaximumDiagnosticCharacters = 80 };
        await using var worker = new WorkerHarness(
            new SunderWorkerOptions([WorkerHarness.Registration(handler)]),
            limits);
        await worker.HandshakeAndActivateAsync();

        await worker.SendInvocationAsync("domain1", "unary", "domain");
        using (var domain = await worker.ReadAsync())
        {
            var error = domain.RootElement.GetProperty("error");
            Assert.Equal("domain", error.GetProperty("kind").GetString());
            Assert.Equal("example.rejected", error.GetProperty("code").GetString());
            Assert.Equal("Request rejected.", error.GetProperty("message").GetString());
        }

        await worker.SendInvocationAsync("fault1", "unary", "fault");
        using (var fault = await worker.ReadAsync())
        {
            var error = fault.RootElement.GetProperty("error");
            Assert.Equal("provider-fault", error.GetProperty("kind").GetString());
            Assert.Equal("rpc.provider.handler-fault", error.GetProperty("code").GetString());
            Assert.Equal("The process provider handler failed.", error.GetProperty("message").GetString());
        }

        var diagnostic = worker.Diagnostics.ToString().TrimEnd('\r', '\n');
        Assert.DoesNotContain('\r', diagnostic);
        Assert.DoesNotContain('\n', diagnostic);
        Assert.DoesNotContain('\u0001', diagnostic);
        Assert.True(diagnostic.Length <= limits.MaximumDiagnosticCharacters);
        Assert.EndsWith("[truncated]", diagnostic, StringComparison.Ordinal);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task Worker_FailsClosedOnOutOfOrderUnknownAndEnvironmentMismatch()
    {
        await using (var outOfOrder = CreateNoOpWorker())
        {
            await outOfOrder.SendAsync(new { type = "host.activate", sessionGeneration = 1 });
            await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
                await outOfOrder.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await using (var unknown = CreateNoOpWorker())
        {
            await unknown.HandshakeAndActivateAsync();
            await unknown.SendAsync(new { type = "host.unknown" });
            await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
                await unknown.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await using (var mismatch = CreateNoOpWorker())
        {
            await mismatch.SendAsync(new
            {
                type = "host.hello",
                protocol = WorkerProtocol.Name,
                protocolVersion = 1,
                challenge = "challenge",
                packageId = "other.package",
                packageVersion = WorkerHarness.PackageVersion,
                activationId = WorkerHarness.ActivationId,
                sessionId = WorkerHarness.SessionId,
                providers = Array.Empty<object>(),
            });
            await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
                await mismatch.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Worker_FailsClosedOnDuplicateInvocationIdsAndDuplicateJsonProperties()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestRpcHandler
        {
            Unary = async (_, _, _, _, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return default;
            },
        };
        await using (var duplicate = new WorkerHarness(
                         new SunderWorkerOptions([WorkerHarness.Registration(handler)])))
        {
            await duplicate.HandshakeAndActivateAsync();
            await duplicate.SendInvocationAsync("same", "unary", "wait");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await duplicate.SendInvocationAsync("same", "unary", "wait");
            await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
                await duplicate.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await using (var duplicateProperty = CreateNoOpWorker())
        {
            await duplicateProperty.SendRawJsonAsync(
                "{\"type\":\"host.hello\",\"Type\":\"host.hello\"}");
            await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
                await duplicateProperty.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Worker_IgnoresCancellationThatCrossesACompletedResponse()
    {
        var handler = new TestRpcHandler
        {
            Unary = static (_, _, _, request, _) => ValueTask.FromResult(request.Clone()),
        };
        await using var worker = new WorkerHarness(
            new SunderWorkerOptions([WorkerHarness.Registration(handler)]));
        await worker.HandshakeAndActivateAsync();
        await worker.SendInvocationAsync("completed1", "unary", "echo");
        using (var result = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
        }

        await worker.SendAsync(new { type = "host.cancel", id = "completed1" });
        await worker.ShutdownAsync();

        Assert.Equal(string.Empty, worker.Diagnostics.ToString());
    }

    [Fact]
    public async Task Worker_RunCancellationRemainsCancellationDuringActivationCallback()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerHarness(new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        });
        await worker.HandshakeAndActivateAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        worker.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(string.Empty, worker.Diagnostics.ToString());
    }

    [Fact]
    public async Task Worker_RunCancellationRemainsCancellationDuringShutdownCallback()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerHarness(new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnShutdown = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        });
        await worker.HandshakeAndActivateAsync();
        await worker.SendAsync(new { type = "host.shutdown", shutdownId = "shutdown1", reason = "test" });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        worker.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(string.Empty, worker.Diagnostics.ToString());
    }

    private static WorkerHarness CreateNoOpWorker()
        => new(new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())]));

    private static async IAsyncEnumerable<JsonElement> Events(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return JsonSerializer.SerializeToElement(new { sequence = 1 });
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return JsonSerializer.SerializeToElement(new { sequence = 2 });
    }
}
