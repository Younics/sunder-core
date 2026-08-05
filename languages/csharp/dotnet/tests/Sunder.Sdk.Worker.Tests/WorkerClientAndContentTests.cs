using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker.Internal;
using Sunder.Sdk.Worker.Tests.TestSupport;

namespace Sunder.Sdk.Worker.Tests;

public sealed class WorkerClientAndContentTests
{
    [Fact]
    public async Task Client_PerformsLookupDiscoveryWatchInvokeAndSubscribe()
    {
        var completion = new TaskCompletionSource<ClientResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = RunClientAsync,
        };
        await using var worker = new WorkerHarness(options);
        await worker.HandshakeAndActivateAsync();

        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.get-provider", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = WorkerHarness.ProviderSnapshot(),
            });
        }

        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.discover", request.RootElement.GetProperty("type").GetString());
            Assert.Equal(WorkerHarness.ContractId, request.RootElement.GetProperty("contractId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    revision = 3,
                    sequence = 4,
                    providers = new[] { WorkerHarness.ProviderSnapshot() },
                    resetRequired = false,
                },
            });
        }

        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.watch", request.RootElement.GetProperty("type").GetString());
            var id = request.RootElement.GetProperty("id").GetString();
            await worker.SendAsync(new
            {
                type = "host.event",
                id,
                value = new { revision = 4, sequence = 5, kind = "reset-required", provider = (object?)null },
            });
            await worker.SendAsync(new { type = "host.complete", id });
        }

        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.invoke", request.RootElement.GetProperty("type").GetString());
            Assert.Equal("2030-01-02T03:04:05.6780000Z", request.RootElement.GetProperty("deadlineUtc").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new { accepted = true },
            });
        }

        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.subscribe", request.RootElement.GetProperty("type").GetString());
            var id = request.RootElement.GetProperty("id").GetString();
            await worker.SendAsync(new { type = "host.event", id, value = new { sequence = 9 } });
            await worker.SendAsync(new { type = "host.complete", id });
        }

        var results = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkerHarness.ProviderId, results.Provider.ProviderId);
        Assert.Equal(3, results.Catalog.Revision);
        Assert.Equal(SunderRpcCatalogEventKind.ResetRequired, results.CatalogEvent.Kind);
        Assert.True(results.InvokeResult.GetProperty("accepted").GetBoolean());
        Assert.Equal(9, results.SubscriptionEvent.GetProperty("sequence").GetInt32());
        Assert.True(results.CallScopeUnsupported);
        await worker.ShutdownAsync();

        async ValueTask RunClientAsync(ISunderRpcClient client, CancellationToken cancellationToken)
        {
            try
            {
                var scopeUnsupported = await Assert.ThrowsAsync<NotSupportedException>(async () =>
                    await client.CreateCallScopeAsync(cancellationToken: cancellationToken));
                var provider = await client.GetProviderAsync(
                                   new SunderRpcEndpointReference("rpc1_example"),
                                   cancellationToken)
                               ?? throw new InvalidOperationException("Provider was not returned.");
                var catalog = await client.DiscoverAsync(WorkerHarness.ContractId, cancellationToken);
                SunderRpcCatalogEvent? catalogEvent = null;
                await foreach (var item in client.WatchAsync(3, 4, cancellationToken))
                {
                    catalogEvent = item;
                }
                var invokeResult = await client.InvokeAsync(
                    new SunderRpcEndpointReference("rpc1_example"),
                    "messages",
                    "send",
                    JsonSerializer.SerializeToElement(new { message = "hello" }),
                    new SunderRpcCallOptions(new DateTimeOffset(2030, 1, 2, 3, 4, 5, 678, TimeSpan.Zero)),
                    cancellationToken);
                JsonElement subscriptionEvent = default;
                await foreach (var item in client.SubscribeAsync(
                                   new SunderRpcEndpointReference("rpc1_example"),
                                   "messages",
                                   "watch",
                                   JsonSerializer.SerializeToElement(new { message = "hello" }),
                                   cancellationToken: cancellationToken))
                {
                    subscriptionEvent = item;
                }
                completion.TrySetResult(new ClientResults(
                    provider,
                    catalog,
                    catalogEvent ?? throw new InvalidOperationException("Catalog event was not returned."),
                    invokeResult,
                    subscriptionEvent,
                    scopeUnsupported is not null));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
                throw;
            }
        }
    }

    [Fact]
    public async Task Client_CancellationSendsWorkerCancelAndDrainsHostTerminalResponse()
    {
        var cancellation = new TaskCompletionSource<CancellationTokenSource>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (client, _) =>
            {
                using var source = new CancellationTokenSource();
                cancellation.TrySetResult(source);
                try
                {
                    await foreach (var ignored in client.WatchAsync(0, 0, source.Token))
                    {
                    }
                }
                catch (OperationCanceledException) when (source.IsCancellationRequested)
                {
                    callbackCompleted.TrySetResult();
                }
            },
        };
        await using var worker = new WorkerHarness(options);
        await worker.HandshakeAndActivateAsync();

        string id;
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.watch", request.RootElement.GetProperty("type").GetString());
            id = request.RootElement.GetProperty("id").GetString()!;
        }
        (await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(5))).Cancel();
        using (var cancel = await worker.ReadAsync())
        {
            Assert.Equal("worker.cancel", cancel.RootElement.GetProperty("type").GetString());
            Assert.Equal(id, cancel.RootElement.GetProperty("id").GetString());
        }
        await worker.SendAsync(new
        {
            type = "host.error",
            id,
            error = new
            {
                kind = "cancelled",
                code = "rpc.call.cancelled",
                message = "The RPC call was cancelled.",
            },
        });
        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.ShutdownAsync();
        Assert.Equal(string.Empty, worker.Diagnostics.ToString());
    }

    [Fact]
    public async Task Provider_DoesNotForwardHostAuthenticatedDomainErrors()
    {
        ISunderRpcClient? client = null;
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestRpcHandler
        {
            Unary = async (_, _, _, request, cancellationToken) =>
                await client!.InvokeAsync(
                    new SunderRpcEndpointReference("rpc1_dependency"),
                    "dependency",
                    "call",
                    request,
                    cancellationToken: cancellationToken),
        };
        var options = new SunderWorkerOptions([WorkerHarness.Registration(handler)])
        {
            OnActivated = (rpc, _) =>
            {
                client = rpc;
                activated.TrySetResult();
                return ValueTask.CompletedTask;
            },
        };
        await using var worker = new WorkerHarness(options);
        await worker.HandshakeAndActivateAsync();
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.SendInvocationAsync("forward1", "unary", "forward");

        using (var outbound = await worker.ReadAsync())
        {
            Assert.Equal("worker.invoke", outbound.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.error",
                id = outbound.RootElement.GetProperty("id").GetString(),
                error = new
                {
                    kind = "domain",
                    code = "dependency.rejected",
                    message = "The dependency rejected the request.",
                },
            });
        }

        using (var response = await worker.ReadAsync())
        {
            Assert.Equal("worker.error", response.RootElement.GetProperty("type").GetString());
            Assert.Equal("provider-fault", response.RootElement.GetProperty("error").GetProperty("kind").GetString());
        }
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task Client_FailsClosedOnDuplicateTerminalResponses()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (client, cancellationToken) =>
            {
                await client.DiscoverAsync(WorkerHarness.ContractId, cancellationToken);
                completed.TrySetResult();
            },
        };
        await using var worker = new WorkerHarness(options);
        await worker.HandshakeAndActivateAsync();
        string id;
        using (var request = await worker.ReadAsync())
        {
            id = request.RootElement.GetProperty("id").GetString()!;
        }
        var response = new
        {
            type = "host.result",
            id,
            value = new { revision = 1, sequence = 1, providers = Array.Empty<object>(), resetRequired = false },
        };
        await worker.SendAsync(response);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.SendAsync(response);

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task InvocationContent_RegistersOpensAndDiscardsWithinInvocation()
    {
        var handler = new TestRpcHandler
        {
            Unary = static async (context, _, _, request, cancellationToken) =>
            {
                var referenceValue = request.GetProperty("reference");
                var reference = new SunderRpcContentReference(
                    referenceValue.GetProperty("id").GetString()!,
                    referenceValue.GetProperty("length").GetInt64(),
                    referenceValue.GetProperty("sha256").GetString()!,
                    referenceValue.GetProperty("mediaType").GetString()!,
                    referenceValue.GetProperty("fileName").GetString()!,
                    referenceValue.GetProperty("expiresAtUtc").GetDateTimeOffset(),
                    SunderRpcContentRepeatability.SingleUse);
                await using var source = new MemoryStream(Encoding.UTF8.GetBytes("provider payload"));
                var registered = await context.RegisterContentAsync(
                    source,
                    new SunderRpcContentRegistrationOptions(
                        "text/plain",
                        "provider.txt",
                        source.Length),
                    cancellationToken);
                string openedText;
                await using (var opened = await context.OpenContentAsync(reference, cancellationToken))
                using (var reader = new StreamReader(opened, Encoding.UTF8, leaveOpen: true))
                {
                    openedText = await reader.ReadToEndAsync(cancellationToken);
                }
                return JsonSerializer.SerializeToElement(new { registeredId = registered.Id, openedText });
            },
        };
        await using var worker = new WorkerHarness(
            new SunderWorkerOptions([WorkerHarness.Registration(handler)]));
        await worker.HandshakeAndActivateAsync();
        await worker.SendInvocationAsync(
            "content1",
            "unary",
            "content",
            new { reference = WorkerHarness.ContentReference("caller-content", 14) });

        using (var register = await worker.ReadAsync())
        {
            Assert.Equal("worker.content-register", register.RootElement.GetProperty("type").GetString());
            Assert.Equal("content1", register.RootElement.GetProperty("invocationId").GetString());
            var filePath = register.RootElement.GetProperty("filePath").GetString()!;
            Assert.StartsWith(worker.DataPath, filePath, StringComparison.Ordinal);
            Assert.Equal("provider payload", await File.ReadAllTextAsync(filePath));
            await worker.SendAsync(new
            {
                type = "host.result",
                id = register.RootElement.GetProperty("id").GetString(),
                value = WorkerHarness.ContentReference("provider-content", 16),
            });
        }

        var callerPath = Path.Combine(worker.DataPath, "caller-content.txt");
        await File.WriteAllTextAsync(callerPath, "caller payload");
        using (var open = await worker.ReadAsync())
        {
            Assert.Equal("worker.content-open", open.RootElement.GetProperty("type").GetString());
            Assert.Equal("content1", open.RootElement.GetProperty("invocationId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = open.RootElement.GetProperty("id").GetString(),
                value = new { handleId = "opened-content", filePath = callerPath },
            });
        }

        using (var discard = await worker.ReadAsync())
        {
            Assert.Equal("worker.content-discard", discard.RootElement.GetProperty("type").GetString());
            Assert.Equal("opened-content", discard.RootElement.GetProperty("handleId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = discard.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }

        using (var result = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
            Assert.Equal("provider-content", result.RootElement.GetProperty("value").GetProperty("registeredId").GetString());
            Assert.Equal("caller payload", result.RootElement.GetProperty("value").GetProperty("openedText").GetString());
        }
        await worker.ShutdownAsync();
    }

    private sealed record ClientResults(
        SunderRpcProviderSnapshot Provider,
        SunderRpcCatalogSnapshot Catalog,
        SunderRpcCatalogEvent CatalogEvent,
        JsonElement InvokeResult,
        JsonElement SubscriptionEvent,
        bool CallScopeUnsupported);
}
