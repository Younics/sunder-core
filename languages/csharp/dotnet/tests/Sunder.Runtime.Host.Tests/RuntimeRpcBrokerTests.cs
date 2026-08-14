using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Format;
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
    public async Task Matching_UsesExactVersionAndAbiRatherThanFullDescriptorHash()
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

        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var response = await client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" }));

        Assert.True(response.GetProperty("accepted").GetBoolean());
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
    public async Task ManagedClient_InvariantReportFaultsOnlyExactProviderPackage()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "sunder.package.agent",
            healthyHandler: new TestHandler());
        await fixture.GrantAsync("sunder.package.agent", SunderRpcProtocol.DiscoverAction);
        var client = fixture.CreateClient(activation.Caller);
        var providers = (await client.DiscoverAsync("example.rpc")).Providers;
        var failing = Assert.Single(providers, static provider => provider.PackageId == "provider.package");
        var healthy = Assert.Single(providers, static provider => provider.PackageId == "healthy.package");

        var accepted = await client.TryReportInvariantViolationAsync(
            failing.Endpoint,
            new InvalidOperationException("reported invariant\r\nprivate detail"));

        Assert.True(accepted);
        Assert.Equal(PackageReadinessState.Failed, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
        Assert.Equal(PackageFailureOrigin.RuntimeRpcProvider, fixture.Owner.State.GetSessionPackage("provider.package")!.FailureOrigin);
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("healthy.package")!.Readiness);
        Assert.Equal(healthy.PackageId, Assert.Single(fixture.Catalog.GetSnapshot().Providers).PackageId);
        Assert.False(fixture.Catalog.TryGetActiveEndpoint(failing.Endpoint, out var faulted, out var stale));
        Assert.True(stale);
        Assert.Equal(RuntimeRpcBroker.InvariantViolationFaultCode, faulted!.Snapshot.FaultCode);
        var lastError = Assert.IsType<string>(fixture.Owner.State.GetSessionPackage("provider.package")!.LastError);
        Assert.DoesNotContain('\r', lastError);
        Assert.DoesNotContain('\n', lastError);
    }

    [Fact]
    public async Task InvariantReport_RejectsStaleEndpointWithoutFaultingReplacement()
    {
        await using var fixture = new RpcFixture();
        var first = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "sunder.package.agent");
        await fixture.GrantAsync("sunder.package.agent", SunderRpcProtocol.DiscoverAction);
        var staleEndpoint = Assert.Single((await fixture.CreateClient(first.Caller)
            .DiscoverAsync("example.rpc")).Providers).Endpoint;

        var replacement = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "sunder.package.agent");
        var replacementClient = fixture.CreateClient(replacement.Caller);

        Assert.False(await replacementClient.TryReportInvariantViolationAsync(
            staleEndpoint,
            new InvalidOperationException("stale report")));
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
        Assert.Single(fixture.Catalog.GetSnapshot().Providers);
    }

    [Fact]
    public async Task InvariantReport_RejectsUntrustedCaller()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler());
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        Assert.False(await client.TryReportInvariantViolationAsync(
            endpoint,
            new InvalidOperationException("untrusted report")));
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
        Assert.Single(fixture.Catalog.GetSnapshot().Providers);
    }

    [Fact]
    public async Task InvariantReport_RequiresExistingEndpointVisibilityGrant()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "sunder.package.agent");
        var endpoint = Assert.Single(fixture.Catalog.GetSnapshot().Providers).Endpoint;

        Assert.False(await fixture.CreateClient(activation.Caller).TryReportInvariantViolationAsync(
            endpoint,
            new InvalidOperationException("not visible")));
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
    }

    [Fact]
    public async Task AppCaller_InvariantReportUsesExactSessionIdentity()
    {
        await using var fixture = new RpcFixture();
        await fixture.PublishAsync(
            new TestHandler(),
            webCaller: true,
            callerPackageId: "sunder.package.agent");
        var app = await fixture.OpenAppCallerAsync("sunder.package.agent");
        await fixture.GrantAsync(
            "sunder.package.agent",
            app.ManifestSha256,
            SunderRpcProtocol.DiscoverAction,
            SunderPackageFormat.AppHostRole);
        var endpoint = Assert.Single((await fixture.Broker.DiscoverAppAsync(
            app.Session.SessionId,
            "example.rpc",
            CancellationToken.None)).Providers).Endpoint;

        Assert.True(await fixture.Broker.TryReportInvariantViolationAppAsync(
            app.Session.SessionId,
            endpoint,
            "app invariant",
            CancellationToken.None));
        Assert.Equal(PackageReadinessState.Failed, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
    }

    [Fact]
    public async Task InvariantReport_DuplicateRaceHasSingleWinner()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "sunder.package.agent");
        await fixture.GrantAsync("sunder.package.agent", SunderRpcProtocol.DiscoverAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        using var start = new ManualResetEventSlim();

        Task<bool> ReportAsync() => Task.Run(async () =>
        {
            start.Wait();
            return await client.TryReportInvariantViolationAsync(
                endpoint,
                new InvalidOperationException("duplicate report"));
        });

        var reports = new[] { ReportAsync(), ReportAsync() };
        start.Set();
        var results = await Task.WhenAll(reports);

        Assert.Equal(1, results.Count(static accepted => accepted));
        Assert.Equal(1, results.Count(static accepted => !accepted));
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
            SunderRpcProtocol.DiscoverAction,
            SunderPackageFormat.AppHostRole);
        await fixture.GrantAsync(
            "caller.package",
            manifestSha256,
            SunderRpcProtocol.InvokeAction,
            SunderPackageFormat.AppHostRole);
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
            SunderRpcProtocol.DiscoverAction,
            SunderPackageFormat.AppHostRole);

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
    public async Task ProviderHandlerFault_CapturesSafeExactContextAndSanitizesPackageState()
    {
        const string secret = "provider-secret=do-not-persist";
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Unary = static (_, _) => throw new SecretBearingProviderException(secret),
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var provider = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers);

        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            provider.Endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());

        Assert.True(RuntimeRpcFailureContextStore.TryGet(failure, out var context));
        Assert.Equal("caller.package", context.CallerPackageId);
        Assert.Equal(provider.PackageId, context.ProviderPackageId);
        Assert.Equal(provider.PackageVersion, context.ProviderPackageVersion);
        Assert.Equal(provider.ProviderId, context.ProviderId);
        Assert.Equal(provider.ContractId, context.ContractId);
        Assert.Equal(provider.ActivationId, context.ProviderActivationId);
        Assert.Equal("messages", context.ServiceId);
        Assert.Equal("send", context.MethodId);
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, context.ErrorKind);
        Assert.Equal("rpc.provider.handler-fault", context.ErrorCode);
        Assert.Equal(typeof(SecretBearingProviderException).FullName, context.ExceptionType);
        Assert.Equal(
            RuntimeRpcFailureContextStore.CreateExceptionFingerprint(
                typeof(SecretBearingProviderException).FullName!),
            context.ExceptionFingerprint);
        Assert.True(context.ExceptionType.Length <= RuntimeRpcFailureContextStore.MaximumExceptionTypeCharacters);
        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        Assert.Equal(
            "The package schema-first RPC provider faulted.",
            fixture.Owner.State.GetSessionPackage(provider.PackageId)!.LastError);
        Assert.DoesNotContain(
            fixture.Owner.GetSnapshot().Errors,
            error => error.Contains(secret, StringComparison.Ordinal));
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
            var key = PermissionKey(SunderRpcProtocol.InvokeAction);
            using (var store = new RuntimeRpcPermissionStore(paths))
            {
                await store.SetAsync(
                    key,
                    RuntimeRpcPermissionState.Granted,
                    CancellationToken.None);
                Assert.True(store.TryAcquire(key, out var lease));
                revocation = lease!.RevocationToken;
                Assert.False(store.TryAcquire(
                    key with { CallerPackageVersion = "2.0.0" },
                    out _));
                Assert.False(store.TryAcquire(
                    key with { ManifestSha256 = new string('b', 64) },
                    out _));
                Assert.False(store.TryAcquire(
                    key with { CallerRole = SunderPackageFormat.AppHostRole },
                    out _));
                Assert.False(store.TryAcquire(
                    key with { ContractVersion = "2.0.0" },
                    out _));
                Assert.False(store.TryAcquire(
                    key with { AbiSha256 = new string('b', 64) },
                    out _));
                await store.SetAsync(
                    key,
                    RuntimeRpcPermissionState.Denied,
                    CancellationToken.None);
                Assert.True(revocation.IsCancellationRequested);
                using var document = JsonDocument.Parse(File.ReadAllBytes(paths.RpcPermissionFilePath));
                Assert.Equal(2, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            }
            using var reloaded = new RuntimeRpcPermissionStore(paths);
            Assert.False(reloaded.TryAcquire(key, out _));
            Assert.Contains(
                reloaded.GetSnapshot([]).Permissions,
                permission => permission.Key == key
                              && permission.State == RuntimeRpcPermissionState.Denied);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionStore_ExactRoleRevocationCancelsOnlyMatchingLease()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var store = new RuntimeRpcPermissionStore(new RuntimePackagePaths(root));
            var runtimeKey = PermissionKey(SunderRpcProtocol.InvokeAction);
            var appKey = runtimeKey with { CallerRole = SunderPackageFormat.AppHostRole };
            await store.SetAsync(runtimeKey, RuntimeRpcPermissionState.Granted, CancellationToken.None);
            await store.SetAsync(appKey, RuntimeRpcPermissionState.Granted, CancellationToken.None);
            Assert.True(store.TryAcquire(runtimeKey, out var runtimeLease));
            Assert.True(store.TryAcquire(appKey, out var appLease));

            await store.SetAsync(runtimeKey, RuntimeRpcPermissionState.Denied, CancellationToken.None);

            Assert.True(runtimeLease!.RevocationToken.IsCancellationRequested);
            Assert.False(appLease!.RevocationToken.IsCancellationRequested);
            Assert.True(store.TryAcquire(appKey, out _));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionStore_RevocationCallbacksRunAfterWriteGateIsReleased()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var store = new RuntimeRpcPermissionStore(new RuntimePackagePaths(root));
            var invokeKey = PermissionKey(SunderRpcProtocol.InvokeAction);
            var discoverKey = PermissionKey(SunderRpcProtocol.DiscoverAction);
            await store.SetAsync(invokeKey, RuntimeRpcPermissionState.Granted, CancellationToken.None);
            Assert.True(store.TryAcquire(invokeKey, out var lease));
            using var registration = lease!.RevocationToken.Register(() =>
                store.SetAsync(
                        discoverKey,
                        RuntimeRpcPermissionState.Granted,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());

            await Task.Run(() => store.SetAsync(
                    invokeKey,
                    RuntimeRpcPermissionState.Denied,
                    CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(store.IsGranted(discoverKey));
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
        var runtimeUse = Assert.Single(subject.ContractUses);
        subject = subject with
        {
            ContractUses =
            [
                runtimeUse,
                new SunderPackageContractUseManifest
                {
                    Role = SunderPackageFormat.AppHostRole,
                    ContractId = runtimeUse.ContractId,
                    ContractVersion = runtimeUse.ContractVersion,
                    AbiSha256 = runtimeUse.AbiSha256,
                    Required = runtimeUse.Required,
                    Actions = runtimeUse.Actions,
                },
            ],
        };
        try
        {
            using (var store = new RuntimeRpcPermissionStore(paths))
            {
                await store.GrantDeclaredInstalledActionsAsync([subject], CancellationToken.None);

                Assert.Equal(1, store.Revision);
                Assert.All(
                    new[] { SunderPackageFormat.AppHostRole, SunderPackageFormat.RuntimeHostRole },
                    role => Assert.All(
                        new[] { "discover", "invoke", "subscribe" },
                        action => Assert.True(store.TryAcquire(
                            PermissionKey(
                                action,
                                callerRole: role,
                                packageId: subject.PackageId,
                                packageVersion: subject.PackageVersion,
                                manifestSha256: subject.ManifestSha256),
                            out _))));

                var deniedKey = PermissionKey(
                    SunderRpcProtocol.InvokeAction,
                    packageId: subject.PackageId,
                    packageVersion: subject.PackageVersion,
                    manifestSha256: subject.ManifestSha256);
                await store.SetAsync(
                    deniedKey,
                    RuntimeRpcPermissionState.Denied,
                    CancellationToken.None);
                var deniedRevision = store.Revision;
                await store.GrantDeclaredInstalledActionsAsync([subject], CancellationToken.None);

                Assert.False(store.TryAcquire(deniedKey, out _));
                Assert.True(store.TryAcquire(
                    deniedKey with { CallerRole = SunderPackageFormat.AppHostRole },
                    out _));
                Assert.Equal(deniedRevision, store.Revision);

                var updatedSubject = subject with
                {
                    PackageVersion = "2.0.0",
                    ManifestSha256 = new string('b', 64),
                };
                await store.GrantDeclaredInstalledActionsAsync([updatedSubject], CancellationToken.None);
                Assert.True(store.TryAcquire(
                    deniedKey with
                    {
                        CallerPackageVersion = updatedSubject.PackageVersion,
                        ManifestSha256 = updatedSubject.ManifestSha256,
                    },
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
                PermissionKey(SunderRpcProtocol.InvokeAction)),
            RuntimeRpcPermissionState.Denied));
        Assert.False(File.Exists(fixture.PermissionFilePath));
    }

    [Fact]
    public async Task PermissionService_UsesExactRoleAndRejectsStaleCallerFence()
    {
        await using var fixture = new RpcFixture();
        await fixture.PublishAsync(new TestHandler(), webCaller: true);
        var app = await fixture.OpenAppCallerAsync();
        var runtimeKey = PermissionKey(
            SunderRpcProtocol.InvokeAction,
            manifestSha256: app.ManifestSha256);
        var runtimeSnapshot = await fixture.SetPermissionAsync(
            new RuntimeRpcPermissionUpdateRequest(runtimeKey),
            RuntimeRpcPermissionState.Denied);
        var runtimePermission = Assert.Single(runtimeSnapshot.Permissions, permission =>
            permission.Key == runtimeKey);
        Assert.Equal(RuntimeRpcPermissionState.Denied, runtimePermission.State);

        var appKey = runtimeKey with { CallerRole = SunderPackageFormat.AppHostRole };
        var appSnapshot = await fixture.SetPermissionAsync(
            new RuntimeRpcPermissionUpdateRequest(appKey),
            RuntimeRpcPermissionState.Granted);
        Assert.Equal(
            RuntimeRpcPermissionState.Denied,
            Assert.Single(appSnapshot.Permissions, permission => permission.Key == runtimeKey).State);
        Assert.Equal(
            RuntimeRpcPermissionState.Granted,
            Assert.Single(appSnapshot.Permissions, permission => permission.Key == appKey).State);

        await Assert.ThrowsAsync<RuntimeValidationException>(() => fixture.SetPermissionAsync(
            new RuntimeRpcPermissionUpdateRequest(runtimeKey with
            {
                ManifestSha256 = new string('f', 64),
            }),
            RuntimeRpcPermissionState.Granted));
        await Assert.ThrowsAsync<RuntimeValidationException>(() => fixture.SetPermissionAsync(
            new RuntimeRpcPermissionUpdateRequest(runtimeKey with
            {
                AbiSha256 = new string('f', 64),
            }),
            RuntimeRpcPermissionState.Granted));
    }

    [Fact]
    public async Task RpcReadiness_RequiresCompatibleProviderAndEveryActionGrant()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var store = new RuntimeRpcPermissionStore(new RuntimePackagePaths(root));
            var descriptor = Contract();
            var caller = CreatePackage("caller.package", descriptor, null, usesContract: true);
            var provider = CreatePackage("provider.package", descriptor, new TestHandler(), usesContract: false);
            var session = CreateSession(caller, provider);
            var evaluator = new RuntimeRpcReadinessEvaluator(store);

            Assert.Equal(["caller.package"], evaluator.Evaluate(session));
            Assert.Equal(
                PackageReadinessState.Degraded,
                session.GetSessionPackages().Single(package => package.PackageId == "caller.package").Readiness);

            foreach (var action in new[]
                     {
                         SunderRpcProtocol.DiscoverAction,
                         SunderRpcProtocol.InvokeAction,
                         SunderRpcProtocol.SubscribeAction,
                     })
            {
                await store.SetAsync(
                    PermissionKey(action),
                    RuntimeRpcPermissionState.Granted,
                    CancellationToken.None);
            }
            Assert.Equal(["caller.package"], evaluator.Evaluate(session));
            Assert.Equal(
                PackageReadinessState.Ready,
                session.GetSessionPackages().Single(package => package.PackageId == "caller.package").Readiness);

            Assert.True(session.MarkPackageFailed(
                "provider.package",
                PackageFailureOrigin.RuntimeRpcProvider,
                "faulted",
                out _));
            Assert.Equal(["caller.package"], evaluator.Evaluate(session));
            Assert.Equal(
                PackageReadinessState.Degraded,
                session.GetSessionPackages().Single(package => package.PackageId == "caller.package").Readiness);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RpcReadiness_OwnerAndOptionalUsesDoNotRequirePublicGrant()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        try
        {
            using var store = new RuntimeRpcPermissionStore(new RuntimePackagePaths(root));
            var descriptor = Contract();
            var owner = CreatePackage(
                "owner.package",
                descriptor,
                new TestHandler(),
                usesContract: true,
                providerAudience: SunderPackageFormat.OwnerProviderAudience);
            var optional = CreatePackage("optional.package", descriptor, null, usesContract: true);
            var optionalUse = optional.RpcContractUses.Single();
            optional = optional with
            {
                RpcContractUses =
                [
                    new SunderPackageContractUseManifest
                    {
                        Role = optionalUse.Role,
                        ContractId = optionalUse.ContractId,
                        ContractVersion = optionalUse.ContractVersion,
                        AbiSha256 = optionalUse.AbiSha256,
                        Required = false,
                        Actions = optionalUse.Actions,
                    },
                ],
            };
            var evaluator = new RuntimeRpcReadinessEvaluator(store);
            var ownerSession = CreateSession(owner);
            var optionalSession = CreateSession(optional);

            Assert.Empty(evaluator.Evaluate(ownerSession));
            Assert.Empty(evaluator.Evaluate(optionalSession));
            Assert.All(ownerSession.GetSessionPackages(), package =>
                Assert.Equal(PackageReadinessState.Ready, package.Readiness));
            Assert.All(optionalSession.GetSessionPackages(), package =>
                Assert.Equal(PackageReadinessState.Ready, package.Readiness));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RpcReadiness_MetadataRefreshPreservesSessionGeneration()
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
        var paths = new RuntimePackagePaths(root);
        using var store = new RuntimeRpcPermissionStore(paths);
        var evaluator = new RuntimeRpcReadinessEvaluator(store);
        var events = new RuntimeEventStreamService();
        var owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            events,
            rpcReadiness: evaluator);
        var descriptor = Contract();
        var session = CreateSession(
            CreatePackage("caller.package", descriptor, null, usesContract: true),
            CreatePackage("provider.package", descriptor, new TestHandler(), usesContract: false));
        evaluator.Evaluate(session);
        try
        {
            await owner.PublishAsync(
                session,
                owner.Sources.Snapshot(),
                [],
                [],
                expectedGeneration: 0);
            var degraded = owner.GetSnapshot();
            Assert.Equal(1, degraded.SessionGeneration);
            Assert.Equal(
                PackageReadinessState.Degraded,
                degraded.SessionPackages.Single(package => package.PackageId == "caller.package").Readiness);

            foreach (var action in new[]
                     {
                         SunderRpcProtocol.DiscoverAction,
                         SunderRpcProtocol.InvokeAction,
                         SunderRpcProtocol.SubscribeAction,
                     })
            {
                await store.SetAsync(
                    PermissionKey(action),
                    RuntimeRpcPermissionState.Granted,
                    CancellationToken.None);
            }
            Assert.True(owner.RefreshRpcReadiness());
            var ready = owner.GetSnapshot();
            Assert.Equal(degraded.SessionGeneration, ready.SessionGeneration);
            Assert.True(ready.EventSequence > degraded.EventSequence);
            Assert.Equal(
                PackageReadinessState.Ready,
                ready.SessionPackages.Single(package => package.PackageId == "caller.package").Readiness);
            Assert.Contains(
                events.GetSnapshot(degraded.EventSequence).Events,
                runtimeEvent => runtimeEvent.Kind == RuntimeEventKind.SnapshotMetadataChanged
                                && runtimeEvent.SessionGeneration == degraded.SessionGeneration);

            await store.SetAsync(
                PermissionKey(SunderRpcProtocol.InvokeAction),
                RuntimeRpcPermissionState.Denied,
                CancellationToken.None);
            Assert.True(owner.RefreshRpcReadiness());
            Assert.Equal(
                PackageReadinessState.Degraded,
                owner.GetSnapshot().SessionPackages.Single(package => package.PackageId == "caller.package").Readiness);
        }
        finally
        {
            await owner.State.ClearActiveSessionAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, failure.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, failure.Error.RetrySafety);
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
    public async Task CallScope_ExpiryDuringRegistrationCleansPartialContentAndScope()
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
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var scope = await client.CreateCallScopeAsync(
            new SunderRpcCallOptions(clock.GetUtcNow().AddMinutes(1)));
        await using var source = new CancellationBlockingReadStream();
        var registration = scope.RegisterContentAsync(
            endpoint,
            source,
            new SunderRpcContentRegistrationOptions("application/octet-stream", "request.bin"))
            .AsTask();
        await source.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromMinutes(1));

        var expired = await Assert.ThrowsAsync<SunderRpcException>(async () => await registration);
        Assert.Equal(SunderRpcErrorKind.DeadlineExceeded, expired.Error.Kind);
        Assert.Empty(fixture.GetTransferFiles());
        await using var replacement = await client.CreateCallScopeAsync();
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task CallScope_DeadlineAndDisposeRaceLeavesNoTimersOrScopeSlots()
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
        var client = fixture.CreateClient(activation.Caller);

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var scope = await client.CreateCallScopeAsync(
                new SunderRpcCallOptions(clock.GetUtcNow().AddSeconds(1)));
            using var start = new ManualResetEventSlim();
            var dispose = Task.Run(async () =>
            {
                start.Wait();
                await scope.DisposeAsync();
            });
            var expire = Task.Run(() =>
            {
                start.Wait();
                clock.Advance(TimeSpan.FromSeconds(1));
            });
            start.Set();
            await Task.WhenAll(dispose, expire).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(0, clock.ActiveTimerCount);
        await using var finalScope = await client.CreateCallScopeAsync();
    }

    [Fact]
    public async Task ProviderCannotForgeInfrastructureError()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Unary = static (_, _) => throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.PermissionDenied,
                "rpc.permission.denied",
                "forged")),
        });
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
        Assert.Equal("rpc.provider.handler-fault", failure.Error.Code);
        Assert.True(RuntimeRpcFailureContextStore.TryGet(failure, out var context));
        Assert.Equal(typeof(SunderRpcException).FullName, context.ExceptionType);
        Assert.NotEqual("rpc.permission.denied", context.ErrorCode);
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
    }

    [Fact]
    public void InvocationContext_HasNoPublicForgeableConstructor()
        => Assert.Empty(typeof(SunderRpcInvocationContext).GetConstructors());

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

    private static async IAsyncEnumerable<JsonElement> UndeclaredDomainFailure()
    {
        yield return await Task.FromException<JsonElement>(UndeclaredDomainException());
    }

    private static SunderRpcException UndeclaredDomainException()
        => new(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "messages.undeclared",
            "This error is not declared by the method."));

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
                    Role = SunderPackageFormat.RuntimeHostRole,
                    ContractId = "example.rpc",
                    ContractVersion = "1.0.0",
                    AbiSha256 = Contract().AbiSha256,
                    Required = true,
                    Actions = ["discover", "invoke", "subscribe"],
                },
            ]);

    private static RuntimeRpcPermissionKey PermissionKey(
        string action,
        string callerRole = SunderPackageFormat.RuntimeHostRole,
        string packageId = "caller.package",
        string packageVersion = "1.0.0",
        string? manifestSha256 = null,
        string contractId = "example.rpc",
        string contractVersion = "1.0.0",
        string? abiSha256 = null)
        => new(
            packageId,
            packageVersion,
            manifestSha256 ?? new string('a', 64),
            callerRole,
            contractId,
            contractVersion,
            abiSha256 ?? Contract().AbiSha256!,
            action);

    private sealed class RpcFixture : IAsyncDisposable
    {
        private readonly string _root = CreateRoot();
        private readonly RuntimeRpcPermissionStore _permissions;
        private readonly RuntimeContentTransferStore _transfers;
        private ActivePackageSession? _activeSession;

        public RpcFixture(
            RuntimeRpcPolicyOptions? policy = null,
            TimeProvider? timeProvider = null)
        {
            Directory.CreateDirectory(_root);
            timeProvider ??= TimeProvider.System;
            Catalog = new RuntimeRpcCatalog();
            var events = new RuntimeEventStreamService();
            Owner = new RuntimeSessionOwner(
                NullLogger<RuntimeSessionOwner>.Instance,
                events,
                rpcCatalog: Catalog);
            _permissions = new RuntimeRpcPermissionStore(new RuntimePackagePaths(_root));
            _transfers = new RuntimeContentTransferStore(
                new RuntimePackagePaths(_root),
                timeProvider: timeProvider);
            AppSessions = new RuntimeRpcAppSessionManager(Owner.State, policy ?? new RuntimeRpcPolicyOptions());
            Broker = new RuntimeRpcBroker(
                Catalog,
                _permissions,
                Owner.State,
                Owner,
                policy,
                timeProvider,
                CancellationToken.None,
                AppSessions,
                _transfers,
                new RuntimeTransportPolicyOptions());
        }

        public RuntimeRpcCatalog Catalog { get; }
        public RuntimeSessionOwner Owner { get; }
        public RuntimeRpcBroker Broker { get; }
        public RuntimeRpcAppSessionManager AppSessions { get; }
        public string PermissionFilePath => new RuntimePackagePaths(_root).RpcPermissionFilePath;
        public string[] GetTransferFiles()
        {
            var path = new RuntimePackagePaths(_root).TransferRootPath;
            return Directory.Exists(path) ? Directory.GetFiles(path) : [];
        }

        public async Task<RpcActivation> PublishAsync(
            TestHandler handler,
            bool providerUsesContract = false,
            bool webCaller = false,
            PackageSourceKind callerSourceKind = PackageSourceKind.Installed,
            TestHandler? siblingHandler = null,
            string callerPackageId = "caller.package",
            TestHandler? healthyHandler = null,
            SunderRpcContractDescriptor? callerContract = null,
            bool runtimeCallerUsesContract = true,
            bool appCallerUsesContract = true,
            string providerAudience = SunderPackageFormat.PublicProviderAudience,
            SunderRpcContractDescriptor? providerContract = null)
        {
            var descriptor = providerContract ?? Contract();
            callerContract ??= descriptor;
            var caller = CreatePackage(
                callerPackageId,
                callerContract,
                null,
                usesContract: runtimeCallerUsesContract,
                sourceKind: callerSourceKind);
            if (webCaller)
            {
                caller = AddWebTarget(caller, callerContract, appCallerUsesContract);
            }
            var provider = CreatePackage(
                "provider.package",
                descriptor,
                handler,
                providerUsesContract,
                siblingHandler: siblingHandler,
                providerAudience: providerAudience);
            var healthy = healthyHandler is null
                ? null
                : CreatePackage(
                    "healthy.package",
                    descriptor,
                    healthyHandler,
                    usesContract: false,
                    providerId: "healthy.provider");
            var session = healthy is null
                ? CreateSession(caller, provider)
                : CreateSession(caller, provider, healthy);
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

        public Task GrantAsync(
            string packageId,
            string manifestSha256,
            string action,
            string callerRole = SunderPackageFormat.RuntimeHostRole)
            => _permissions.SetAsync(
                ActivePermissionKey(packageId, manifestSha256, callerRole, action),
                RuntimeRpcPermissionState.Granted,
                CancellationToken.None);

        public Task<(RuntimeRpcAppSessionDescriptor Session, string ManifestSha256, PackageTargetDescriptor Target)> OpenAppCallerAsync(
            string packageId = "caller.package")
        {
            var source = _activeSession!.LoadedPackageMap[packageId].Source;
            var target = source.Manifest!.Targets!.Single()!;
            var manifestSha256 = RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex!);
            var descriptor = PackageTargetSelection.ToDescriptor(target);
            return OpenAsync();

            async Task<(RuntimeRpcAppSessionDescriptor, string, PackageTargetDescriptor)> OpenAsync()
            {
                var session = await AppSessions.OpenAsync(
                    new RuntimeRpcAppSessionOpenRequest(
                        packageId,
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
                ActivePermissionKey(
                    "caller.package",
                    new string('a', 64),
                    SunderPackageFormat.RuntimeHostRole,
                    action),
                RuntimeRpcPermissionState.Denied,
                CancellationToken.None);

        private RuntimeRpcPermissionKey ActivePermissionKey(
            string packageId,
            string manifestSha256,
            string callerRole,
            string action)
        {
            var package = _activeSession!.LoadedPackageMap[packageId];
            var uses = callerRole == SunderPackageFormat.RuntimeHostRole
                ? package.RpcContractUses
                : package.Source.Manifest!.UsesContracts ?? [];
            var use = uses.Where(static candidate => candidate is not null)
                .Select(static candidate => candidate!)
                .Single(candidate =>
                string.Equals(candidate.Role, callerRole, StringComparison.Ordinal)
                && string.Equals(candidate.ContractId, "example.rpc", StringComparison.Ordinal)
                && (candidate.Actions ?? []).Contains(action, StringComparer.Ordinal));
            return new RuntimeRpcPermissionKey(
                package.Descriptor.PackageId,
                package.Descriptor.Version,
                manifestSha256,
                callerRole,
                use.ContractId!,
                use.ContractVersion!,
                use.AbiSha256!,
                action);
        }

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
            _transfers.Dispose();
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
        SunderRpcContractDescriptor descriptor,
        bool usesContract)
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
            UsesContracts = usesContract
                ? (package.Source.Manifest.UsesContracts ?? [])
                    .Concat(
                    [
                        new SunderPackageContractUseManifest
                        {
                            Role = SunderPackageFormat.AppHostRole,
                            ContractId = descriptor.ContractId,
                            ContractVersion = descriptor.Version,
                            AbiSha256 = descriptor.AbiSha256,
                            Required = true,
                            Actions = ["discover", "invoke", "subscribe"],
                        },
                    ])
                    .ToArray()
                : package.Source.Manifest.UsesContracts,
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
                    RequiredHostCapabilities = ["sdk-baseline-1-1.v1", "rpc.v2"],
                    Views =
                    [
                        new SunderPackageWebViewManifest
                        {
                            ViewId = package.Source.Manifest!.Id + ".main",
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
        PackageSourceKind sourceKind = PackageSourceKind.Installed,
        TestHandler? siblingHandler = null,
        string providerId = "example.provider",
        string providerAudience = SunderPackageFormat.PublicProviderAudience)
    {
        var root = CreateRoot();
        Directory.CreateDirectory(root);
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
                    DescriptorSha256 = descriptor.Sha256,
                    AbiSha256 = descriptor.AbiSha256,
                    Exposure = SunderPackageFormat.PublicContractExposure,
                },
            ],
            UsesContracts = usesContract
                ?
                [
                    new SunderPackageContractUseManifest
                    {
                        Role = SunderPackageFormat.RuntimeHostRole,
                        ContractId = descriptor.ContractId,
                        ContractVersion = descriptor.Version,
                        AbiSha256 = descriptor.AbiSha256,
                        Required = true,
                        Actions = ["discover", "invoke", "subscribe"],
                    },
                ]
                : [],
            Provides = handler is null
                ? []
                : siblingHandler is null
                    ? [ProviderManifest(descriptor, providerId, providerAudience)]
                    :
                    [
                        ProviderManifest(descriptor, providerId, providerAudience),
                        ProviderManifest(descriptor, "example.sibling", providerAudience),
                    ],
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
            new LocalPackageFileStore(Path.Combine(root, "files")),
            EmptyTestPackageSettings.Instance,
            new TestRuntimePackageWorker())
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
            var handlers = new Dictionary<string, TestHandler>(StringComparer.Ordinal)
            {
                [providerId] = handler,
            };
            if (siblingHandler is not null) handlers.Add("example.sibling", siblingHandler);
            package = package with
            {
                RpcProviders = manifest.Provides!.Select(static declaration => declaration!)
                    .ToDictionary(
                        static declaration => declaration.ProviderId!,
                        declaration => new RuntimeRpcProviderRegistration(
                            declaration.ProviderId!,
                            descriptor.ContractId,
                            descriptor.Version,
                            descriptor.Sha256,
                            descriptor,
                            handlers[declaration.ProviderId!],
                            declaration),
                        StringComparer.Ordinal),
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
                     0,
                     package.RpcContractUses.Select(use => new PackageRpcContractUseDescriptor(
                         use.Role!,
                         use.ContractId!,
                         use.ContractVersion!,
                         use.AbiSha256!,
                         use.Required == true,
                         (use.Actions ?? []).Where(static action => action is not null).Select(static action => action!).ToArray()))
                         .ToArray()),
                 StringComparer.OrdinalIgnoreCase));

    private static SunderPackageProviderManifest ProviderManifest(
        SunderRpcContractDescriptor descriptor,
        string providerId = "example.provider",
        string audience = SunderPackageFormat.PublicProviderAudience)
        => new()
        {
            ProviderId = providerId,
            ContractId = descriptor.ContractId,
            ContractVersion = descriptor.Version,
            DescriptorSha256 = descriptor.Sha256,
            AbiSha256 = descriptor.AbiSha256,
            Role = SunderPackageFormat.RuntimeHostRole,
            Audience = audience,
        };

    private static SunderRpcContractDescriptor Contract(
        string version = "1.0.0",
        int requestMaximumLength = 128,
        string? requestTitle = null,
        string unaryIdempotency = "none")
    {
        var title = requestTitle is null
            ? string.Empty
            : $",\"title\":{JsonSerializer.Serialize(requestTitle)}";
        return SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes($$$$"""
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "descriptorVersion":2,
              "contractId":"example.rpc",
              "version":"{{{{version}}}}",
              "errors":{"messages.rejected":{"description":"The message was rejected."}},
              "services":{"messages":{"methods":{
                "send":{"kind":"unary","idempotency":"{{{{unaryIdempotency}}}}","errors":["messages.rejected"],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}},
                "watch":{"kind":"server-stream","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"eventSchema":{"$ref":"#/$defs/Response"}}
              }}},
              "$defs":{
                "Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":{{{{requestMaximumLength}}}}}},"required":["message"],"additionalProperties":false{{{{title}}}}},
                "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}
              }
            }
            """));
    }

    private sealed record RpcActivation(
        RuntimeRpcCallerStamp Caller,
        RuntimeRpcCallerStamp Provider,
        TestHandler Handler,
        ActivePackageSession Session);

    private sealed class CancellationBlockingReadStream : Stream
    {
        private int _reads;

        public TaskCompletionSource Blocked { get; } = new(
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
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public int ActiveTimerCount
        {
            get
            {
                lock (_gate) return _timers.Count(timer => !timer.IsDisposed);
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                ChangeLocked(timer, dueTime, period);
            }
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
            lock (_gate) _utcNow += duration;
            while (true)
            {
                ManualTimer[] due;
                lock (_gate)
                {
                    due = _timers
                        .Where(timer => !timer.IsDisposed && timer.DueAtUtc is { } at && at <= _utcNow)
                        .ToArray();
                    foreach (var timer in due)
                    {
                        if (timer.Period == Timeout.InfiniteTimeSpan)
                        {
                            timer.DueAtUtc = null;
                        }
                        else
                        {
                            do
                            {
                                timer.DueAtUtc += timer.Period;
                            } while (timer.DueAtUtc <= _utcNow);
                        }
                    }
                }
                if (due.Length == 0) return;
                foreach (var timer in due) timer.Invoke();
            }
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (timer.IsDisposed) return false;
                ChangeLocked(timer, dueTime, period);
                return true;
            }
        }

        private void ChangeLocked(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            timer.DueAtUtc = dueTime == Timeout.InfiniteTimeSpan ? null : _utcNow + dueTime;
            timer.Period = period;
        }

        private void Dispose(ManualTimer timer)
        {
            lock (_gate)
            {
                if (timer.IsDisposed) return;
                timer.IsDisposed = true;
                timer.DueAtUtc = null;
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            public bool IsDisposed { get; set; }
            public DateTimeOffset? DueAtUtc { get; set; }
            public TimeSpan Period { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
                => owner.Change(this, dueTime, period);

            public void Dispose() => owner.Dispose(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Invoke()
            {
                if (!IsDisposed) callback(state);
            }
        }
    }

    private sealed class TestHandler : ISunderRpcServiceHandler
    {
        public Func<JsonElement, CancellationToken, ValueTask<JsonElement>> Unary { get; set; } =
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
            };

        public Func<SunderRpcInvocationContext, JsonElement, CancellationToken, ValueTask<JsonElement>>? UnaryWithContext { get; set; }

        public Func<JsonElement, CancellationToken, IAsyncEnumerable<JsonElement>> Stream { get; set; } =
            static (_, cancellationToken) => Single(cancellationToken);

        public ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken = default)
            => UnaryWithContext is null
                ? Unary(request, cancellationToken)
                : UnaryWithContext(context, request, cancellationToken);

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

    private sealed class SecretBearingProviderException(string message) : Exception(message);
}
