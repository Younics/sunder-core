using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker.Internal;
using Sunder.Sdk.Worker.Tests.TestSupport;

namespace Sunder.Sdk.Worker.Tests;

public sealed class WorkerV2RuntimeTests
{
    [Fact]
    public async Task WorkerV2_ComposesThenStartsCommitsActivatesInvokesAndShutsDown()
    {
        var lifecycle = new List<string>();
        var handler = new TestRpcHandler
        {
            Unary = static (_, _, _, request, _) => ValueTask.FromResult(request.Clone()),
        };
        SunderWorkerContext? configuredContext = null;
        SunderWorkerGenerationContext? committedGeneration = null;
        await using var worker = new WorkerHarness(context =>
        {
            configuredContext = context;
            lifecycle.Add("configured");
            return new SunderWorkerV2Options([WorkerHarness.Registration(handler)])
            {
                OnCandidateStarted = _ =>
                {
                    lifecycle.Add("candidate-started");
                    return ValueTask.CompletedTask;
                },
                OnGenerationCommitted = (generation, _) =>
                {
                    committedGeneration = generation;
                    lifecycle.Add("generation-committed");
                    return ValueTask.CompletedTask;
                },
                OnActivated = (activation, _) =>
                {
                    Assert.Same(context, activation);
                    lifecycle.Add("activated");
                    return ValueTask.CompletedTask;
                },
                OnShutdown = (_, _) =>
                {
                    lifecycle.Add("shutdown");
                    return ValueTask.CompletedTask;
                },
            };
        });

        await worker.HandshakeAndActivateAsync();

        Assert.NotNull(configuredContext);
        Assert.Equal(
            ["configured", "candidate-started", "generation-committed", "activated"],
            lifecycle);
        Assert.Equal(Guid.ParseExact(WorkerHarness.ActivationId, "N"), committedGeneration?.ActivationId);
        Assert.Equal(7, committedGeneration?.SessionGeneration);

        await worker.SendInvocationAsync("unary1", "unary", "echo");
        using (var result = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
            Assert.Equal("hello", result.RootElement.GetProperty("value").GetProperty("message").GetString());
        }

        await worker.SendInvocationAsync("stream1", "server-stream", "watch");
        using (var complete = await worker.ReadAsync())
        {
            Assert.Equal("worker.complete", complete.RootElement.GetProperty("type").GetString());
        }

        await worker.ShutdownAsync("activation-retired");
        Assert.Equal("shutdown", lifecycle[^1]);
        Assert.Equal(string.Empty, worker.Diagnostics.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerV2_ProviderFaultEmitsOnlySafeDiagnosticBeforeGenericError(
        bool diagnosticAccepted)
    {
        const string secret = "provider-secret=must-never-persist";
        var handler = new TestRpcHandler
        {
            Unary = static (_, _, _, _, _) => throw new SecretBearingProviderException(secret),
        };
        await using var worker = new WorkerHarness(_ => new SunderWorkerV2Options(
            [WorkerHarness.Registration(handler)]));
        await worker.HandshakeAndActivateAsync();

        await worker.SendInvocationAsync("providerFault1", "unary", "send");
        string diagnosticId;
        using (var diagnostic = await worker.ReadAsync())
        {
            var root = diagnostic.RootElement;
            Assert.Equal(
                [
                    "type",
                    "id",
                    "invocationId",
                    "providerId",
                    "serviceId",
                    "methodId",
                    "exceptionType",
                    "exceptionFingerprint",
                ],
                root.EnumerateObject().Select(static property => property.Name));
            Assert.Equal("worker.provider-fault-diagnostic", root.GetProperty("type").GetString());
            diagnosticId = root.GetProperty("id").GetString()!;
            Assert.Equal("providerFault1", root.GetProperty("invocationId").GetString());
            Assert.Equal(WorkerHarness.ProviderId, root.GetProperty("providerId").GetString());
            Assert.Equal("messages", root.GetProperty("serviceId").GetString());
            Assert.Equal("send", root.GetProperty("methodId").GetString());
            var exceptionType = typeof(SecretBearingProviderException).FullName!;
            Assert.Equal(exceptionType, root.GetProperty("exceptionType").GetString());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exceptionType))).ToLowerInvariant(),
                root.GetProperty("exceptionFingerprint").GetString());
            Assert.DoesNotContain(secret, root.GetRawText(), StringComparison.Ordinal);
        }

        await worker.SendAsync(diagnosticAccepted
            ? new
            {
                type = "host.result",
                id = diagnosticId,
                value = (object?)null,
            }
            : new
            {
                type = "host.error",
                id = diagnosticId,
                error = (object)new
                {
                    kind = "unavailable",
                    code = "rpc.logging.unavailable",
                    message = "Host logging is unavailable.",
                },
            });

        using (var response = await worker.ReadAsync())
        {
            var root = response.RootElement;
            Assert.Equal("worker.error", root.GetProperty("type").GetString());
            Assert.Equal("providerFault1", root.GetProperty("id").GetString());
            var error = root.GetProperty("error");
            Assert.Equal("provider-fault", error.GetProperty("kind").GetString());
            Assert.Equal("rpc.provider.handler-fault", error.GetProperty("code").GetString());
            Assert.Equal("The process provider handler failed.", error.GetProperty("message").GetString());
            Assert.DoesNotContain(secret, root.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(SecretBearingProviderException), root.GetRawText(), StringComparison.Ordinal);
        }

        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_RejectsV1HelloWithoutFallback()
    {
        var configured = false;
        await using var worker = new WorkerHarness(_ =>
        {
            configured = true;
            return new SunderWorkerV2Options([WorkerHarness.Registration(new TestRpcHandler())]);
        });

        await worker.SendAsync(new
        {
            type = "host.hello",
            protocol = "sunder.worker.v1",
            protocolVersion = 1,
            challenge = "challenge",
            packageId = WorkerHarness.PackageId,
            packageVersion = WorkerHarness.PackageVersion,
            activationId = WorkerHarness.ActivationId,
            sessionId = WorkerHarness.SessionId,
            providers = new[]
            {
                new
                {
                    providerId = WorkerHarness.ProviderId,
                    contractId = WorkerHarness.ContractId,
                    contractVersion = WorkerHarness.ContractVersion,
                    contractSha256 = WorkerHarness.ContractSha256,
                },
            },
        });

        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(configured);
    }

    [Fact]
    public async Task WorkerV2_AcceptsHigherSessionGenerationForSameSurvivingActivation()
    {
        var handler = new TestRpcHandler
        {
            Unary = static (_, _, _, request, _) => ValueTask.FromResult(request.Clone()),
        };
        await using var worker = new WorkerHarness(_ => new SunderWorkerV2Options(
            [WorkerHarness.Registration(handler)]));
        await worker.HandshakeAndActivateAsync(sessionGeneration: 7);

        await worker.SendInvocationAsync(
            "restamped1",
            "unary",
            "echo",
            sessionGeneration: 8);
        using (var result = await worker.ReadAsync())
        {
            Assert.Equal("worker.result", result.RootElement.GetProperty("type").GetString());
            Assert.Equal("hello", result.RootElement.GetProperty("value").GetProperty("message").GetString());
        }
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_RejectsProviderGenerationBelowInitialCommitBeforeEnteringHandler()
    {
        var handlerEntryCount = 0;
        var handler = new TestRpcHandler
        {
            Unary = (_, _, _, request, _) =>
            {
                Interlocked.Increment(ref handlerEntryCount);
                return ValueTask.FromResult(request.Clone());
            },
        };
        await using var worker = new WorkerHarness(_ => new SunderWorkerV2Options(
            [WorkerHarness.Registration(handler)]));
        await worker.HandshakeAndActivateAsync(sessionGeneration: 7);

        await worker.SendInvocationAsync(
            "stale1",
            "unary",
            "echo",
            sessionGeneration: 6);

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("exact registered provider activation", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref handlerEntryCount));
    }

    [Fact]
    public async Task WorkerV2_ActivationCanCallHostBeforeAcknowledgingReadiness()
    {
        SunderRpcCatalogSnapshot? discovered = null;
        await using var worker = new WorkerHarness(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                discovered = await context.Rpc.DiscoverAsync(WorkerHarness.ContractId, cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();

        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.discover", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    revision = 3,
                    sequence = 4,
                    providers = Array.Empty<object>(),
                    resetRequired = false,
                },
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }

        Assert.Equal(3, discovered?.Revision);
        await worker.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerV2_ShutdownInterruptsPendingCandidateOrCommitCallback(bool duringCommit)
    {
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerHarness(_ => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnCandidateStarted = duringCommit
                ? null
                : async cancellationToken =>
                {
                    callbackStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
            OnGenerationCommitted = duringCommit
                ? async (_, cancellationToken) =>
                {
                    callbackStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                : null,
        });
        await worker.HandshakeAsync();
        await worker.SendAsync(new { type = "host.start-candidate" });
        if (duringCommit)
        {
            using var started = await worker.ReadAsync();
            Assert.Equal("worker.candidate-started", started.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.commit-generation",
                activationId = WorkerHarness.ActivationId,
                sessionGeneration = 7,
            });
        }
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.SendAsync(new
        {
            type = "host.shutdown",
            shutdownId = "shutdown1",
            reason = "candidate-discarded",
        });
        using (var acknowledgement = await worker.ReadAsync())
        {
            Assert.Equal("worker.shutdown-ack", acknowledgement.RootElement.GetProperty("type").GetString());
        }
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV2_ExactCommitRetryIsIdempotentWhileCallbackRuns()
    {
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCount = 0;
        await using var worker = new WorkerHarness(_ => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnGenerationCommitted = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref callbackCount);
                callbackStarted.TrySetResult();
                await releaseCallback.Task.WaitAsync(cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.SendAsync(new { type = "host.start-candidate" });
        using (var started = await worker.ReadAsync())
        {
            Assert.Equal("worker.candidate-started", started.RootElement.GetProperty("type").GetString());
        }
        var commit = new
        {
            type = "host.commit-generation",
            activationId = WorkerHarness.ActivationId,
            sessionGeneration = 7,
        };
        await worker.SendAsync(commit);
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.SendAsync(commit);
        await Task.Delay(100);

        Assert.False(worker.RunTask.IsCompleted);
        releaseCallback.TrySetResult();
        using (var committed = await worker.ReadAsync())
        {
            Assert.Equal("worker.generation-committed", committed.RootElement.GetProperty("type").GetString());
        }
        var nextOutput = worker.ReadAsync().AsTask();
        await Task.Delay(100);
        Assert.False(nextOutput.IsCompleted);
        await worker.SendAsync(new
        {
            type = "host.shutdown",
            shutdownId = "shutdown1",
            reason = "candidate-discarded",
        });
        using (var acknowledgement = await nextOutput)
        {
            Assert.Equal("worker.shutdown-ack", acknowledgement.RootElement.GetProperty("type").GetString());
        }
        Assert.Equal(1, callbackCount);
        await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WorkerV2_CallScopeRoutesRpcAndClosesOnce()
    {
        var requestedDeadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var clampedDeadline = requestedDeadline.AddSeconds(-5);
        SunderRpcProviderSnapshot? provider = null;
        SunderRpcCatalogSnapshot? catalog = null;
        JsonElement invocation = default;
        var invariantReportAccepted = false;
        var catalogEvents = new List<SunderRpcCatalogEvent>();
        var streamEvents = new List<JsonElement>();
        await using var worker = new WorkerHarness(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                var scope = await context.Rpc.CreateCallScopeAsync(
                    new SunderRpcCallOptions(requestedDeadline),
                    cancellationToken);
                Assert.Equal(clampedDeadline, scope.DeadlineUtc);
                provider = await scope.GetProviderAsync(
                    new SunderRpcEndpointReference("rpc1_example"),
                    cancellationToken);
                invariantReportAccepted = await scope.TryReportInvariantViolationAsync(
                    new SunderRpcEndpointReference("rpc1_example"),
                    new InvalidOperationException("line one\r\nline two " + new string('x', 600)),
                    cancellationToken);
                catalog = await scope.DiscoverAsync(WorkerHarness.ContractId, cancellationToken);
                invocation = await scope.InvokeAsync(
                    new SunderRpcEndpointReference("rpc1_example"),
                    "messages",
                    "send",
                    JsonSerializer.SerializeToElement(new { message = "hello" }),
                    cancellationToken: cancellationToken);
                await foreach (var item in scope.WatchAsync(3, 4, cancellationToken))
                {
                    catalogEvents.Add(item);
                }
                await foreach (var item in scope.SubscribeAsync(
                                   new SunderRpcEndpointReference("rpc1_example"),
                                   "messages",
                                   "watch",
                                   JsonSerializer.SerializeToElement(new { message = "hello" }),
                                   cancellationToken: cancellationToken))
                {
                    streamEvents.Add(item);
                }
                await scope.DisposeAsync();
                await scope.DisposeAsync();
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });

        const string scopeId = "worker-scope-test";
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", request.RootElement.GetProperty("type").GetString());
            Assert.Equal(requestedDeadline, request.RootElement.GetProperty("deadlineUtc").GetDateTimeOffset());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = clampedDeadline.UtcDateTime.ToString("O"),
                },
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.get-provider", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = WorkerHarness.ProviderSnapshot(),
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.report-invariant-violation", scopeId);
            Assert.Equal("rpc1_example", request.RootElement.GetProperty("endpointReference").GetString());
            var message = request.RootElement.GetProperty("exceptionMessage").GetString();
            Assert.NotNull(message);
            Assert.Equal(512, message.Length);
            Assert.DoesNotContain('\r', message);
            Assert.DoesNotContain('\n', message);
            Assert.DoesNotContain(nameof(InvalidOperationException), message, StringComparison.Ordinal);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = true,
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.discover", scopeId);
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
            AssertScopedRequest(request.RootElement, "worker.invoke", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new { accepted = true },
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.watch", scopeId);
            var id = request.RootElement.GetProperty("id").GetString();
            await worker.SendAsync(new
            {
                type = "host.event",
                id,
                value = new
                {
                    revision = 4,
                    sequence = 5,
                    kind = "added",
                    provider = WorkerHarness.ProviderSnapshot(),
                },
            });
            await worker.SendAsync(new { type = "host.complete", id });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.subscribe", scopeId);
            var id = request.RootElement.GetProperty("id").GetString();
            await worker.SendAsync(new { type = "host.event", id, value = new { sequence = 1 } });
            await worker.SendAsync(new { type = "host.complete", id });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.scope-close", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }

        Assert.Equal("example.provider", provider?.ProviderId);
        Assert.True(invariantReportAccepted);
        Assert.Equal(3, catalog?.Revision);
        Assert.True(invocation.GetProperty("accepted").GetBoolean());
        Assert.Single(catalogEvents);
        Assert.Single(streamEvents);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_CallScopeRegistersOpensAndReleasesContent()
    {
        const string content = "scope-content";
        string? opened = null;
        await using var worker = new WorkerHarness(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                await using var source = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                var reference = await scope.RegisterContentAsync(
                    new SunderRpcEndpointReference("rpc1_example"),
                    source,
                    new SunderRpcContentRegistrationOptions("text/plain", "request.txt", source.Length),
                    cancellationToken);
                await using var stream = await scope.OpenContentAsync(reference, cancellationToken);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                opened = await reader.ReadToEndAsync(cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });

        const string scopeId = "worker-scope-content";
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }

        string stagingPath;
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.content-register", scopeId);
            Assert.Equal("rpc1_example", request.RootElement.GetProperty("endpointReference").GetString());
            stagingPath = request.RootElement.GetProperty("filePath").GetString()!;
            Assert.StartsWith(worker.DataPath, stagingPath, StringComparison.Ordinal);
            Assert.Equal(content, await File.ReadAllTextAsync(stagingPath));
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = WorkerHarness.ContentReference("rpc-content-scope", content.Length),
            });
        }
        using (var request = await worker.ReadAsync())
        {
            Assert.False(File.Exists(stagingPath));
            AssertScopedRequest(request.RootElement, "worker.content-open", scopeId);
            var hostPath = Path.Combine(worker.DataPath, "opened-scope-content.txt");
            await File.WriteAllTextAsync(hostPath, content);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new { handleId = "scope-handle", filePath = hostPath },
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.content-release", scopeId);
            Assert.Equal("scope-handle", request.RootElement.GetProperty("handleId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var request = await worker.ReadAsync())
        {
            AssertScopedRequest(request.RootElement, "worker.scope-close", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }

        Assert.Equal(content, opened);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_CallScopeRejectsDuplicateHostScopeId()
    {
        await using var worker = new WorkerHarness(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var first = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });

        for (var index = 0; index < 2; index++)
        {
            using var request = await worker.ReadAsync();
            Assert.Equal("worker.scope-open", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = request.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId = "duplicate-scope",
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }

        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("reused RPC call-scope id", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerV2_CancelledScopeOpenReleasesCrossedHostResult()
    {
        using var scopeCancellation = new CancellationTokenSource();
        var cancelled = false;
        SunderWorkerContext? configuredContext = null;
        var limits = WorkerLimits.Default with { MaximumCallScopes = 1 };
        await using var worker = new WorkerHarness(context =>
        {
            configuredContext = context;
            return new SunderWorkerV2Options([WorkerHarness.Registration(new TestRpcHandler())])
            {
                OnActivated = async (_, _) =>
                {
                    try
                    {
                        await context.Rpc.CreateCallScopeAsync(cancellationToken: scopeCancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                },
            };
        }, limits);
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });

        string openId;
        using (var open = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", open.RootElement.GetProperty("type").GetString());
            openId = open.RootElement.GetProperty("id").GetString()!;
        }
        scopeCancellation.Cancel();
        var activated = false;
        while (true)
        {
            using var output = await worker.ReadAsync();
            var type = output.RootElement.GetProperty("type").GetString();
            if (type == "worker.activated")
            {
                activated = true;
                continue;
            }
            Assert.Equal("worker.cancel", type);
            Assert.Equal(openId, output.RootElement.GetProperty("id").GetString());
            break;
        }
        await worker.SendAsync(new
        {
            type = "host.result",
            id = openId,
            value = new
            {
                scopeId = "crossed-scope",
                deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
            },
        });

        var closed = false;
        while (!activated || !closed)
        {
            using var output = await worker.ReadAsync();
            var type = output.RootElement.GetProperty("type").GetString();
            if (type == "worker.activated")
            {
                activated = true;
                continue;
            }
            Assert.Equal("worker.scope-close", type);
            Assert.Equal("crossed-scope", output.RootElement.GetProperty("scopeId").GetString());
            closed = true;
            var exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await configuredContext!.Rpc.CreateCallScopeAsync());
            Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = output.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }

        Assert.True(cancelled);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_CancelledContentOpenReleasesCrossedHostHandle()
    {
        using var openCancellation = new CancellationTokenSource();
        var allowScopeClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        await using var worker = new WorkerHarness(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                try
                {
                    await scope.OpenContentAsync(
                        new SunderRpcContentReference(
                            "crossed-content",
                            1,
                            new string('d', 64),
                            "text/plain",
                            "crossed.txt",
                            DateTimeOffset.UtcNow.AddMinutes(1),
                            SunderRpcContentRepeatability.SingleUse),
                        openCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    await allowScopeClose.Task.WaitAsync(cancellationToken);
                }
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });

        const string scopeId = "crossed-content-scope";
        using (var openScope = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", openScope.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = openScope.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }
        string contentOpenId;
        using (var contentOpen = await worker.ReadAsync())
        {
            AssertScopedRequest(contentOpen.RootElement, "worker.content-open", scopeId);
            contentOpenId = contentOpen.RootElement.GetProperty("id").GetString()!;
        }
        openCancellation.Cancel();
        using (var cancellation = await worker.ReadAsync())
        {
            Assert.Equal("worker.cancel", cancellation.RootElement.GetProperty("type").GetString());
            Assert.Equal(contentOpenId, cancellation.RootElement.GetProperty("id").GetString());
        }
        var crossedContentPath = Path.Combine(worker.DataPath, "crossed-content.txt");
        await File.WriteAllBytesAsync(crossedContentPath, [42]);
        await worker.SendAsync(new
        {
            type = "host.result",
            id = contentOpenId,
            value = new
            {
                handleId = "crossed-handle",
                filePath = crossedContentPath,
            },
        });
        using (var release = await worker.ReadAsync())
        {
            AssertScopedRequest(release.RootElement, "worker.content-release", scopeId);
            Assert.Equal("crossed-handle", release.RootElement.GetProperty("handleId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = release.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }

        allowScopeClose.TrySetResult();
        using (var close = await worker.ReadAsync())
        {
            AssertScopedRequest(close.RootElement, "worker.scope-close", scopeId);
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

        Assert.True(cancelled);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_CrossedScopeOpenRetainsCapacityUntilScopeCloseTerminates()
    {
        var attemptSecondOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOpenAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exhausted = false;
        var limits = WorkerLimits.Default with { MaximumContentHandles = 1 };
        await using var worker = new WorkerHarness(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                var first = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                var second = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                var firstOpen = first.OpenContentAsync(
                    new SunderRpcContentReference(
                        "crossed-capacity-content",
                        1,
                        new string('d', 64),
                        "text/plain",
                        "crossed-capacity.txt",
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        SunderRpcContentRepeatability.SingleUse),
                    cancellationToken).AsTask();
                var firstDispose = first.DisposeAsync().AsTask();
                await attemptSecondOpen.Task.WaitAsync(cancellationToken);
                try
                {
                    await second.OpenContentAsync(
                        new SunderRpcContentReference(
                            "second-capacity-content",
                            1,
                            new string('e', 64),
                            "text/plain",
                            "second-capacity.txt",
                            DateTimeOffset.UtcNow.AddMinutes(1),
                            SunderRpcContentRepeatability.SingleUse),
                        cancellationToken);
                }
                catch (SunderRpcException exception) when (
                    exception.Error.Kind == SunderRpcErrorKind.ResourceExhausted)
                {
                    exhausted = true;
                }
                secondOpenAttempted.TrySetResult();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstOpen);
                await firstDispose;
                await second.DisposeAsync();
            },
        }, limits);
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });

        var scopeIds = new List<string>();
        for (var index = 0; index < 2; index++)
        {
            using var open = await worker.ReadAsync();
            Assert.Equal("worker.scope-open", open.RootElement.GetProperty("type").GetString());
            var scopeId = "capacity-scope-" + index;
            scopeIds.Add(scopeId);
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

        string? contentOpenId = null;
        string? firstCloseId = null;
        for (var index = 0; index < 3; index++)
        {
            using var output = await worker.ReadAsync();
            switch (output.RootElement.GetProperty("type").GetString())
            {
                case "worker.content-open":
                    contentOpenId = output.RootElement.GetProperty("id").GetString();
                    Assert.Equal(scopeIds[0], output.RootElement.GetProperty("scopeId").GetString());
                    break;
                case "worker.cancel":
                    break;
                case "worker.scope-close":
                    firstCloseId = output.RootElement.GetProperty("id").GetString();
                    Assert.Equal(scopeIds[0], output.RootElement.GetProperty("scopeId").GetString());
                    break;
                default:
                    throw new Xunit.Sdk.XunitException("Unexpected worker output while closing crossed content open.");
            }
        }
        Assert.NotNull(contentOpenId);
        Assert.NotNull(firstCloseId);
        var contentPath = Path.Combine(worker.DataPath, "crossed-capacity.txt");
        await File.WriteAllBytesAsync(contentPath, [42]);
        await worker.SendAsync(new
        {
            type = "host.result",
            id = contentOpenId,
            value = new { handleId = "crossed-capacity-handle", filePath = contentPath },
        });

        var nextOutput = worker.ReadAsync().AsTask();
        attemptSecondOpen.TrySetResult();
        await secondOpenAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exhausted);
        Assert.False(nextOutput.IsCompleted);

        await worker.SendAsync(new { type = "host.result", id = firstCloseId, value = (object?)null });
        using (var secondClose = await nextOutput)
        {
            Assert.Equal("worker.scope-close", secondClose.RootElement.GetProperty("type").GetString());
            Assert.Equal(scopeIds[1], secondClose.RootElement.GetProperty("scopeId").GetString());
            await worker.SendAsync(new
            {
                type = "host.result",
                id = secondClose.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_ScopeCloseWaitsForCrossedHandleRelease()
    {
        using var openCancellation = new CancellationTokenSource();
        await using var worker = new WorkerHarness(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                try
                {
                    await scope.OpenContentAsync(
                        new SunderRpcContentReference(
                            "release-before-close",
                            1,
                            new string('d', 64),
                            "text/plain",
                            "release-before-close.txt",
                            DateTimeOffset.UtcNow.AddMinutes(1),
                            SunderRpcContentRepeatability.SingleUse),
                        openCancellation.Token);
                }
                catch (OperationCanceledException) when (openCancellation.IsCancellationRequested)
                {
                }
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });

        const string scopeId = "release-before-close-scope";
        using (var openScope = await worker.ReadAsync())
        {
            await worker.SendAsync(new
            {
                type = "host.result",
                id = openScope.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    scopeId,
                    deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O"),
                },
            });
        }
        string contentOpenId;
        using (var contentOpen = await worker.ReadAsync())
        {
            contentOpenId = contentOpen.RootElement.GetProperty("id").GetString()!;
        }
        openCancellation.Cancel();
        using (var cancellation = await worker.ReadAsync())
        {
            Assert.Equal("worker.cancel", cancellation.RootElement.GetProperty("type").GetString());
        }
        var contentPath = Path.Combine(worker.DataPath, "release-before-close.txt");
        await File.WriteAllBytesAsync(contentPath, [42]);
        await worker.SendAsync(new
        {
            type = "host.result",
            id = contentOpenId,
            value = new { handleId = "release-before-close-handle", filePath = contentPath },
        });

        string releaseId;
        using (var release = await worker.ReadAsync())
        {
            AssertScopedRequest(release.RootElement, "worker.content-release", scopeId);
            releaseId = release.RootElement.GetProperty("id").GetString()!;
        }
        var close = worker.ReadAsync().AsTask();
        await Task.Delay(100);
        Assert.False(close.IsCompleted);
        await worker.SendAsync(new
        {
            type = "host.error",
            id = releaseId,
            error = new
            {
                kind = "unavailable",
                code = "rpc.scope.unavailable",
                message = "The RPC call scope is stale or unavailable.",
            },
        });
        using (var closeOutput = await close)
        {
            AssertScopedRequest(closeOutput.RootElement, "worker.scope-close", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = closeOutput.RootElement.GetProperty("id").GetString(),
                value = (object?)null,
            });
        }
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_ScopeCloseUsesControlLaneWhenRpcLaneIsFull()
    {
        var limits = WorkerLimits.Default with { MaximumOutboundCalls = 1 };
        var discoverCancelled = false;
        await using var worker = new WorkerHarness(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                var discovery = scope.DiscoverAsync(WorkerHarness.ContractId, cancellationToken).AsTask();
                await scope.DisposeAsync();
                try
                {
                    await discovery;
                }
                catch (OperationCanceledException)
                {
                    discoverCancelled = true;
                }
            },
        }, limits);
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });

        const string scopeId = "saturated-scope";
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
        string discoverId;
        using (var discover = await worker.ReadAsync())
        {
            AssertScopedRequest(discover.RootElement, "worker.discover", scopeId);
            discoverId = discover.RootElement.GetProperty("id").GetString()!;
        }

        string? closeId = null;
        for (var index = 0; index < 2; index++)
        {
            using var output = await worker.ReadAsync();
            var type = output.RootElement.GetProperty("type").GetString();
            if (type == "worker.cancel")
            {
                Assert.Equal(discoverId, output.RootElement.GetProperty("id").GetString());
                await worker.SendAsync(new
                {
                    type = "host.error",
                    id = discoverId,
                    error = new
                    {
                        kind = "cancelled",
                        code = "rpc.call.cancelled",
                        message = "cancelled",
                    },
                });
            }
            else
            {
                AssertScopedRequest(output.RootElement, "worker.scope-close", scopeId);
                closeId = output.RootElement.GetProperty("id").GetString();
            }
        }
        Assert.NotNull(closeId);
        await worker.SendAsync(new { type = "host.result", id = closeId, value = (object?)null });
        using (var activated = await worker.ReadAsync())
        {
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());
        }

        Assert.True(discoverCancelled);
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_ScopeDeadlineRevokesOpenContentStream()
    {
        var streamReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Stream? openedStream = null;
        await using var worker = new WorkerHarness(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                await using var stream = await scope.OpenContentAsync(
                    new SunderRpcContentReference(
                        "deadline-content",
                        1,
                        new string('d', 64),
                        "text/plain",
                        "deadline.txt",
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        SunderRpcContentRepeatability.SingleUse),
                    cancellationToken);
                openedStream = stream;
                streamReady.TrySetResult();
                await releaseActivation.Task.WaitAsync(cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });

        const string scopeId = "deadline-scope";
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
                    deadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(300).UtcDateTime.ToString("O"),
                },
            });
        }
        using (var openContent = await worker.ReadAsync())
        {
            AssertScopedRequest(openContent.RootElement, "worker.content-open", scopeId);
            var path = Path.Combine(worker.DataPath, "deadline-content.txt");
            await File.WriteAllBytesAsync(path, [42]);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = openContent.RootElement.GetProperty("id").GetString(),
                value = new { handleId = "deadline-handle", filePath = path },
            });
        }
        await streamReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await openedStream!.ReadExactlyAsync(new byte[1]));

        releaseActivation.TrySetResult();
        using (var close = await worker.ReadAsync())
        {
            AssertScopedRequest(close.RootElement, "worker.scope-close", scopeId);
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
        await worker.ShutdownAsync();
    }

    [Fact]
    public async Task WorkerV2_MalformedHostContentHandleFaultsRuntime()
    {
        await using var worker = new WorkerHarness(context => new SunderWorkerV2Options(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, cancellationToken) =>
            {
                await using var scope = await context.Rpc.CreateCallScopeAsync(cancellationToken: cancellationToken);
                await scope.OpenContentAsync(
                    new SunderRpcContentReference(
                        "escaped-content",
                        1,
                        new string('d', 64),
                        "text/plain",
                        "escaped.txt",
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        SunderRpcContentRepeatability.SingleUse),
                    cancellationToken);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.SendAsync(new { type = "host.activate", sessionGeneration = 7 });

        const string scopeId = "malformed-content-scope";
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
        using (var openContent = await worker.ReadAsync())
        {
            AssertScopedRequest(openContent.RootElement, "worker.content-open", scopeId);
            await worker.SendAsync(new
            {
                type = "host.result",
                id = openContent.RootElement.GetProperty("id").GetString(),
                value = new
                {
                    handleId = "escaped-handle",
                    filePath = Path.Combine(Path.GetTempPath(), "outside-worker-data.content"),
                },
            });
        }
        var failure = await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await worker.RunTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("escaped", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertScopedRequest(JsonElement request, string type, string scopeId)
    {
        Assert.Equal(type, request.GetProperty("type").GetString());
        Assert.Equal(scopeId, request.GetProperty("scopeId").GetString());
    }

    private sealed class SecretBearingProviderException(string message) : Exception(message);
}
