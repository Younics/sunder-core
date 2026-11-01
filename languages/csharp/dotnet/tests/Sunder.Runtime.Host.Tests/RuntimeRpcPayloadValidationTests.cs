using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;
using static Sunder.Runtime.Host.Tests.RuntimeRpcTestPackages;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcPayloadValidationTests
{
    [Fact]
    public async Task InvalidProviderOutput_FaultsOnlyExactProviderActivation()
    {
        await using var fixture = new RpcFixture();
        var handler = new TestHandler
        {
            Unary = static (_, _) => ValueTask.FromResult(
                JsonSerializer.SerializeToElement(new { accepted = "not-a-boolean" })),
        };
        var activation = await fixture.PublishAsync(handler);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var diagnosticLoggedWhileActive = false;
        fixture.OwnerLogger.OnLog = entry =>
        {
            if (entry.EventId.Name == "RuntimeRpcProviderFault")
            {
                diagnosticLoggedWhileActive = Assert.Single(fixture.Catalog.GetSnapshot().Providers).State
                                              == SunderRpcProviderState.Active;
            }
        };

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.Equal("The provider returned output that violates its contract.", failure.Error.Message);
        Assert.True(RuntimeRpcFailureContextStore.TryGet(failure, out var diagnostic));
        Assert.Equal("caller.package", diagnostic.CallerPackageId);
        Assert.Equal("1.0.0", diagnostic.CallerPackageVersion);
        Assert.Equal(activation.Caller.ActivationId, diagnostic.CallerActivationId);
        Assert.Equal("provider.package", diagnostic.ProviderPackageId);
        Assert.Equal("1.0.0", diagnostic.ProviderPackageVersion);
        Assert.Equal("example.provider", diagnostic.ProviderId);
        Assert.Equal("example.rpc", diagnostic.ContractId);
        Assert.Equal(activation.Provider.ActivationId, diagnostic.ProviderActivationId);
        Assert.Equal("messages", diagnostic.ServiceId);
        Assert.Equal("send", diagnostic.MethodId);
        Assert.Equal("unary", diagnostic.CallKind);
        Assert.Equal("type-mismatch", diagnostic.ValidationCode);
        Assert.Equal("$.*", diagnostic.ValidationPath);
        Assert.True(diagnostic.ValueUtf8Bytes > 0);
        Assert.True(diagnostic.ValueMaximumDepth > 0);
        Assert.True(diagnostic.ValueTotalNodes > 0);
        Assert.True(diagnostic.ValueMaximumObjectMembers > 0);
        Assert.Equal(0, diagnostic.ValueInlineDecodedBytes);
        Assert.Equal(typeof(RuntimeRpcSemanticValidationException).FullName, diagnostic.ExceptionType);
        Assert.Equal(
            RuntimeRpcFailureContextStore.CreateExceptionFingerprint(diagnostic.ExceptionType),
            diagnostic.ExceptionFingerprint);
        Assert.DoesNotContain("not-a-boolean", failure.ToString(), StringComparison.Ordinal);
        var persistedDiagnostic = Assert.Single(
            fixture.OwnerLogger.Entries,
            entry => entry.EventId.Name == "RuntimeRpcProviderFault");
        Assert.True(diagnosticLoggedWhileActive);
        Assert.Null(persistedDiagnostic.Exception);
        Assert.Contains("validationCode=type-mismatch", persistedDiagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("validationPath=$.*", persistedDiagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("accepted", persistedDiagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-boolean", persistedDiagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(PackageReadinessState.Failed, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("caller.package")!.Readiness);
        Assert.Equal(fixture.Owner.Generation, fixture.Catalog.SessionGeneration);
        Assert.True(fixture.Catalog.TryGetPackage(
            activation.Caller.PackageId,
            activation.Caller.ActivationId,
            out var survivingCaller));
        Assert.Equal(fixture.Owner.Generation, survivingCaller!.SessionGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostWriterRejectionIsOperationalAndDoesNotFaultEveryProviderHandler(bool closed)
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Unary = (_, _) => throw new WorkerTransportResourceException(closed ? WorkerTransportFailureKind.WriteQueueClosed
                : WorkerTransportFailureKind.WriteQueueAdmission, "host-only queue diagnostic"),
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var provider = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers);
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(provider.Endpoint,
            "messages", "send", JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());
        Assert.Equal(closed ? SunderRpcErrorKind.Unavailable : SunderRpcErrorKind.ResourceExhausted, error.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, error.Error.DispatchState);
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage(provider.PackageId)!.Readiness);
        Assert.DoesNotContain(fixture.OwnerLogger.Entries, entry => entry.EventId.Name == "RuntimeRpcProviderFault");
    }

    [Fact]
    public async Task RequestAndStreamOutput_AreSchemaValidated()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler());
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        await fixture.GrantAsync(SunderRpcProtocol.SubscribeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        var invalidRequest = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { Message = "wrong-case" })));
        Assert.Equal(SunderRpcErrorKind.Validation, invalidRequest.Error.Kind);

        activation.Handler.Stream = static (_, _) => Values(
            JsonSerializer.SerializeToElement(new { accepted = true }),
            JsonSerializer.SerializeToElement(new { accepted = "bad" }));
        await using var stream = client.SubscribeAsync(
            endpoint,
            "messages",
            "watch",
            JsonSerializer.SerializeToElement(new { message = "hello" })).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        var invalidEvent = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await stream.MoveNextAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, invalidEvent.Error.Kind);
        Assert.True(RuntimeRpcFailureContextStore.TryGet(invalidEvent, out var diagnostic));
        Assert.Equal("server-stream", diagnostic.CallKind);
        Assert.Equal("type-mismatch", diagnostic.ValidationCode);
        Assert.Equal("$.*", diagnostic.ValidationPath);
        Assert.True(diagnostic.ValueUtf8Bytes > 0);
    }

    [Fact]
    public async Task RequestBytes_AcceptExactLimitAndRejectOneByteOverBeforeDispatch()
    {
        var dispatches = 0;
        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
        {
            MaxPayloadBytes = 17,
        });
        var contract = Contract(requestMaximumLength: 3);
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Unary = (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref dispatches);
                return ValueTask.FromResult(
                    JsonSerializer.SerializeToElement(new { accepted = true }));
            },
        }, callerContract: contract, providerContract: contract);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        _ = await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "aaa" }));
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "aaaa" })));

        Assert.Equal(1, dispatches);
        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, failure.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, failure.Error.DispatchState);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    [Fact]
    public async Task UnaryOutputBytes_AcceptExactLimitAndRejectOneByteOverWithoutProviderFault()
    {
        var handler = new TestHandler();
        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
        {
            MaxPayloadBytes = 17,
        });
        var activation = await fixture.PublishAsync(handler);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        _ = await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "a" }));
        handler.Unary = static (_, _) => ValueTask.FromResult(
            JsonSerializer.SerializeToElement(new { accepted = "xxx" }));
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "a" })));

        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, failure.Error.Kind);
        Assert.False(RuntimeRpcFailureContextStore.TryGet(failure, out _));
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
        handler.Unary = static (_, _) => ValueTask.FromResult(
            JsonSerializer.SerializeToElement(new { accepted = true }));
        var valid = await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "a" }));
        Assert.True(valid.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task StreamEventBytes_AcceptExactLimitAndRejectOneByteOverWithoutProviderFault()
    {
        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
        {
            MaxPayloadBytes = 17,
        });
        var handler = new TestHandler
        {
            Stream = static (_, _) => Values(
                JsonSerializer.SerializeToElement(new { accepted = true }),
                JsonSerializer.SerializeToElement(new { accepted = "xxx" })),
        };
        var activation = await fixture.PublishAsync(handler);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        await fixture.GrantAsync(SunderRpcProtocol.SubscribeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        await using var stream = client.SubscribeAsync(
            endpoint,
            "messages",
            "watch",
            JsonSerializer.SerializeToElement(new { message = "a" })).GetAsyncEnumerator();

        Assert.True(await stream.MoveNextAsync());
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await stream.MoveNextAsync().AsTask());

        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, failure.Error.Kind);
        Assert.False(RuntimeRpcFailureContextStore.TryGet(failure, out _));
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
        var valid = await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "a" }));
        Assert.True(valid.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task DomainErrorDetailsResourceLimitDoesNotFaultProvider()
    {
        var descriptor = Contract(declareErrorDetails: true);
        var handler = new TestHandler
        {
            Unary = static (_, _) => throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Domain,
                "messages.rejected",
                "not accepted",
                details: JsonSerializer.SerializeToElement(new { reason = new string('x', 100) }))),
        };
        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
        {
            MaxPayloadBytes = 32,
        });
        var activation = await fixture.PublishAsync(
            handler,
            callerContract: descriptor,
            providerContract: descriptor);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        var exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "a" })));

        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
        Assert.False(RuntimeRpcFailureContextStore.TryGet(exhausted, out _));
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
        handler.Unary = static (_, _) => ValueTask.FromResult(
            JsonSerializer.SerializeToElement(new { accepted = true }));
        Assert.True((await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "a" }))).GetProperty("accepted").GetBoolean());
    }

    [Theory]
    [InlineData("request", "rpc.request.inline-decoded-byte-limit")]
    [InlineData("output", "rpc.output.inline-decoded-byte-limit")]
    [InlineData("stream-event", "rpc.output.inline-decoded-byte-limit")]
    [InlineData("error-details", "rpc.error-details.inline-decoded-byte-limit")]
    public async Task InlineDecodedAggregateLimitIsCallLocalAndDoesNotFaultProvider(
        string surface,
        string expectedCode)
    {
        var contract = InlineDecodedContract();
        var exact = InlinePayload(5, 5, "one");
        var over = InlinePayload(6, 5, "one");
        var dispatches = 0;
        var handler = new TestHandler();
        switch (surface)
        {
            case "request":
                handler.Unary = (_, _) =>
                {
                    Interlocked.Increment(ref dispatches);
                    return ValueTask.FromResult(exact);
                };
                break;
            case "output":
                handler.Unary = (_, _) =>
                {
                    Interlocked.Increment(ref dispatches);
                    return ValueTask.FromResult(over);
                };
                break;
            case "stream-event":
                handler.Stream = (_, _) =>
                {
                    Interlocked.Increment(ref dispatches);
                    return Values(over, over);
                };
                break;
            case "error-details":
                handler.Unary = (_, _) =>
                {
                    Interlocked.Increment(ref dispatches);
                    throw new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.Domain,
                        "messages.rejected",
                        "not accepted",
                        details: over));
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(surface));
        }

        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
        {
            MaxPayloadBytes = 4096,
            MaxInlineDecodedBytes = 10,
        });
        var activation = await fixture.PublishAsync(
            handler,
            callerContract: contract,
            providerContract: contract);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        if (surface == "stream-event")
        {
            await fixture.GrantAsync(SunderRpcProtocol.SubscribeAction);
        }
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        SunderRpcException exhausted;
        if (surface == "stream-event")
        {
            await using var events = client.SubscribeAsync(
                    endpoint,
                    "messages",
                    "watch",
                    exact)
                .GetAsyncEnumerator();
            exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await events.MoveNextAsync().AsTask());
        }
        else
        {
            exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await client.InvokeAsync(
                    endpoint,
                    "messages",
                    "send",
                    surface == "request" ? over : exact));
        }

        Assert.True(exhausted.IsHostAuthenticated);
        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
        Assert.Equal(expectedCode, exhausted.Error.Code);
        Assert.False(RuntimeRpcFailureContextStore.TryGet(exhausted, out _));
        Assert.Equal(surface == "request" ? 0 : 1, dispatches);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);

        handler.Unary = static (_, _) => ValueTask.FromResult(InlinePayload(5, 5, "one"));
        var valid = await client.InvokeAsync(endpoint, "messages", "send", exact);
        Assert.Equal("one", valid.GetProperty("choice").GetProperty("kind").GetString());
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    [Fact]
    public async Task GenericJsonByteLimitRunsBeforeInlineDecodedLimit()
    {
        var contract = InlineDecodedContract();
        var dispatches = 0;
        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
        {
            MaxPayloadBytes = 64,
            MaxInlineDecodedBytes = 1,
        });
        var activation = await fixture.PublishAsync(
            new TestHandler
            {
                Unary = (_, _) =>
                {
                    Interlocked.Increment(ref dispatches);
                    return ValueTask.FromResult(InlinePayload(0, 0, "one"));
                },
            },
            callerContract: contract,
            providerContract: contract);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        var exhausted = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                InlinePayload(20, 20, "one")));

        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, exhausted.Error.Kind);
        Assert.Equal("rpc.request.byte-limit", exhausted.Error.Code);
        Assert.Equal(0, dispatches);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    [Fact]
    public async Task InProcessInvalidOutputPersistsAggregateInlineMeasurementToRuntimeLog()
    {
        var contract = InlineDecodedContract();
        var invalid = InlinePayload(5, 5, "unknown");
        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
        {
            MaxPayloadBytes = 4096,
            MaxInlineDecodedBytes = 10,
        });
        var activation = await fixture.PublishAsync(
            new TestHandler
            {
                Unary = (_, _) => ValueTask.FromResult(invalid),
            },
            callerContract: contract,
            providerContract: contract);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var loggedBeforeRetirement = false;
        fixture.OwnerLogger.OnLog = entry =>
        {
            if (entry.EventId.Name == "RuntimeRpcProviderFault")
            {
                loggedBeforeRetirement = Assert.Single(fixture.Catalog.GetSnapshot().Providers).State
                                         == SunderRpcProviderState.Active;
            }
        };

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(endpoint, "messages", "send", InlinePayload(5, 5, "one")));

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.True(RuntimeRpcFailureContextStore.TryGet(failure, out var diagnostic));
        Assert.Equal(10, diagnostic.ValueInlineDecodedBytes);
        var runtimeLog = Assert.Single(
            fixture.OwnerLogger.Entries,
            entry => entry.EventId.Name == "RuntimeRpcProviderFault");
        Assert.True(loggedBeforeRetirement);
        Assert.Equal(LogLevel.Error, runtimeLog.Level);
        Assert.Null(runtimeLog.Exception);
        Assert.Contains("valueInlineDecodedBytes=10", runtimeLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("primary", runtimeLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("choice", runtimeLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown", runtimeLog.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
    }

    [Fact]
    public void SemanticValidationDiagnosticUsesStructuredValidatorCodeAndPath()
    {
        var contract = Contract(requestMaximumLength: 3);
        Assert.False(contract.IsValidDetailed(
            "#/$defs/Request",
            JsonSerializer.SerializeToElement(new { message = "too long" }),
            out _,
            out var failure));

        var exception = RuntimeRpcSemanticValidationException.Schema(
            failure,
            new RuntimeRpcValueMeasurement(1, 1, 1, 1));

        Assert.Equal("string-length", exception.ValidationCode);
        Assert.Equal("$.*", exception.ValidationPath);
    }

    [Fact]
    public async Task ProviderContentLimit_IsResourceExhaustedWithoutProviderFault()
    {
        await using var fixture = new RpcFixture(transportPolicy: new RuntimeTransportPolicyOptions
        {
            MaxRpcContentBytes = 3,
        });
        var activation = await fixture.PublishAsync(new TestHandler
        {
            UnaryWithContext = async (context, _request, cancellationToken) =>
            {
                await using var content = new MemoryStream([1, 2, 3, 4]);
                _ = await context.RegisterContentAsync(
                    content,
                    new SunderRpcContentRegistrationOptions(
                        "application/octet-stream",
                        "content.bin",
                        content.Length),
                    cancellationToken);
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "a" })));

        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, failure.Error.Kind);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    [Fact]
    public async Task StreamEventLimit_FailsCallWithoutFaultingProvider()
    {
        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions { MaxStreamEvents = 1 });
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Stream = static (_, _) => Values(
                JsonSerializer.SerializeToElement(new { accepted = true }),
                JsonSerializer.SerializeToElement(new { accepted = true })),
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.SubscribeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        await using var stream = client.SubscribeAsync(
            endpoint,
            "messages",
            "watch",
            JsonSerializer.SerializeToElement(new { message = "hello" })).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await stream.MoveNextAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, failure.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, failure.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, failure.Error.RetrySafety);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    private static SunderRpcContractDescriptor InlineDecodedContract()
        => SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.rpc",
              "version":"1.0.0",
              "errors":{"messages.rejected":{"description":"The message was rejected.","detailsSchema":{"$ref":"#/$defs/Payload"}}},
              "services":{"messages":{"methods":{
                "send":{"kind":"unary","idempotency":"none","errors":["messages.rejected"],"requestSchema":{"$ref":"#/$defs/Payload"},"responseSchema":{"$ref":"#/$defs/Payload"}},
                "watch":{"kind":"server-stream","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Payload"},"eventSchema":{"$ref":"#/$defs/Payload"}}
              }}},
              "$defs":{
                "Bytes":{"type":"string","contentEncoding":"base64"},
                "BytesAlias":{"$ref":"#/$defs/Bytes"},
                "ChoiceOne":{"type":"object","properties":{"kind":{"type":"string","const":"one"},"data":{"$ref":"#/$defs/Bytes"}},"required":["kind","data"],"additionalProperties":false},
                "ChoiceTwo":{"type":"object","properties":{"kind":{"type":"string","const":"two"},"data":{"$ref":"#/$defs/BytesAlias"}},"required":["kind","data"],"additionalProperties":false},
                "Encoded":{"type":"object","properties":{"kind":{"type":"string","const":"encoded"},"data":{"$ref":"#/$defs/Bytes"}},"required":["kind","data"],"additionalProperties":false},
                "Text":{"type":"object","properties":{"kind":{"type":"string","const":"text"},"data":{"type":"string"}},"required":["kind","data"],"additionalProperties":false},
                "Payload":{"type":"object","properties":{"primary":{"$ref":"#/$defs/BytesAlias"},"choice":{"oneOf":[{"$ref":"#/$defs/ChoiceOne"},{"$ref":"#/$defs/ChoiceTwo"}]},"representation":{"oneOf":[{"$ref":"#/$defs/Encoded"},{"$ref":"#/$defs/Text"}]}},"required":["primary","choice","representation"],"additionalProperties":false}
              }
            }
            """));

    private static JsonElement InlinePayload(
        int primaryBytes,
        int choiceBytes,
        string choiceKind)
        => JsonSerializer.SerializeToElement(new
        {
            primary = Convert.ToBase64String(new byte[primaryBytes]),
            choice = new
            {
                kind = choiceKind,
                data = Convert.ToBase64String(new byte[choiceBytes]),
            },
            representation = new
            {
                kind = "text",
                data = Convert.ToBase64String(new byte[100]),
            },
        });
}
