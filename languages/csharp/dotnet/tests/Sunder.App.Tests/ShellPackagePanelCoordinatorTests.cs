using Avalonia.Controls;
using Avalonia.Layout;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Features.Shell.Layout;
using Sunder.App.Features.Shell.Panels;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views.Controls;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ShellPackagePanelCoordinatorTests
{
    [Fact]
    public void SelectItem_AlreadyActiveNonToggleMiddleItem_IsNoOp()
    {
        using var fixture = new CoordinatorFixture(RailPlacement.Middle);
        fixture.ResetObservations();
        var activeView = fixture.Panel.HostedView;

        fixture.Coordinator.SelectItem(fixture.Item, allowToggle: false);

        Assert.Same(activeView, fixture.Panel.HostedView);
        Assert.Equal(0, fixture.ApplyCount);
        Assert.Equal(0, fixture.NavigationNotificationCount);
        Assert.Equal(0, fixture.LayoutNotificationCount);
        Assert.Equal(0, fixture.PersistenceCount);
    }

    [Fact]
    public async Task OpenPackageViewPanelAsync_CachedReactivationIsVisibleBeforeNavigationCompletes()
    {
        using var fixture = new CoordinatorFixture(RailPlacement.RightTop);
        var cachedView = Assert.IsType<HostedPackageViewBoundary>(fixture.Panel.HostedView);
        Assert.True(fixture.Coordinator.ClosePackageViewPanel(CoordinatorFixture.ViewId));
        var releaseNavigation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        fixture.Navigation.NavigateAsync = async cancellationToken =>
        {
            fixture.Navigation.Started.TrySetResult();
            await releaseNavigation.Task.WaitAsync(cancellationToken);
        };

        var reopen = fixture
            .Coordinator.OpenPackageViewPanelAsync(CoordinatorFixture.ViewId)
            .AsTask();
        await fixture.Navigation.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(reopen.IsCompleted);
        Assert.True(fixture.Panel.HasHostedView);
        Assert.Equal(CoordinatorFixture.ViewId, fixture.Panel.ActiveViewId);
        Assert.Same(cachedView, fixture.Panel.HostedView);

        releaseNavigation.SetResult();
        Assert.True(await reopen);
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        public const string ViewId = "package.view";

        private readonly PackageViewHostService _packageViewHostService;

        public CoordinatorFixture(RailPlacement placement)
        {
            Navigation = new NavigationProbe();
            var registry = new AppPackageViewRegistry();
            var serviceProvider = new ServiceCollection()
                .AddSingleton(Navigation)
                .BuildServiceProvider();
            registry.RegisterPackageView<BlockingNavigationView>(
                "package",
                new PackageViewRegistration(ViewId, "Package View"),
                serviceProvider
            );
            _packageViewHostService = new PackageViewHostService(
                registry,
                [],
                [serviceProvider],
                [],
                sessionFolder: null,
                uiDispatcher: new ImmediateUiDispatcher()
            );
            var packageView = new ShellPackageView(
                ViewId,
                "package",
                "Package",
                "1.0.0",
                "Package View",
                "P",
                placement,
                PackageReadinessState.Ready,
                ShowInHotbarByDefault: true,
                PackageGlyph: "P"
            );
            var viewsById = new Dictionary<string, ShellPackageView>(
                StringComparer.OrdinalIgnoreCase
            )
            {
                [ViewId] = packageView,
            };
            var shellState = new ShellState { HasInitializedLayout = true };
            ShellSelectionState.SetSelectedViewId(shellState, placement, ViewId);
            Bar = new PackageIconBarViewModel(
                placement,
                placement == RailPlacement.Middle ? Orientation.Horizontal : Orientation.Vertical,
                (_, _, _) => { },
                _ => ValueTask.FromResult(false),
                _ => false
            );
            Item = new ShellItemViewModel(
                ViewId,
                "P",
                iconUri: null,
                "Package View",
                "Package",
                "Package View",
                placement,
                _ => { }
            );
            Bar.SetItems([Item]);
            Panel = new ShellPanelViewModel();
            var selectionPresenter = new ShellSelectionPresenter();
            selectionPresenter.Select(Bar, placement, Item);
            var panelContentPresenter = new ShellPanelContentPresenter(
                _packageViewHostService,
                [],
                []
            );

            void ApplyPanelContent(
                RailPlacement targetPlacement,
                string? viewId,
                bool createHostedView
            )
            {
                ApplyCount++;
                panelContentPresenter.Apply(
                    Panel,
                    targetPlacement,
                    viewId,
                    viewsById,
                    placement == RailPlacement.Middle ? 1 : 0,
                    createHostedView
                );
            }

            ApplyPanelContent(placement, ViewId, createHostedView: true);
            Coordinator = new ShellPackagePanelCoordinator(
                viewsById,
                shellState,
                _packageViewHostService,
                selectionPresenter,
                _ => Bar,
                _ => Panel,
                ApplyPanelContent,
                _ => true,
                (_, _, _) => ValueTask.FromResult(true),
                _ => NavigationNotificationCount++,
                _ => { },
                () => LayoutNotificationCount++,
                () => PersistenceCount++
            );
        }

        public ShellPackagePanelCoordinator Coordinator { get; }

        public NavigationProbe Navigation { get; }

        public PackageIconBarViewModel Bar { get; }

        public ShellItemViewModel Item { get; }

        public ShellPanelViewModel Panel { get; }

        public int ApplyCount { get; private set; }

        public int NavigationNotificationCount { get; private set; }

        public int LayoutNotificationCount { get; private set; }

        public int PersistenceCount { get; private set; }

        public void ResetObservations()
        {
            ApplyCount = 0;
            NavigationNotificationCount = 0;
            LayoutNotificationCount = 0;
            PersistenceCount = 0;
        }

        public void Dispose() =>
            _packageViewHostService.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class BlockingNavigationView(NavigationProbe navigation)
        : Control,
            IPackageViewNavigationTarget
    {
        public ValueTask OnNavigatedToAsync(
            PackageViewNavigationContext context,
            CancellationToken cancellationToken = default
        ) => navigation.NavigateAsync(cancellationToken);
    }

    private sealed class NavigationProbe
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<CancellationToken, ValueTask> NavigateAsync { get; set; } =
            static _ => ValueTask.CompletedTask;
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
}
