using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeRpcBrokerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedPublicationKeepsIssuedBindingInflightMutationStreamAndScopeContent(bool reject)
    {
        await using var fixture = new RpcFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        SunderRpcContentReference? content = null;
        var handler = new TestHandler
        {
            UnaryWithContext = async (context, _, token) =>
            {
                Interlocked.Increment(ref dispatches);
                await using var bytes = new MemoryStream([1, 2, 3]);
                content = await context.RegisterContentAsync(bytes,
                    new("application/octet-stream", "scope.bin", bytes.Length), token);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return JsonSerializer.SerializeToElement(new { accepted = true });
            },
            Stream = (_, token) => Stream(token),
        };
        var active = await fixture.PublishAsync(handler, webCaller: true, transform: package => package with
        {
            Worker = new PublicationWorker(), ActivationSessionGeneration = 1,
            Source = package.Source with { ContentIdentity = new string('a', 64), ContentIndex = package.Source.ContentIndex ?? new SunderPackageContentIndex(1, []) },
        });
        foreach (var action in new[] { "discover", "invoke", "subscribe" }) await fixture.GrantAsync(action);
        var client = fixture.CreateClient(active.Caller);
        await using var bindings = new SunderRpcServiceBindingLifetime(client);
        var snapshot = await bindings.DiscoverAsync("example.rpc", deadline.Token);
        var original = Assert.Single(snapshot.Providers);
        var issued = bindings.CreateProvider(original, static (_, _, _) => new object());
        var callerPackage = active.Session.LoadedPackageMap[active.Caller.PackageId];
        using var dataOperation = Assert.IsType<RuntimeRpcActivationLease>(fixture.Broker.AcquirePublishedPackageDataLease(
            active.Caller, callerPackage.ActivationSessionGeneration!.Value, callerPackage.Worker));
        Assert.Throws<RuntimeUnavailableException>(() => fixture.Broker.AcquirePublishedPackageDataLease(
            active.Caller, callerPackage.ActivationSessionGeneration.Value + 1, callerPackage.Worker));
        Assert.Throws<RuntimeUnavailableException>(() => fixture.Broker.AcquirePublishedPackageDataLease(
            active.Caller, callerPackage.ActivationSessionGeneration.Value, new PublicationWorker()));
        await using var scope = await client.CreateCallScopeAsync(cancellationToken: deadline.Token);
        var app = await fixture.OpenAppCallerAsync();
        foreach (var action in new[] { "discover", "invoke", "subscribe" })
            await fixture.GrantAsync("caller.package", app.ManifestSha256, action, SunderPackageFormat.AppHostRole);
        var appState = fixture.Broker.CreateAppCallScope(app.Session.SessionId, null, deadline.Token);
        await using var appScope = new RuntimeRpcCallScope(fixture.Broker, appState);
        await using var appStream = appScope.SubscribeAsync(original.Endpoint, "messages", "watch",
            JsonSerializer.SerializeToElement(new { message = "app stream" }), cancellationToken: deadline.Token).GetAsyncEnumerator(deadline.Token);
        Assert.True(await appStream.MoveNextAsync());
        var appNext = appStream.MoveNextAsync().AsTask();
        await using var stream = client.SubscribeAsync(original.Endpoint, "messages", "watch",
            JsonSerializer.SerializeToElement(new { message = "stream" }), cancellationToken: deadline.Token).GetAsyncEnumerator(deadline.Token);
        Assert.True(await stream.MoveNextAsync());
        var next = stream.MoveNextAsync().AsTask();
        var mutation = scope.InvokeAsync(original.Endpoint, "messages", "send",
            JsonSerializer.SerializeToElement(new { message = "mutation" }), cancellationToken: deadline.Token).AsTask();
        await entered.Task.WaitAsync(deadline.Token);
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var before = fixture.Owner.State.ActiveSession;
                var candidate = before.CreateRetainedRemovalCandidate(before.GetActivePackageSources());
                await candidate.RetainUnchangedWorkersAsync(before, [], deadline.Token);
                var publication = await fixture.Owner.PreparePublicationAsync(candidate, fixture.Owner.Generation, deadline.Token);
                if (reject)
                {
                    fixture.Owner.DiscardPublication(publication);
                    candidate.CancelRetentionReservation();
                    await candidate.DisposeAsync();
                }
                else
                {
                    await fixture.Owner.CommitPublicationAsync(publication, fixture.Owner.Sources.Snapshot(), [], [],
                        generation => fixture.Catalog.ActivateSession(candidate, generation));
                }
                Assert.Equal(original.Endpoint, Assert.Single(fixture.Catalog.GetSnapshot().Providers).Endpoint);
                Assert.Equal(original.SessionGeneration, Assert.Single(fixture.Catalog.GetSnapshot().Providers).SessionGeneration);
                Assert.False(issued.RetirementToken.IsCancellationRequested);
                Assert.False(dataOperation.RetirementToken.IsCancellationRequested);
                await issued.EnsureCurrentAsync(deadline.Token);
                Assert.False(mutation.IsCompleted);
                Assert.False(next.IsCompleted);
                Assert.False(appNext.IsCompleted);
                Assert.Single((await appScope.DiscoverAsync("example.rpc", deadline.Token)).Providers);
            }
            await using var opened = await scope.OpenContentAsync(Assert.IsType<SunderRpcContentReference>(content), deadline.Token);
            Assert.Equal(1, opened.ReadByte());
            release.TrySetResult();
            Assert.True((await mutation.WaitAsync(deadline.Token)).GetProperty("accepted").GetBoolean());
            Assert.False(await next.WaitAsync(deadline.Token));
            Assert.False(await appNext.WaitAsync(deadline.Token));
            Assert.Equal(1, dispatches);
            Assert.True(fixture.Catalog.DeactivatePackage(original.PackageId, original.ActivationId, faulted: false));
            await Assert.ThrowsAsync<SunderRpcException>(async () => await issued.EnsureCurrentAsync(deadline.Token));
            Assert.True(issued.RetirementToken.IsCancellationRequested);
            Assert.False(dataOperation.RetirementToken.IsCancellationRequested);
            Assert.True(fixture.Catalog.DeactivatePackage(active.Caller.PackageId, active.Caller.ActivationId, faulted: false));
            Assert.True(dataOperation.RetirementToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
            await mutation;
            await next;
            await appNext;
        }

        async IAsyncEnumerable<JsonElement> Stream([EnumeratorCancellation] CancellationToken token)
        {
            yield return JsonSerializer.SerializeToElement(new { accepted = true });
            await release.Task.WaitAsync(token);
        }
    }
}
