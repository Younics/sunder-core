using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;
using static Sunder.Runtime.Host.Tests.RuntimeRpcTestPackages;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcAuthorizationTests
{
    [Fact]
    public async Task Permission_DefaultDenyGrantDomainErrorAndSpoofProtection()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler());
        var client = fixture.CreateClient(activation.Caller);

        var denied = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.DiscoverAsync("example.rpc"));
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, denied.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, denied.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, denied.Error.RetrySafety);

        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var provider = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers);
        Assert.Equal(Contract().Sha256, provider.DescriptorSha256);
        Assert.Equal(Contract().AbiSha256, provider.AbiSha256);
        var endpoint = provider.Endpoint;
        var response = await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }));
        Assert.True(response.GetProperty("accepted").GetBoolean());

        var spoofed = fixture.CreateClient(new RuntimeRpcCallerStamp("caller.package", Guid.NewGuid()));
        var spoofFailure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await spoofed.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, spoofFailure.Error.Kind);

        activation.Handler.Unary = static (_, _) => throw new SunderRpcException(
            new SunderRpcError(SunderRpcErrorKind.Domain, "messages.rejected", "not accepted"));
        var domain = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));
        Assert.Equal(SunderRpcErrorKind.Domain, domain.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, domain.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, domain.Error.RetrySafety);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndeclaredDomainError_FaultsProviderForUnaryAndStream(bool stream)
    {
        await using var fixture = new RpcFixture();
        var handler = new TestHandler();
        if (stream)
        {
            handler.Stream = static (_, _) => UndeclaredDomainFailure();
        }
        else
        {
            handler.Unary = static (_, _) => throw UndeclaredDomainException();
        }
        var activation = await fixture.PublishAsync(handler);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(stream ? SunderRpcProtocol.SubscribeAction : SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var request = JsonSerializer.SerializeToElement(new { message = "hello" });

        SunderRpcException failure;
        if (stream)
        {
            await using var subscription = client.SubscribeAsync(
                endpoint,
                "messages",
                "watch",
                request).GetAsyncEnumerator();
            failure = await Assert.ThrowsAsync<SunderRpcException>(() => subscription.MoveNextAsync().AsTask());
        }
        else
        {
            failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                request).AsTask());
        }

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.Equal("rpc.provider.undeclared-domain-error", failure.Error.Code);
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(PackageReadinessState.Failed, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
    }

    [Fact]
    public async Task DeclaredDomainErrorDetails_AreOptionalValidatedAndPreserved()
    {
        var descriptor = Contract(declareErrorDetails: true);
        await using var fixture = new RpcFixture();
        var handler = new TestHandler
        {
            Unary = static (_, _) => throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Domain,
                "messages.rejected",
                "not accepted")),
        };
        var activation = await fixture.PublishAsync(
            handler,
            callerContract: descriptor,
            providerContract: descriptor);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var request = JsonSerializer.SerializeToElement(new { message = "hello" });

        var withoutDetails = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            request).AsTask());
        Assert.Equal(SunderRpcErrorKind.Domain, withoutDetails.Error.Kind);
        Assert.Null(withoutDetails.Error.Details);

        handler.Unary = static (_, _) => throw new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "messages.rejected",
            "not accepted",
            details: JsonSerializer.SerializeToElement(new { reason = "duplicate" })));
        var withDetails = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            request).AsTask());

        Assert.Equal("duplicate", withDetails.Error.Details!.Value.GetProperty("reason").GetString());
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UndeclaredOrInvalidDomainErrorDetails_FaultProvider(
        bool declareErrorDetails,
        bool useInvalidDetails)
    {
        var descriptor = Contract(declareErrorDetails: declareErrorDetails);
        var details = useInvalidDetails
            ? JsonSerializer.SerializeToElement(new { reason = 42 })
            : JsonSerializer.SerializeToElement(new { reason = "not-declared" });
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(
            new TestHandler
            {
                Unary = (_, _) => throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Domain,
                    "messages.rejected",
                    "not accepted",
                    details: details)),
            },
            callerContract: descriptor,
            providerContract: descriptor);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.Equal("rpc.provider.invalid-error-details", failure.Error.Code);
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
    }

    [Fact]
    public async Task DevelopmentCaller_UsesEveryDeclaredActionWithoutDurableGrant()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            callerSourceKind: PackageSourceKind.Dev);
        var client = fixture.CreateClient(activation.Caller);

        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var response = await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }));

        Assert.True(response.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task IdempotentUnary_DispatchedFailureIsRetrySafeButIsNotRetried()
    {
        await using var fixture = new RpcFixture();
        var attempts = 0;
        var descriptor = Contract(unaryIdempotency: "idempotent");
        var activation = await fixture.PublishAsync(
            new TestHandler
            {
                Unary = (_, _) =>
                {
                    attempts++;
                    throw new SunderRpcException(new SunderRpcError(
                        SunderRpcErrorKind.Domain,
                        "messages.rejected",
                        "not accepted"));
                },
            },
            callerContract: descriptor,
            providerContract: descriptor);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })));

        Assert.Equal(1, attempts);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, failure.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, failure.Error.RetrySafety);
    }

    [Fact]
    public async Task Matching_RejectsDescriptorDriftEvenWithEqualVersionAndAbi()
    {
        await using var fixture = new RpcFixture();
        var callerContract = Contract(requestTitle: "Caller-local annotation");
        var providerContract = Contract();
        Assert.NotEqual(providerContract.Sha256, callerContract.Sha256);
        Assert.Equal(providerContract.AbiSha256, callerContract.AbiSha256);
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            callerContract: callerContract);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);

        Assert.Empty((await client.DiscoverAsync("example.rpc")).Providers);
        var endpoint = Assert.Single(fixture.Catalog.GetSnapshot().Providers).Endpoint;
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(endpoint, "messages", "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());
        Assert.Equal(SunderRpcDispatchState.NotDispatched, failure.Error.DispatchState);
    }

    [Fact]
    public async Task Matching_RejectsAbiAndCompleteVersionMismatches()
    {
        await using (var fixture = new RpcFixture())
        {
            var callerContract = Contract(requestMaximumLength: 127);
            Assert.NotEqual(Contract().AbiSha256, callerContract.AbiSha256);
            var activation = await fixture.PublishAsync(
                new TestHandler(),
                callerContract: callerContract);
            await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
            await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
            var client = fixture.CreateClient(activation.Caller);
            var endpoint = Assert.Single(fixture.Catalog.GetSnapshot().Providers).Endpoint;

            Assert.Empty((await client.DiscoverAsync("example.rpc")).Providers);
            Assert.Null(await client.GetProviderAsync(endpoint));
            var denied = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());
            Assert.Equal(SunderRpcErrorKind.PermissionDenied, denied.Error.Kind);
        }

        await using (var fixture = new RpcFixture())
        {
            var activation = await fixture.PublishAsync(
                new TestHandler(),
                callerContract: Contract(version: "1.0.0+caller"));
            await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);

            Assert.Empty((await fixture.CreateClient(activation.Caller)
                .DiscoverAsync("example.rpc")).Providers);
        }
    }

    [Fact]
    public async Task CallerRole_SelectsOnlyRoleLocalUses()
    {
        await using (var fixture = new RpcFixture())
        {
            var activation = await fixture.PublishAsync(
                new TestHandler(),
                webCaller: true,
                runtimeCallerUsesContract: false,
                appCallerUsesContract: true);
            await fixture.GrantAsync(
                "caller.package",
                new string('a', 64),
                SunderRpcProtocol.DiscoverAction,
                SunderPackageFormat.AppHostRole);
            var runtimeDenied = await Assert.ThrowsAsync<SunderRpcException>(() =>
                fixture.CreateClient(activation.Caller).DiscoverAsync("example.rpc").AsTask());
            Assert.Equal(SunderRpcErrorKind.PermissionDenied, runtimeDenied.Error.Kind);

            var app = await fixture.OpenAppCallerAsync();
            await fixture.GrantAsync(
                "caller.package",
                app.ManifestSha256,
                SunderRpcProtocol.DiscoverAction,
                SunderPackageFormat.AppHostRole);
            Assert.Single((await fixture.Broker.DiscoverAppAsync(
                app.Session.SessionId,
                "example.rpc",
                CancellationToken.None)).Providers);
        }

        await using (var fixture = new RpcFixture())
        {
            var activation = await fixture.PublishAsync(
                new TestHandler(),
                webCaller: true,
                runtimeCallerUsesContract: true,
                appCallerUsesContract: false);
            await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
            Assert.Single((await fixture.CreateClient(activation.Caller)
                .DiscoverAsync("example.rpc")).Providers);

            var app = await fixture.OpenAppCallerAsync();
            var appDenied = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Broker
                .DiscoverAppAsync(app.Session.SessionId, "example.rpc", CancellationToken.None)
                .AsTask());
            Assert.Equal(SunderRpcErrorKind.PermissionDenied, appDenied.Error.Kind);
        }
    }

    [Fact]
    public async Task OwnerAudience_IsEnforcedForDiscoveryGetInvokeAndSubscribe()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            providerUsesContract: true,
            providerAudience: SunderPackageFormat.OwnerProviderAudience);
        foreach (var action in new[]
                 {
                     SunderRpcProtocol.DiscoverAction,
                     SunderRpcProtocol.InvokeAction,
                     SunderRpcProtocol.SubscribeAction,
                 })
        {
            await fixture.GrantAsync(action);
        }
        var endpoint = Assert.Single(fixture.Catalog.GetSnapshot().Providers).Endpoint;
        var request = JsonSerializer.SerializeToElement(new { message = "hello" });
        var publicCaller = fixture.CreateClient(activation.Caller);

        Assert.Empty((await publicCaller.DiscoverAsync("example.rpc")).Providers);
        Assert.Null(await publicCaller.GetProviderAsync(endpoint));
        var invokeDenied = await Assert.ThrowsAsync<SunderRpcException>(() => publicCaller.InvokeAsync(
            endpoint,
            "messages",
            "send",
            request).AsTask());
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, invokeDenied.Error.Kind);
        await using (var subscription = publicCaller.SubscribeAsync(
                         endpoint,
                         "messages",
                         "watch",
                         request).GetAsyncEnumerator())
        {
            var subscribeDenied = await Assert.ThrowsAsync<SunderRpcException>(() =>
                subscription.MoveNextAsync().AsTask());
            Assert.Equal(SunderRpcErrorKind.PermissionDenied, subscribeDenied.Error.Kind);
        }

        var owner = fixture.CreateClient(activation.Provider);
        Assert.Equal(endpoint, Assert.Single((await owner.DiscoverAsync("example.rpc")).Providers).Endpoint);
        Assert.True((await owner.InvokeAsync(endpoint, "messages", "send", request))
            .GetProperty("accepted").GetBoolean());
        await foreach (var item in owner.SubscribeAsync(endpoint, "messages", "watch", request))
        {
            Assert.True(item.GetProperty("accepted").GetBoolean());
        }
    }

    [Fact]
    public async Task Revocation_CancelsActiveCallWithoutFaultingProvider()
    {
        await using var fixture = new RpcFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHandler
        {
            Unary = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        };
        var activation = await fixture.PublishAsync(handler);
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
        await fixture.RevokeAsync(SunderRpcProtocol.InvokeAction);

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        Assert.Equal(SunderRpcErrorKind.Cancelled, failure.Error.Kind);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, failure.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, failure.Error.RetrySafety);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    [Fact]
    public async Task Revocation_RejectsResponseFromProviderThatIgnoresCancellation()
    {
        await using var fixture = new RpcFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Unary = async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
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

        await fixture.RevokeAsync(SunderRpcProtocol.InvokeAction);
        release.SetResult();

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        Assert.Equal(SunderRpcErrorKind.Cancelled, failure.Error.Kind);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    [Fact]
    public async Task Broker_EnforcesConcurrencyDeadlineAndNestedDepthWithoutFaultingProvider()
    {
        await using (var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
                     {
                         MaxConcurrentCallsPerCaller = 1,
                         MaxNestedCallDepth = 3,
                     }))
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var activation = await fixture.PublishAsync(new TestHandler
            {
                Unary = async (_, cancellationToken) =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return JsonSerializer.SerializeToElement(new { accepted = true });
                },
            });
            await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
            await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
            var client = fixture.CreateClient(activation.Caller);
            var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
            var first = client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "one" })).AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var limited = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await client.InvokeAsync(
                    endpoint,
                    "messages",
                    "send",
                    JsonSerializer.SerializeToElement(new { message = "two" })));
            Assert.Equal(SunderRpcErrorKind.ResourceExhausted, limited.Error.Kind);
            release.TrySetResult();
            await first;
        }

        await using (var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
                     {
                         DefaultDeadline = TimeSpan.FromMilliseconds(100),
                     }))
        {
            var activation = await fixture.PublishAsync(new TestHandler
            {
                Unary = async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return JsonSerializer.SerializeToElement(new { accepted = true });
                },
            });
            await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
            await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
            var client = fixture.CreateClient(activation.Caller);
            var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
            var timeout = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await client.InvokeAsync(
                    endpoint,
                    "messages",
                    "send",
                    JsonSerializer.SerializeToElement(new { message = "wait" }),
                    new SunderRpcCallOptions(DateTimeOffset.UtcNow.AddHours(1))));
            Assert.Equal(SunderRpcErrorKind.DeadlineExceeded, timeout.Error.Kind);
            Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
        }

        await using (var fixture = new RpcFixture(new RuntimeRpcPolicyOptions { MaxNestedCallDepth = 2 }))
        {
            var handler = new TestHandler();
            var activation = await fixture.PublishAsync(handler, providerUsesContract: true);
            await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
            await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
            await fixture.GrantAsync("provider.package", SunderRpcProtocol.InvokeAction);
            var callerClient = fixture.CreateClient(activation.Caller);
            var providerClient = fixture.CreateClient(activation.Provider);
            var endpoint = Assert.Single((await callerClient.DiscoverAsync("example.rpc")).Providers).Endpoint;
            handler.Unary = (request, cancellationToken) => providerClient.InvokeAsync(
                endpoint,
                "messages",
                "send",
                request,
                cancellationToken: cancellationToken);

            var depth = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await callerClient.InvokeAsync(
                    endpoint,
                    "messages",
                    "send",
                    JsonSerializer.SerializeToElement(new { message = "recursive" })));
            Assert.Equal(SunderRpcErrorKind.ResourceExhausted, depth.Error.Kind);
            Assert.Equal("rpc.call.depth-limit", depth.Error.Code);
            Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, depth.Error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Unsafe, depth.Error.RetrySafety);
            Assert.Contains("depth 3", depth.Error.Message);
            Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);

            handler.Unary = static (_, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
            var healthy = await callerClient.InvokeAsync(endpoint, "messages", "send",
                JsonSerializer.SerializeToElement(new { message = "after-recursion" }));
            Assert.True(healthy.GetProperty("accepted").GetBoolean());
        }

        // With the generous default nesting guard, actual simultaneous-call
        // capacity can be reached first. It must still unwind without retiring
        // the provider or leaking the leases needed by a subsequent request.
        await using (var fixture = new RpcFixture())
        {
            var handler = new TestHandler();
            var activation = await fixture.PublishAsync(handler, providerUsesContract: true);
            await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
            await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
            await fixture.GrantAsync("provider.package", SunderRpcProtocol.InvokeAction);
            var callerClient = fixture.CreateClient(activation.Caller);
            var providerClient = fixture.CreateClient(activation.Provider);
            var endpoint = Assert.Single((await callerClient.DiscoverAsync("example.rpc")).Providers).Endpoint;
            SunderRpcException? childRejection = null;
            handler.Unary = async (request, token) =>
            {
                await Task.Yield();
                try { return await providerClient.InvokeAsync(endpoint, "messages", "send", request, cancellationToken: token); }
                catch (SunderRpcException failure)
                {
                    childRejection = failure;
                    return JsonSerializer.SerializeToElement(new { accepted = true });
                }
            };

            var handled = await callerClient.InvokeAsync(endpoint, "messages", "send",
                JsonSerializer.SerializeToElement(new { message = "recursive-capacity" }));
            Assert.True(handled.GetProperty("accepted").GetBoolean());
            Assert.NotNull(childRejection);
            Assert.Equal("rpc.caller.concurrency-limit", childRejection.Error.Code);
            Assert.Equal(SunderRpcDispatchState.NotDispatched, childRejection.Error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Safe, childRejection.Error.RetrySafety);
            Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
            handler.Unary = static (_, _) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
            Assert.True((await callerClient.InvokeAsync(endpoint, "messages", "send",
                JsonSerializer.SerializeToElement(new { message = "after-capacity" }))).GetProperty("accepted").GetBoolean());
        }
    }

    [Fact]
    public void InvocationContext_HasNoPublicForgeableConstructor()
        => Assert.Empty(typeof(SunderRpcInvocationContext).GetConstructors());

    private static async IAsyncEnumerable<JsonElement> UndeclaredDomainFailure()
    {
        yield return await Task.FromException<JsonElement>(UndeclaredDomainException());
    }

    private static SunderRpcException UndeclaredDomainException()
        => new(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "messages.undeclared",
            "This error is not declared by the method."));
}
