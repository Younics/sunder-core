using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ShellOwnershipArchitectureTests
{
    [Fact]
    public void SuccessfulShell_HasOneAggregateOwnerAndStartupTransfersThatOwner()
    {
        var sessionFields = InstanceFields(typeof(ShellSession));
        Assert.Contains(sessionFields, field => field.FieldType == typeof(MainWindow));
        Assert.Contains(sessionFields, field => field.FieldType == typeof(MainWindowViewModel));
        Assert.Contains(sessionFields, field => field.FieldType == typeof(WindowLauncher));
        Assert.Contains(sessionFields, field => field.FieldType == typeof(RuntimeEventSubscriptionService));
        Assert.Contains(sessionFields, field => field.FieldType == typeof(PackageViewHostService));
        Assert.Contains(sessionFields, field => field.FieldType == typeof(ServiceProvider));
        Assert.Contains(sessionFields, field => field.FieldType == typeof(OwnedTaskObserver));

        var startupFields = InstanceFields(typeof(ShellStartupResult));
        Assert.Collection(
            startupFields,
            field => Assert.Equal(typeof(ShellSession), field.FieldType),
            field => Assert.Equal(typeof(DevPackageOwnerSession), field.FieldType));

        var appFields = InstanceFields(typeof(Sunder.App.App));
        Assert.Single(appFields, field => field.FieldType == typeof(ShellSession));
        Assert.DoesNotContain(appFields, field => field.FieldType == typeof(WindowLauncher));
        Assert.DoesNotContain(appFields, field => field.FieldType == typeof(RuntimeEventSubscriptionService));
        Assert.DoesNotContain(appFields, field => field.FieldType == typeof(PackageViewHostService));
        Assert.DoesNotContain(appFields, field => field.FieldType == typeof(MainWindowViewModel));
    }

    [Fact]
    public void PackageHost_DelegatesGenerationResponsibilitiesToOwnedComponents()
    {
        var hostFields = InstanceFields(typeof(PackageViewHostService));
        Assert.Contains(hostFields, field => field.FieldType == typeof(AppPackageGenerationBuilder));
        Assert.Contains(hostFields, field => field.FieldType == typeof(AppPackageGenerationPublisher));
        Assert.Contains(hostFields, field => field.FieldType == typeof(PackageIconGenerationCoordinator));
        Assert.Contains(hostFields, field => field.FieldType == typeof(AppPackageGenerationRetirementQueue));
        Assert.Contains(hostFields, field => field.FieldType == typeof(AppPackageLifecycleCoordinator));
        Assert.Equal(
            typeof(AppPackageGenerationTransaction),
            typeof(AppPackageGenerationPublisher).GetMethod(
                "BeginTransaction",
                BindingFlags.Instance | BindingFlags.Public)?.ReturnType);

        var lifecycleFields = InstanceFields(typeof(AppPackageLifecycleCoordinator));
        Assert.Contains(lifecycleFields, field => field.FieldType == typeof(AppPackageGenerationBuilder));
        Assert.Contains(lifecycleFields, field => field.FieldType == typeof(AppPackageGenerationPublisher));
        Assert.Contains(lifecycleFields, field => field.FieldType == typeof(PackageIconGenerationCoordinator));
        Assert.Contains(lifecycleFields, field => field.FieldType == typeof(AppPackageGenerationRetirementQueue));
    }

    [Fact]
    public async Task ShutdownCoordinator_WhenOwnedTaskIgnoresCancellation_UsesBudgetAndRunsCleanupOnce()
    {
        using var observer = new OwnedTaskObserver("shutdown test");
        var taskStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.Run(async _ =>
        {
            taskStarted.SetResult();
            await releaseTask.Task;
        }, "ignoring cancellation");
        await taskStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var coordinator = new AppShutdownCoordinator(observer, TimeSpan.FromMilliseconds(40));
        var cleanupCount = 0;

        try
        {
            var first = coordinator.ShutdownAsync(() =>
            {
                Interlocked.Increment(ref cleanupCount);
                return Task.CompletedTask;
            });
            var second = coordinator.ShutdownAsync(() =>
            {
                Interlocked.Increment(ref cleanupCount);
                return Task.CompletedTask;
            });

            Assert.Same(first, second);
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, cleanupCount);
        }
        finally
        {
            releaseTask.TrySetResult();
            await observer.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static FieldInfo[] InstanceFields(Type type)
        => type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
}
