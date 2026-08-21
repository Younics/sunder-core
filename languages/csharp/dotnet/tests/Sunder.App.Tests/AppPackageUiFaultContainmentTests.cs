using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.App.Views.Controls;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageUiFaultContainmentTests
{
    [Fact]
    public async Task ConstructionFault_ProducesHostFallbackWithoutDisablingSiblingView()
    {
        var root = CreateTempDirectory();
        var notifications = new NotificationCenterService(Path.Combine(root, "notifications.json"));
        var registry = new AppPackageViewRegistry();
        var constructionCount = 0;
        var siblingView = new DisposableSiblingView();
        registry.RegisterPackageView(
            "agent",
            new PackageViewRegistration("agent.chat", "Chat"),
            () =>
            {
                constructionCount++;
                throw new InvalidOperationException("private constructor failure");
            });
        registry.RegisterPackageView(
            "agent",
            new PackageViewRegistration("agent.workspaces", "Workspaces"),
            () => siblingView);
        await using var host = new PackageViewHostService(
            registry,
            [],
            [],
            [],
            sessionFolder: null,
            notificationCenter: notifications,
            uiDispatcher: ImmediateUiDispatcher.Instance);

        Assert.Null(host.GetOrCreateView("agent.chat"));
        Assert.Null(host.GetOrCreateView("agent.chat"));
        var fallback = Assert.IsType<HostedPackageViewBoundary>(
            host.CreateHostedViewBoundary("agent", "agent.chat", hostedView: null));

        Assert.Equal(1, constructionCount);
        Assert.True(fallback.IsFaulted);
        Assert.Null(fallback.HostedView);
        Assert.Same(siblingView, host.GetOrCreateView("agent.workspaces"));
        Assert.Single(host.FilterEnabledPackages([CreateActivePackage()]));
        var notification = Assert.Single(notifications.ListNotifications());
        Assert.DoesNotContain("private constructor failure", notification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownViewFault_DrainsAndDisposesOnlyThatView_WhileSiblingAndProviderSurvive()
    {
        var root = CreateTempDirectory();
        var notifications = new NotificationCenterService(Path.Combine(root, "notifications.json"));
        var providerProbe = new ProviderProbe();
        var serviceProvider = new ServiceCollection()
            .AddSingleton(providerProbe)
            .BuildServiceProvider();
        var faultingView = new BlockingWarmupFaultingView();
        var siblingView = new DisposableSiblingView();
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView(
            "agent",
            new PackageViewRegistration("agent.chat", "Chat"),
            () => faultingView);
        registry.RegisterPackageView(
            "agent",
            new PackageViewRegistration("agent.workspaces", "Workspaces"),
            () => siblingView);
        await using var host = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            sessionFolder: null,
            notificationCenter: notifications,
            uiDispatcher: ImmediateUiDispatcher.Instance);
        var deliveredFaults = 0;
        host.PackageFaulted += (_, _) => throw new InvalidOperationException("subscriber failed");
        host.PackageFaulted += (_, fault) =>
        {
            Assert.Equal("agent.chat", fault.ViewId);
            deliveredFaults++;
        };
        var warmup = host.PreloadViewAsync(
            "agent.chat",
            host.CurrentGenerationId,
            CancellationToken.None);
        await faultingView.WarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var sibling = host.GetOrCreateView("agent.workspaces");
        var boundary = Assert.IsType<HostedPackageViewBoundary>(
            host.CreateHostedViewBoundary("agent", "agent.chat", faultingView));

        boundary.Measure(new Size(100, 100));
        boundary.Measure(new Size(100, 100));
        await faultingView.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(boundary.IsFaulted);
        Assert.Null(boundary.HostedView);
        Assert.False(faultingView.IsDisposed);
        Assert.Same(siblingView, sibling);
        Assert.Same(sibling, host.GetOrCreateView("agent.workspaces"));
        Assert.False(siblingView.IsDisposed);
        Assert.False(providerProbe.IsDisposed);
        Assert.Single(host.FilterEnabledPackages([CreateActivePackage()]));

        faultingView.ReleaseCancellationCleanup.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => warmup.WaitAsync(TimeSpan.FromSeconds(2)));
        await TestSupport.AsyncAssert.WaitForConditionAsync(() => faultingView.IsDisposed);

        Assert.Equal(1, deliveredFaults);
        var notification = Assert.Single(notifications.ListNotifications());
        Assert.Equal("agent view unavailable", notification.Title);
        Assert.Equal(PackageNotificationSeverity.Error, notification.Severity);
        Assert.DoesNotContain("private view failure", notification.Message, StringComparison.Ordinal);
        Assert.True(notification.Message.Length <= 150);
    }

    [Fact]
    public async Task SettingsViewFault_ClearsBoundaryAndCannotPromoteOrReattachDisposedControl()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        registry.RegisterSettingsView<FaultingSettingsView>("agent", serviceProvider);
        await using var host = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            sessionFolder: null,
            uiDispatcher: ImmediateUiDispatcher.Instance);
        var target = Assert.IsType<AppPackageSettingsViewNavigationTarget>(
            host.GetSettingsViewForNavigation("agent", requireReplacement: false));
        var settingsView = Assert.IsType<FaultingSettingsView>(target.View);
        var boundary = Assert.IsType<HostedPackageViewBoundary>(
            host.CreateHostedViewBoundary("agent", "settings:agent", settingsView));

        await boundary.OnNavigatedToAsync(new PackageViewNavigationContext(
            "settings:agent",
            new Dictionary<string, string?>()));
        target.Candidate?.Dispose();
        await TestSupport.AsyncAssert.WaitForConditionAsync(() => settingsView.IsDisposed);

        Assert.True(boundary.IsFaulted);
        Assert.Null(boundary.HostedView);
        Assert.Null(host.GetOrCreateSettingsView("agent"));
        Assert.False(target.Candidate?.Promote() ?? false);
        boundary.Measure(new Size(100, 100));
        Assert.DoesNotContain(settingsView, boundary.Children);
    }

    [Fact]
    public async Task PackageAttributedFault_QuarantinesAppRoleButUnknownFaultIsNotHandled()
    {
        var root = CreateTempDirectory();
        var notifications = new NotificationCenterService(Path.Combine(root, "notifications.json"));
        var registry = new AppPackageViewRegistry();
        var notificationQueuedBeforeCleanup = true;
        var firstView = new DisposableSiblingView(() =>
            notificationQueuedBeforeCleanup &= notifications.ListNotifications().Count == 1);
        var secondView = new DisposableSiblingView(() =>
            notificationQueuedBeforeCleanup &= notifications.ListNotifications().Count == 1);
        registry.RegisterPackageView(
            "agent",
            new PackageViewRegistration("agent.chat", "Chat"),
            () => firstView);
        registry.RegisterPackageView(
            "agent",
            new PackageViewRegistration("agent.workspaces", "Workspaces"),
            () => secondView);
        await using var host = new PackageViewHostService(
            registry,
            [],
            [],
            [],
            sessionFolder: null,
            notificationCenter: notifications,
            uiDispatcher: ImmediateUiDispatcher.Instance);
        var firstBoundary = Assert.IsType<HostedPackageViewBoundary>(
            host.CreateHostedViewBoundary(
                "agent",
                "agent.chat",
                host.GetOrCreateView("agent.chat")));
        var secondBoundary = Assert.IsType<HostedPackageViewBoundary>(
            host.CreateHostedViewBoundary(
                "agent",
                "agent.workspaces",
                host.GetOrCreateView("agent.workspaces")));
        host.PackageFaulted += (_, _) => throw new InvalidOperationException(
            "cleanup subscriber failure");

        Assert.False(host.TryHandleUnhandledException(new InvalidOperationException("unknown")));
        host.RegisterPackageAssemblyForFaultAttribution("agent", typeof(AppPackageUiFaultContainmentTests).Assembly);
        var attributed = Assert.Throws<InvalidOperationException>(ThrowAttributedFault);
        Assert.False(host.TryHandleUnhandledException(new AggregateException(
            attributed,
            new OutOfMemoryException())));

        Assert.True(host.TryHandleUnhandledException(attributed));
        Assert.True(firstBoundary.IsFaulted);
        Assert.True(secondBoundary.IsFaulted);
        Assert.Null(firstBoundary.HostedView);
        Assert.Null(secondBoundary.HostedView);
        Assert.Empty(host.FilterEnabledPackages([CreateActivePackage()]));
        await TestSupport.AsyncAssert.WaitForConditionAsync(
            () => firstView.IsDisposed && secondView.IsDisposed);
        Assert.True(notificationQueuedBeforeCleanup);

        var notification = Assert.Single(notifications.ListNotifications());
        Assert.Equal("agent App UI unavailable", notification.Title);
        Assert.DoesNotContain("dispatcher package secret", notification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageAttributedFault_OffUiThreadDoesNotBlockAndLateViewFaultKeepsPackageFallback()
    {
        var root = CreateTempDirectory();
        var notifications = new NotificationCenterService(Path.Combine(root, "notifications.json"));
        var dispatcher = new DeferredUiDispatcher();
        var registry = new AppPackageViewRegistry(dispatcher);
        await using var host = new PackageViewHostService(
            registry,
            [],
            [],
            [],
            sessionFolder: null,
            notificationCenter: notifications,
            uiDispatcher: dispatcher);
        host.RegisterPackageAssemblyForFaultAttribution(
            "agent",
            typeof(AppPackageUiFaultContainmentTests).Assembly);
        var attributed = Assert.Throws<InvalidOperationException>(ThrowAttributedFault);

        try
        {
            var handled = Task.Run(() => host.TryHandleUnhandledException(attributed));

            Assert.True(await handled.WaitAsync(TimeSpan.FromSeconds(2)));
            await dispatcher.DispatchRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(host.TryHandleKnownViewFailure(
                "agent",
                "agent.chat",
                new InvalidOperationException("late private view failure")));
        }
        finally
        {
            dispatcher.Release();
        }

        Assert.True(host.IsPackageAppUiQuarantined("agent"));
        Assert.Equal("agent App UI unavailable",
            Assert.Single(notifications.ListNotifications()).Title);

    }

    [Fact]
    public async Task PackageAttributedFault_WhenDetachedCleanupThrowsFatalFault_PropagatesIt()
    {
        var fatalFault = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new AppPackageViewRegistry();
        registry.RegisterPackageView(
            "agent",
            new PackageViewRegistration("agent.chat", "Chat"),
            static () => new FatalDisposalView());
        await using var host = new PackageViewHostService(
            registry,
            [],
            [],
            [],
            sessionFolder: null,
            uiDispatcher: ImmediateUiDispatcher.Instance,
            fatalCleanupFaultHandler: exception => fatalFault.TrySetResult(exception));
        Assert.NotNull(host.GetOrCreateView("agent.chat"));
        host.RegisterPackageAssemblyForFaultAttribution(
            "agent",
            typeof(AppPackageUiFaultContainmentTests).Assembly);
        var attributed = Assert.Throws<InvalidOperationException>(ThrowAttributedFault);

        Assert.True(host.TryHandleUnhandledException(attributed));

        Assert.IsType<OutOfMemoryException>(
            await fatalFault.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task UnobservedTaskRoute_RequiresEveryAggregateBranchToBePackageAttributed()
    {
        await using var host = new PackageViewHostService(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            sessionFolder: null,
            uiDispatcher: ImmediateUiDispatcher.Instance);
        host.RegisterPackageAssemblyForFaultAttribution("agent", typeof(AppPackageUiFaultContainmentTests).Assembly);
        var attributed = Assert.Throws<InvalidOperationException>(ThrowAttributedFault);
        var mixed = new AggregateException(
            attributed,
            new InvalidOperationException("unattributed branch"));

        Assert.False(host.TryHandleUnobservedTaskException(mixed));
        Assert.True(host.TryHandleUnobservedTaskException(new AggregateException(attributed)));
    }

    private static ActivePackageDescriptor CreateActivePackage()
        => new(
            "agent",
            "Agent",
            "1.0.0",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            Icon: null,
            IsEnabled: true,
            PackageReadinessState.Ready,
            Views: []);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sunder-app-fault-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAttributedFault()
        => throw new InvalidOperationException("dispatcher package secret");

    private sealed class BlockingWarmupFaultingView : Control, IPackageViewWarmupTarget, IDisposable
    {
        public TaskCompletionSource WarmupStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCancellationCleanup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }

        public async ValueTask WarmupAsync(CancellationToken cancellationToken = default)
        {
            WarmupStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await ReleaseCancellationCleanup.Task;
                throw;
            }
        }

        protected override Size MeasureOverride(Size availableSize)
            => throw new InvalidOperationException("private view failure");

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FaultingSettingsView : Control, IPackageViewNavigationTarget, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException(
                new InvalidOperationException("private settings failure"));

        public void Dispose() => IsDisposed = true;
    }

    private sealed class DisposableSiblingView(Action? onDispose = null) : Control, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            onDispose?.Invoke();
            IsDisposed = true;
        }
    }

    private sealed class ProviderProbe : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FatalDisposalView : Control, IDisposable
    {
        public void Dispose() => throw new OutOfMemoryException("fatal cleanup failure");
    }

    private sealed class DeferredUiDispatcher : IUiDispatcher
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _released;

        public TaskCompletionSource DispatchRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CheckAccess() => Volatile.Read(ref _released) != 0;

        public Task InvokeAsync(Action action)
            => InvokeAfterReleaseAsync(() =>
            {
                action();
                return Task.CompletedTask;
            });

        public Task InvokeAsync(Func<Task> action) => InvokeAfterReleaseAsync(action);

        public Task<T> InvokeAsync<T>(Func<T> action)
            => InvokeAfterReleaseAsync(() => Task.FromResult(action()));

        public Task<T> InvokeAsync<T>(Func<Task<T>> action)
            => InvokeAfterReleaseAsync(action);

        public void Release()
        {
            Interlocked.Exchange(ref _released, 1);
            _release.TrySetResult();
        }

        private async Task InvokeAfterReleaseAsync(Func<Task> action)
        {
            DispatchRequested.TrySetResult();
            await _release.Task;
            await action();
        }

        private async Task<T> InvokeAfterReleaseAsync<T>(Func<Task<T>> action)
        {
            DispatchRequested.TrySetResult();
            await _release.Task;
            return await action();
        }
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
