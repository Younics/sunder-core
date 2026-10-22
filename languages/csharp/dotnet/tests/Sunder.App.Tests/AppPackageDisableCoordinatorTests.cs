using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageDisableCoordinatorTests
{
    [Fact]
    public async Task DisablePackageAsync_AfterRecoverableCleanupFailure_RetriesCleanup()
    {
        var disabled = false;
        var attempts = 0;
        await using var backgroundTasks = new BackgroundTaskQueueService();
        var coordinator = CreateCoordinator(
            backgroundTasks,
            _ => disabled ? false : disabled = true,
            _ => disabled);

        Task<AppPackageUnloadResult> UnloadAsync(string _, CancellationToken __, bool ___)
            => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<AppPackageUnloadResult>(
                    new InvalidOperationException("cleanup failed"))
                : Task.FromResult(new AppPackageUnloadResult(
                    WasLoaded: true,
                    AppPackageUiCleanupOutcome.Proven));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.DisablePackageAsync(
            this,
            "agent",
            "Package failed.",
            PackageFailureOrigin.AppHostedView,
            exception: null,
            UnloadAsync,
            quarantineBoundariesAsync: ProvenBoundaryCleanupAsync,
            CancellationToken.None));

        await coordinator.DisablePackageAsync(
            this,
            "agent",
            "Package failed again.",
            PackageFailureOrigin.AppHostedView,
            exception: null,
            UnloadAsync,
            quarantineBoundariesAsync: ProvenBoundaryCleanupAsync,
            CancellationToken.None);
        await coordinator.DisablePackageAsync(
            this,
            "agent",
            "Duplicate failure.",
            PackageFailureOrigin.AppHostedView,
            exception: null,
            UnloadAsync,
            quarantineBoundariesAsync: ProvenBoundaryCleanupAsync,
            CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task DisablePackageAsync_WhenCleanupThrowsFatalFault_PropagatesIt()
    {
        var disabled = false;
        await using var backgroundTasks = new BackgroundTaskQueueService();
        var coordinator = CreateCoordinator(
            backgroundTasks,
            _ => disabled ? false : disabled = true,
            _ => disabled);
        var fatal = new OutOfMemoryException("fatal cleanup fault");

        var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            coordinator.DisablePackageAsync(
                this,
                "agent",
                "Package failed.",
                PackageFailureOrigin.AppHostedView,
                exception: null,
                (_, _, _) => Task.FromException<AppPackageUnloadResult>(fatal),
                quarantineBoundariesAsync: ProvenBoundaryCleanupAsync,
                CancellationToken.None));

        Assert.Same(fatal, thrown);
    }

    [Fact]
    public async Task DisablePackageAsync_WhenCleanupIsUnproven_DoesNotStartOverlappingCleanup()
    {
        var disabled = false;
        var attempts = 0;
        await using var backgroundTasks = new BackgroundTaskQueueService();
        var coordinator = CreateCoordinator(
            backgroundTasks,
            _ => disabled ? false : disabled = true,
            _ => disabled);

        Task<AppPackageUnloadResult> UnloadAsync(
            string _,
            CancellationToken __,
            bool ___)
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new AppPackageUnloadResult(
                WasLoaded: true,
                AppPackageUiCleanupOutcome.Unproven));
        }

        await coordinator.DisablePackageAsync(
            this,
            "agent",
            "Package failed.",
            PackageFailureOrigin.AppUnhandledUi,
            exception: null,
            UnloadAsync,
            quarantineBoundariesAsync: ProvenBoundaryCleanupAsync,
            CancellationToken.None);
        await coordinator.DisablePackageAsync(
            this,
            "agent",
            "Duplicate package failure.",
            PackageFailureOrigin.AppUnhandledUi,
            exception: null,
            UnloadAsync,
            quarantineBoundariesAsync: ProvenBoundaryCleanupAsync,
            CancellationToken.None);

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task TryBeginDisablePackage_WhenViewDisposalFails_ReportsCleanupAsUnproven()
    {
        var disabled = false;
        await using var backgroundTasks = new BackgroundTaskQueueService();
        var viewRegistry = new AppPackageViewRegistry(ImmediateUiDispatcher.Instance);
        viewRegistry.RegisterPackageView(
            "agent",
            new PackageViewRegistration("agent.chat", "Chat"),
            static () => new ThrowingDisposeView());
        var viewFacade = new AppPackageHostedViewFacade(
            viewRegistry,
            _ => disabled,
            static (_, _, _) => false);
        Assert.NotNull(viewFacade.GetOrCreateView("agent.chat"));
        var coordinator = new AppPackageDisableCoordinator(
            viewRegistry,
            viewFacade,
            new AppPackageRuntimeWorkStopper(backgroundTasks),
            new AppPackageFaultNotifier(notificationService: null),
            _ => disabled ? false : disabled = true,
            _ => disabled);

        Assert.True(coordinator.TryBeginDisablePackage(
            this,
            "agent",
            "Package failed.",
            PackageFailureOrigin.AppUnhandledUi,
            exception: null,
            (_, _, _) => Task.FromResult(new AppPackageUnloadResult(
                WasLoaded: true,
                AppPackageUiCleanupOutcome.Proven)),
            quarantineBoundariesAsync: ProvenBoundaryCleanupAsync,
            deferNotification: false,
            out var cleanup));

        Assert.Equal(
            AppPackageUiCleanupOutcome.Unproven,
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static AppPackageDisableCoordinator CreateCoordinator(
        BackgroundTaskQueueService backgroundTasks,
        Func<string, bool> markPackageDisabled,
        Func<string, bool> isPackageDisabled)
    {
        var viewRegistry = new AppPackageViewRegistry(ImmediateUiDispatcher.Instance);
        var viewFacade = new AppPackageHostedViewFacade(
            viewRegistry,
            isPackageDisabled,
            static (_, _, _) => false);
        return new AppPackageDisableCoordinator(
            viewRegistry,
            viewFacade,
            new AppPackageRuntimeWorkStopper(backgroundTasks),
            new AppPackageFaultNotifier(notificationService: null),
            markPackageDisabled,
            isPackageDisabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unload_WhenViewDisposalFails_StillDrainsOwnedServicesWithoutRetirementAcknowledgement(bool useDeadline)
    {
        using var temporary = TemporaryDirectoryScope.Create("app-failed-view-drain");
        await using var background = new BackgroundTaskQueueService();
        using var assemblies = new AppSharedAssemblyRegistry([]);
        var registry = new AppPackageViewRegistry(ImmediateUiDispatcher.Instance);
        registry.RegisterPackageView("agent", new PackageViewRegistration("agent.chat", "Chat"), static () => new ThrowingDisposeView());
        Assert.NotNull(new AppPackageHostedViewFacade(registry, _ => false, static (_, _, _) => false).GetOrCreateView("agent.chat"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;
        var acknowledgements = 0;
        var provider = new ServiceCollection().AddSingleton(_ => new DelayedOwner(async () =>
        {
            entered.SetResult();
            await release.Task;
            completed = true;
        })).BuildServiceProvider();
        _ = provider.GetRequiredService<DelayedOwner>();
        var coordinator = new AppPackageUnloadCoordinator(registry, new AppPackageRuntimeWorkStopper(background),
            new AppPackageAssemblyTracker(), assemblies, _ => { }, _ => { });
        var package = new ActivePackageDescriptor("agent", "Agent", "1.0.0", PackageHostRoles.App, null, true, PackageReadinessState.Ready, []);
        var handle = new AppLoadedPackageHandle(package, RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, "failed-view-drain"),
            temporary.RootPath, provider, null)
        {
            LoadAdmission = new(Guid.NewGuid(), "permit", _ => { acknowledgements++; return Task.CompletedTask; }),
        };

        var cleanup = coordinator.UnloadPackageAsync("agent", handle, useDeadline);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try { Assert.False(cleanup.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.Equal(AppPackageUiCleanupOutcome.Unproven, await cleanup.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(completed);
        Assert.Equal(0, acknowledgements);
    }

    private sealed class DelayedOwner(Func<Task> cleanup) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(cleanup());
    }

    private static Task<AppPackageUiCleanupOutcome> ProvenBoundaryCleanupAsync()
        => Task.FromResult(AppPackageUiCleanupOutcome.Proven);

    private sealed class ThrowingDisposeView : Control, IDisposable
    {
        public void Dispose()
            => throw new InvalidOperationException("view cleanup failed");
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public static ImmediateUiDispatcher Instance { get; } = new();

        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action) => action();

        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());

        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
