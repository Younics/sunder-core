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
            OnActivated = (context, _) =>
            {
                activated.TrySetResult(context.Rpc);
                return ValueTask.CompletedTask;
            },
            OnShutdown = (context, _) =>
            {
                shutdown.TrySetResult(context.Reason);
                return ValueTask.CompletedTask;
            },
        };
        await using var worker = new WorkerHarness(_ => options);
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
    public async Task Worker_OutboundSubscribeCountOverflowCancelsOnlyThatCall()
    {
        var activated = new TaskCompletionSource<ISunderRpcClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var limits = WorkerLimits.Default with { MaximumStreamQueueMessages = 2 };
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = (context, _) =>
            {
                activated.TrySetResult(context.Rpc);
                return ValueTask.CompletedTask;
            },
        }, limits);
        await worker.HandshakeAndActivateAsync();
        var client = await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var endpoint = new SunderRpcEndpointReference("rpc1_private-endpoint");
        const string serviceId = "messages\r\nprivate/path";
        const string methodId = "watch\tprivate";
        await using var stream = client.SubscribeAsync(
                endpoint,
                serviceId,
                methodId,
                JsonSerializer.SerializeToElement(new { token = "request-secret" }))
            .GetAsyncEnumerator();
        var firstMove = stream.MoveNextAsync().AsTask();
        string callId;
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.subscribe", request.RootElement.GetProperty("type").GetString());
            callId = request.RootElement.GetProperty("id").GetString()!;
        }
        await worker.SendAsync(new { type = "host.event", id = callId, value = new { sequence = 1 } });
        Assert.True(await firstMove.WaitAsync(TimeSpan.FromSeconds(5)));

        await worker.SendAsync(new { type = "host.event", id = callId, value = new { sequence = 2 } });
        await worker.SendAsync(new { type = "host.event", id = callId, value = new { sequence = 3 } });
        await worker.SendAsync(new { type = "host.event", id = callId, value = new { sequence = 4 } });
        using (var cancellation = await worker.ReadAsync())
        {
            Assert.Equal("worker.cancel", cancellation.RootElement.GetProperty("type").GetString());
            Assert.Equal(callId, cancellation.RootElement.GetProperty("id").GetString());
        }

        var exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await stream.MoveNextAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
        Assert.Equal("rpc.worker.stream-queue-limit", exhausted.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, exhausted.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, exhausted.Error.RetrySafety);
        Assert.Null(exhausted.Error.Details);
        Assert.StartsWith(
            "The worker RPC stream queue exceeded its count or byte bound.",
            exhausted.Error.Message,
            StringComparison.Ordinal);
        Assert.Contains("limit=count", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("queuedMessages=2/2", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("operation=subscribe", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("service=messages__private_path", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.Contains("method=watch_private", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint.Value, exhausted.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("request-secret", exhausted.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', exhausted.Error.Message);
        Assert.DoesNotContain('\n', exhausted.Error.Message);
        Assert.True(exhausted.Error.Message.Length <= 512);

        await worker.SendAsync(new
        {
            type = "host.error",
            id = callId,
            error = new
            {
                kind = "cancelled",
                code = "rpc.call.cancelled",
                message = "The RPC call was cancelled.",
                dispatchState = "possibly-dispatched",
                retrySafety = "unsafe",
            },
        });

        var sibling = client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "still-active" })).AsTask();
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.invoke", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new { accepted = true },
            });
        }
        Assert.True((await sibling).GetProperty("accepted").GetBoolean());
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task Worker_MapsOnlyDomainErrorsAndBoundsProviderDiagnostics()
    {
        var handler = new TestRpcHandler
        {
            DeclaredErrors = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal)
            {
                ["messages"] = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["domain"] = ["example.rejected"],
                    ["undeclared-domain"] = [],
                },
            },
            Unary = static (_, _, methodId, _, _) => methodId switch
            {
                "domain" => ValueTask.FromException<JsonElement>(new SunderRpcException(
                    new SunderRpcError(
                        SunderRpcErrorKind.Domain,
                        "example.rejected",
                        "Request rejected.",
                        details: JsonSerializer.SerializeToElement(new { reason = "duplicate" })))),
                "undeclared-domain" => ValueTask.FromException<JsonElement>(new SunderRpcException(
                    new SunderRpcError(SunderRpcErrorKind.Domain, "example.undeclared", "Must remain private."))),
                _ => ValueTask.FromException<JsonElement>(new InvalidOperationException(
                    "failure\n" + new string('x', 512) + "\u0001")),
            },
        };
        var limits = WorkerLimits.Default with { MaximumDiagnosticCharacters = 80 };
        await using var worker = new WorkerHarness(
            _ => new SunderWorkerOptions([WorkerHarness.Registration(handler)]),
            limits);
        await worker.HandshakeAndActivateAsync();

        await worker.SendInvocationAsync("domain1", "unary", "domain");
        using (var domain = await worker.ReadAsync())
        {
            var error = domain.RootElement.GetProperty("error");
            Assert.Equal("domain", error.GetProperty("kind").GetString());
            Assert.Equal("example.rejected", error.GetProperty("code").GetString());
            Assert.Equal("Request rejected.", error.GetProperty("message").GetString());
            Assert.Equal("duplicate", error.GetProperty("details").GetProperty("reason").GetString());
        }

        await worker.SendInvocationAsync("undeclared1", "unary", "undeclared-domain");
        await CompleteProviderFaultDiagnosticAsync(worker, "undeclared1");
        using (var undeclared = await worker.ReadAsync())
        {
            var error = undeclared.RootElement.GetProperty("error");
            Assert.Equal("provider-fault", error.GetProperty("kind").GetString());
            Assert.Equal("rpc.provider.handler-fault", error.GetProperty("code").GetString());
            Assert.Equal("The process provider handler failed.", error.GetProperty("message").GetString());
        }

        await worker.SendInvocationAsync("fault1", "unary", "fault");
        await CompleteProviderFaultDiagnosticAsync(worker, "fault1");
        using (var fault = await worker.ReadAsync())
        {
            var error = fault.RootElement.GetProperty("error");
            Assert.Equal("provider-fault", error.GetProperty("kind").GetString());
            Assert.Equal("rpc.provider.handler-fault", error.GetProperty("code").GetString());
            Assert.Equal("The process provider handler failed.", error.GetProperty("message").GetString());
        }

        var diagnostics = worker.Diagnostics.ToString()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.DoesNotContain('\u0001', diagnostic);
            Assert.True(diagnostic.Length <= limits.MaximumDiagnosticCharacters);
        });
        Assert.EndsWith("[truncated]", diagnostics[^1], StringComparison.Ordinal);
        await worker.ShutdownAsync();
    }

    private static async Task CompleteProviderFaultDiagnosticAsync(WorkerHarness worker, string invocationId)
    {
        using var diagnosticRequest = await worker.ReadAsync();
        Assert.Equal(
            "worker.provider-fault-diagnostic",
            diagnosticRequest.RootElement.GetProperty("type").GetString());
        Assert.Equal(invocationId, diagnosticRequest.RootElement.GetProperty("invocationId").GetString());
        await worker.SendAsync(new
        {
            type = "host.result",
            id = diagnosticRequest.RootElement.GetProperty("id").GetString(),
            value = (object?)null,
        });
    }

    [Fact]
    public async Task Worker_FailsClosedOnOutOfOrderUnknownAndEnvironmentMismatch()
    {
        await using (var outOfOrder = CreateNoOpWorker())
        {
            await outOfOrder.ActivateAsync(1);
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
                protocolVersion = WorkerProtocol.Version,
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
    public async Task Worker_EnforcesExactPackageAndCallerSemanticVersionLengthBoundaries()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "WorkerV1Conformance", "corpus.json")));
        var boundary = corpus.RootElement.GetProperty("semanticVersionBoundary");
        var prefix = boundary.GetProperty("prefix").GetString()!;
        var suffixCharacter = Assert.Single(boundary.GetProperty("suffixCharacter").GetString()!);
        var maximumLength = boundary.GetProperty("maximumLength").GetInt32();
        var maximumVersion = prefix + new string(
            suffixCharacter,
            boundary.GetProperty("acceptedSuffixLength").GetInt32());
        var overlongVersion = prefix + new string(
            suffixCharacter,
            boundary.GetProperty("rejectedSuffixLength").GetInt32());
        Assert.Equal(maximumLength, maximumVersion.Length);
        Assert.Equal(maximumLength + 1, overlongVersion.Length);

        SunderWorkerContext? configured = null;
        await using (var packageAccepted = new WorkerHarness(
            context =>
            {
                configured = context;
                return new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())]);
            },
            packageVersion: maximumVersion))
        {
            await packageAccepted.HandshakeAndActivateAsync();
            Assert.Equal(maximumVersion, configured?.Version);
            await packageAccepted.ShutdownAsync();
        }

        var failure = Assert.Throws<InvalidOperationException>(() => new WorkerHarness(
            _ => new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())]),
            packageVersion: overlongVersion));
        Assert.Contains("SUNDER_PACKAGE_VERSION", failure.Message, StringComparison.Ordinal);

        var callerVersion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestRpcHandler
        {
            Unary = (context, _, _, request, _) =>
            {
                callerVersion.TrySetResult(context.CallerPackageVersion);
                return ValueTask.FromResult(request.Clone());
            },
        };
        await using var callerWorker = new WorkerHarness(
            _ => new SunderWorkerOptions([WorkerHarness.Registration(handler)]));
        await callerWorker.HandshakeAndActivateAsync();
        await callerWorker.SendInvocationAsync(
            "maximum-caller-version",
            "unary",
            "echo",
            callerPackageVersion: maximumVersion);
        using (var result = await callerWorker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
        }
        Assert.Equal(maximumVersion, await callerVersion.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await callerWorker.SendInvocationAsync(
            "overlong-caller-version",
            "unary",
            "echo",
            callerPackageVersion: overlongVersion);
        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await callerWorker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Worker_RequiresExactClosedResourceProfileHash()
    {
        Assert.Equal(
            "15bde75ca257659d5742c38f678a445bc4bdfb7e21eff9b81fedc3195e0a3ec9",
            WorkerHarness.ResourceProfileHash);
        await using var worker = CreateNoOpWorker();

        await worker.SendHelloAsync(
            "challenge",
            MinimumResourceProfile(),
            new string('0', 64));

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("resourceProfileHash", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_ExposesValidatedResourceProfileDuringConfigurationAndThroughCallScope()
    {
        var hostProfile = MinimumResourceProfile();
        var expected = new SunderRpcResourceProfile(
            WorkerWire.ComputeResourceProfileHash(hostProfile),
            hostProfile.RpcValueBytes,
            hostProfile.ValueDepth,
            hostProfile.ValueNodes,
            hostProfile.ObjectMembers,
            hostProfile.InlineDecodedBytes,
            hostProfile.ContentReferenceBytes);
        SunderRpcResourceProfile? configuredProfile = null;
        SunderRpcResourceProfile? scopeProfile = null;
        await using var worker = new WorkerHarness(context =>
        {
            configuredProfile = context.Rpc.ResourceProfile;
            return new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())])
            {
                OnActivated = async (_, cancellationToken) =>
                {
                    await using var scope = await context.Rpc.CreateCallScopeAsync(
                        cancellationToken: cancellationToken);
                    scopeProfile = scope.ResourceProfile;
                },
            };
        });

        await worker.HandshakeAsync(resourceProfile: hostProfile);
        Assert.Equal(expected, configuredProfile);
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();
        const string scopeId = "resource-profile-scope";
        using (var open = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", open.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = open.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }
        using (var close = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-close", close.RootElement.GetProperty("type").GetString());
            Assert.Equal(scopeId, close.RootElement.GetProperty("scopeId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = close.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }

        Assert.Same(configuredProfile, scopeProfile);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task Worker_OversizedOutboundRequestIsNotRegisteredOrDispatched()
    {
        ISunderRpcClient? client = null;
        var limits = WorkerLimits.Default with { MaximumOutboundCalls = 1 };
        await using var worker = new WorkerHarness(
            _ => new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())])
            {
                OnActivated = (context, _) =>
                {
                    client = context.Rpc;
                    return ValueTask.CompletedTask;
                },
            },
            limits);
        await ActivateAsync(worker, MinimumResourceProfile());

        var oversized = JsonSerializer.SerializeToElement(new { text = new string('"', 600_000) });
        var exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client!.InvokeAsync(
                new SunderRpcEndpointReference("rpc1_example"),
                "messages",
                "echo",
                oversized).AsTask());

        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, exhausted.Error.DispatchState);
        var valid = client!.InvokeAsync(
            new SunderRpcEndpointReference("rpc1_example"),
            "messages",
            "echo",
            JsonSerializer.SerializeToElement(new { accepted = true })).AsTask();
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.invoke", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new { accepted = true },
            });
        }
        Assert.True((await valid).GetProperty("accepted").GetBoolean());
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task Worker_OversizedProviderResultIsCallLocalResourceExhaustion()
    {
        var handler = new TestRpcHandler
        {
            Unary = static (_, _, methodId, request, _) => ValueTask.FromResult(
                methodId == "oversized"
                    ? JsonSerializer.SerializeToElement(new { text = new string('"', 600_000) })
                    : request.Clone()),
        };
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(handler)]));
        await ActivateAsync(worker, MinimumResourceProfile());

        await worker.SendInvocationAsync("oversized1", "unary", "oversized");
        using (var response = await worker.ReadAsync())
        {
            Assert.Equal("worker.error", response.RootElement.GetProperty("type").GetString());
            Assert.Equal("resource-exhausted", response.RootElement.GetProperty("error").GetProperty("kind").GetString());
            Assert.Equal("rpc.worker.response-limit", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        Assert.False(worker.RunTask.IsCompleted);

        await worker.SendInvocationAsync("valid1", "unary", "echo");
        using (var response = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", response.RootElement.GetProperty("type").GetString());
        }
        Assert.False(worker.RunTask.IsCompleted);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task Worker_OversizedProviderStreamEventIsCallLocalResourceExhaustion()
    {
        var handler = new TestRpcHandler
        {
            Unary = static (_, _, _, request, _) => ValueTask.FromResult(request.Clone()),
            Stream = static (_, _, _, _, cancellationToken) => OversizedEvent(cancellationToken),
        };
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(handler)]));
        await ActivateAsync(worker, MinimumResourceProfile());

        await worker.SendInvocationAsync("oversized-stream-1", "server-stream", "oversized");
        using (var response = await worker.ReadAsync())
        {
            Assert.Equal("worker.error", response.RootElement.GetProperty("type").GetString());
            Assert.Equal("resource-exhausted", response.RootElement.GetProperty("error").GetProperty("kind").GetString());
            Assert.Equal("rpc.worker.response-limit", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        Assert.False(worker.RunTask.IsCompleted);

        await worker.SendInvocationAsync("valid-after-stream-1", "unary", "echo");
        using (var response = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", response.RootElement.GetProperty("type").GetString());
        }
        Assert.False(worker.RunTask.IsCompleted);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task Worker_PreservesHostDomainErrorDetailsAndRejectsInfrastructureDetails()
    {
        var activated = new TaskCompletionSource<ISunderRpcClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = (context, _) =>
            {
                activated.TrySetResult(context.Rpc);
                return ValueTask.CompletedTask;
            },
        });
        await worker.HandshakeAndActivateAsync();
        var client = await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var invocation = client.InvokeAsync(
            new SunderRpcEndpointReference("rpc1_example"),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask();
        string callId;
        using (var request = await worker.ReadAsync())
        {
            callId = request.RootElement.GetProperty("id").GetString()!;
        }
        await worker.SendAsync(new
        {
            type = "host.error",
            id = callId,
            error = new
            {
                kind = "domain",
                code = "messages.rejected",
                message = "Rejected.",
                dispatchState = "possibly-dispatched",
                retrySafety = "unsafe",
                details = new { reason = "duplicate" },
            },
        });

        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => invocation);
        Assert.Equal("duplicate", failure.Error.Details!.Value.GetProperty("reason").GetString());

        var forged = client.InvokeAsync(
            new SunderRpcEndpointReference("rpc1_example"),
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "again" })).AsTask();
        using (var request = await worker.ReadAsync())
        {
            callId = request.RootElement.GetProperty("id").GetString()!;
        }
        await worker.SendAsync(new
        {
            type = "host.error",
            id = callId,
            error = new
            {
                kind = "unavailable",
                code = "rpc.host.forged",
                message = "Forged.",
                dispatchState = "possibly-dispatched",
                retrySafety = "unsafe",
                details = new { reason = "forged" },
            },
        });

        await Assert.ThrowsAsync<WorkerProtocolException>(() => worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<Exception>(() => forged);
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
                         _ => new SunderWorkerOptions([WorkerHarness.Registration(handler)])))
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_IgnoresCancellationThatCrossesACompletedResponse(bool churnWhileActive)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestRpcHandler
        {
            Unary = async (_, _, method, request, token) =>
            {
                if (method == "wait") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
                return request.Clone();
            },
        };
        await using var worker = new WorkerHarness(
            _ => new SunderWorkerOptions([WorkerHarness.Registration(handler)]),
            WorkerLimits.Default with { MaximumRememberedHostIds = 2 });
        await worker.HandshakeAndActivateAsync();
        await worker.SendInvocationAsync("completed1", "unary", churnWhileActive ? "wait" : "echo");
        if (churnWhileActive)
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 3; index++)
            {
                var id = "churn" + index;
                await worker.SendInvocationAsync(id, "unary", "echo");
                using var response = await worker.ReadAsync();
                Assert.Equal(id, response.RootElement.GetProperty("id").GetString());
            }
            release.TrySetResult();
        }
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
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();
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
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
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

    [Fact]
    public async Task Worker_ShutdownDrainsOutboundClientCallsBeforeShutdownCallback()
    {
        var activated = new TaskCompletionSource<ISunderRpcClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerHarness(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = (context, _) =>
            {
                activated.TrySetResult(context.Rpc);
                return ValueTask.CompletedTask;
            },
            OnShutdown = (_, _) =>
            {
                shutdown.TrySetResult();
                return ValueTask.CompletedTask;
            },
        });
        await worker.HandshakeAndActivateAsync();
        var client = await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var discover = client.DiscoverAsync("example.outbound", CancellationToken.None).AsTask();
        string callId;
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.discover", request.RootElement.GetProperty("type").GetString());
            callId = request.RootElement.GetProperty("id").GetString()!;
        }

        await worker.SendAsync(new { type = "host.shutdown", shutdownId = "shutdown1", reason = "test" });
        using (var cancellation = await worker.ReadAsync())
        {
            Assert.Equal("worker.cancel", cancellation.RootElement.GetProperty("type").GetString());
            Assert.Equal(callId, cancellation.RootElement.GetProperty("id").GetString());
        }
        await Task.Delay(50);
        Assert.False(shutdown.Task.IsCompleted);
        await worker.SendAsync(new
        {
            type = "host.error",
            id = callId,
            error = new
            {
                kind = "cancelled",
                code = "rpc.call.cancelled",
                message = "The RPC call was cancelled.",
                dispatchState = "possibly-dispatched",
                retrySafety = "unsafe",
            },
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discover);
        await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var acknowledgement = await worker.ReadAsync())
        {
            Assert.Equal("worker.shutdown-ack", acknowledgement.RootElement.GetProperty("type").GetString());
        }
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(string.Empty, worker.Diagnostics.ToString());
    }

    [Fact]
    public async Task Worker_ShutdownCompletesWhenProtocolInputIgnoresCancellation()
    {
        await using var worker = new WorkerHarness(
            _ => new SunderWorkerOptions([WorkerHarness.Registration(new TestRpcHandler())]),
            ignoreInputReadCancellation: true);
        await worker.HandshakeAndActivateAsync();

        await worker.ShutdownAsync();

        Assert.Equal(string.Empty, worker.Diagnostics.ToString());
    }

    private static WorkerHarness CreateNoOpWorker()
        => new(_ => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())]));

    private static WorkerResourceProfile MinimumResourceProfile()
        => new(
            Version: 1,
            RpcValueBytes: 1024 * 1024,
            ValueDepth: 16,
            ValueNodes: 16_384,
            ObjectMembers: 256,
            InlineDecodedBytes: 16 * 1024,
            ContentReferenceBytes: 1024 * 1024);

    private static async Task ActivateAsync(WorkerHarness worker, WorkerResourceProfile profile)
    {
        await worker.HandshakeAsync(resourceProfile: profile);
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();
        using var activated = await worker.ReadAsync();
        Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
    }

    private static async IAsyncEnumerable<JsonElement> Events(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return JsonSerializer.SerializeToElement(new { sequence = 1 });
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return JsonSerializer.SerializeToElement(new { sequence = 2 });
    }

    private static async IAsyncEnumerable<JsonElement> OversizedEvent(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return JsonSerializer.SerializeToElement(new { text = new string('"', 600_000) });
        await Task.CompletedTask;
    }
}
