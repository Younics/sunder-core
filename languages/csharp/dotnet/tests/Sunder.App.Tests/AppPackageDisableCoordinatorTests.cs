using Avalonia.Controls;
using Sunder.App.Services;
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
        await using var backgroundProcesses = new BackgroundProcessQueueService();
        var coordinator = CreateCoordinator(
            backgroundProcesses,
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
        await using var backgroundProcesses = new BackgroundProcessQueueService();
        var coordinator = CreateCoordinator(
            backgroundProcesses,
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
        await using var backgroundProcesses = new BackgroundProcessQueueService();
        var coordinator = CreateCoordinator(
            backgroundProcesses,
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
        await using var backgroundProcesses = new BackgroundProcessQueueService();
        var viewRegistry = new AppPackageViewRegistry();
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
            new AppPackageRuntimeWorkStopper(backgroundProcesses),
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
            out var cleanup));

        Assert.Equal(
            AppPackageUiCleanupOutcome.Unproven,
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static AppPackageDisableCoordinator CreateCoordinator(
        BackgroundProcessQueueService backgroundProcesses,
        Func<string, bool> markPackageDisabled,
        Func<string, bool> isPackageDisabled)
    {
        var viewRegistry = new AppPackageViewRegistry();
        var viewFacade = new AppPackageHostedViewFacade(
            viewRegistry,
            isPackageDisabled,
            static (_, _, _) => false);
        return new AppPackageDisableCoordinator(
            viewRegistry,
            viewFacade,
            new AppPackageRuntimeWorkStopper(backgroundProcesses),
            new AppPackageFaultNotifier(notificationService: null),
            markPackageDisabled,
            isPackageDisabled);
    }

    private static Task<AppPackageUiCleanupOutcome> ProvenBoundaryCleanupAsync()
        => Task.FromResult(AppPackageUiCleanupOutcome.Proven);

    private sealed class ThrowingDisposeView : Control, IDisposable
    {
        public void Dispose()
            => throw new InvalidOperationException("view cleanup failed");
    }
}
