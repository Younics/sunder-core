using Sunder.App.Views;
using Xunit;

namespace Sunder.App.Tests;

public sealed class WizardDialogLaunchCoordinatorTests
{
    [Fact]
    public async Task ConcurrentLaunch_IsIgnoredWhileFirstDialogIsActive()
    {
        var coordinator = new WizardDialogLaunchCoordinator();
        var dialogEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDialog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputEnabled = true;
        var suppressionCount = 0;
        var dialogCount = 0;

        Action SuppressInput()
        {
            suppressionCount++;
            inputEnabled = false;
            return () => inputEnabled = true;
        }

        var firstLaunch = coordinator.RunAsync(
            SuppressInput,
            () => Task.CompletedTask,
            () => true,
            async () =>
            {
                dialogCount++;
                dialogEntered.SetResult();
                await completeDialog.Task;
                return null;
            });
        await dialogEntered.Task;

        await coordinator.RunAsync(
            SuppressInput,
            () => Task.CompletedTask,
            () => true,
            () =>
            {
                dialogCount++;
                return Task.FromResult<Func<Task>?>(null);
            });

        Assert.True(coordinator.IsActive);
        Assert.False(inputEnabled);
        Assert.Equal(1, suppressionCount);
        Assert.Equal(1, dialogCount);
        Assert.True(coordinator.HandleCloseRequest());

        completeDialog.SetResult();
        await firstLaunch;

        Assert.False(coordinator.IsActive);
        Assert.True(inputEnabled);
        Assert.False(coordinator.HandleCloseRequest());
    }

    [Fact]
    public async Task DialogFailure_RestoresInputAndAllowsAnotherLaunch()
    {
        var coordinator = new WizardDialogLaunchCoordinator();
        var inputEnabled = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(
            () =>
            {
                inputEnabled = false;
                return () => inputEnabled = true;
            },
            () => Task.CompletedTask,
            () => true,
            () => Task.FromException<Func<Task>?>(new InvalidOperationException("dialog failed"))));

        Assert.False(coordinator.IsActive);
        Assert.True(inputEnabled);

        var dialogCount = 0;
        await coordinator.RunAsync(
            () => static () => { },
            () => Task.CompletedTask,
            () => true,
            () =>
            {
                dialogCount++;
                return Task.FromResult<Func<Task>?>(null);
            });

        Assert.Equal(1, dialogCount);
    }

    [Fact]
    public async Task SuccessfulDialog_RestoresInputAndReleasesGateBeforeFollowUp()
    {
        var coordinator = new WizardDialogLaunchCoordinator();
        var calls = new List<string>();
        var inputEnabled = true;

        await coordinator.RunAsync(
            () =>
            {
                calls.Add("suppress");
                inputEnabled = false;
                return () =>
                {
                    calls.Add("restore");
                    inputEnabled = true;
                };
            },
            () => Task.CompletedTask,
            () => true,
            () =>
            {
                calls.Add("dialog");
                return Task.FromResult<Func<Task>?>(() =>
                {
                    Assert.True(inputEnabled);
                    Assert.False(coordinator.IsActive);
                    calls.Add("follow-up");
                    return Task.CompletedTask;
                });
            });

        Assert.Equal(["suppress", "dialog", "restore", "follow-up"], calls);
    }

    [Fact]
    public async Task OwnerUnavailableAfterPreparation_SkipsDialogAndRestoresInput()
    {
        var coordinator = new WizardDialogLaunchCoordinator();
        var canShow = true;
        var inputEnabled = true;
        var dialogCount = 0;

        await coordinator.RunAsync(
            () =>
            {
                inputEnabled = false;
                return () => inputEnabled = true;
            },
            () => Task.CompletedTask,
            () => canShow,
            () =>
            {
                dialogCount++;
                return Task.FromResult<Func<Task>?>(null);
            },
            _ =>
            {
                canShow = false;
                return Task.FromResult(true);
            });

        Assert.Equal(0, dialogCount);
        Assert.True(inputEnabled);
        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public async Task CloseDuringPreparation_CancelsLaunchAndAllowsWindowClose()
    {
        var coordinator = new WizardDialogLaunchCoordinator();
        var preparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inputEnabled = true;
        var dialogCount = 0;
        var launch = coordinator.RunAsync(
            () =>
            {
                inputEnabled = false;
                return () => inputEnabled = true;
            },
            () => Task.CompletedTask,
            () => true,
            () =>
            {
                dialogCount++;
                return Task.FromResult<Func<Task>?>(null);
            },
            async cancellationToken =>
            {
                preparationEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            });
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(coordinator.HandleCloseRequest());
        await launch.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, dialogCount);
        Assert.True(inputEnabled);
        Assert.False(coordinator.IsActive);
    }
}
