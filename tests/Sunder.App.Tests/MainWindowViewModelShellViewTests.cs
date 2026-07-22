using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Features.Shell.Layout;
using Sunder.App.Features.Shell.Lifecycle;
using Sunder.App.Features.Shell.Menus;
using Sunder.App.Features.Shell.Panels;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.App.Views.Controls;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Notifications;
using Xunit;
using static Sunder.App.Tests.TestSupport.AsyncAssert;
using static Sunder.App.Tests.TestSupport.TestPaths;

namespace Sunder.App.Tests;

public sealed class MainWindowViewModelShellViewTests
{
    [Fact]
    public async Task InitialHostedViewNavigation_CompletesBeforeRevealAndShellPersistence()
    {
        var rootPath = CreateTempDirectory();
        var probe = new InitialNavigationProbe();
        var packageViewHostService = CreateNavigationPackageViewHostService(probe);
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService,
            deferInitialHostedViews: true,
            uiDispatcher: new ImmediateUiDispatcher()
        );
        var releaseNavigation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        probe.NavigateAsync = async (context, cancellationToken) =>
        {
            probe.Context = context;
            probe.Started.SetResult();
            await releaseNavigation.Task.WaitAsync(cancellationToken);
        };
        var attachmentObserved = false;

        var activation = harness.ViewModel.ActivateDeferredInitialHostedViewsAsync(() =>
        {
            attachmentObserved = harness.ViewModel.MiddlePanel.HostedView is not null;
            return Task.CompletedTask;
        });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(attachmentObserved);
        Assert.True(probe.NavigationObservedWarmup);
        Assert.False(activation.IsCompleted);
        Assert.False(File.Exists(harness.StatePath));
        Assert.Equal("agent.chat", probe.Context?.ViewId);
        Assert.Empty(
            probe.Context?.Parameters
                ?? throw new InvalidOperationException("Navigation context was not captured.")
        );

        releaseNavigation.SetResult();
        await activation;
        Assert.False(File.Exists(harness.StatePath));

