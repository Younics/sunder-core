using Avalonia.Controls;
using Avalonia.Layout;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Features.Shell.Layout;
using Sunder.App.Features.Shell.Panels;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ShellPackageViewPreloaderTests
{
    [Fact]
    public async Task PreloadAsync_WarmsVisibleThenOverflowThenHiddenAndRetainsEveryView()
    {
        var probe = new PreloadProbe();
        var services = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        Register<MiddleView>(registry, services, "view.middle");
        Register<VisibleRightView>(registry, services, "view.right.visible");
        Register<OverflowRightView>(registry, services, "view.right.overflow");
        Register<HiddenLeftView>(registry, services, "view.left.hidden");
        registry.RegisterSettingsView<SettingsView>("package", services);
        await using var host = new PackageViewHostService(
            registry,
            [],
            [services],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: new ImmediateUiDispatcher());
        var state = new ShellState
        {
            HiddenHotbarViewIds = ["view.left.hidden"],
        };
        var views = new Dictionary<string, ShellPackageView>(StringComparer.OrdinalIgnoreCase)
        {
            ["view.middle"] = View("view.middle", RailPlacement.Middle),
            ["view.right.visible"] = View("view.right.visible", RailPlacement.RightTop),
            ["view.right.overflow"] = View("view.right.overflow", RailPlacement.RightTop),
            ["view.left.hidden"] = View("view.left.hidden", RailPlacement.LeftTop),
        };
        var layout = CreateLayout();
        layout.MiddleBar.SetItems([Item(views["view.middle"])]);
        layout.RightTopBar.SetItems(
            [Item(views["view.right.visible"]), Item(views["view.right.overflow"])]);
        layout.RightTopBar.UpdateVisibleCapacity(2);
        var frameCount = 0;
        var preloader = new ShellPackageViewPreloader(
            views,
            state,
            host,
            layout.GetSlots,
            new ImmediateUiDispatcher());

        preloader.RetainHotbarViews();

        Assert.Equal(
            ["view.middle", "view.right.visible", "view.right.overflow"],
            probe.ConstructedViewIds);
        Assert.Empty(probe.WarmedViewIds);
        Assert.NotNull(layout.MiddlePanel.GetRetainedView("view.middle"));
        Assert.NotNull(layout.RightTopPanel.GetRetainedView("view.right.visible"));
        Assert.NotNull(layout.RightTopPanel.GetRetainedView("view.right.overflow"));
        Assert.Null(layout.LeftTopPanel.GetRetainedView("view.left.hidden"));

        await preloader.PreloadAsync(
            async (work, cancellationToken) =>
            {
                frameCount++;
                await work(cancellationToken);
            },
            CancellationToken.None);

        Assert.Equal(
            ["view.middle", "view.right.visible", "view.right.overflow", "view.left.hidden"],
            probe.WarmedViewIds);
        Assert.DoesNotContain("settings", probe.ConstructedViewIds);
        Assert.Equal(4, frameCount);
        Assert.NotNull(layout.MiddlePanel.GetRetainedView("view.middle"));
        Assert.NotNull(layout.RightTopPanel.GetRetainedView("view.right.visible"));
        Assert.NotNull(layout.RightTopPanel.GetRetainedView("view.right.overflow"));
        Assert.NotNull(layout.LeftTopPanel.GetRetainedView("view.left.hidden"));
        Assert.All(layout.GetSlots().SelectMany(slot => slot.Panel.HostedViews), view => Assert.False(view.IsActive));
    }

    [Fact]
    public async Task CanceledPreloadBeforeOpportunity_DoesNotConstructOrRetainAndCanResume()
    {
        var probe = new PreloadProbe();
        var services = new ServiceCollection().AddSingleton(probe).BuildServiceProvider();
        var registry = new AppPackageViewRegistry();
        Register<VisibleRightView>(registry, services, "view.right.visible");
        await using var host = new PackageViewHostService(
            registry,
            [],
            [services],
            [],
            faultReporter: null,
            sessionFolder: null,
            uiDispatcher: new ImmediateUiDispatcher());
        var views = new Dictionary<string, ShellPackageView>(StringComparer.OrdinalIgnoreCase)
        {
            ["view.right.visible"] = View("view.right.visible", RailPlacement.RightTop),
        };
        var layout = CreateLayout();
        layout.RightTopBar.SetItems([Item(views["view.right.visible"])]);
        var preloader = new ShellPackageViewPreloader(
            views,
            new ShellState(),
            host,
            layout.GetSlots,
            new ImmediateUiDispatcher());
        var opportunityStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var canceledPass = preloader.PreloadAsync(
            async (_, cancellationToken) =>
            {
                opportunityStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            cancellation.Token);
        await opportunityStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledPass);
        Assert.Empty(probe.ConstructedViewIds);
        Assert.Null(layout.RightTopPanel.GetRetainedView("view.right.visible"));

        await preloader.PreloadAsync(
            static (work, cancellationToken) => work(cancellationToken),
            CancellationToken.None);
        Assert.Equal(["view.right.visible"], probe.WarmedViewIds);
        Assert.NotNull(layout.RightTopPanel.GetRetainedView("view.right.visible"));
    }

    private static void Register<TView>(
        AppPackageViewRegistry registry,
        IServiceProvider services,
        string viewId)
        where TView : Control
        => registry.RegisterPackageView<TView>(
            "package",
            new PackageViewRegistration(viewId, viewId),
            services);

    private static ShellPackageView View(string viewId, RailPlacement placement)
        => new(
            viewId,
            "package",
            "Package",
            "1.0.0",
            viewId,
            "P",
            placement,
            PackageReadinessState.Ready,
            ShowInHotbarByDefault: true);

    private static ShellItemViewModel Item(ShellPackageView view)
        => new(
            view.ViewId,
            view.Glyph,
            iconUri: null,
            view.Title,
            view.PackageDisplayName,
            view.Title,
            view.Placement,
            _ => { });

    private static ShellLayoutPresenter CreateLayout()
        => new(
            (_, _, _) => { },
            _ => ValueTask.FromResult(false),
            _ => false,
            _ => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { });

    private sealed class PreloadProbe
    {
        public List<string> ConstructedViewIds { get; } = [];

        public List<string> WarmedViewIds { get; } = [];
    }

    private abstract class WarmupView : Control, IPackageViewWarmupTarget
    {
        private readonly PreloadProbe _probe;
        private readonly string _viewId;

        protected WarmupView(PreloadProbe probe, string viewId)
        {
            _probe = probe;
            _viewId = viewId;
            probe.ConstructedViewIds.Add(viewId);
        }

        public ValueTask WarmupAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _probe.WarmedViewIds.Add(_viewId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MiddleView(PreloadProbe probe) : WarmupView(probe, "view.middle");

    private sealed class VisibleRightView(PreloadProbe probe)
        : WarmupView(probe, "view.right.visible");

    private sealed class OverflowRightView(PreloadProbe probe)
        : WarmupView(probe, "view.right.overflow");

    private sealed class HiddenLeftView(PreloadProbe probe)
        : WarmupView(probe, "view.left.hidden");

    private sealed class SettingsView(PreloadProbe probe)
        : WarmupView(probe, "settings");

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
