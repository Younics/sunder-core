using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RuntimeRpcBrokerTests
{
    [Theory]
    [InlineData("exact")]
    [InlineData("birth")]
    [InlineData("published-generation")]
    [InlineData("activation")]
    [InlineData("worker")]
    [InlineData("successor-birth")]
    [InlineData("successor-worker")]
    [InlineData("retired")]
    public async Task PublicationDrainRequiresExactPreviousAndSuccessorAuthorityAndCancellationReopensAdmission(string mismatch)
    {
        await using var fixture = new RpcFixture();
        await fixture.Owner.State.PublishSessionAsync(ActivePackageSession.CreateEmpty());
        var worker = new PublicationWorker();
        var activation = await fixture.PublishAsync(new TestHandler(), transform: package =>
            package.Descriptor.PackageId == "caller.package"
                ? package with { Worker = worker, ActivationSessionGeneration = 1 }
                : package);
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        var previous = activation.Session.LoadedPackageMap["caller.package"];
        var next = mismatch switch
        {
            "successor-birth" => previous with { ActivationSessionGeneration = 2 },
            "successor-worker" => previous with { Worker = new PublicationWorker() },
            _ => previous,
        };
        var successor = CreateSession(next);
        var callerPackage = mismatch switch
        {
            "activation" => previous with { RuntimeActivationId = Guid.NewGuid() },
            "worker" => previous with { Worker = new PublicationWorker() },
            _ => previous,
        };
        var caller = new RuntimeRpcPackageActivation(callerPackage,
            sessionGeneration: mismatch == "published-generation" ? 1 : fixture.Owner.Generation,
            activationGeneration: mismatch == "birth" ? 2 : 1);
        if (mismatch == "retired") await caller.Retire();
        using var held = fixture.Owner.State.AcquireLease();
        using var cancel = new CancellationTokenSource();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = held.RetirementToken.Register(() => drain.TrySetResult());
        var preparation = fixture.Owner.State.PreparePublicationAsync(successor, fixture.Owner.Generation, cancel.Token);
        try
        {
            await drain.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(preparation.IsCompleted);
            Assert.True(fixture.Owner.Generation > previous.ActivationSessionGeneration);
            Assert.Throws<RuntimeUnavailableException>(() => fixture.Owner.State.AcquireLease());
            if (mismatch == "exact")
            {
                Assert.Null(fixture.Owner.State.AcquireRpcLease(caller));
                var error = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                    await fixture.CreateClient(activation.Caller).DiscoverAsync("example.rpc"));
                Assert.Equal(SunderRpcErrorKind.Unavailable, error.Error.Kind);
                Assert.Equal("rpc.session.transitioning", error.Error.Code);
                Assert.Equal(SunderRpcDispatchState.NotDispatched, error.Error.DispatchState);
                Assert.Equal(SunderRpcRetrySafety.Safe, error.Error.RetrySafety);
                var forged = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                    await fixture.CreateClient(new("caller.package", Guid.NewGuid())).DiscoverAsync("example.rpc"));
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, forged.Error.Kind);
            }
            else
            {
                // Only an exact continuing birth gets the temporary-fence mapping;
                // ordinary unavailable/stale handling remains for every mismatch.
                Assert.Throws<RuntimeUnavailableException>(() => fixture.Owner.State.AcquireRpcLease(caller));
            }
        }
        finally
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            held.Dispose();
            await successor.DisposeAsync();
        }
        Assert.Single((await fixture.CreateClient(activation.Caller).DiscoverAsync("example.rpc")).Providers);
        using var reopened = fixture.Owner.State.AcquireRpcLease(new(previous, fixture.Owner.Generation, activationGeneration: 1));
        Assert.NotNull(reopened);
        Assert.Same(activation.Session, reopened.Session);
    }

    private sealed class PublicationWorker : TestRuntimePackageWorker
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task GenerationCompletion => _completion.Task;
    }
}
