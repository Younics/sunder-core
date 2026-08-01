using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
using Sunder.Package.Hosting;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Infrastructure.Storage;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeRpcBrokerTests
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

        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
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
        Assert.Equal(SunderRpcErrorKind.Unavailable, spoofFailure.Error.Kind);

        activation.Handler.Unary = static (_, _) => throw new SunderRpcException(
            new SunderRpcError(SunderRpcErrorKind.Domain, "messages.rejected", "not accepted"));
        var domain = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));
        Assert.Equal(SunderRpcErrorKind.Domain, domain.Error.Kind);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
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
    public async Task AppCallerSession_IsManifestAndGenerationFencedAndCloseCancelsActiveCall()
    {
        await using var fixture = new RpcFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Unary = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        }, webCaller: true);
        var (session, manifestSha256, target) = await fixture.OpenAppCallerAsync();

        var denied = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await fixture.Broker.DiscoverAppAsync(session.SessionId, "example.rpc", CancellationToken.None));
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, denied.Error.Kind);

        await fixture.GrantAsync(
            "caller.package",
            manifestSha256,
            SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(
            "caller.package",
            manifestSha256,
            SunderRpcProtocol.InvokeAction);
        var endpoint = Assert.Single((await fixture.Broker.DiscoverAppAsync(
            session.SessionId,
            "example.rpc",
            CancellationToken.None)).Providers).Endpoint;
        var call = fixture.Broker.InvokeAppAsync(
            session.SessionId,
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "wait" }),
            options: null,
            CancellationToken.None).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.AppSessions.Close(session.SessionId);

        var cancelled = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        Assert.Equal(SunderRpcErrorKind.Cancelled, cancelled.Error.Kind);
        var retired = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await fixture.Broker.DiscoverAppAsync(session.SessionId, "example.rpc", CancellationToken.None));
        Assert.Equal(SunderRpcErrorKind.Unavailable, retired.Error.Kind);
        await Assert.ThrowsAsync<RuntimeStaleGenerationException>(() => fixture.AppSessions.OpenAsync(
            new RuntimeRpcAppSessionOpenRequest(
                "caller.package",
                "1.0.0",
                new string('f', 64),
                target,
                fixture.Owner.Generation,
                Guid.NewGuid()),
            CancellationToken.None));
    }

    [Fact]
    public async Task AppCallerSession_CandidateAppGenerationDoesNotRetireCommittedSession()
    {
        await using var fixture = new RpcFixture();
        await fixture.PublishAsync(new TestHandler(), webCaller: true);
        var first = await fixture.OpenAppCallerAsync();
        await fixture.GrantAsync(
            "caller.package",
            first.ManifestSha256,
            SunderRpcProtocol.DiscoverAction);

        var second = await fixture.OpenAppCallerAsync();

        Assert.Single((await fixture.Broker.DiscoverAppAsync(
            first.Session.SessionId,
            "example.rpc",
            CancellationToken.None)).Providers);
        Assert.Single((await fixture.Broker.DiscoverAppAsync(
            second.Session.SessionId,
            "example.rpc",
            CancellationToken.None)).Providers);

        fixture.AppSessions.Close(second.Session.SessionId);

        Assert.Single((await fixture.Broker.DiscoverAppAsync(
            first.Session.SessionId,
            "example.rpc",
            CancellationToken.None)).Providers);
    }

    [Fact]
    public async Task RetirementCallbacksCannotInterruptAppSessionOrCatalogRetirement()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler(), webCaller: true);
        var app = await fixture.OpenAppCallerAsync();
        var appCaller = fixture.AppSessions.GetRequired(app.Session.SessionId);
        using var appCallback = appCaller.RetirementToken.Register(
            static () => throw new InvalidOperationException("app callback failed"));
        Assert.True(fixture.Catalog.TryGetPackage(
            "caller.package",
            activation.Caller.ActivationId,
            out var runtimeCaller));
        using var runtimeCallback = runtimeCaller!.RetirementToken.Register(
            static () => throw new InvalidOperationException("runtime callback failed"));

        fixture.AppSessions.Close(app.Session.SessionId);
        Assert.True(fixture.Catalog.DeactivatePackage(
            "caller.package",
            activation.Caller.ActivationId,
            faulted: false));
    }

    [Fact]
    public async Task Catalog_StaleSessionActivationCannotRegressPublishedGeneration()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler());
        var newerGeneration = fixture.Owner.Generation + 1;
        fixture.Catalog.ActivateSession(activation.Session, newerGeneration);
        var newerSnapshot = fixture.Catalog.GetSnapshot();

        fixture.Catalog.ActivateSession(activation.Session, newerGeneration - 1);

        var current = fixture.Catalog.GetSnapshot();
        Assert.Equal(newerGeneration, fixture.Catalog.SessionGeneration);
        Assert.Equal(newerSnapshot.Revision, current.Revision);
        Assert.Equal(
            newerSnapshot.Providers.Select(static provider => provider.Endpoint),
            current.Providers.Select(static provider => provider.Endpoint));
    }

    [Fact]
    public async Task Catalog_RebuildPreservesRetirementCallbacksForLaterPackageDeactivation()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler());
        Assert.True(fixture.Catalog.TryGetPackage(
            "caller.package",
            activation.Caller.ActivationId,
            out var originalCaller));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var callback = originalCaller!.RetirementToken.Register(() =>
        {
            callbackStarted.TrySetResult();
            releaseCallback.Wait();
        });

        try
        {
            fixture.Catalog.ActivateSession(activation.Session, fixture.Owner.Generation + 1);
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var retirement = fixture.Catalog.DeactivatePackageWithRetirement(
                "caller.package",
                activation.Caller.ActivationId,
                faulted: true);

            Assert.False(retirement.IsCompleted);
            releaseCallback.Set();
            await retirement.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseCallback.Set();
        }
    }

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

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await client.InvokeAsync(
                endpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
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

    [Fact]
    public async Task Replacement_UsesNewEndpointAndOldReferenceNeverInvokesReplacement()
    {
        await using var fixture = new RpcFixture();
        var first = await fixture.PublishAsync(new TestHandler());
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var firstClient = fixture.CreateClient(first.Caller);
        var oldEndpoint = Assert.Single((await firstClient.DiscoverAsync("example.rpc")).Providers).Endpoint;

        var second = await fixture.PublishAsync(new TestHandler());
        var secondClient = fixture.CreateClient(second.Caller);
        var newEndpoint = Assert.Single((await secondClient.DiscoverAsync("example.rpc")).Providers).Endpoint;

        Assert.NotEqual(oldEndpoint, newEndpoint);
        var stale = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await secondClient.InvokeAsync(
                oldEndpoint,
                "messages",
                "send",
                JsonSerializer.SerializeToElement(new { message = "hello" })));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        Assert.True((await secondClient.InvokeAsync(
            newEndpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }))).GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task SessionRetirement_CancelsCallAndDrainsBeforeReplacementDisposal()
    {
        await using var fixture = new RpcFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await fixture.PublishAsync(new TestHandler
        {
            Unary = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    stopped.TrySetResult();
                }
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(first.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var call = client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "wait" })).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var replacement = fixture.PublishAsync(new TestHandler());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await replacement;
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        Assert.Equal(SunderRpcErrorKind.Cancelled, failure.Error.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerAndCalleeRetirement_CancelExactActiveCall(bool retireCaller)
    {
        await using var fixture = new RpcFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Unary = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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

        var stamp = retireCaller ? activation.Caller : activation.Provider;
        Assert.True(fixture.Catalog.DeactivatePackage(
            stamp.PackageId,
            stamp.ActivationId,
            faulted: false));
        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        Assert.Equal(
            retireCaller ? SunderRpcErrorKind.Cancelled : SunderRpcErrorKind.StaleEndpoint,
            failure.Error.Kind);
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
            Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
        }
    }

    [Fact]
    public async Task CatalogWatch_ConsumesAndReleasesCallerConcurrencyBudget()
    {
        await using var fixture = new RpcFixture(new RuntimeRpcPolicyOptions
        {
            MaxConcurrentCallsPerCaller = 1,
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
            var limited = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await client.InvokeAsync(
                    endpoint,
                    "messages",
                    "send",
                    JsonSerializer.SerializeToElement(new { message = "blocked" })));
            Assert.Equal(SunderRpcErrorKind.ResourceExhausted, limited.Error.Kind);
        }

        var response = await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "released" }));
        Assert.True(response.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task PermissionStore_IsDurableFencedAndCancelsOnDenial()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        var paths = new RuntimePackagePaths(root);
        try
        {
            CancellationToken revocation;
            using (var store = new RuntimeRpcPermissionStore(paths))
            {
                await store.SetAsync(
                    "caller.package",
                    "1.0.0",
                    new string('a', 64),
                    "example.rpc",
                    "invoke",
                    RuntimeRpcPermissionState.Granted,
                    CancellationToken.None);
                Assert.True(store.TryAcquire(
                    "caller.package",
                    "1.0.0",
                    new string('a', 64),
                    "example.rpc",
                    "invoke",
                    out var lease));
                revocation = lease!.RevocationToken;
                Assert.False(store.TryAcquire(
                    "caller.package",
                    "2.0.0",
                    new string('a', 64),
                    "example.rpc",
                    "invoke",
                    out _));
                Assert.False(store.TryAcquire(
                    "caller.package",
                    "1.0.0",
                    new string('b', 64),
                    "example.rpc",
                    "invoke",
                    out _));
                await store.SetAsync(
                    "caller.package",
                    "1.0.0",
                    new string('a', 64),
                    "example.rpc",
                    "invoke",
                    RuntimeRpcPermissionState.Denied,
                    CancellationToken.None);
                Assert.True(revocation.IsCancellationRequested);
            }
            using var reloaded = new RuntimeRpcPermissionStore(paths);
            Assert.False(reloaded.TryAcquire(
                "caller.package",
                "1.0.0",
                new string('a', 64),
                "example.rpc",
                "invoke",
                out _));
            Assert.Contains(
                reloaded.GetSnapshot([]).Permissions,
                permission => permission.State == RuntimeRpcPermissionState.Denied);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionStore_InstallPolicyGrantsDeclarationsAndPreservesExactDenialUntilUpdate()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        var paths = new RuntimePackagePaths(root);
        var subject = PermissionSubject(PackageSourceKind.Installed);
        try
        {
            using (var store = new RuntimeRpcPermissionStore(paths))
            {
                await store.GrantDeclaredInstalledActionsAsync([subject], CancellationToken.None);

                Assert.Equal(1, store.Revision);
                Assert.All(
                    new[] { "discover", "invoke", "subscribe" },
                    action => Assert.True(store.TryAcquire(
                        subject.PackageId,
                        subject.PackageVersion,
                        subject.ManifestSha256,
                        "example.rpc",
                        action,
                        out _)));

                await store.SetAsync(
                    subject.PackageId,
                    subject.PackageVersion,
                    subject.ManifestSha256,
                    "example.rpc",
                    "invoke",
                    RuntimeRpcPermissionState.Denied,
                    CancellationToken.None);
                var deniedRevision = store.Revision;
                await store.GrantDeclaredInstalledActionsAsync([subject], CancellationToken.None);

                Assert.False(store.TryAcquire(
                    subject.PackageId,
                    subject.PackageVersion,
                    subject.ManifestSha256,
                    "example.rpc",
                    "invoke",
                    out _));
                Assert.Equal(deniedRevision, store.Revision);

                var updatedSubject = subject with
                {
                    PackageVersion = "2.0.0",
                    ManifestSha256 = new string('b', 64),
                };
                await store.GrantDeclaredInstalledActionsAsync([updatedSubject], CancellationToken.None);
                Assert.True(store.TryAcquire(
                    updatedSubject.PackageId,
                    updatedSubject.PackageVersion,
                    updatedSubject.ManifestSha256,
                    "example.rpc",
                    "invoke",
                    out _));
                subject = updatedSubject;
            }

            using var reloaded = new RuntimeRpcPermissionStore(paths);
            Assert.All(
                reloaded.GetSnapshot([subject]).Permissions,
                permission =>
                {
                    Assert.Equal(RuntimeRpcPermissionState.Granted, permission.State);
                    Assert.True(permission.Effective);
                });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PermissionStore_DevelopmentPolicyIsEffectiveWithoutDurableRecords()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        var paths = new RuntimePackagePaths(root);
        try
        {
            using var store = new RuntimeRpcPermissionStore(paths);
            var snapshot = store.GetSnapshot([PermissionSubject(PackageSourceKind.Dev)]);

            Assert.Equal(0, snapshot.Revision);
            Assert.Equal(3, snapshot.Permissions.Count);
            Assert.All(snapshot.Permissions, permission =>
            {
                Assert.Equal(RuntimeRpcPermissionState.Granted, permission.State);
                Assert.True(permission.Effective);
                Assert.Null(permission.UpdatedAtUtc);
            });
            Assert.False(File.Exists(paths.RpcPermissionFilePath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionService_RejectsDurableDevelopmentPermissionMutation()
    {
        await using var fixture = new RpcFixture();
        await fixture.PublishAsync(
            new TestHandler(),
            webCaller: true,
            callerSourceKind: PackageSourceKind.Dev);

        await Assert.ThrowsAsync<RuntimeValidationException>(() => fixture.SetPermissionAsync(
            new RuntimeRpcPermissionUpdateRequest(
                "caller.package",
                "example.rpc",
                SunderRpcProtocol.InvokeAction),
            RuntimeRpcPermissionState.Denied));
        Assert.False(File.Exists(fixture.PermissionFilePath));
    }

    [Fact]
    public async Task Catalog_ReportsReplayGapAndEvictsSlowSubscriber()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler());
        var subscription = fixture.Catalog.Subscribe(
            fixture.Catalog.Revision,
            fixture.Catalog.GetSnapshot().Sequence);
        await using (subscription)
        {
            for (var generation = 2; generation < 80; generation++)
            {
                fixture.Catalog.ActivateSession(activation.Session, generation);
            }
            await Assert.ThrowsAsync<SlowRuntimeStreamConsumerException>(() =>
                DrainAsync(subscription.Reader));
        }

        var page = fixture.Catalog.GetEventPage(afterRevision: 1, afterSequence: 1);
        Assert.True(page.ResetRequired);
        Assert.Empty(page.Events);
    }

    [Fact]
    public async Task Catalog_DuplicateProviderIdsFailAtomically()
    {
        var descriptor = Contract();
        var first = CreatePackage("first.package", descriptor, new TestHandler(), usesContract: false);
        var second = CreatePackage("second.package", descriptor, new TestHandler(), usesContract: false);
        var session = CreateSession(first, second);
        using var catalog = new RuntimeRpcCatalog();

        var exception = Assert.Throws<InvalidOperationException>(() => catalog.ActivateSession(session, 1));
        Assert.Contains("more than one", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, catalog.Revision);
        Assert.Empty(catalog.GetSnapshot().Providers);
        await session.DisposeAsync();
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
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);
    }

    [Fact]
    public async Task ContentReference_ValidatesAudienceGenerationHashAndUseCount()
    {
        var root = CreateRoot();
        try
        {
            var paths = new RuntimePackagePaths(root);
            using var store = new RuntimeContentTransferStore(paths);
            var contentPath = Path.Combine(root, "content.bin");
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(contentPath, [1, 2, 3]);
            var reference = await store.RegisterRpcContentAsync(
                contentPath,
                "application/octet-stream",
                "content.bin",
                "provider.package",
                "caller.package",
                generation: 7,
                DateTimeOffset.UtcNow.AddMinutes(1),
                SunderRpcContentRepeatability.SingleUse,
                maximumUses: 1);

            Assert.Null(store.AcquireRpcContent(reference, "provider.package", "other.package", 7));
            Assert.Null(store.AcquireRpcContent(reference, "provider.package", "caller.package", 8));
            var lease = store.AcquireRpcContent(reference, "provider.package", "caller.package", 7);
            Assert.NotNull(lease);
            Assert.Equal(1, lease.UseNumber);
            Assert.Null(store.AcquireRpcContent(reference, "provider.package", "caller.package", 7));

            var tamperReference = await store.RegisterRpcContentAsync(
                contentPath,
                "application/octet-stream",
                "content.bin",
                "provider.package",
                "caller.package",
                generation: 7,
                DateTimeOffset.UtcNow.AddMinutes(1),
                SunderRpcContentRepeatability.Repeatable,
                maximumUses: 2);
            await File.WriteAllBytesAsync(contentPath, [9, 9, 9]);
            Assert.Null(store.AcquireRpcContent(tamperReference, "provider.package", "caller.package", 7));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ContributionRegistry_RequiresManifestDeclarationAndRejectsDuplicates()
    {
        var descriptor = Contract();
        var declaration = ProviderManifest(descriptor);
        var manifest = new SunderPackageManifest { Provides = [declaration] };
        var contracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal)
        {
            [PackageSessionPreparer.ContractKey(descriptor.ContractId, descriptor.Version)] = descriptor,
        };
        var registry = new RuntimePackageContributionRegistry(
            new ServiceCollection().BuildServiceProvider(),
            "provider.package",
            manifest: manifest,
            rpcContracts: contracts);
        registry.RegisterRpcProvider("example.provider", new TestHandler());
        Assert.Throws<InvalidOperationException>(() =>
            registry.RegisterRpcProvider("example.provider", new TestHandler()));

        var undeclared = new RuntimePackageContributionRegistry(
            new ServiceCollection().BuildServiceProvider(),
            "provider.package",
            manifest: new SunderPackageManifest(),
            rpcContracts: contracts);
        Assert.Throws<InvalidOperationException>(() =>
            undeclared.RegisterRpcProvider("example.provider", new TestHandler()));
    }

    private static async IAsyncEnumerable<JsonElement> Values(
        JsonElement first,
        JsonElement second,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return first;
        await Task.Yield();
        yield return second;
    }

    private static async Task DrainAsync<T>(System.Threading.Channels.ChannelReader<T> reader)
    {
        await foreach (var _ in reader.ReadAllAsync())
        {
        }
    }

    private static string CreateRoot()
        => Path.Combine(Path.GetTempPath(), "sunder-rpc-tests", Guid.NewGuid().ToString("N"));

    private static RuntimeRpcPermissionSubject PermissionSubject(PackageSourceKind sourceKind)
        => new(
            "caller.package",
            "1.0.0",
            new string('a', 64),
            sourceKind,
            [
                new SunderPackageContractUseManifest
                {
                    ContractId = "example.rpc",
                    VersionRange = "1.0.0",
                    Required = true,
                    Actions = ["discover", "invoke", "subscribe"],
                },
            ]);

    private sealed class RpcFixture : IAsyncDisposable
    {
        private readonly string _root = CreateRoot();
        private readonly RuntimeRpcPermissionStore _permissions;
        private ActivePackageSession? _activeSession;

        public RpcFixture(RuntimeRpcPolicyOptions? policy = null)
        {
            Directory.CreateDirectory(_root);
            Catalog = new RuntimeRpcCatalog();
            var events = new RuntimeEventStreamService();
            Owner = new RuntimeSessionOwner(
                NullLogger<RuntimeSessionOwner>.Instance,
                events,
                rpcCatalog: Catalog);
            _permissions = new RuntimeRpcPermissionStore(new RuntimePackagePaths(_root));
            AppSessions = new RuntimeRpcAppSessionManager(Owner.State, policy ?? new RuntimeRpcPolicyOptions());
            Broker = new RuntimeRpcBroker(
                Catalog,
                _permissions,
                Owner.State,
                Owner,
                policy,
                TimeProvider.System,
                CancellationToken.None,
                AppSessions);
        }

        public RuntimeRpcCatalog Catalog { get; }
        public RuntimeSessionOwner Owner { get; }
        public RuntimeRpcBroker Broker { get; }
        public RuntimeRpcAppSessionManager AppSessions { get; }
        public string PermissionFilePath => new RuntimePackagePaths(_root).RpcPermissionFilePath;

        public async Task<RpcActivation> PublishAsync(
            TestHandler handler,
            bool providerUsesContract = false,
            bool webCaller = false,
            PackageSourceKind callerSourceKind = PackageSourceKind.Installed)
        {
            var descriptor = Contract();
            var caller = CreatePackage(
                "caller.package",
                descriptor,
                null,
                usesContract: true,
                sourceKind: callerSourceKind);
            if (webCaller)
            {
                caller = AddWebTarget(caller, descriptor);
            }
            var provider = CreatePackage("provider.package", descriptor, handler, providerUsesContract);
            var session = CreateSession(caller, provider);
            await Owner.PublishAsync(
                session,
                Owner.Sources.Snapshot(),
                [],
                [],
                Owner.Generation);
            Catalog.ActivateSession(session, Owner.Generation);
            _activeSession = session;
            return new RpcActivation(
                new RuntimeRpcCallerStamp(caller.Descriptor.PackageId, caller.RuntimeActivationId),
                new RuntimeRpcCallerStamp(provider.Descriptor.PackageId, provider.RuntimeActivationId),
                handler,
                session);
        }

        public RuntimeRpcClient CreateClient(RuntimeRpcCallerStamp stamp) => new(Broker, stamp);

        public Task GrantAsync(string action)
            => GrantAsync("caller.package", action);

        public Task GrantAsync(string packageId, string action)
            => GrantAsync(packageId, new string('a', 64), action);

        public Task GrantAsync(string packageId, string manifestSha256, string action)
            => _permissions.SetAsync(
                packageId,
                "1.0.0",
                manifestSha256,
                "example.rpc",
                action,
                RuntimeRpcPermissionState.Granted,
                CancellationToken.None);

        public Task<(RuntimeRpcAppSessionDescriptor Session, string ManifestSha256, PackageTargetDescriptor Target)> OpenAppCallerAsync()
        {
            var source = _activeSession!.LoadedPackageMap["caller.package"].Source;
            var target = source.Manifest!.Targets!.Single()!;
            var manifestSha256 = RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex!);
            var descriptor = PackageTargetSelection.ToDescriptor(target);
            return OpenAsync();

            async Task<(RuntimeRpcAppSessionDescriptor, string, PackageTargetDescriptor)> OpenAsync()
            {
                var session = await AppSessions.OpenAsync(
                    new RuntimeRpcAppSessionOpenRequest(
                        "caller.package",
                        "1.0.0",
                        manifestSha256,
                        descriptor,
                        Owner.Generation,
                        Guid.NewGuid()),
                    CancellationToken.None);
                return (session, manifestSha256, descriptor);
            }
        }

        public Task RevokeAsync(string action)
            => _permissions.SetAsync(
                "caller.package",
                "1.0.0",
                new string('a', 64),
                "example.rpc",
                action,
                RuntimeRpcPermissionState.Denied,
                CancellationToken.None);

        public Task<RuntimeRpcPermissionSnapshot> SetPermissionAsync(
            RuntimeRpcPermissionUpdateRequest request,
            RuntimeRpcPermissionState state)
            => new RuntimeRpcPermissionService(Owner.State, _permissions)
                .SetAsync(request, state, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            if (_activeSession is not null)
            {
                try
                {
                    await Owner.State.ClearActiveSessionAsync();
                }
                catch
                {
                }
            }
            Catalog.Dispose();
            AppSessions.Dispose();
            _permissions.Dispose();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static ActiveLoadedPackage AddWebTarget(
        ActiveLoadedPackage package,
        SunderRpcContractDescriptor descriptor)
    {
        var root = package.Source.SourceFolder;
        var manifestFolder = Path.Combine(root, "manifest");
        var contractFolder = Path.Combine(root, "payload", "shared", "contracts");
        Directory.CreateDirectory(manifestFolder);
        Directory.CreateDirectory(contractFolder);
        var descriptorBytes = descriptor.GetCanonicalUtf8();
        File.WriteAllBytes(Path.Combine(contractFolder, "rpc.json"), descriptorBytes);
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = package.Source.Manifest!.Id,
            Name = package.Source.Manifest.Id,
            Version = package.Source.Manifest.Version,
            ContractBundles = package.Source.Manifest.ContractBundles,
            UsesContracts = package.Source.Manifest.UsesContracts,
            Provides = package.Source.Manifest.Provides,
            Targets =
            [
                new SunderPackageTargetManifest
                {
                    Role = SunderPackageFormat.AppHostRole,
                    Rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                    Kind = SunderPackageFormat.WebTargetKind,
                    EntryPoint = "index.html",
                    SdkVersion = "1.1.0",
                    RequiredHostCapabilities = ["sdk-baseline-1-1.v1", "rpc.v1"],
                    Views =
                    [
                        new SunderPackageWebViewManifest
                        {
                            ViewId = "caller.package.main",
                            DisplayName = "Caller",
                            Route = "/",
                            DefaultPlacement = "middle",
                            ShowInHotbar = true,
                        },
                    ],
                },
            ],
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        File.WriteAllBytes(Path.Combine(manifestFolder, "sunder-package.json"), manifestBytes);
        var contentIndex = new SunderPackageContentIndex(
            SunderPackageFormat.CurrentContentIndexVersion,
            [
                new SunderPackageContentIndexEntry(
                    SunderPackageFormat.ManifestPath,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                    manifestBytes.Length),
                new SunderPackageContentIndexEntry(
                    SunderPackageFormat.SharedPayloadRoot + "contracts/rpc.json",
                    descriptor.Sha256,
                    descriptorBytes.Length),
            ]);
        return package with
        {
            Source = package.Source with
            {
                HostRoles = PackageHostRoles.App | PackageHostRoles.Runtime,
                Manifest = manifest,
                ContentIndex = contentIndex,
            },
        };
    }

    private static ActiveLoadedPackage CreatePackage(
        string packageId,
        SunderRpcContractDescriptor descriptor,
        TestHandler? handler,
        bool usesContract,
        PackageSourceKind sourceKind = PackageSourceKind.Installed)
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        var assemblyPath = typeof(RuntimeRpcBrokerTests).Assembly.Location;
        var manifest = new SunderPackageManifest
        {
            Id = packageId,
            Version = "1.0.0",
            ContractBundles =
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = descriptor.ContractId,
                    Version = descriptor.Version,
                    DescriptorPath = "contracts/rpc.json",
                    Sha256 = descriptor.Sha256,
                },
            ],
            UsesContracts = usesContract
                ?
                [
                    new SunderPackageContractUseManifest
                    {
                        ContractId = descriptor.ContractId,
                        VersionRange = descriptor.Version,
                        Required = true,
                        Actions = ["discover", "invoke", "subscribe"],
                    },
                ]
                : [],
            Provides = handler is null ? [] : [ProviderManifest(descriptor)],
        };
        var source = new RuntimePackageSource(
            packageId,
            sourceKind,
            root,
            Manifest: manifest);
        var package = new ActiveLoadedPackage(
            new ActivePackageDescriptor(
                packageId,
                packageId,
                "1.0.0",
                PackageHostRoles.Runtime,
                null,
                true,
                PackageReadinessState.Ready,
                []),
            source,
            SettingsSchema: null,
            new JsonPackageKeyValueStore(Path.Combine(root, "state.json")),
            new JsonPackageSecretsStore(
                Path.Combine(root, "secrets.json"),
                null,
                null,
                new RestrictedFileMasterKeyProtection()),
            AuthHandler: null,
            CallbackHandlers: new Dictionary<string, IPackageCallbackHandler>(StringComparer.OrdinalIgnoreCase),
            BackgroundServices: [],
            new ServiceCollection().BuildServiceProvider(),
            new RuntimePackageLoadContext(
                packageId,
                assemblyPath,
                new RuntimeSharedAssemblyRegistry([Path.GetDirectoryName(assemblyPath)!])),
            EmptyTestPackageSettings.Instance)
        {
            RpcContracts = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal)
            {
                [PackageSessionPreparer.ContractKey(descriptor.ContractId, descriptor.Version)] = descriptor,
            },
            RpcContractUses = (manifest.UsesContracts ?? []).Select(static use => use!).ToArray(),
            RpcManifestSha256 = new string('a', 64),
        };
        if (handler is not null)
        {
            var declaration = Assert.Single(manifest.Provides)!;
            package = package with
            {
                RpcProviders = new Dictionary<string, RuntimeRpcProviderRegistration>(StringComparer.Ordinal)
                {
                    [declaration.ProviderId!] = new RuntimeRpcProviderRegistration(
                        declaration.ProviderId!,
                        descriptor.ContractId,
                        descriptor.Version,
                        descriptor.Sha256,
                        descriptor,
                        handler,
                        declaration),
                },
            };
        }
        return package;
    }

    private static ActivePackageSession CreateSession(params ActiveLoadedPackage[] packages)
        => new(
            sessionFolder: null,
            packages.ToDictionary(static package => package.Descriptor.PackageId, StringComparer.OrdinalIgnoreCase),
            packages.ToDictionary(
                static package => package.Descriptor.PackageId,
                static package => new SessionPackageDescriptor(
                    package.Descriptor.PackageId,
                    package.Descriptor.PackageId,
                    package.Descriptor.Version,
                    PackageHostRoles.Runtime,
                    null,
                    true,
                    PackageReadinessState.Ready,
                    [],
                    null,
                    null,
                    null,
                    0),
                StringComparer.OrdinalIgnoreCase));

    private static SunderPackageProviderManifest ProviderManifest(SunderRpcContractDescriptor descriptor)
        => new()
        {
            ProviderId = "example.provider",
            ContractId = descriptor.ContractId,
            ContractVersion = descriptor.Version,
            ContractSha256 = descriptor.Sha256,
            Role = SunderPackageFormat.RuntimeHostRole,
        };

    private static SunderRpcContractDescriptor Contract()
        => SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "contractId":"example.rpc",
              "version":"1.0.0",
              "services":[{"serviceId":"messages","methods":[
                {"methodId":"send","kind":"unary","requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
                {"methodId":"watch","kind":"server-stream","requestSchema":{"$ref":"#/$defs/Request"},"eventSchema":{"$ref":"#/$defs/Response"}}
              ]}],
              "$defs":{
                "Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128}},"required":["message"],"additionalProperties":false},
                "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}
              }
            }
            """));

    private sealed record RpcActivation(
        RuntimeRpcCallerStamp Caller,
        RuntimeRpcCallerStamp Provider,
        TestHandler Handler,
        ActivePackageSession Session);

    private sealed class TestHandler : ISunderRpcServiceHandler
    {
        public Func<JsonElement, CancellationToken, ValueTask<JsonElement>> Unary { get; set; } =
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
            };

        public Func<JsonElement, CancellationToken, IAsyncEnumerable<JsonElement>> Stream { get; set; } =
            static (_, cancellationToken) => Single(cancellationToken);

        public ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken = default)
            => Unary(request, cancellationToken);

        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken = default)
            => Stream(request, cancellationToken);

        private static async IAsyncEnumerable<JsonElement> Single(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return JsonSerializer.SerializeToElement(new { accepted = true });
            await Task.CompletedTask;
        }
    }
}
