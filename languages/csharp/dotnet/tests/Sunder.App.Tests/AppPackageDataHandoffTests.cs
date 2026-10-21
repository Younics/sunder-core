using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageDataHandoffTests
{
    [Fact]
    public async Task CancellationBeforeAdmissionDoesNotInvokeTheMutationOrPoisonSettlement()
    {
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoked = false;
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication.RunDataMutationAsync(() =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(invoked);
        using var handoff = publication.BeginDataHandoff();
        await handoff.Drained;
        publication.Revoke();
        Assert.True(await publication.CompleteDataMutationsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettlementCannotFinishOnFirstCompletionWhileAnotherAcceptedWriteStillOwnsResources(bool firstFailed)
    {
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = publication.RunDataMutationAsync(() => first.Task);
        var two = publication.RunDataMutationAsync(() => second.Task);
        using var handoff = publication.BeginDataHandoff();
        publication.Revoke();
        var completion = publication.CompleteDataMutationsAsync();
        try
        {
            if (firstFailed) first.SetException(new IOException("Uncertain first write."));
            else first.SetResult();
            if (firstFailed) await Assert.ThrowsAsync<IOException>(() => one);
            else await one;
            Assert.False(handoff.Drained.IsCompleted);
            Assert.False(completion.IsCompleted);
        }
        finally { first.TrySetResult(); second.TrySetResult(); }
        await two;
        if (firstFailed) await Assert.ThrowsAsync<InvalidOperationException>(() => handoff.Drained);
        else await handoff.Drained;
        Assert.Equal(!firstFailed, await completion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffClosesNewAdmissionAndRequiresAcknowledgementOfEveryAcceptedWrite(bool failedWrite)
    {
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = "initial";
        var write = publication.RunDataMutationAsync(async () =>
        {
            entered.TrySetResult();
            await finish.Task;
            if (failedWrite) throw new IOException("Uncertain write result must not be reported as successful settlement.");
            value = "old-final";
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var handoff = publication.BeginDataHandoff();
        try
        {
            Assert.False(handoff.Drained.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => handoff.Commit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => publication.RunDataMutationAsync(() =>
            {
                value = "not-admitted";
                return Task.CompletedTask;
            }));
            Assert.Equal("initial", value);
        }
        finally { finish.TrySetResult(); }

        if (failedWrite)
        {
            await Assert.ThrowsAsync<IOException>(() => write);
            await Assert.ThrowsAsync<InvalidOperationException>(() => handoff.Drained);
            Assert.Throws<InvalidOperationException>(() => handoff.Commit());
            handoff.Dispose();
            using (var rejected = publication.BeginDataHandoff())
                await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.Drained);
            // A failed write blocks unproven replacement, not ordinary recovery
            // inside the still-published activation. A later success alone cannot
            // prove that the failed remote request has finished on the Runtime.
            await publication.RunDataMutationAsync(() => Task.CompletedTask);
            publication.Revoke();
            Assert.False(await publication.CompleteDataMutationsAsync());
        }
        else
        {
            await write;
            await handoff.Drained;
            handoff.Commit();
            Assert.True(publication.IsRevoked);
            handoff.Dispose();
            await Assert.ThrowsAsync<InvalidOperationException>(() => publication.RunDataMutationAsync(() => Task.CompletedTask));
            var replacement = new AppPackageGenerationPublication();
            replacement.Publish();
            await replacement.RunDataMutationAsync(() => { value = "replacement"; return Task.CompletedTask; });
            Assert.Equal("replacement", value);
            Assert.True(await publication.CompleteDataMutationsAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortedHandoffReopensOnlyAnActivationWhichWasNotIndependentlyRevoked(bool revoke)
    {
        var publication = new AppPackageGenerationPublication();
        publication.Publish();
        using var first = publication.BeginDataHandoff();
        using var second = publication.BeginDataHandoff();
        await Task.WhenAll(first.Drained, second.Drained);
        if (revoke) publication.Revoke();
        first.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => publication.RunDataMutationAsync(() => Task.CompletedTask));
        second.Dispose();
        if (revoke)
            await Assert.ThrowsAsync<InvalidOperationException>(() => publication.RunDataMutationAsync(() => Task.CompletedTask));
        else await publication.RunDataMutationAsync(() => Task.CompletedTask);
    }
}
