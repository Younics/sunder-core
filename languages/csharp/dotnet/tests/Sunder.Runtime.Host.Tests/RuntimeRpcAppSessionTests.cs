using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcAppSessionTests
{
    [Theory]
    [InlineData("close")]
    [InlineData("generation")]
    [InlineData("request")]
    public async Task AppCallerSession_IsManifestAndGenerationFencedAndCloseCancelsActiveCall(string boundary)
    {
        await using var fixture = new RpcFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        var handler = new TestHandler
        {
            Unary = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref dispatches);
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
        };
        await fixture.PublishAsync(new TestHandler(), webCaller: true, healthyHandler: handler);
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
            CancellationToken.None)).Providers, provider => provider.PackageId == "healthy.package").Endpoint;
        using var cancellation = new CancellationTokenSource();
        var call = fixture.Broker.InvokeAppAsync(
            session.SessionId,
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "wait" }),
            options: null,
            cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (boundary == "close") fixture.AppSessions.Close(session.SessionId);
        else if (boundary == "request") await cancellation.CancelAsync();
        else
        {
            // A provider in this App's effective import closure faults, while
            // the called healthy provider and its execution remain live.
            PackageActivationIdentity identity;
            using (var lease = fixture.Owner.State.AcquireLease())
            {
                var package = Assert.IsType<ActiveLoadedPackage>(fixture.Owner.State.GetLoadedPackage(lease, "provider.package"));
                identity = lease.GetPackageActivationIdentity(package);
            }
            Assert.True(fixture.Owner.HandleRpcProviderFault("provider.package", identity,
                new InvalidOperationException("retire unrelated package"), "rpc.provider.test"));
        }

        var cancelled = await Assert.ThrowsAsync<SunderRpcException>(async () => await call);
        Assert.Equal(boundary == "request" ? SunderRpcErrorKind.Cancelled : SunderRpcErrorKind.Unavailable, cancelled.Error.Kind);
        Assert.Equal(boundary == "request" ? "rpc.call.cancelled" : "rpc.app-session.retired", cancelled.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, cancelled.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, cancelled.Error.RetrySafety);
        Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(cancelled));
        Assert.Equal(1, dispatches); // Classification is not permission to replay a mutation.
        Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers,
            provider => provider.PackageId == "healthy.package").State);
        if (boundary != "request")
        {
            var retired = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await fixture.Broker.DiscoverAppAsync(session.SessionId, "example.rpc", CancellationToken.None));
            Assert.Equal(SunderRpcErrorKind.Unavailable, retired.Error.Kind);
            Assert.Equal("rpc.app-session.retired", retired.Error.Code);
        }
        else Assert.Contains((await fixture.Broker.DiscoverAppAsync(session.SessionId, "example.rpc", CancellationToken.None)).Providers,
            provider => provider.Endpoint == endpoint);
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

    [Theory]
    [InlineData("close")]
    [InlineData("generation")]
    [InlineData("request")]
    [InlineData("scope")]
    [InlineData("permission")]
    public async Task CatalogWatch_AppCallerRetirementRetainsItsCauseWithoutHidingOtherCancellation(string boundary)
    {
        await using var fixture = new RpcFixture();
        await fixture.PublishAsync(new TestHandler(), webCaller: true, healthyHandler: new TestHandler());
        var app = await fixture.OpenAppCallerAsync();
        await fixture.GrantAsync("caller.package", app.ManifestSha256, SunderRpcProtocol.DiscoverAction, SunderPackageFormat.AppHostRole);
        var snapshot = await fixture.Broker.DiscoverAppAsync(app.Session.SessionId, "example.rpc", default);
        using var cancellation = new CancellationTokenSource();
        var scope = boundary == "scope" ? fixture.Broker.CreateAppCallScope(app.Session.SessionId, null, default) : null;
        var stream = scope is null
            ? fixture.Broker.WatchAppAsync(app.Session.SessionId, snapshot.Revision + 1, snapshot.Sequence, cancellation.Token)
            : fixture.Broker.WatchScopeAsync(scope, snapshot.Revision + 1, snapshot.Sequence, cancellation.Token);
        await using var watch = stream.GetAsyncEnumerator(cancellation.Token);
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal(SunderRpcCatalogEventKind.ResetRequired, watch.Current.Kind); // Real subscription admitted.
        var stopped = DrainAsync();
        if (boundary == "close") fixture.AppSessions.Close(app.Session.SessionId);
        else if (boundary == "request") await cancellation.CancelAsync();
        else if (boundary == "scope") await fixture.Broker.CloseAppCallScopeAsync(app.Session.SessionId, scope!.Id);
        else if (boundary == "permission") await fixture.RevokeAsync(SunderRpcProtocol.DiscoverAction, app.ManifestSha256, SunderPackageFormat.AppHostRole);
        else
        {
            PackageActivationIdentity identity;
            using (var lease = fixture.Owner.State.AcquireLease())
            {
                var package = Assert.IsType<ActiveLoadedPackage>(fixture.Owner.State.GetLoadedPackage(lease, "provider.package"));
                identity = lease.GetPackageActivationIdentity(package);
            }
            Assert.True(fixture.Owner.HandleRpcProviderFault("provider.package", identity,
                new InvalidOperationException("unrelated package fault"), "rpc.provider.test"));
        }
        if (boundary is "close" or "generation")
        {
            var failure = await Assert.ThrowsAsync<SunderRpcException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("rpc.app-session.retired", failure.Error.Code);
            Assert.Equal(SunderRpcErrorKind.Unavailable, failure.Error.Kind);
            Assert.Equal(SunderRpcProviderState.Active, Assert.Single(fixture.Catalog.GetSnapshot().Providers,
                provider => provider.PackageId == "healthy.package").State);
        }
        else
        {
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(failure.CancellationToken.IsCancellationRequested);
            Assert.NotNull(fixture.AppSessions.GetRequired(app.Session.SessionId));
        }
        async Task DrainAsync() { while (await watch.MoveNextAsync()) { } }
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
            fixture.Catalog.ActivateSession(ActivePackageSession.CreateEmpty(), fixture.Owner.Generation + 1);
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
}
