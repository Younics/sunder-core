using System.Text.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcContentAuthorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallScope_ExtendedDeadlineIsAppliedOnlyToOptedInTarget(bool targetOptedIn)
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new RpcFixture(
            new RuntimeRpcPolicyOptions
            {
                DefaultDeadline = TimeSpan.FromMinutes(5),
                MaximumExtendedDeadline = TimeSpan.FromMinutes(35),
            },
            clock);
        DateTimeOffset? invocationDeadline = null;
        var activation = await fixture.PublishAsync(
            new TestHandler
            {
                UnaryWithContext = (context, _, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    invocationDeadline = context.DeadlineUtc;
                    return ValueTask.FromResult(
                        JsonSerializer.SerializeToElement(new { accepted = true }));
                },
            },
            providerRequiredHostCapabilities: targetOptedIn
                ? [SunderSdkCapabilities.RpcExtendedDeadlineV1]
                : null);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var requestedDeadline = clock.GetUtcNow().AddMinutes(30);
        await using var scope = await client.CreateCallScopeAsync(
            new SunderRpcCallOptions(requestedDeadline));

        Assert.Equal(requestedDeadline, scope.DeadlineUtc);
        await scope.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "scoped" }));

        Assert.Equal(
            targetOptedIn ? requestedDeadline : clock.GetUtcNow().AddMinutes(5),
            invocationDeadline);
    }

    [Fact]
    public async Task CallScope_RetainsInheritedDepthAfterParentInvocationCompletes()
    {
        await using var fixture = new RpcFixture();
        ISunderRpcCallScope? inheritedScope = null;
        RuntimeRpcClient? providerClient = null;
        int? inheritedCallDepth = null;
        var activation = await fixture.PublishAsync(
            new TestHandler
            {
                UnaryWithContext = async (context, request, cancellationToken) =>
                {
                    if (request.GetProperty("message").GetString() == "open-scope")
                    {
                        inheritedScope = await providerClient!.CreateCallScopeAsync(
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        inheritedCallDepth = context.CallDepth;
                    }
                    return JsonSerializer.SerializeToElement(new { accepted = true });
                },
            },
            providerUsesContract: true);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        await fixture.GrantAsync("provider.package", SunderRpcProtocol.InvokeAction);
        var callerClient = fixture.CreateClient(activation.Caller);
        providerClient = fixture.CreateClient(activation.Provider);
        var endpoint = Assert.Single((await callerClient.DiscoverAsync("example.rpc")).Providers).Endpoint;

        await callerClient.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "open-scope" }));
        Assert.NotNull(inheritedScope);
        await inheritedScope.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "after-parent" }));

        Assert.Equal(2, inheritedCallDepth);
        await inheritedScope.DisposeAsync();
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
            var providerEndpoint = new RuntimeRpcContentEndpoint("rpc1_test", Guid.NewGuid());
            var reference = await store.RegisterRpcContentAsync(
                contentPath,
                "application/octet-stream",
                "content.bin",
                "provider.package",
                "caller.package",
                generation: 7,
                providerEndpoint,
                DateTimeOffset.UtcNow.AddMinutes(1),
                SunderRpcContentRepeatability.SingleUse,
                maximumUses: 1);

            Assert.Null(store.AcquireRpcContent(
                reference,
                "provider.package",
                "other.package",
                7,
                providerEndpoint));
            Assert.Null(store.AcquireRpcContent(
                reference,
                "provider.package",
                "caller.package",
                8,
                providerEndpoint));
            Assert.Null(store.AcquireRpcContent(
                reference,
                "provider.package",
                "caller.package",
                7,
                providerEndpoint with { EndpointReference = "rpc1_sibling" }));
            var lease = store.AcquireRpcContent(
                reference,
                "provider.package",
                "caller.package",
                7,
                providerEndpoint);
            Assert.NotNull(lease);
            Assert.Equal(1, lease.UseNumber);
            Assert.Null(store.AcquireRpcContent(
                reference,
                "provider.package",
                "caller.package",
                7,
                providerEndpoint));
            store.ReleaseRpcContent(lease);

            var tamperReference = await store.RegisterRpcContentAsync(
                contentPath,
                "application/octet-stream",
                "content.bin",
                "provider.package",
                "caller.package",
                generation: 7,
                providerEndpoint,
                DateTimeOffset.UtcNow.AddMinutes(1),
                SunderRpcContentRepeatability.Repeatable,
                maximumUses: 2);
            await File.WriteAllBytesAsync(contentPath, [9, 9, 9]);
            Assert.Null(store.AcquireRpcContent(
                tamperReference,
                "provider.package",
                "caller.package",
                7,
                providerEndpoint));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ContentAuthority_RevocationWinsLateRegistrationRaceWithoutFileLeak()
    {
        var root = CreateRoot();
        try
        {
            var paths = new RuntimePackagePaths(root);
            using var store = new RuntimeContentTransferStore(paths);
            var authority = new RuntimeRpcContentAuthority("race-authority");
            await using var source = new ReleaseBlockingReadStream();
            var registration = store.RegisterRpcContentAsync(
                source,
                expectedLength: 1,
                "application/octet-stream",
                "content.bin",
                "caller.package",
                "provider.package",
                generation: 7,
                new RuntimeRpcContentEndpoint("rpc1_target", Guid.NewGuid()),
                DateTimeOffset.UtcNow.AddMinutes(1),
                SunderRpcContentRepeatability.SingleUse,
                maximumUses: 1,
                CancellationToken.None,
                authority);
            await source.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

            store.DiscardRpcContentAuthority(authority);
            source.Release.TrySetResult();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await registration);
            Assert.Empty(Directory.Exists(paths.TransferRootPath)
                ? Directory.GetFiles(paths.TransferRootPath)
                : []);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CallScope_ContentAuthorityEndsWithInvocationAndScopeDisposal()
    {
        await using var fixture = new RpcFixture();
        SunderRpcInvocationContext? retainedContext = null;
        SunderRpcContentReference? requestContent = null;
        SunderRpcContentReference? responseContent = null;
        var activation = await fixture.PublishAsync(new TestHandler
        {
            UnaryWithContext = async (context, _, cancellationToken) =>
            {
                retainedContext = context;
                await using (var input = await context.OpenContentAsync(
                                 requestContent!,
                                 cancellationToken))
                using (var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: false))
                {
                    Assert.Equal("request-content", await reader.ReadToEndAsync(cancellationToken));
                }
                await using var output = new MemoryStream(Encoding.UTF8.GetBytes("response-content"));
                responseContent = await context.RegisterContentAsync(
                    output,
                    new SunderRpcContentRegistrationOptions(
                        "text/plain",
                        "response.txt",
                        output.Length,
                        Repeatability: SunderRpcContentRepeatability.Repeatable,
                        MaximumUses: 2),
                    cancellationToken);
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var scope = await client.CreateCallScopeAsync();
        await using var request = new MemoryStream(Encoding.UTF8.GetBytes("request-content"));
        requestContent = await scope.RegisterContentAsync(
            endpoint,
            request,
            new SunderRpcContentRegistrationOptions("text/plain", "request.txt", request.Length));

        var result = await scope.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "content" }));

        Assert.True(result.GetProperty("accepted").GetBoolean());
        var retainedFailure = await Assert.ThrowsAsync<SunderRpcException>(() => retainedContext!
            .RegisterContentAsync(
                new MemoryStream([1]),
                new SunderRpcContentRegistrationOptions("application/octet-stream", "late.bin", 1))
            .AsTask());
        Assert.Equal(SunderRpcErrorKind.Unavailable, retainedFailure.Error.Kind);

        await using var response = await scope.OpenContentAsync(responseContent!);
        await scope.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await response.ReadAsync(new byte[1]));
        Assert.Throws<ObjectDisposedException>(() => scope.OpenContentAsync(responseContent!));
    }

    [Fact]
    public async Task CallScope_RequestContentIsBoundToExactSiblingEndpoint()
    {
        await using var fixture = new RpcFixture();
        SunderRpcContentReference? requestContent = null;
        var targetHandler = new TestHandler
        {
            UnaryWithContext = async (context, _, cancellationToken) =>
            {
                await using var input = await context.OpenContentAsync(requestContent!, cancellationToken);
                using var reader = new StreamReader(input, Encoding.UTF8, leaveOpen: false);
                Assert.Equal("endpoint-bound", await reader.ReadToEndAsync(cancellationToken));
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        };
        var siblingDenied = false;
        var siblingHandler = new TestHandler
        {
            UnaryWithContext = async (context, _, cancellationToken) =>
            {
                var failure = await Assert.ThrowsAsync<SunderRpcException>(() => context
                    .OpenContentAsync(requestContent!, cancellationToken)
                    .AsTask());
                siblingDenied = failure.Error.Kind == SunderRpcErrorKind.NotFound;
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        };
        var activation = await fixture.PublishAsync(targetHandler, siblingHandler: siblingHandler);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var providers = (await client.DiscoverAsync("example.rpc")).Providers;
        var targetEndpoint = providers.Single(provider => provider.ProviderId == "example.provider").Endpoint;
        var siblingEndpoint = providers.Single(provider => provider.ProviderId == "example.sibling").Endpoint;
        await using var scope = await client.CreateCallScopeAsync();
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("endpoint-bound"));
        requestContent = await scope.RegisterContentAsync(
            targetEndpoint,
            source,
            new SunderRpcContentRegistrationOptions("text/plain", "request.txt", source.Length));

        await scope.InvokeAsync(
            siblingEndpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "sibling" }));
        Assert.True(siblingDenied);

        var response = await scope.InvokeAsync(
            targetEndpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "target" }));
        Assert.True(response.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task CallScope_ProviderRetirementCancelsOpenContentAndRejectsReopen()
    {
        await using var fixture = new RpcFixture();
        SunderRpcContentReference? responseContent = null;
        var activation = await fixture.PublishAsync(new TestHandler
        {
            UnaryWithContext = async (context, _, cancellationToken) =>
            {
                await using var output = new MemoryStream(Encoding.UTF8.GetBytes("response-content"));
                responseContent = await context.RegisterContentAsync(
                    output,
                    new SunderRpcContentRegistrationOptions(
                        "text/plain",
                        "response.txt",
                        output.Length,
                        Repeatability: SunderRpcContentRepeatability.Repeatable,
                        MaximumUses: 2),
                    cancellationToken);
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        await using var scope = await client.CreateCallScopeAsync();
        await scope.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "content" }));
        await using var open = await scope.OpenContentAsync(responseContent!);

        Assert.True(fixture.Catalog.DeactivatePackage(
            activation.Provider.PackageId,
            activation.Provider.ActivationId,
            faulted: false));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            _ = await open.ReadAsync(new byte[1]));
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => scope
            .OpenContentAsync(responseContent!)
            .AsTask());
        Assert.Equal(SunderRpcErrorKind.NotFound, failure.Error.Kind);
    }

    [Fact]
    public async Task AppCallScope_UsesExactCallerIdentityAndReclaimsExplicitlyClosedCapacity()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new RpcFixture(
            new RuntimeRpcPolicyOptions
            {
                DefaultDeadline = TimeSpan.FromMinutes(5),
                MaxCallScopesPerCaller = 1,
            },
            clock);
        await fixture.PublishAsync(new TestHandler(), webCaller: true);
        var firstApp = await fixture.OpenAppCallerAsync();
        var secondApp = await fixture.OpenAppCallerAsync();
        var firstCaller = fixture.AppSessions.GetRequired(firstApp.Session.SessionId);
        var secondCaller = fixture.AppSessions.GetRequired(secondApp.Session.SessionId);
        var firstScope = fixture.Broker.CreateAppCallScope(
            firstApp.Session.SessionId,
            options: null,
            CancellationToken.None);
        var secondScope = fixture.Broker.CreateAppCallScope(
            secondApp.Session.SessionId,
            options: null,
            CancellationToken.None);

        Assert.Same(firstCaller, firstScope.Caller);
        Assert.NotSame(secondCaller, firstScope.Caller);
        Assert.StartsWith("rpc-scope-", firstScope.Id, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(firstScope.Id["rpc-scope-".Length..], "N", out _));
        Assert.Equal(firstScope.Id, firstScope.ContentAuthority.Id);
        using (var lease = fixture.Owner.State.AcquireLease())
        {
            Assert.Equal(lease.Generation, firstScope.SessionGeneration);
            Assert.Same(firstCaller, firstScope.SessionGenerationIdentity);
        }
        Assert.Same(
            firstScope,
            fixture.Broker.GetAppCallScope(firstApp.Session.SessionId, firstScope.Id));

        var limited = Assert.Throws<SunderRpcException>(() => fixture.Broker.CreateAppCallScope(
            firstApp.Session.SessionId,
            options: null,
            CancellationToken.None));
        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, limited.Error.Kind);
        Assert.Equal("rpc.scope.limit", limited.Error.Code);
        Assert.Equal("The caller RPC call-scope limit was reached.", limited.Error.Message);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, limited.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Safe, limited.Error.RetrySafety);

        AssertScopeUnavailable(Assert.Throws<SunderRpcException>(() => fixture.Broker.GetAppCallScope(
            secondApp.Session.SessionId,
            firstScope.Id)));
        AssertScopeUnavailable(await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await fixture.Broker.CloseAppCallScopeAsync(secondApp.Session.SessionId, firstScope.Id)));

        await fixture.Broker.CloseAppCallScopeAsync(firstApp.Session.SessionId, firstScope.Id);
        var replacement = fixture.Broker.CreateAppCallScope(
            firstApp.Session.SessionId,
            options: null,
            CancellationToken.None);
        await fixture.Broker.CloseAppCallScopeAsync(firstApp.Session.SessionId, replacement.Id);
        await fixture.Broker.CloseAppCallScopeAsync(secondApp.Session.SessionId, secondScope.Id);
        fixture.AppSessions.Close(firstApp.Session.SessionId);
        fixture.AppSessions.Close(secondApp.Session.SessionId);
        Assert.Equal(0, clock.ActiveTimerCount);

        static void AssertScopeUnavailable(SunderRpcException failure)
        {
            Assert.Equal(SunderRpcErrorKind.Unavailable, failure.Error.Kind);
            Assert.Equal("rpc.scope.unavailable", failure.Error.Code);
            Assert.Equal("The RPC call scope is stale or unavailable.", failure.Error.Message);
            Assert.Equal(SunderRpcDispatchState.NotDispatched, failure.Error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Safe, failure.Error.RetrySafety);
        }
    }

    [Fact]
    public async Task CallScope_CloseDuringInvocationAcquisitionRetainsCancelledTokenWithoutRevivingAuthority()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new RpcFixture(timeProvider: clock);
        var mutations = 0;
        var observedCancellation = false;
        await fixture.PublishAsync(new TestHandler
        {
            Unary = (_, token) =>
            {
                observedCancellation |= token.IsCancellationRequested;
                token.ThrowIfCancellationRequested();
                mutations++;
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
            },
        }, webCaller: true);
        var app = await fixture.OpenAppCallerAsync();
        foreach (var action in new[] { SunderRpcProtocol.DiscoverAction, SunderRpcProtocol.InvokeAction })
            await fixture.GrantAsync("caller.package", app.ManifestSha256, action, SunderPackageFormat.AppHostRole);
        var endpoint = Assert.Single((await fixture.Broker.DiscoverAppAsync(
            app.Session.SessionId, "example.rpc", CancellationToken.None)).Providers).Endpoint;
        var scope = fixture.Broker.CreateAppCallScope(app.Session.SessionId, null, CancellationToken.None);
        var originalToken = scope.RevocationToken;
        clock.OnTimerCreated = () =>
        {
            // The invocation deadline timer is allocated after initial scope
            // validation, immediately before the broker links its revocation token.
            clock.OnTimerCreated = null;
            fixture.Broker.CloseAppCallScopeAsync(app.Session.SessionId, scope.Id).AsTask().GetAwaiter().GetResult();
        };

        var failure = await Assert.ThrowsAsync<SunderRpcException>(async () => await fixture.Broker.InvokeScopeAsync(
            scope, endpoint, "messages", "send", JsonSerializer.SerializeToElement(new { message = "race" }), null, CancellationToken.None));

        Assert.Equal(SunderRpcErrorKind.Cancelled, failure.Error.Kind);
        Assert.Equal("rpc.call.cancelled", failure.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, failure.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, failure.Error.RetrySafety);
        Assert.True(observedCancellation);
        Assert.Equal(0, mutations);
        Assert.Equal(originalToken, scope.RevocationToken);
        Assert.True(scope.RevocationToken.IsCancellationRequested);
        Assert.True(scope.IsRevoked);
        Assert.True(scope.ContentAuthority.IsRevoked);
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers).State);

        var replacement = fixture.Broker.CreateAppCallScope(app.Session.SessionId, null, CancellationToken.None);
        Assert.NotEqual(originalToken, replacement.RevocationToken);
        Assert.False(replacement.RevocationToken.IsCancellationRequested);
        var response = await fixture.Broker.InvokeScopeAsync(replacement, endpoint, "messages", "send",
            JsonSerializer.SerializeToElement(new { message = "fresh" }), null, CancellationToken.None);
        Assert.True(response.GetProperty("accepted").GetBoolean());
        Assert.Equal(1, mutations);
        await fixture.Broker.CloseAppCallScopeAsync(app.Session.SessionId, replacement.Id);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact]
    public async Task CallScope_DeadlineRevokesWatchContentAndReleasesCapacity()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new RpcFixture(
            new RuntimeRpcPolicyOptions
            {
                DefaultDeadline = TimeSpan.FromMinutes(5),
                MaxCallScopesPerCaller = 1,
            },
            clock);
        var activation = await fixture.PublishAsync(new TestHandler());
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var snapshot = await client.DiscoverAsync("example.rpc");
        var endpoint = Assert.Single(snapshot.Providers).Endpoint;
        var scope = await client.CreateCallScopeAsync(
            new SunderRpcCallOptions(clock.GetUtcNow().AddMinutes(1)));
        await using var source = new MemoryStream([1, 2, 3]);
        await scope.RegisterContentAsync(
            endpoint,
            source,
            new SunderRpcContentRegistrationOptions("application/octet-stream", "request.bin", source.Length));
        await using var watch = scope.WatchAsync(
            snapshot.Revision + 1,
            snapshot.Sequence).GetAsyncEnumerator();
        Assert.True(await watch.MoveNextAsync());
        var waiting = watch.MoveNextAsync().AsTask();

        clock.Advance(TimeSpan.FromMinutes(1));

        var expired = await Assert.ThrowsAsync<SunderRpcException>(async () => await waiting);
        Assert.Equal(SunderRpcErrorKind.DeadlineExceeded, expired.Error.Kind);
        Assert.Empty(fixture.GetTransferFiles());
        Assert.Equal(0, clock.ActiveTimerCount);
        await using var replacement = await client.CreateCallScopeAsync();
        var stale = await Assert.ThrowsAsync<SunderRpcException>(() => scope
            .DiscoverAsync("example.rpc")
            .AsTask());
        Assert.Equal(SunderRpcErrorKind.DeadlineExceeded, stale.Error.Kind);
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task AppCallScope_CloseAfterDeadlineIsIdempotent()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new RpcFixture(
            new RuntimeRpcPolicyOptions { DefaultDeadline = TimeSpan.FromMinutes(5) },
            clock);
        await fixture.PublishAsync(new TestHandler(), webCaller: true);
        var app = await fixture.OpenAppCallerAsync();
        var scope = fixture.Broker.CreateAppCallScope(
            app.Session.SessionId,
            new SunderRpcCallOptions(clock.GetUtcNow().AddMinutes(1)),
            CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1));

        await fixture.Broker.CloseAppCallScopeAsync(app.Session.SessionId, scope.Id);
        await fixture.Broker.CloseAppCallScopeAsync(app.Session.SessionId, scope.Id);
    }

    [Fact]
    public async Task PackageFault_RevokesOldAppCallScopeAndFreshAppGenerationCanCallHealthyProvider()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            webCaller: true,
            healthyHandler: new TestHandler());
        var app = await fixture.OpenAppCallerAsync();
        await fixture.GrantAsync(
            "caller.package",
            app.ManifestSha256,
            SunderRpcProtocol.DiscoverAction,
            SunderPackageFormat.AppHostRole);
        await fixture.GrantAsync(
            "caller.package",
            app.ManifestSha256,
            SunderRpcProtocol.InvokeAction,
            SunderPackageFormat.AppHostRole);
        var oldCaller = fixture.AppSessions.GetRequired(app.Session.SessionId);
        var oldScope = fixture.Broker.CreateAppCallScope(
            app.Session.SessionId,
            options: null,
            CancellationToken.None);
        var oldProviders = (await fixture.Broker.DiscoverScopeAsync(
            oldScope,
            "example.rpc",
            CancellationToken.None)).Providers;
        var healthy = Assert.Single(oldProviders, provider => provider.PackageId == "healthy.package");
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var revocation = oldScope.RevocationToken.Register(() => revoked.TrySetResult());
        PackageActivationIdentity failedIdentity;
        using (var lease = fixture.Owner.State.AcquireLease())
        {
            var failedPackage = Assert.IsType<ActiveLoadedPackage>(
                fixture.Owner.State.GetLoadedPackage(lease, "provider.package"));
            failedIdentity = lease.GetPackageActivationIdentity(failedPackage);
        }

        Assert.True(fixture.Owner.HandleRpcProviderFault(
            "provider.package",
            failedIdentity,
            new InvalidOperationException("fault exact provider"),
            "rpc.provider.test"));
        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(oldCaller.RetirementToken.IsCancellationRequested);
        using (var currentLease = fixture.Owner.State.AcquireLease())
        {
            Assert.False(oldCaller.IsCurrent(currentLease));
        }
        var staleScope = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Broker
            .DiscoverScopeAsync(oldScope, "example.rpc", CancellationToken.None)
            .AsTask());
        Assert.Equal(SunderRpcErrorKind.Unavailable, staleScope.Error.Kind);
        await fixture.Broker.CloseCallScopeAsync(oldScope);

        var freshApp = await fixture.OpenAppCallerAsync();
        var freshScope = fixture.Broker.CreateAppCallScope(
            freshApp.Session.SessionId,
            options: null,
            CancellationToken.None);
        var freshProvider = Assert.Single((await fixture.Broker.DiscoverScopeAsync(
            freshScope,
            "example.rpc",
            CancellationToken.None)).Providers);
        Assert.Equal(healthy.Endpoint, freshProvider.Endpoint);
        var response = await fixture.Broker.InvokeScopeAsync(
            freshScope,
            freshProvider.Endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "fresh" }),
            options: null,
            CancellationToken.None);
        Assert.True(response.GetProperty("accepted").GetBoolean());

        await fixture.Broker.CloseAppCallScopeAsync(freshApp.Session.SessionId, freshScope.Id);
        fixture.AppSessions.Close(freshApp.Session.SessionId);
        fixture.AppSessions.Close(app.Session.SessionId);
        Assert.Same(activation.Session, fixture.Owner.State.ActiveSession);
    }

    private sealed class ReleaseBlockingReadStream : Stream
    {
        private int _reads;

        public TaskCompletionSource Blocked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                buffer.Span[0] = 1;
                return 1;
            }
            Blocked.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