        harness.ViewModel.CompleteInitialReveal();
        await WaitForConditionAsync(() => File.Exists(harness.StatePath));
    }

    [Fact]
    public async Task InitialHostedViewNavigation_PropagatesStartupCancellationBeforeReveal()
    {
        var rootPath = CreateTempDirectory();
        var probe = new InitialNavigationProbe();
        var packageViewHostService = CreateNavigationPackageViewHostService(probe);
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService,
            deferInitialHostedViews: true,
            uiDispatcher: new ImmediateUiDispatcher()
        );
        var navigationCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        probe.NavigateAsync = async (_, cancellationToken) =>
        {
            probe.Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                navigationCancelled.SetResult();
                throw;
            }
        };
        using var cancellation = new CancellationTokenSource();

        var activation = harness.ViewModel.ActivateDeferredInitialHostedViewsAsync(
            cancellationToken: cancellation.Token
        );
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);
        await navigationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(File.Exists(harness.StatePath));
    }

    [Fact]
    public async Task OpenPackageViewPanelAsync_AddsHiddenViewToHotbarAndOpensPanel()
    {
        using var harness = CreateHarness();

        Assert.False(harness.ViewModel.IsViewInHotbar("agent.subsessions"));

        var opened = await harness.ViewModel.OpenPackageViewPanelAsync(
            "agent.subsessions",
            new Dictionary<string, string?> { ["sessionId"] = Guid.NewGuid().ToString("N") }
        );

        Assert.True(opened);
        Assert.True(harness.ViewModel.IsViewInHotbar("agent.subsessions"));
        Assert.True(harness.ViewModel.HasRightTopPanelContent);
        Assert.Contains(
            harness.ViewModel.ListHotbarViews(),
            view => view.ViewId == "agent.subsessions" && view.IsOpen
        );
    }

    [Fact]
    public async Task HotbarSelection_PreparesEagerlyRetainedViewBeforeActivationAndNavigation()
    {
        var rootPath = CreateTempDirectory();
        var probe = new InitialNavigationProbe();
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        registry.RegisterPackageView<DisposablePackageView>(
            "agent",
            new PackageViewRegistration("agent.chat", "Chat"),
            serviceProvider);
        registry.RegisterPackageView<InitialNavigationPackageView>(
            "agent",
            new PackageViewRegistration("agent.workspaces", "Workspaces"),
            serviceProvider);
        var packageViewHostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            sessionFolder: null,
            uiDispatcher: new ImmediateUiDispatcher());
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService);
        var workGate = new WorkGate();
        harness.ViewModel.ConfigurePackageViewWorkScheduler(workGate.RunAsync);
        var releaseNavigation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var navigationCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        probe.NavigateAsync = async (_, cancellationToken) =>
        {
            probe.Started.TrySetResult();
            try
            {
                await releaseNavigation.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                navigationCancelled.TrySetResult();
                throw;
            }
        };
        var item = Assert.Single(harness.ViewModel.RightTopBar.Items);
        var retainedView = harness.ViewModel.RightTopPanel.GetRetainedView("agent.workspaces");
        Assert.NotNull(retainedView);
        Assert.False(harness.ViewModel.RightTopPanel.HasHostedView);

        item.Activate();

        await workGate.WaitForRequestAsync();
        Assert.True(harness.ViewModel.HasRightTopPanelContent);
        Assert.Null(harness.ViewModel.RightTopPanel.HostedView);
        Assert.False(probe.WarmupCompleted);
        Assert.False(probe.Started.Task.IsCompleted);

        workGate.ReleaseNext();
        await workGate.WaitForRequestAsync();
        Assert.True(probe.WarmupCompleted);
        Assert.Same(retainedView, harness.ViewModel.RightTopPanel.HostedView);
        Assert.False(probe.Started.Task.IsCompleted);

        workGate.ReleaseNext();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(probe.NavigationObservedWarmup);

        item.Activate();

        Assert.False(harness.ViewModel.HasRightTopPanelContent);
        Assert.False(harness.ViewModel.RightTopPanel.HasHostedView);
        Assert.Same(retainedView, harness.ViewModel.RightTopPanel.GetRetainedView("agent.workspaces"));
        await navigationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PreparedRetainedSideView_ReactivatesBeforeDeferredNavigation()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.workspaces"));
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService);
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));
        var retained = Assert.Single(
            harness.ViewModel.RightTopPanel.HostedViews,
            view => view.ViewId == "agent.workspaces");
        Assert.True(harness.ViewModel.ClosePackageViewPanel("agent.workspaces"));
        Assert.False(retained.IsActive);
        Assert.Null(harness.ViewModel.RightTopPanel.HostedView);
        var workGate = new WorkGate();
        harness.ViewModel.ConfigurePackageViewWorkScheduler(workGate.RunAsync);

        var reopen = harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces").AsTask();
        await workGate.WaitForRequestAsync();

        Assert.True(retained.IsActive);
        Assert.Same(retained.View, harness.ViewModel.RightTopPanel.HostedView);
        workGate.ReleaseNext();
        await workGate.WaitForRequestAsync();
        workGate.ReleaseNext();
        Assert.True(await reopen.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RapidSamePlacementSwitch_OnlyLatestViewAttachesAndNavigates()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.workspaces"),
            ("agent", "agent.subsessions"));
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService);
        Assert.True(await harness.ViewModel.AddPackageViewToDefaultHotbarAsync(
            "agent.subsessions"));
        var workGate = new WorkGate();
        harness.ViewModel.ConfigurePackageViewWorkScheduler(workGate.RunAsync);

        var first = harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces").AsTask();
        await workGate.WaitForRequestAsync();
        var latest = harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions").AsTask();
        await workGate.WaitForRequestAsync();
        await workGate.WaitForRequestAsync();

        Assert.Equal("agent.subsessions", harness.ViewModel.RightTopPanel.ActiveViewId);
        Assert.Null(harness.ViewModel.RightTopPanel.HostedView);
        workGate.ReleaseNext();
        workGate.ReleaseNext();
        await workGate.WaitForRequestAsync();
        Assert.Equal("agent.subsessions", harness.ViewModel.RightTopPanel.ActiveViewId);
        Assert.Same(
            harness.ViewModel.RightTopPanel.GetRetainedView("agent.subsessions"),
            harness.ViewModel.RightTopPanel.HostedView);
        workGate.ReleaseNext();

        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await latest.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("agent.subsessions", harness.ViewModel.RightTopPanel.ActiveViewId);
        Assert.Contains(
            harness.ViewModel.RightTopPanel.HostedViews,
            view => view.ViewId == "agent.workspaces" && !view.IsActive);
    }

    [Fact]
    public async Task ClosePanel_DetachesImmediatelyWithSingleGeometryCommit()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.workspaces"));
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService);
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));
        var hostedView = harness.ViewModel.RightTopPanel.HostedView;
        var shellStateChangeCount = 0;
        harness.ViewModel.ShellViewStateChanged += () => shellStateChangeCount++;

        Assert.True(harness.ViewModel.ClosePackageViewPanel("agent.workspaces"));

        Assert.Null(harness.ViewModel.RightTopPanel.HostedView);
        Assert.False(harness.ViewModel.RightTopPanel.IsDockVisible);
        Assert.Equal(1, shellStateChangeCount);
        Assert.Contains(
            harness.ViewModel.RightTopPanel.HostedViews,
            view => ReferenceEquals(view.View, hostedView) && !view.IsActive);
    }

    [Fact]
    public async Task ReopenAfterImmediateClose_ReusesRetainedContent()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.workspaces"));
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService);
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));
        var hostedView = harness.ViewModel.RightTopPanel.HostedView;
        var shellStateChangeCount = 0;
        harness.ViewModel.ShellViewStateChanged += () => shellStateChangeCount++;

        Assert.True(harness.ViewModel.ClosePackageViewPanel("agent.workspaces"));
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));

        Assert.Same(hostedView, harness.ViewModel.RightTopPanel.HostedView);
        Assert.True(harness.ViewModel.RightTopPanel.IsDockVisible);
        Assert.Equal(2, shellStateChangeCount);
    }

    [Fact]
    public async Task PanelInteraction_DoesNotCancelOrRestartActivePreloadPass()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.chat"),
            ("agent", "agent.workspaces"),
            ("agent", "agent.subsessions"));
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService);
        var firstPreloadWaitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPreloadWait = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var preloadCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var preloadWaitCount = 0;
        CancellationToken firstPreloadCancellation = default;
        harness.ViewModel.StartPackageViewPreloading(
            async (work, cancellationToken) =>
            {
                var waitIndex = Interlocked.Increment(ref preloadWaitCount);
                if (waitIndex == 1)
                {
                    firstPreloadCancellation = cancellationToken;
                    firstPreloadWaitStarted.TrySetResult();
                    await releaseFirstPreloadWait.Task.WaitAsync(cancellationToken);
                }

                await work(cancellationToken);
                if (waitIndex == 3)
                {
                    preloadCompleted.TrySetResult();
                }
            },
            static (work, cancellationToken) => work(cancellationToken));
        await firstPreloadWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(
            await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces")
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.False(firstPreloadCancellation.IsCancellationRequested);
        releaseFirstPreloadWait.TrySetResult();
        await preloadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, preloadWaitCount);
        Assert.False(firstPreloadCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task ReloadPackageViewAsync_DoesNotRunPreloadCancellationCallbacksInline()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.workspaces"));
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService);
        var preloadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var waitCount = 0;
        CancellationToken preloadCancellation = default;
        var reloadThreadId = 0;
        var reloadCallInProgress = 0;
        var callbackRanInline = 0;
        harness.ViewModel.StartPackageViewPreloading(
            async (work, cancellationToken) =>
            {
                if (Interlocked.Increment(ref waitCount) != 1)
                {
                    await work(cancellationToken);
                    return;
                }

                preloadCancellation = cancellationToken;
                using var registration = cancellationToken.Register(() =>
                {
                    if (
                        Environment.CurrentManagedThreadId == Volatile.Read(ref reloadThreadId)
                        && Volatile.Read(ref reloadCallInProgress) != 0
                    )
                    {
                        Interlocked.Exchange(ref callbackRanInline, 1);
                    }
                });
                preloadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            static (work, cancellationToken) => work(cancellationToken));
        await preloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref reloadThreadId, Environment.CurrentManagedThreadId);
        Volatile.Write(ref reloadCallInProgress, 1);
        Task<bool> reload;
        try
        {
            reload = harness.ViewModel.ReloadPackageViewAsync("agent.workspaces").AsTask();
        }
        finally
        {
            Volatile.Write(ref reloadCallInProgress, 0);
        }

        Assert.True(preloadCancellation.IsCancellationRequested);
        Assert.Equal(0, Volatile.Read(ref callbackRanInline));
        Assert.True(await reload.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DeferredPresentation_OnlyLatestRequestNavigatesWithItsParameters()
    {
        var rootPath = CreateTempDirectory();
        var probe = new InitialNavigationProbe();
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        registry.RegisterPackageView<InitialNavigationPackageView>(
            "agent",
            new PackageViewRegistration("agent.workspaces", "Workspaces"),
            serviceProvider);
        var packageViewHostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            sessionFolder: null,
            uiDispatcher: new ImmediateUiDispatcher());
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService);
        var workGate = new WorkGate();
        harness.ViewModel.ConfigurePackageViewWorkScheduler(workGate.RunAsync);
        probe.NavigateAsync = (context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.Context = context;
            probe.Started.TrySetResult();
            return ValueTask.CompletedTask;
        };

        var firstOpen = harness.ViewModel.OpenPackageViewPanelAsync(
            "agent.workspaces",
            new Dictionary<string, string?> { ["request"] = "first" }).AsTask();
        await workGate.WaitForRequestAsync();
        Assert.True(harness.ViewModel.ClosePackageViewPanel("agent.workspaces"));
        var latestOpen = harness.ViewModel.OpenPackageViewPanelAsync(
            "agent.workspaces",
            new Dictionary<string, string?> { ["request"] = "latest" }).AsTask();

        for (var workIndex = 0; workIndex < 3; workIndex++)
        {
            await workGate.WaitForRequestAsync();
            workGate.ReleaseNext();
        }

        Assert.False(await firstOpen.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await latestOpen.WaitAsync(TimeSpan.FromSeconds(2)));
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("latest", probe.Context?.Parameters["request"]);
    }

    [Fact]
    public async Task ClosePackageViewPanelAsync_ClosesPanelWithoutRemovingHotbarItem()
    {
        using var harness = CreateHarness();
        await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions");

        var closed = harness.ViewModel.ClosePackageViewPanel("agent.subsessions");

        Assert.True(closed);
        Assert.True(harness.ViewModel.IsViewInHotbar("agent.subsessions"));
        Assert.False(harness.ViewModel.HasRightTopPanelContent);
        Assert.Contains(
            harness.ViewModel.ListHotbarViews(),
            view => view.ViewId == "agent.subsessions" && !view.IsOpen
        );
    }

    [Fact]
    public void ClosePackageViewPanel_WhenOnlyMiddleViewIsSelected_ClearsMiddleSelection()
    {
        using var harness = CreateHarness();

        var closed = harness.ViewModel.ClosePackageViewPanel("agent.chat");

        Assert.True(closed);
        Assert.False(harness.ViewModel.HasMiddleSelection);
        Assert.False(harness.ViewModel.MiddlePanel.HasHostedView);
        Assert.Contains(
            harness.ViewModel.ListHotbarViews(),
            view => view.ViewId == "agent.chat" && !view.IsOpen
        );
    }

    [Fact]
    public async Task ClosePackageViewPanel_WhenClosingFirstMiddleView_SelectsNextMiddleView()
    {
        using var harness = CreateHarness();
        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.Middle, 1);
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.chat"));

        var closed = harness.ViewModel.ClosePackageViewPanel("agent.chat");

        var hotbarViews = harness.ViewModel.ListHotbarViews();
        Assert.True(closed);
        Assert.True(harness.ViewModel.HasMiddleSelection);
        Assert.Contains(hotbarViews, view => view.ViewId == "agent.chat" && !view.IsOpen);
        Assert.Contains(
            hotbarViews,
            view =>
                view.ViewId == "agent.workspaces"
                && view.Placement == PackageViewPlacement.Middle
                && view.IsOpen
        );
    }

    [Fact]
    public async Task RemovePackageViewFromHotbar_HidesViewAndClosesPanel()
    {
        using var harness = CreateHarness();
        await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions");

        var removed = harness.ViewModel.RemovePackageViewFromHotbar("agent.subsessions");

        Assert.True(removed);
        Assert.False(harness.ViewModel.IsViewInHotbar("agent.subsessions"));
        Assert.False(harness.ViewModel.HasRightTopPanelContent);
        Assert.DoesNotContain(
            harness.ViewModel.ListHotbarViews(),
            view => view.ViewId == "agent.subsessions"
        );
    }

    [Fact]
    public async Task ReloadPackageViewAsync_ReplacesOpenHostedView()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService();
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.chat"));
        var originalView = AssertHostedView<DisposablePackageView>(
            harness.ViewModel.MiddlePanel.HostedView
        );

        var reloaded = await harness.ViewModel.ReloadPackageViewAsync("agent.chat");
        var reloadedView = AssertHostedView<DisposablePackageView>(
            harness.ViewModel.MiddlePanel.HostedView
        );

        Assert.True(reloaded);
        Assert.True(originalView.IsDisposed);
        Assert.NotSame(originalView, reloadedView);
        Assert.True(harness.ViewModel.HasMiddleSelection);
        Assert.Contains(
            harness.ViewModel.ListHotbarViews(),
            view => view.ViewId == "agent.chat" && view.IsOpen
        );
    }

    [Fact]
    public async Task ReloadPackageViewAsync_WhenViewIsClosed_InvalidatesWithoutCreatingHostedView()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.chat"),
            ("agent", "agent.workspaces")
        );
        DisposablePackageView.ResetCreatedCount();
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );
        var createdCountAfterInitialSelection = DisposablePackageView.CreatedCount;

        var reloaded = await harness.ViewModel.ReloadPackageViewAsync("agent.workspaces");

        Assert.True(reloaded);
        Assert.Equal(createdCountAfterInitialSelection, DisposablePackageView.CreatedCount);
        Assert.False(harness.ViewModel.RightTopPanel.HasHostedView);
    }

    [Fact]
    public async Task ReloadPackageViewAsync_DoesNotReattachViewClosedDuringDeferredReload()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService();
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService);
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.chat"));
        var originalView = AssertHostedView<DisposablePackageView>(
            harness.ViewModel.MiddlePanel.HostedView);
        var createdCountBeforeReload = DisposablePackageView.CreatedCount;
        var workRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWork = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ViewModel.ConfigurePackageViewWorkScheduler(async (work, cancellationToken) =>
        {
            workRequested.TrySetResult();
            await releaseWork.Task.WaitAsync(cancellationToken);
            await work(cancellationToken);
        });

        var reload = harness.ViewModel.ReloadPackageViewAsync("agent.chat").AsTask();
        await workRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(createdCountBeforeReload, DisposablePackageView.CreatedCount);
        Assert.False(originalView.IsDisposed);
        Assert.True(harness.ViewModel.ClosePackageViewPanel("agent.chat"));
        releaseWork.TrySetResult();

        Assert.False(await reload.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(originalView.IsDisposed);
        Assert.False(harness.ViewModel.HasMiddleSelection);
        Assert.False(harness.ViewModel.MiddlePanel.HasHostedView);
    }

    [Fact]
    public async Task ReloadPackageViewAsync_DoesNotSupersedeLaterParameterizedOpen()
    {
        var rootPath = CreateTempDirectory();
        var probe = new InitialNavigationProbe();
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        registry.RegisterPackageView<InitialNavigationPackageView>(
            "agent",
            new PackageViewRegistration("agent.workspaces", "Workspaces"),
            serviceProvider);
        var packageViewHostService = new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            sessionFolder: null,
            uiDispatcher: new ImmediateUiDispatcher());
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService);
        probe.NavigateAsync = (context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.Context = context;
            return ValueTask.CompletedTask;
        };
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));
        var workGate = new WorkGate();
        harness.ViewModel.ConfigurePackageViewWorkScheduler(workGate.RunAsync);

        var reload = harness.ViewModel.ReloadPackageViewAsync("agent.workspaces").AsTask();
        await workGate.WaitForRequestAsync();
        var latestOpen = harness.ViewModel.OpenPackageViewPanelAsync(
            "agent.workspaces",
            new Dictionary<string, string?> { ["request"] = "latest" }).AsTask();
        await workGate.WaitForRequestAsync();

        workGate.ReleaseNext();
        await workGate.WaitForRequestAsync();
        workGate.ReleaseNext();

        Assert.False(await reload.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await latestOpen.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("latest", probe.Context?.Parameters["request"]);
    }

    [Fact]
    public void GetMainMenuItems_IncludesCommandGlyphsAndHotbarState()
    {
        using var harness = CreateHarness();

        var group = Assert.Single(GetPackageMenuGroups(harness.ViewModel));

        Assert.Equal("A", group.Glyph);
        Assert.Contains(
            group.Children,
            view => view.Id == "view:agent.chat" && view.Glyph == "A" && !view.IsEnabled
        );
        Assert.Contains(
            group.Children,
            view => view.Id == "view:agent.workspaces" && view.Glyph == "W" && !view.IsEnabled
        );
        Assert.Contains(
            group.Children,
            view => view.Id == "view:agent.subsessions" && view.Glyph == "S" && view.IsEnabled
        );
    }

    [Fact]
    public async Task PackageShellViewService_ListHotbarViews_UsesSnapshotWhenCalledOffUiThread()
    {
        var shellViewService = new AppPackageShellViewService();
        using var harness = CreateHarness(shellViewService: shellViewService);

        var initialViews = await Task.Run(() => shellViewService.ListHotbarViews());

        Assert.Contains(initialViews, view => view.ViewId == "agent.chat" && view.IsOpen);
        Assert.False(shellViewService.IsViewInHotbar("agent.subsessions"));

        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions"));

        var updatedViews = await Task.Run(() => shellViewService.ListHotbarViews());
        Assert.True(shellViewService.IsViewInHotbar("agent.subsessions"));
        Assert.Contains(updatedViews, view => view.ViewId == "agent.subsessions" && view.IsOpen);

        harness.ViewModel.Dispose();
        Assert.Empty(shellViewService.ListHotbarViews());
    }

    [Fact]
    public async Task SwitchingPackageViews_DoesNotChangePanelWidths()
    {
        using var harness = CreateHarness();
        var originalLeftWidth = harness.ViewModel.LeftPanelWidth;
        var originalRightWidth = harness.ViewModel.RightPanelWidth;

        await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces");
        await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions");
        await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces");
        await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions");

        Assert.Equal(originalLeftWidth, harness.ViewModel.LeftPanelWidth);
        Assert.Equal(originalRightWidth, harness.ViewModel.RightPanelWidth);
    }

    [Fact]
    public async Task SwitchingPackageViews_RetainsOpenedHostedViews()
    {
        DisposablePackageView.ResetCreatedCount();
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.workspaces"),
            ("agent", "agent.subsessions")
        );
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );

        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));
        var workspaceView = AssertHostedView<DisposablePackageView>(
            harness.ViewModel.RightTopPanel.HostedView
        );

        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions"));
        var subsessionsView = AssertHostedView<DisposablePackageView>(
            harness.ViewModel.RightTopPanel.HostedView
        );

        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));
        var reopenedWorkspaceView = AssertHostedView<DisposablePackageView>(
            harness.ViewModel.RightTopPanel.HostedView
        );

        Assert.Same(workspaceView, reopenedWorkspaceView);
        Assert.NotSame(workspaceView, subsessionsView);
        Assert.Equal(2, DisposablePackageView.CreatedCount);
        Assert.Contains(
            harness.ViewModel.RightTopPanel.HostedViews,
            view => view.ViewId == "agent.workspaces"
        );
        Assert.Contains(
            harness.ViewModel.RightTopPanel.HostedViews,
            view => view.ViewId == "agent.subsessions"
        );
    }

    [Fact]
    public async Task RemovePackageViewFromHotbar_RetainsHiddenHostedViewForReuse()
    {
        DisposablePackageView.ResetCreatedCount();
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.workspaces"),
            ("agent", "agent.subsessions")
        );
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions"));
        var retainedWorkspace = Assert.Single(
            harness.ViewModel.RightTopPanel.HostedViews,
            view => view.ViewId == "agent.workspaces");

        var removed = harness.ViewModel.RemovePackageViewFromHotbar("agent.workspaces");

        Assert.True(removed);
        Assert.Contains(retainedWorkspace, harness.ViewModel.RightTopPanel.HostedViews);
        Assert.False(retainedWorkspace.IsActive);
        Assert.Contains(
            harness.ViewModel.RightTopPanel.HostedViews,
            view => view.ViewId == "agent.subsessions"
        );
        AssertHostedView<DisposablePackageView>(harness.ViewModel.RightTopPanel.HostedView);
    }

    [Fact]
    public async Task OpeningAndClosingPackageView_DoesNotChangePanelWidths()
    {
        using var harness = CreateHarness();
        var originalLeftWidth = harness.ViewModel.LeftPanelWidth;
        var originalRightWidth = harness.ViewModel.RightPanelWidth;

        for (var index = 0; index < 5; index++)
        {
            Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions"));
            Assert.True(harness.ViewModel.ClosePackageViewPanel("agent.subsessions"));
        }

        Assert.Equal(originalLeftWidth, harness.ViewModel.LeftPanelWidth);
        Assert.Equal(originalRightWidth, harness.ViewModel.RightPanelWidth);
    }

    [Fact]
    public async Task LayoutController_CommitsOncePerOpenAndCloseAndSkipsSameSideSwitch()
    {
        using var harness = CreateHarness();
        var shellGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,0,*"),
        };
        var topGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("0,0,*,0,0"),
        };
        var bottomGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,0,*"),
        };
        shellGrid.Arrange(new Rect(0, 0, 1200, 800));
        topGrid.Arrange(new Rect(0, 0, 1200, 560));
        bottomGrid.Arrange(new Rect(0, 0, 1200, 236));
        var leftSplitter = new GridSplitter();
        var rightSplitter = new GridSplitter();
        var bottomRowSplitter = new GridSplitter();
        var bottomColumnSplitter = new GridSplitter();
        var controller = new MainWindowLayoutController(
            shellGrid,
            topGrid,
            bottomGrid,
            leftSplitter,
            rightSplitter,
            bottomRowSplitter,
            bottomColumnSplitter,
            new Border(),
            new Border(),
            new Border(),
            new Border(),
            () => harness.ViewModel);
        controller.ApplyAdaptiveLayout();
        var initialCommitCount = controller.GeometryCommitCount;
        harness.ViewModel.ShellViewStateChanged += controller.ApplyAdaptiveLayout;

        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));
        var openedCommitCount = controller.GeometryCommitCount;
        Assert.Equal(initialCommitCount + 1, openedCommitCount);

        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.subsessions"));
        Assert.Equal(openedCommitCount, controller.GeometryCommitCount);

        Assert.True(harness.ViewModel.ClosePackageViewPanel("agent.subsessions"));
        Assert.Equal(openedCommitCount + 1, controller.GeometryCommitCount);
    }

    [Fact]
    public async Task ApplyPackageLifecycleChangesAsync_WhenRuntimeHasNoActivePackages_RemovesLoadedPackageViews()
    {
        using var harness = CreateHarness(new EmptyRuntimeApiClientFactory());

        Assert.Contains(
            GetPackageMenuGroups(harness.ViewModel),
            group => group.Id == "package:agent"
        );
        Assert.True(harness.ViewModel.IsViewInHotbar("agent.chat"));

        await harness.ViewModel.ApplyPackageLifecycleSnapshotAsync(CreateRuntimeSnapshot([], []));

        Assert.Empty(GetPackageMenuGroups(harness.ViewModel));
        Assert.Empty(harness.ViewModel.ListHotbarViews());
        Assert.False(harness.ViewModel.HasMiddleSelection);
        Assert.Equal("No packages loaded", harness.ViewModel.SyncStatusText);
    }

    [Fact]
    public async Task ApplyPackageLifecycleChangesAsync_WhenPackageRemoved_RetiresCachedPackageViewsAfterCommit()
    {
        var rootPath = CreateTempDirectory();
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var packageViewHostService = CreatePackageViewHostService();
        await packageViewHostService.ApplyPackageDeltaAsync(
            [CreateActiveAgentPackage()],
            [RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder)]
        );
        using var harness = CreateHarness(
            rootPath,
            new EmptyRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.chat"));
        var hostedBoundary = Assert.IsType<HostedPackageViewBoundary>(
            harness.ViewModel.MiddlePanel.HostedView
        );
        var hostedView = hostedBoundary.HostedView;

        await harness.ViewModel.ApplyPackageLifecycleSnapshotAsync(CreateRuntimeSnapshot([], []));
        await packageViewHostService.WaitForRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(
            Assert.IsType<bool>(
                hostedView
                    .GetType()
                    .GetProperty(nameof(ShellLifecycleThreadAffinedPackageView.IsDisposed))
                    ?.GetValue(hostedView)
            )
        );
    }

    [Fact]
    public async Task ApplyPackageLifecycleSnapshotAsync_StabilizesSelectedCandidateBeforeVisibleCommit()
    {
        var rootPath = CreateTempDirectory();
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var navigationGatePath = Path.Combine(rootPath, "navigation-gate");
        var package = CreateActiveAgentPackage();
        var firstSource = RuntimeContractTestData.Snapshot(
            "agent",
            PackageSourceKind.Dev,
            packageSourceFolder
        );
        var packageViewHostService = CreatePackageViewHostService();
        await packageViewHostService.ApplyPackageDeltaAsync([package], [firstSource]);
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );
        var originalBoundary = Assert.IsType<HostedPackageViewBoundary>(
            harness.ViewModel.MiddlePanel.HostedView
        );
        var originalView = originalBoundary.HostedView;
        File.WriteAllText(
            Path.Combine(
                packageSourceFolder,
                ShellLifecycleTestPackageModule.NavigationGatePathFileName
            ),
            navigationGatePath
        );
        File.WriteAllText(Path.Combine(packageSourceFolder, "replacement-content"), string.Empty);
        var replacementSource = RuntimeContractTestData.Snapshot(
            "agent",
            PackageSourceKind.Dev,
            packageSourceFolder
        );

        var apply = harness.ViewModel.ApplyPackageLifecycleSnapshotAsync(
            CreateRuntimeSnapshot([package], [replacementSource], generation: 2),
            ["agent"]
        );
        try
        {
            await WaitForConditionAsync(() => File.Exists(navigationGatePath + ".started"));

            Assert.False(apply.IsCompleted);
            Assert.Same(originalBoundary, harness.ViewModel.MiddlePanel.HostedView);
            Assert.Same(originalView, packageViewHostService.GetOrCreateView("agent.chat"));
            Assert.False(
                Assert.IsType<bool>(
                    originalView
                        .GetType()
                        .GetProperty(nameof(ShellLifecycleThreadAffinedPackageView.IsDisposed))
                        ?.GetValue(originalView)
                )
            );

            File.WriteAllText(navigationGatePath + ".release", string.Empty);
            await apply.WaitAsync(TimeSpan.FromSeconds(5));
            await packageViewHostService
                .WaitForRetirementsAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            var replacementBoundary = Assert.IsType<HostedPackageViewBoundary>(
                harness.ViewModel.MiddlePanel.HostedView
            );
            Assert.NotSame(originalBoundary, replacementBoundary);
            Assert.NotSame(originalView, replacementBoundary.HostedView);
            Assert.True(
                Assert.IsType<bool>(
                    originalView
                        .GetType()
                        .GetProperty(nameof(ShellLifecycleThreadAffinedPackageView.IsDisposed))
                        ?.GetValue(originalView)
                )
            );
        }
        finally
        {
            File.WriteAllText(navigationGatePath + ".release", string.Empty);
        }
    }

    [Fact]
    public async Task ApplyPackageLifecycleSnapshotAsync_StagesAllSelectionsBeforeExactOnceAttachmentAwareNavigation()
    {
        var rootPath = CreateTempDirectory();
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var activityPath = Path.Combine(rootPath, "navigation-activity");
        var package = CreateActiveAgentPackage();
        var firstSource = RuntimeContractTestData.Snapshot(
            "agent",
            PackageSourceKind.Dev,
            packageSourceFolder
        );
        var packageViewHostService = CreatePackageViewHostService();
        await packageViewHostService.ApplyPackageDeltaAsync([package], [firstSource]);
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));
        File.WriteAllText(
            Path.Combine(
                packageSourceFolder,
                ShellLifecycleTestPackageModule.NavigationActivityPathFileName
            ),
            activityPath
        );
        File.WriteAllText(
            Path.Combine(
                packageSourceFolder,
                ShellLifecycleTestPackageModule.WaitForAttachmentMarkerFileName
            ),
            string.Empty
        );
        File.WriteAllText(Path.Combine(packageSourceFolder, "replacement-content"), string.Empty);
        var replacementSource = RuntimeContractTestData.Snapshot(
            "agent",
            PackageSourceKind.Dev,
            packageSourceFolder
        );

        await harness
            .ViewModel.ApplyPackageLifecycleSnapshotAsync(
                CreateRuntimeSnapshot([package], [replacementSource], generation: 2),
                ["agent"]
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        var activity = File.ReadAllLines(activityPath);
        Assert.Equal(1, activity.Count(line => line == "navigation-started:agent.chat"));
        Assert.Equal(1, activity.Count(line => line == "navigation-completed:agent.chat"));
        Assert.Equal(1, activity.Count(line => line == "navigation-started:agent.workspaces"));
        Assert.Equal(1, activity.Count(line => line == "navigation-completed:agent.workspaces"));
        Assert.True(
            Array.IndexOf(activity, "loaded")
                < Array.IndexOf(activity, "navigation-started:agent.chat")
        );
        Assert.Equal(2, harness.StagingSurface.MaximumStagedViewCount);
        Assert.Equal(2, harness.StagingSurface.StagedViews.Count);
        Assert.Empty(harness.StagingSurface.ActiveViews);
        Assert.Same(
            harness.StagingSurface.StagedViews.Single(view => GetViewId(view) == "agent.chat"),
            Assert
                .IsType<HostedPackageViewBoundary>(harness.ViewModel.MiddlePanel.HostedView)
                .HostedView
        );
        Assert.Same(
            harness.StagingSurface.StagedViews.Single(view =>
                GetViewId(view) == "agent.workspaces"
            ),
            Assert
                .IsType<HostedPackageViewBoundary>(harness.ViewModel.RightTopPanel.HostedView)
                .HostedView
        );
    }

    [Fact]
    public async Task ApplyPackageLifecycleSnapshotAsync_WhenAttachmentAwareNavigationFails_DetachesAndDisposesCandidate()
    {
        var rootPath = CreateTempDirectory();
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var activityPath = Path.Combine(rootPath, "navigation-activity");
        var package = CreateActiveAgentPackage();
        var firstSource = RuntimeContractTestData.Snapshot(
            "agent",
            PackageSourceKind.Dev,
            packageSourceFolder
        );
        var packageViewHostService = CreatePackageViewHostService();
        await packageViewHostService.ApplyPackageDeltaAsync([package], [firstSource]);
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );
        var originalBoundary = Assert.IsType<HostedPackageViewBoundary>(
            harness.ViewModel.MiddlePanel.HostedView
        );
        var originalView = originalBoundary.HostedView;
        File.WriteAllText(
            Path.Combine(
                packageSourceFolder,
                ShellLifecycleTestPackageModule.NavigationActivityPathFileName
            ),
            activityPath
        );
        File.WriteAllText(
            Path.Combine(
                packageSourceFolder,
                ShellLifecycleTestPackageModule.WaitForAttachmentMarkerFileName
            ),
            string.Empty
        );
        File.WriteAllText(
            Path.Combine(
                packageSourceFolder,
                ShellLifecycleTestPackageModule.ThrowNavigationMarkerFileName
            ),
            string.Empty
        );
        File.WriteAllText(Path.Combine(packageSourceFolder, "replacement-content"), string.Empty);
        var replacementSource = RuntimeContractTestData.Snapshot(
            "agent",
            PackageSourceKind.Dev,
            packageSourceFolder
        );

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ViewModel.ApplyPackageLifecycleSnapshotAsync(
                CreateRuntimeSnapshot([package], [replacementSource], generation: 2),
                ["agent"]
            )
        );

        var candidateView = Assert.Single(harness.StagingSurface.StagedViews);
        await packageViewHostService.WaitForRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("requested navigation failure", error.Message, StringComparison.Ordinal);
        Assert.Same(originalBoundary, harness.ViewModel.MiddlePanel.HostedView);
        Assert.Same(originalView, packageViewHostService.GetOrCreateView("agent.chat"));
        Assert.False(GetIsDisposed(originalView));
        Assert.True(GetIsDisposed(candidateView));
        Assert.Null(candidateView.Parent);
        Assert.Empty(harness.StagingSurface.ActiveViews);
        Assert.Equal(
            1,
            File.ReadAllLines(activityPath).Count(line => line == "navigation-started:agent.chat")
        );
    }

    [Fact]
    public async Task ApplyPackageLifecycleSnapshotAsync_WhenAttachmentAwareNavigationIsCancelled_DetachesAndPreservesCurrentGeneration()
    {
        var rootPath = CreateTempDirectory();
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var navigationGatePath = Path.Combine(rootPath, "navigation-gate");
        var package = CreateActiveAgentPackage();
        var firstSource = RuntimeContractTestData.Snapshot(
            "agent",
            PackageSourceKind.Dev,
            packageSourceFolder
        );
        var packageViewHostService = CreatePackageViewHostService();
        await packageViewHostService.ApplyPackageDeltaAsync([package], [firstSource]);
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );
        var originalBoundary = Assert.IsType<HostedPackageViewBoundary>(
            harness.ViewModel.MiddlePanel.HostedView
        );
        var originalView = originalBoundary.HostedView;
        File.WriteAllText(
            Path.Combine(
                packageSourceFolder,
                ShellLifecycleTestPackageModule.NavigationGatePathFileName
            ),
            navigationGatePath
        );
        File.WriteAllText(
            Path.Combine(
                packageSourceFolder,
                ShellLifecycleTestPackageModule.WaitForAttachmentMarkerFileName
            ),
            string.Empty
        );
        File.WriteAllText(Path.Combine(packageSourceFolder, "replacement-content"), string.Empty);
        var replacementSource = RuntimeContractTestData.Snapshot(
            "agent",
            PackageSourceKind.Dev,
            packageSourceFolder
        );
        using var cancellation = new CancellationTokenSource();

        var apply = harness.ViewModel.ApplyPackageLifecycleSnapshotAsync(
            CreateRuntimeSnapshot([package], [replacementSource], generation: 2),
            ["agent"],
            cancellation.Token
        );
        try
        {
            await WaitForConditionAsync(() => File.Exists(navigationGatePath + ".started"));
            var candidateView = Assert.Single(harness.StagingSurface.StagedViews);
            Assert.Single(harness.StagingSurface.ActiveViews);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
            await packageViewHostService
                .WaitForRetirementsAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(originalBoundary, harness.ViewModel.MiddlePanel.HostedView);
            Assert.Same(originalView, packageViewHostService.GetOrCreateView("agent.chat"));
            Assert.False(GetIsDisposed(originalView));
            Assert.True(GetIsDisposed(candidateView));
            Assert.Null(candidateView.Parent);
            Assert.Empty(harness.StagingSurface.ActiveViews);
        }
        finally
        {
            File.WriteAllText(navigationGatePath + ".release", string.Empty);
        }
    }

    [Fact]
    public async Task ApplyPackageLifecycleChangesAsync_WhenImpactedPackageRemainsActive_PreservesShellLayout()
    {
        using var harness = CreateActivePackageHarness();
        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.LeftTop, 0);

        Assert.True(harness.ViewModel.HasLeftTopPanelContent);
        Assert.True(harness.ViewModel.HasMiddleSelection);
        Assert.False(harness.ViewModel.IsViewInHotbar("agent.subsessions"));

        await harness.ViewModel.ApplyPackageLifecycleSnapshotAsync(
            harness.RuntimeSnapshot!,
            ["agent"]
        );

        var hotbarViews = harness.ViewModel.ListHotbarViews();
        Assert.True(harness.ViewModel.HasLeftTopPanelContent);
        Assert.True(harness.ViewModel.HasMiddleSelection);
        Assert.False(harness.ViewModel.IsViewInHotbar("agent.subsessions"));
        Assert.Equal(333, harness.ViewModel.LeftPanelWidth);
        Assert.Equal(444, harness.ViewModel.RightPanelWidth);
        Assert.Contains(
            hotbarViews,
            view =>
                view.ViewId == "agent.workspaces"
                && view.Placement == PackageViewPlacement.LeftTop
                && view.IsOpen
        );
        Assert.Contains(
            hotbarViews,
            view =>
                view.ViewId == "agent.chat"
                && view.Placement == PackageViewPlacement.Middle
                && view.IsOpen
        );
        Assert.DoesNotContain(hotbarViews, view => view.ViewId == "agent.subsessions");
        Assert.Contains(
            GetPackageMenuGroups(harness.ViewModel),
            group => group.Id == "package:agent"
        );
        Assert.Equal("1 package(s) active", harness.ViewModel.SyncStatusText);
    }

    [Fact]
    public async Task ShellPackageLifecyclePresenter_PreparedCommitRebuildsItemsWithoutRenavigatingStabilizedSelection()
    {
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.chat"),
            ("tools", "tools.dashboard")
        );
        try
        {
            var shellState = new ShellState
            {
                HasInitializedLayout = true,
                ViewPlacements = new Dictionary<string, RailPlacement>
                {
                    ["agent.chat"] = RailPlacement.Middle,
                    ["tools.dashboard"] = RailPlacement.Middle,
                },
                SelectedMiddleViewId = "tools.dashboard",
            };
            var viewsById = new Dictionary<string, ShellPackageView>(
                StringComparer.OrdinalIgnoreCase
            )
            {
                ["agent.chat"] = new(
                    "agent.chat",
                    "agent",
                    "Agent",
                    "1.0.0",
                    "Chat",
                    "A",
                    RailPlacement.Middle,
                    PackageReadinessState.Ready,
                    ShowInHotbarByDefault: true,
                    PackageGlyph: "A"
                ),
                ["tools.dashboard"] = new(
                    "tools.dashboard",
                    "tools",
                    "Tools",
                    "1.0.0",
                    "Dashboard",
                    "T",
                    RailPlacement.Middle,
                    PackageReadinessState.Ready,
                    ShowInHotbarByDefault: true,
                    PackageGlyph: "T"
                ),
            };
            var middleBar = new PackageIconBarViewModel(
                RailPlacement.Middle,
                Orientation.Horizontal,
                (_, _, _) => { },
                _ => ValueTask.FromResult(false),
                _ => false
            );
            var middlePanel = new ShellPanelViewModel();
            var selectionPresenter = new ShellSelectionPresenter();
            var panelContentPresenter = new ShellPanelContentPresenter(
                packageViewHostService,
                [],
                []
            );
            var navigatedViewIds = new List<string>();
            var railCollectionPresenter = new ShellRailCollectionPresenter(
                viewsById,
                shellState,
                selectionPresenter,
                panelContentPresenter,
                CreateShellItem,
                navigatedViewIds.Add,
                _ => { }
            );
            var slots = new[]
            {
                new ShellPlacementSlot(RailPlacement.Middle, middleBar, middlePanel, _ => { }),
            };
            var lifecyclePresenter = new ShellPackageLifecyclePresenter(
                new ShellCompositionService(),
                viewsById,
                shellState,
                [],
                [],
                _ => { },
                (createHostedViews, stabilizedViewIds) =>
                    railCollectionPresenter.Rebuild(slots, createHostedViews, stabilizedViewIds),
                () => { },
                _ => middlePanel.ClearRetainedViews()
            );
            railCollectionPresenter.Rebuild(slots, createHostedViews: true);
            var originalHostedBoundary = Assert.IsType<HostedPackageViewBoundary>(
                middlePanel.HostedView
            );
            var originalHostedView = Assert.IsType<DisposablePackageView>(
                originalHostedBoundary.HostedView
            );
            var originalToolsItem = Assert.Single(
                middleBar.Items,
                item => item.Id == "tools.dashboard"
            );
            var originalAgentItem = Assert.Single(middleBar.Items, item => item.Id == "agent.chat");
            Assert.Equal(["tools.dashboard"], navigatedViewIds);

            var presentation = lifecyclePresenter.PrepareLifecycleChanges([
                CreateActiveAgentPackage() with
                {
                    Version = "1.0.1",
                },
                CreateActiveToolsPackage(),
            ]);
            lifecyclePresenter.CommitPreparedLifecycleChanges(
                presentation,
                new HashSet<string>(["tools.dashboard"], StringComparer.OrdinalIgnoreCase)
            );

            Assert.NotSame(originalHostedBoundary, middlePanel.HostedView);
            Assert.False(originalHostedView.IsDisposed);
            Assert.NotSame(
                originalToolsItem,
                Assert.Single(middleBar.Items, item => item.Id == "tools.dashboard")
            );
            Assert.NotSame(
                originalAgentItem,
                Assert.Single(middleBar.Items, item => item.Id == "agent.chat")
            );
            Assert.True(selectionPresenter.HasMiddleSelection);
            Assert.Equal(["tools.dashboard"], navigatedViewIds);
        }
        finally
        {
            await packageViewHostService.DisposeAsync();
        }
    }

    [Fact]
    public async Task ApplyPackageLifecycleChangesAsync_WhenPackageRepeatedlyReinstalled_DoesNotDuplicateHotbarViews()
    {
        var rootPath = CreateTempDirectory();
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var packageSource = RuntimeContractTestData.Snapshot(
            "agent",
            PackageSourceKind.Dev,
            packageSourceFolder
        );
        var runtimeApiClientFactory = new MutableRuntimeApiClientFactory
        {
            ActivePackages = [CreateActiveAgentPackage()],
            PackageSources = [packageSource],
        };
        var packageViewHostService = CreatePackageViewHostService();
        using var harness = CreateHarness(
            rootPath,
            runtimeApiClientFactory,
            packageViewHostService,
            packageViewHostService
        );

        for (var index = 0; index < 3; index++)
        {
            runtimeApiClientFactory.ActivePackages = [];
            runtimeApiClientFactory.PackageSources = [];
            await harness.ViewModel.ApplyPackageLifecycleSnapshotAsync(
                CreateRuntimeSnapshot([], [], index * 2 + 2),
                ["agent"]
            );

            runtimeApiClientFactory.ActivePackages = [CreateActiveAgentPackage()];
            runtimeApiClientFactory.PackageSources = [packageSource];
            await harness.ViewModel.ApplyPackageLifecycleSnapshotAsync(
                CreateRuntimeSnapshot([CreateActiveAgentPackage()], [packageSource], index * 2 + 3),
                ["agent"]
            );
        }

        var hotbarViewIds = harness
            .ViewModel.ListHotbarViews()
            .Select(view => view.ViewId)
            .ToArray();

        Assert.Equal(
            hotbarViewIds.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            hotbarViewIds.Length
        );
        Assert.Single(
            hotbarViewIds,
            viewId => string.Equals(viewId, "agent.chat", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Single(
            hotbarViewIds,
            viewId => string.Equals(viewId, "agent.workspaces", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            hotbarViewIds,
            viewId => string.Equals(viewId, "agent.subsessions", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void MovePackageView_WhenSelectedMiddleViewMovesOut_SelectsFirstRemainingMiddleView()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService();
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );

        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.Middle, 1);
        Assert.Contains(
            harness.ViewModel.ListHotbarViews(),
            view =>
                view.ViewId == "agent.workspaces"
                && view.Placement == PackageViewPlacement.Middle
                && view.IsOpen
        );

        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.RightTop, 0);

        var hotbarViews = harness.ViewModel.ListHotbarViews();
        Assert.True(harness.ViewModel.HasMiddleSelection);
        Assert.Contains(
            hotbarViews,
            view =>
                view.ViewId == "agent.chat"
                && view.Placement == PackageViewPlacement.Middle
                && view.IsOpen
        );
        Assert.Contains(
            hotbarViews,
            view =>
                view.ViewId == "agent.workspaces"
                && view.Placement == PackageViewPlacement.RightTop
                && view.IsOpen
        );
        Assert.DoesNotContain(
            hotbarViews,
            view =>
                view.ViewId == "agent.workspaces" && view.Placement == PackageViewPlacement.Middle
        );
        Assert.True(harness.ViewModel.MiddlePanel.HasHostedView);
        Assert.False(harness.ViewModel.MiddlePanel.ShowFallbackLines);
        AssertHostedView<DisposablePackageView>(harness.ViewModel.MiddlePanel.HostedView);
    }

    [Fact]
    public async Task MovePackageView_WhenSelectedHostedViewMovesBetweenSideRails_ClearsPreviousPanelBeforeReusingCachedView()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.chat"),
            ("agent", "agent.workspaces")
        );
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );
        Assert.True(await harness.ViewModel.OpenPackageViewPanelAsync("agent.workspaces"));
        var hostedView = AssertHostedView<DisposablePackageView>(
            harness.ViewModel.RightTopPanel.HostedView
        );

        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.LeftTop, 0);

        Assert.Equal("agent.workspaces", harness.ViewModel.LeftTopPanel.ActiveViewId);
        Assert.Same(
            hostedView,
            AssertHostedView<DisposablePackageView>(harness.ViewModel.LeftTopPanel.HostedView)
        );
        Assert.Null(harness.ViewModel.RightTopPanel.ActiveViewId);
        Assert.False(harness.ViewModel.RightTopPanel.HasHostedView);

        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.RightTop, 0);

        Assert.Equal("agent.workspaces", harness.ViewModel.RightTopPanel.ActiveViewId);
        Assert.Same(
            hostedView,
            AssertHostedView<DisposablePackageView>(harness.ViewModel.RightTopPanel.HostedView)
        );
        Assert.Null(harness.ViewModel.LeftTopPanel.ActiveViewId);
        Assert.False(harness.ViewModel.LeftTopPanel.HasHostedView);
    }

    [Fact]
    public async Task PackageFaulted_RemovesPackageViewsFromShell()
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreateRegisteredPackageViewHostService(
            ("agent", "agent.chat"),
            ("agent", "agent.workspaces")
        );
        using var harness = CreateHarness(
            rootPath,
            new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService
        );

        await packageViewHostService.DisablePackageAsync(
            "agent",
            "Hosted view failed.",
            PackageFailureOrigin.AppHostedView
        );

        Assert.Empty(GetPackageMenuGroups(harness.ViewModel));
        Assert.Empty(harness.ViewModel.ListHotbarViews());
        Assert.False(harness.ViewModel.HasMiddleSelection);
    }

    [Fact]
    public async Task MovePackageView_WhenMovedForwardWithinSameBar_UsesTargetIndexAfterRemoval()
    {
        using var harness = CreateHarness();
        Assert.True(
            await harness.ViewModel.AddPackageViewToHotbarAsync(
                "agent.workspaces",
                PackageViewPlacement.Middle,
                1
            )
        );
        Assert.True(
            await harness.ViewModel.AddPackageViewToHotbarAsync(
                "agent.subsessions",
                PackageViewPlacement.Middle,
                2
            )
        );

        harness.ViewModel.MovePackageView("agent.chat", RailPlacement.Middle, 2);

        Assert.Equal(
            ["agent.workspaces", "agent.subsessions", "agent.chat"],
            GetMiddleHotbarOrder(harness.ViewModel)
        );
    }

    [Fact]
    public async Task MovePackageView_WhenDroppedIntoSameSlotWithinSameBar_DoesNotReorder()
    {
        using var harness = CreateHarness();
        Assert.True(
            await harness.ViewModel.AddPackageViewToHotbarAsync(
                "agent.workspaces",
                PackageViewPlacement.Middle,
                1
            )
        );
        Assert.True(
            await harness.ViewModel.AddPackageViewToHotbarAsync(
                "agent.subsessions",
                PackageViewPlacement.Middle,
                2
            )
        );

        harness.ViewModel.MovePackageView("agent.workspaces", RailPlacement.Middle, 1);

        Assert.Equal(
            ["agent.chat", "agent.workspaces", "agent.subsessions"],
            GetMiddleHotbarOrder(harness.ViewModel)
        );
    }

    [Fact]
    public async Task MovePackageView_WhenMovedUpWithinSameBar_PreservesRequestedTargetIndex()
    {
        using var harness = CreateHarness();
        Assert.True(
            await harness.ViewModel.AddPackageViewToHotbarAsync(
                "agent.workspaces",
                PackageViewPlacement.Middle,
                1
            )
        );
        Assert.True(
            await harness.ViewModel.AddPackageViewToHotbarAsync(
                "agent.subsessions",
                PackageViewPlacement.Middle,
                2
            )
        );

        harness.ViewModel.MovePackageView("agent.subsessions", RailPlacement.Middle, 1);

        Assert.Equal(
            ["agent.chat", "agent.subsessions", "agent.workspaces"],
            GetMiddleHotbarOrder(harness.ViewModel)
        );
    }

    [Fact]
    public async Task MovePackageView_WhenMovedForwardWithinVerticalBar_UsesTargetIndexAfterRemoval()
    {
        using var harness = CreateHarness();
        harness.ViewModel.MovePackageView("agent.chat", RailPlacement.RightTop, 0);
        Assert.True(
            await harness.ViewModel.AddPackageViewToHotbarAsync(
                "agent.subsessions",
                PackageViewPlacement.RightTop,
                2
            )
        );

        harness.ViewModel.MovePackageView("agent.chat", RailPlacement.RightTop, 2);

        Assert.Equal(
            ["agent.workspaces", "agent.subsessions", "agent.chat"],
            GetRightTopHotbarOrder(harness.ViewModel)
        );
    }

    [Fact]
    public async Task BottomSplitPanelContent_IsTrueOnlyWhenBothBottomPanelsHaveContent()
    {
        using var harness = CreateHarness();

        Assert.False(harness.ViewModel.HasAnyBottomPanelContent);
        Assert.False(harness.ViewModel.HasBottomSplitPanelContent);

        Assert.True(
            await harness.ViewModel.AddPackageViewToHotbarAsync(
                "agent.chat",
                PackageViewPlacement.LeftBottom,
                0,
                openPanel: true
            )
        );

        Assert.True(harness.ViewModel.HasAnyBottomPanelContent);
        Assert.False(harness.ViewModel.HasBottomSplitPanelContent);

        Assert.True(
            await harness.ViewModel.AddPackageViewToHotbarAsync(
                "agent.workspaces",
                PackageViewPlacement.RightBottom,
                0,
                openPanel: true
            )
        );

        Assert.True(harness.ViewModel.HasAnyBottomPanelContent);
        Assert.True(harness.ViewModel.HasBottomSplitPanelContent);
    }

    [Fact]
    public void CalculateTopColumnWidths_PreservesRequestedSideWidthsWhenSpaceAllows()
    {
        var widths = ShellLayoutCalculator.CalculateTopColumnWidths(
            totalWidth: 1476,
            requestedLeftWidth: 360,
            requestedRightWidth: 360,
            hasLeftPanel: true,
            hasRightPanel: true
        );

        Assert.Equal(360, widths.LeftWidth);
        Assert.Equal(360, widths.RightWidth);
    }

    [Fact]
    public void CalculateTopColumnWidths_ClampsVisualWidthsWhenMiddleNeedsSpace()
    {
        var widths = ShellLayoutCalculator.CalculateTopColumnWidths(
            totalWidth: 900,
            requestedLeftWidth: 360,
            requestedRightWidth: 360,
            hasLeftPanel: true,
            hasRightPanel: true
        );

        Assert.True(widths.LeftWidth < 360);
        Assert.True(widths.RightWidth < 360);
        Assert.Equal(widths.LeftWidth, widths.RightWidth);
    }

    [Fact]
    public void ShellLayoutCalculator_CalculatesVerticalWeightsForBottomPanels()
    {
        var noBottom = ShellLayoutCalculator.CalculateVerticalWeights(0.42, hasBottom: false);
        var clampedLow = ShellLayoutCalculator.CalculateVerticalWeights(0, hasBottom: true);
        var clampedHigh = ShellLayoutCalculator.CalculateVerticalWeights(1, hasBottom: true);

        Assert.Equal((1, 0, 0), noBottom);
        Assert.Equal(0.10, clampedLow.TopWeight);
        Assert.Equal(ShellLayoutCalculator.SplitterThickness, clampedLow.SplitterHeight);
        Assert.Equal(0.90, clampedLow.BottomWeight, precision: 10);
        Assert.Equal(0.90, clampedHigh.TopWeight);
        Assert.Equal(0.10, clampedHigh.BottomWeight, precision: 10);
    }

    [Fact]
    public void ShellLayoutCalculator_CalculatesBottomColumnWeightsForVisiblePanels()
    {
        Assert.Equal(
            (1, 0),
            ShellLayoutCalculator.CalculateBottomColumnWeights(
                0.35,
                hasLeftBottom: true,
                hasRightBottom: false
            )
        );
        Assert.Equal(
            (0, 1),
            ShellLayoutCalculator.CalculateBottomColumnWeights(
                0.35,
                hasLeftBottom: false,
                hasRightBottom: true
            )
        );
        Assert.Equal(
            (0, 0),
            ShellLayoutCalculator.CalculateBottomColumnWeights(
                0.35,
                hasLeftBottom: false,
                hasRightBottom: false
            )
        );

        var both = ShellLayoutCalculator.CalculateBottomColumnWeights(
            1,
            hasLeftBottom: true,
            hasRightBottom: true
        );
        Assert.Equal(0.99, both.LeftWeight);
        Assert.Equal(0.01, both.RightWeight, precision: 10);
    }

    [Fact]
    public async Task ApplyRuntimeAddressAsync_WhenAddressIsInvalid_DoesNotUseRuntimeApiClient()
    {
        using var harness = CreateHarness(new ThrowingRuntimeApiClientFactory());

        harness.ViewModel.RuntimeAddressText = "not a url";
        await harness.ViewModel.ApplyRuntimeAddressCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsRuntimeBusy);
        Assert.True(harness.ViewModel.IsRuntimeRunning);
        Assert.Equal("Runtime address invalid", harness.ViewModel.SystemStatusText);
        Assert.Contains("not a url", harness.ViewModel.RuntimeLastError);
    }

    [Fact]
    public async Task RefreshRuntimeAsync_WhenStatusAndHealthFail_ClearsBusyState()
    {
        var runtimeApiClientFactory = new StaticRuntimeApiClientFactory(
            [],
            [],
            _ =>
                Task.FromException<SystemStatusResponse?>(
                    new InvalidOperationException("status failed")
                ),
            _ => Task.FromException<bool>(new InvalidOperationException("health failed"))
        );
        using var harness = CreateHarness(runtimeApiClientFactory);

        await harness.ViewModel.RefreshRuntimeCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.IsRuntimeBusy);
        Assert.False(harness.ViewModel.IsRuntimeRunning);
        Assert.False(harness.ViewModel.IsRuntimeReady);
        Assert.Equal("Runtime unavailable", harness.ViewModel.RuntimeStatusText);
        Assert.Equal("Runtime unavailable", harness.ViewModel.SystemStatusText);
        Assert.Equal("status failed", harness.ViewModel.RuntimeLastError);
    }

    private static IReadOnlyList<ShellMenuItem> GetPackageMenuGroups(
        MainWindowViewModel viewModel
    ) =>
        viewModel
            .GetMainMenuItems()
            .Single(item => item.Id == "view")
            .Children.Single(item => item.Id == "view:packages")
            .Children.Where(item => item.Id.StartsWith("package:", StringComparison.Ordinal))
            .ToArray();

    private static MainWindowViewModelHarness CreateHarness(
        IRuntimeApiClientFactory? runtimeApiClientFactory = null,
        AppPackageShellViewService? shellViewService = null
    )
    {
        var rootPath = CreateTempDirectory();
        var packageViewHostService = CreatePackageViewHostService();
        return CreateHarness(
            rootPath,
            runtimeApiClientFactory ?? new ThrowingRuntimeApiClientFactory(),
            packageViewHostService,
            packageViewHostService,
            shellViewService: shellViewService
        );
    }

    private static MainWindowViewModelHarness CreateActivePackageHarness()
    {
        var rootPath = CreateTempDirectory();
        var packageSourceFolder = CreateAppPackageSource(rootPath, "agent");
        var runtimeSnapshot = CreateRuntimeSnapshot(
            [CreateActiveAgentPackage()],
            [RuntimeContractTestData.Snapshot("agent", PackageSourceKind.Dev, packageSourceFolder)]
        );
        var runtimeApiClientFactory = new StaticRuntimeApiClientFactory(
            runtimeSnapshot.ActivePackages,
            runtimeSnapshot.PackageUiSnapshots
        );
        var packageViewHostService = CreatePackageViewHostService();
        return CreateHarness(
            rootPath,
            runtimeApiClientFactory,
            packageViewHostService,
            packageViewHostService,
            runtimeSnapshot: runtimeSnapshot
        );
    }

    private static MainWindowViewModelHarness CreateHarness(
        string rootPath,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        PackageViewHostService packageViewHostService,
        PackageViewHostService? disposablePackageViewHostService = null,
        AppPackageShellViewService? shellViewService = null,
        RuntimePackageSnapshot? runtimeSnapshot = null,
        bool deferInitialHostedViews = false,
        IUiDispatcher? uiDispatcher = null
    )
    {
        var state = new ShellState
        {
            HasInitializedLayout = true,
            ViewPlacements = new Dictionary<string, RailPlacement>
            {
                ["agent.chat"] = RailPlacement.Middle,
                ["agent.workspaces"] = RailPlacement.RightTop,
                ["agent.subsessions"] = RailPlacement.RightTop,
            },
            LeftPanelWidth = 333,
            RightPanelWidth = 444,
            HiddenHotbarViewIds = ["agent.subsessions"],
            SelectedMiddleViewId = "agent.chat",
        };
        var snapshot = new ShellSnapshot(
            [
                new ShellPackageView(
                    "agent.chat",
                    "agent",
                    "Agent",
                    "1.0.0",
                    "Chat",
                    "A",
                    RailPlacement.Middle,
                    PackageReadinessState.Ready,
                    ShowInHotbarByDefault: true,
                    PackageGlyph: "A"
                ),
                new ShellPackageView(
                    "agent.workspaces",
                    "agent",
                    "Agent",
                    "1.0.0",
                    "Workspaces",
                    "W",
                    RailPlacement.RightTop,
                    PackageReadinessState.Ready,
                    ShowInHotbarByDefault: true,
                    PackageGlyph: "A"
                ),
                new ShellPackageView(
                    "agent.subsessions",
                    "agent",
                    "Agent",
                    "1.0.0",
                    "Subsessions",
                    "S",
                    RailPlacement.RightTop,
                    PackageReadinessState.Ready,
                    ShowInHotbarByDefault: false,
                    PackageGlyph: "A"
                ),
            ],
            state,
            StartupWarnings: [],
            StartupErrors: [],
            SystemStatusText: "Runtime Ready",
            SyncStatusText: "3 package view(s) active"
        );
        var statePath = Path.Combine(rootPath, "shell-state.json");
        var viewModel = new MainWindowViewModel(
            new TestWindowLauncher(),
            new ShellStateService(statePath),
            snapshot,
            packageViewHostService,
            new RuntimeConnectionState(AppStartupOptions.DefaultRuntimeUrl),
            runtimeApiClientFactory,
            new RuntimeHostProcessManager(new AppStartupOptions()),
            new SystemStatusResponse("Runtime", "1.0.0", true, DateTimeOffset.UtcNow),
            new NotificationCenterService(Path.Combine(rootPath, "notifications.json")),
            shellViewService,
            deferInitialHostedViews: deferInitialHostedViews,
            uiDispatcher: uiDispatcher ?? new ImmediateUiDispatcher()
        );
        var stagingSurface = new TestPackageViewStagingSurface();
        viewModel.ConfigurePackageViewStagingSurface(
            stagingSurface.Stage,
            stagingSurface.DetachAll
        );
        return new MainWindowViewModelHarness(
            viewModel,
            rootPath,
            statePath,
            stagingSurface,
            disposablePackageViewHostService,
            runtimeSnapshot
        );
    }

    private static RuntimePackageSnapshot CreateRuntimeSnapshot(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        long generation = 1
    ) =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            generation,
            generation,
            RuntimeBootstrapState.Ready,
            activePackages,
            [],
            packageSources,
            [],
            []
        );

    private static ActivePackageDescriptor CreateActiveAgentPackage() =>
        new(
            "agent",
            "Agent",
            "1.0.0",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            null,
            true,
            PackageReadinessState.Ready,
            [
                new PackageViewDescriptor(
                    "agent.chat",
                    "agent",
                    "Chat",
                    new PackageIconDescriptor("A", AssetPath: null),
                    "middle"
                ),
                new PackageViewDescriptor(
                    "agent.workspaces",
                    "agent",
                    "Workspaces",
                    new PackageIconDescriptor("W", AssetPath: null),
                    "right-top"
                ),
                new PackageViewDescriptor(
                    "agent.subsessions",
                    "agent",
                    "Subsessions",
                    new PackageIconDescriptor("S", AssetPath: null),
                    "right-top",
                    ShowInHotbarByDefault: false
                ),
            ]
        );

    private static ActivePackageDescriptor CreateActiveToolsPackage() =>
        new(
            "tools",
            "Tools",
            "1.0.0",
            PackageHostRoles.App | PackageHostRoles.Runtime,
            null,
            true,
            PackageReadinessState.Ready,
            [
                new PackageViewDescriptor(
                    "tools.dashboard",
                    "tools",
                    "Dashboard",
                    new PackageIconDescriptor("T", AssetPath: null),
                    "middle"
                ),
            ]
        );

    private static ShellItemViewModel CreateShellItem(
        ShellPackageView packageView,
        Action<ShellItemViewModel> onSelect
    ) =>
        new(
            packageView.ViewId,
            packageView.Glyph,
            iconUri: null,
            packageView.Title,
            packageView.PackageDisplayName,
            packageView.Title,
            packageView.Placement,
            onSelect
        );

    private static PackageViewHostService CreatePackageViewHostService() =>
        new(
            new AppPackageViewRegistry(),
            [],
            [],
            [],
            sessionFolder: null,
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync,
            uiDispatcher: new ImmediateUiDispatcher()
        );

    private static PackageViewHostService CreateRegisteredPackageViewHostService(
        params (string PackageId, string ViewId)[] registrations
    )
    {
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        if (registrations.Length == 0)
        {
            registrations = [("agent", "agent.chat")];
        }

        foreach (var registration in registrations)
        {
            registry.RegisterPackageView<DisposablePackageView>(
                registration.PackageId,
                new PackageViewRegistration(registration.ViewId, registration.ViewId),
                serviceProvider
            );
        }

        return new PackageViewHostService(
            registry,
            [],
            [],
            [],
            sessionFolder: null,
            uiDispatcher: new ImmediateUiDispatcher()
        );
    }

    private static PackageViewHostService CreateNavigationPackageViewHostService(
        InitialNavigationProbe probe
    )
    {
        var registry = new AppPackageViewRegistry();
        var serviceProvider = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        registry.RegisterPackageView<InitialNavigationPackageView>(
            "agent",
            "agent.chat",
            serviceProvider
        );
        return new PackageViewHostService(
            registry,
            [],
            [serviceProvider],
            [],
            sessionFolder: null,
            uiDispatcher: new ImmediateUiDispatcher()
        );
    }

    private static string CreateAppPackageSource(string rootPath, string packageId)
    {
        var packageSourceFolder = Path.Combine(rootPath, "package-source");
        var libraryFolder = Path.Combine(packageSourceFolder, "lib");
        Directory.CreateDirectory(libraryFolder);

        var assemblyPath = typeof(ShellLifecycleTestPackageModule).Assembly.Location;
        var entryAssemblyFileName = Path.GetFileName(assemblyPath);
        File.WriteAllText(
            Path.Combine(packageSourceFolder, "sunder-package.json"),
            $$"""
            {
              "id": "{{packageId}}",
              "entryAssembly": "{{entryAssemblyFileName}}"
            }
            """
        );
        File.WriteAllBytes(Path.Combine(packageSourceFolder, "icon.png"), [1, 2, 3]);

        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        {
            File.Copy(file, Path.Combine(libraryFolder, Path.GetFileName(file)), overwrite: true);
        }

        var depsPath = Path.ChangeExtension(assemblyPath, ".deps.json");
        if (File.Exists(depsPath))
        {
            File.Copy(
                depsPath,
                Path.Combine(libraryFolder, Path.GetFileName(depsPath)),
                overwrite: true
            );
        }

        return packageSourceFolder;
    }

    private static string[] GetMiddleHotbarOrder(MainWindowViewModel viewModel) =>
        viewModel
            .ListHotbarViews()
            .Where(view => view.Placement == PackageViewPlacement.Middle)
            .OrderBy(view => view.Order)
            .Select(view => view.ViewId)
            .ToArray();

    private static string[] GetRightTopHotbarOrder(MainWindowViewModel viewModel) =>
        viewModel
            .ListHotbarViews()
            .Where(view => view.Placement == PackageViewPlacement.RightTop)
            .OrderBy(view => view.Order)
            .Select(view => view.ViewId)
            .ToArray();

    private static TView AssertHostedView<TView>(object? hostedView)
        where TView : Control
    {
        var boundary = Assert.IsType<HostedPackageViewBoundary>(hostedView);
        return Assert.IsType<TView>(boundary.HostedView);
    }

    private static bool GetIsDisposed(object view) =>
        Assert.IsType<bool>(
            view.GetType()
                .GetProperty(nameof(ShellLifecycleThreadAffinedPackageView.IsDisposed))
                ?.GetValue(view)
        );

    private static string GetViewId(Control view) =>
        Assert.IsType<string>(
            view.GetType()
                .GetProperty(nameof(ShellLifecycleThreadAffinedPackageView.NavigatedViewId))
                ?.GetValue(view)
        );

    private sealed class MainWindowViewModelHarness(
        MainWindowViewModel viewModel,
        string rootPath,
        string statePath,
        TestPackageViewStagingSurface stagingSurface,
        PackageViewHostService? packageViewHostService = null,
        RuntimePackageSnapshot? runtimeSnapshot = null
    ) : IDisposable
    {
        public MainWindowViewModel ViewModel { get; } = viewModel;

        public RuntimePackageSnapshot? RuntimeSnapshot { get; } = runtimeSnapshot;

        public string StatePath { get; } = statePath;

        public TestPackageViewStagingSurface StagingSurface { get; } = stagingSurface;

        public void Dispose()
        {
            ViewModel.Dispose();
            packageViewHostService?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    private sealed class TestWindowLauncher : IWindowLauncher
    {
        public void ShowSettings() { }

        public Task<bool> ShowPackageSettingsAsync(
            string packageId,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(false);

        public void ShowPackages() { }

        public void ShowStacks() { }

        public void ShowDeveloperLogs() { }

        public void CloseForShutdown() { }
    }

    private sealed class ThrowingRuntimeApiClientFactory : IRuntimeApiClientFactory
    {
        public TClient CreateClient<TClient>()
            where TClient : class, IRuntimeClient =>
            throw new InvalidOperationException("Runtime API is not used by these tests.");
    }

    private sealed class EmptyRuntimeApiClientFactory : IRuntimeApiClientFactory
    {
        public TClient CreateClient<TClient>()
            where TClient : class, IRuntimeClient =>
            (TClient)(object)new StaticRuntimeApiClient([], []);
    }

    private sealed class MutableRuntimeApiClientFactory : IRuntimeApiClientFactory
    {
        public IReadOnlyList<ActivePackageDescriptor> ActivePackages { get; set; } = [];

        public IReadOnlyList<PackageUiSnapshotDescriptor> PackageSources { get; set; } = [];

        public TClient CreateClient<TClient>()
            where TClient : class, IRuntimeClient =>
            (TClient)(object)new StaticRuntimeApiClient(ActivePackages, PackageSources);
    }

    private sealed class StaticRuntimeApiClientFactory(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        Func<CancellationToken, Task<SystemStatusResponse?>>? getSystemStatusAsync = null,
        Func<CancellationToken, Task<bool>>? isRuntimeHealthyAsync = null,
        Func<
            CancellationToken,
            Task<IReadOnlyList<ActivePackageDescriptor>>
        >? getActivePackagesAsync = null,
        Func<
            CancellationToken,
            Task<IReadOnlyList<PackageUiSnapshotDescriptor>>
        >? getActivePackageSourcesAsync = null
    ) : IRuntimeApiClientFactory
    {
        public TClient CreateClient<TClient>()
            where TClient : class, IRuntimeClient =>
            (TClient)
                (object)
                    new StaticRuntimeApiClient(
                        activePackages,
                        packageSources,
                        getSystemStatusAsync,
                        isRuntimeHealthyAsync,
                        getActivePackagesAsync,
                        getActivePackageSourcesAsync
                    );
    }

    private sealed class StaticRuntimeApiClient(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        Func<CancellationToken, Task<SystemStatusResponse?>>? getSystemStatusAsync = null,
        Func<CancellationToken, Task<bool>>? isRuntimeHealthyAsync = null,
        Func<
            CancellationToken,
            Task<IReadOnlyList<ActivePackageDescriptor>>
        >? getActivePackagesAsync = null,
        Func<
            CancellationToken,
            Task<IReadOnlyList<PackageUiSnapshotDescriptor>>
        >? getActivePackageSourcesAsync = null
    ) : IRuntimeShellClient
    {
        public Task<SystemStatusResponse?> GetSystemStatusAsync(
            CancellationToken cancellationToken = default
        ) =>
            getSystemStatusAsync?.Invoke(cancellationToken)
            ?? Task.FromResult<SystemStatusResponse?>(null);

        public Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default) =>
            isRuntimeHealthyAsync?.Invoke(cancellationToken) ?? Task.FromResult(true);

        public Task<IReadOnlyList<ActivePackageDescriptor>> GetActivePackagesAsync(
            CancellationToken cancellationToken = default
        ) => getActivePackagesAsync?.Invoke(cancellationToken) ?? Task.FromResult(activePackages);

        public Task<IReadOnlyList<SessionPackageDescriptor>> GetSessionPackagesAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyList<SessionPackageDescriptor>>([]);

        public Task<IReadOnlyList<PackageUiSnapshotDescriptor>> GetActivePackageUiSnapshotsAsync(
            CancellationToken cancellationToken = default
        ) =>
            getActivePackageSourcesAsync?.Invoke(cancellationToken)
            ?? Task.FromResult(packageSources);

        public Task DownloadPackageUiSnapshotAsync(
            PackageUiSnapshotDescriptor snapshot,
            Stream destination,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;

        public Uri CreatePackageAssetUri(string packageId, string assetPath) =>
            new($"https://runtime.test/api/v1/packages/{packageId}/assets/{assetPath}");

        public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class DisposablePackageView
        : Control,
            IPackageViewWarmupTarget,
            IPackageViewNavigationTarget,
            IDisposable
    {
        // Deferred shell tests use an inline dispatcher, so keep lifecycle probes off
        // Avalonia's thread-affined DataContext fallback path.
        public DisposablePackageView()
        {
            CreatedCount++;
        }

        public static int CreatedCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public static void ResetCreatedCount() => CreatedCount = 0;

        public ValueTask WarmupAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class InitialNavigationPackageView(InitialNavigationProbe probe)
        : Control,
            IPackageViewWarmupTarget,
            IPackageViewNavigationTarget
    {
        public ValueTask WarmupAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.WarmupCompleted = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default)
        {
            probe.NavigationObservedWarmup = probe.WarmupCompleted;
            return probe.NavigateAsync(context, cancellationToken);
        }
    }

    private sealed class InitialNavigationProbe
    {
        public bool WarmupCompleted { get; set; }

        public bool NavigationObservedWarmup { get; set; }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PackageViewNavigationContext? Context { get; set; }

        public Func<
            PackageViewNavigationContext,
            CancellationToken,
            ValueTask
        >
            NavigateAsync
        { get; set; } = static (_, _) => ValueTask.CompletedTask;
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
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

    private sealed class WorkGate
    {
        private readonly object _syncRoot = new();
        private readonly Queue<WorkRequest> _work = new();
        private readonly Queue<WorkRequest> _requestNotifications = new();
        private readonly SemaphoreSlim _requested = new(0);

        public async Task RunAsync(
            Func<CancellationToken, Task> work,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            var request = new WorkRequest(completion, cancellationToken);
            lock (_syncRoot)
            {
                _work.Enqueue(request);
                _requestNotifications.Enqueue(request);
            }
            _requested.Release();
            await completion.Task;
            await work(cancellationToken);
        }

        public async Task WaitForRequestAsync()
        {
            while (await _requested.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                WorkRequest request;
                lock (_syncRoot)
                {
                    request = _requestNotifications.Dequeue();
                }
                if (!request.Completion.Task.IsCompleted
                    && !request.CancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }

            throw new TimeoutException("No work request was queued within the timeout.");
        }

        public void ReleaseNext()
        {
            TaskCompletionSource? completion = null;
            lock (_syncRoot)
            {
                while (_work.Count > 0)
                {
                    var candidate = _work.Dequeue();
                    if (!candidate.Completion.Task.IsCompleted
                        && !candidate.CancellationToken.IsCancellationRequested)
                    {
                        completion = candidate.Completion;
                        break;
                    }
                }
            }

            Assert.NotNull(completion);
            completion.TrySetResult();
        }

        private sealed record WorkRequest(
            TaskCompletionSource Completion,
            CancellationToken CancellationToken);
    }

    private sealed class TestPackageViewStagingSurface
    {
        private readonly List<Control> _activeViews = [];

        public List<Control> StagedViews { get; } = [];

        public IReadOnlyList<Control> ActiveViews => _activeViews;

        public int MaximumStagedViewCount { get; private set; }

        public void Stage(Control view)
        {
            StagedViews.Add(view);
            _activeViews.Add(view);
            view.Measure(new Size(800, 600));
            view.Arrange(new Rect(new Size(800, 600)));
            MaximumStagedViewCount = Math.Max(MaximumStagedViewCount, _activeViews.Count);
            view.RaiseEvent(new RoutedEventArgs(Control.LoadedEvent));
        }

        public void DetachAll() => _activeViews.Clear();
    }
}

public sealed class ShellLifecycleTestPackageModule : ISunderAppPackageModule
{
    public const string ActivationGatePathFileName = "activation-gate-path";

    public const string NavigationGatePathFileName = "navigation-gate-path";

    public const string NavigationActivityPathFileName = "navigation-activity-path";

    public const string WaitForAttachmentMarkerFileName = "wait-for-attachment";

    public const string ThrowNavigationMarkerFileName = "throw-navigation";

    public const string SkipViewMarkerFileName = "skip-view";

    public const string ThrowAfterViewMarkerFileName = "throw-after-view";

    public const string RegisterReservedHostCapabilityMarkerFileName =
        "register-reserved-host-capability";

    public const string StageSideEffectsPathFileName = "stage-side-effects-path";

    private string? _packageFolder;

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        _packageFolder = context.ContentRootPath;
        if (HasMarker(RegisterReservedHostCapabilityMarkerFileName))
        {
            services.AddSingleton<IPackageContext>(context);
        }
    }

    public void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services
    )
    {
        if (!HasMarker(SkipViewMarkerFileName))
        {
            registry.RegisterPackageView<ShellLifecycleThreadAffinedPackageView>(
                new PackageViewRegistration("agent.chat", "Chat", iconAssetPath: "icon.png")
            );
            registry.RegisterPackageView<ShellLifecycleThreadAffinedPackageView>(
                new PackageViewRegistration(
                    "agent.workspaces",
                    "Workspaces",
                    defaultPlacement: PackageViewPlacement.RightTop
                )
            );
            registry.RegisterPackageView<ShellLifecycleThreadAffinedPackageView>(
                new PackageViewRegistration(
                    "agent.subsessions",
                    "Subsessions",
                    defaultPlacement: PackageViewPlacement.LeftTop,
                    showInHotbarByDefault: false
                )
            );
        }

        var sideEffectsPathDescriptor = Path.Combine(_packageFolder!, StageSideEffectsPathFileName);
        if (File.Exists(sideEffectsPathDescriptor))
        {
            var sideEffectsPath = File.ReadAllText(sideEffectsPathDescriptor);
            services
                .GetRequiredService<IPackageNotificationService>()
                .PublishAsync(new PackageNotificationRequest("Candidate", "Candidate notification"))
                .GetAwaiter()
                .GetResult();
            services
                .GetRequiredService<IBackgroundProcessQueue>()
                .Enqueue(
                    new BackgroundProcessRequest(
                        "Candidate work",
                        "candidate",
                        BackgroundProcessIndicator.Hidden,
                        BackgroundProcessConcurrencyMode.SequentialWithinGroup,
                        CanCancel: true,
                        _ =>
                        {
                            File.WriteAllText(sideEffectsPath, "executed");
                            return Task.CompletedTask;
                        }
                    )
                );
        }

        var activationGateDescriptor = Path.Combine(_packageFolder!, ActivationGatePathFileName);
        if (File.Exists(activationGateDescriptor))
        {
            var activationGatePath = File.ReadAllText(activationGateDescriptor);
            File.WriteAllText(activationGatePath + ".started", string.Empty);
            while (!File.Exists(activationGatePath + ".release"))
            {
                Thread.Sleep(10);
            }
        }

        if (HasMarker(ThrowAfterViewMarkerFileName))
        {
            throw new InvalidOperationException(
                "Test package requested activation failure after registering contributions."
            );
        }
    }

    private bool HasMarker(string fileName) =>
        !string.IsNullOrWhiteSpace(_packageFolder)
        && File.Exists(Path.Combine(_packageFolder, fileName));
}

public sealed class ShellLifecycleThreadAffinedPackageView
    : Control,
        IDisposable,
        IPackageViewNavigationTarget
{
    private readonly TaskCompletionSource _attachedOrLoaded = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public ShellLifecycleThreadAffinedPackageView()
    {
        AttachedToVisualTree += (_, _) => RecordAttachment("attached");
        Loaded += (_, _) => RecordAttachment("loaded");
    }

    public int OwnerThreadId { get; } = Environment.CurrentManagedThreadId;

    public bool IsDisposed { get; private set; }

    public int DisposeThreadId { get; private set; }

    public string? NavigatedViewId { get; private set; }

    public async ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default
    )
    {
        NavigatedViewId = context.ViewId;
        AppendNavigationActivity($"navigation-started:{context.ViewId}");
        var packageFolder = GetPackageFolder();
        if (
            HasMarker(
                packageFolder,
                ShellLifecycleTestPackageModule.WaitForAttachmentMarkerFileName
            )
        )
        {
            await _attachedOrLoaded.Task.WaitAsync(cancellationToken);
        }

        var gateDescriptor = packageFolder is null
            ? null
            : Path.Combine(
                packageFolder,
                ShellLifecycleTestPackageModule.NavigationGatePathFileName
            );
        if (gateDescriptor is not null && File.Exists(gateDescriptor))
        {
            var gatePath = File.ReadAllText(gateDescriptor);
            File.WriteAllText(gatePath + ".started", context.ViewId);
            while (!File.Exists(gatePath + ".release"))
            {
                await Task.Delay(10, cancellationToken);
            }
        }

        if (HasMarker(packageFolder, ShellLifecycleTestPackageModule.ThrowNavigationMarkerFileName))
        {
            throw new InvalidOperationException("Test package requested navigation failure.");
        }

        AppendNavigationActivity($"navigation-completed:{context.ViewId}");
    }

    public void Dispose()
    {
        IsDisposed = true;
        DisposeThreadId = Environment.CurrentManagedThreadId;
    }

    private void RecordAttachment(string eventName)
    {
        AppendNavigationActivity(eventName);
        _attachedOrLoaded.TrySetResult();
    }

    private void AppendNavigationActivity(string activity)
    {
        var packageFolder = GetPackageFolder();
        var activityDescriptor = packageFolder is null
            ? null
            : Path.Combine(
                packageFolder,
                ShellLifecycleTestPackageModule.NavigationActivityPathFileName
            );
        if (activityDescriptor is null || !File.Exists(activityDescriptor))
        {
            return;
        }

        File.AppendAllLines(File.ReadAllText(activityDescriptor), [activity]);
    }

    private string? GetPackageFolder() =>
        Directory.GetParent(Path.GetDirectoryName(GetType().Assembly.Location)!)?.FullName;

    private static bool HasMarker(string? packageFolder, string markerFileName) =>
        packageFolder is not null && File.Exists(Path.Combine(packageFolder, markerFileName));
}
