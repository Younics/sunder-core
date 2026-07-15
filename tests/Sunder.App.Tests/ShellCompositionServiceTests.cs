using Sunder.App.Models;
using Sunder.App.Features.Shell.State;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ShellCompositionServiceTests
{
    [Fact]
    public void Compose_RemovesStaleStateAndRestoresSavedPlacement()
    {
        var state = new ShellState
        {
            ViewPlacements = new Dictionary<string, RailPlacement>
            {
                ["stale"] = RailPlacement.LeftTop,
                ["agent.chat"] = RailPlacement.RightTop,
            },
            ViewOrder = new Dictionary<string, int>
            {
                ["stale"] = 0,
            },
            HiddenHotbarViewIds = ["stale"],
            SelectedMiddleViewId = "stale",
        };
        var service = new ShellCompositionService();

        var snapshot = service.Compose(
            [CreatePackage("agent", "Agent", [CreateView("agent.chat", "Chat", "middle"), CreateView("agent.home", "Home", "middle")])],
            state,
            new SystemStatusResponse("Runtime", "1.0.0", true, DateTimeOffset.UtcNow),
            warnings: [],
            errors: [],
            ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);

        Assert.False(snapshot.State.ViewPlacements.ContainsKey("stale"));
        Assert.False(snapshot.State.ViewOrder.ContainsKey("stale"));
        Assert.DoesNotContain("stale", snapshot.State.HiddenHotbarViewIds);
        Assert.Equal(RailPlacement.RightTop, snapshot.PackageViews.Single(view => view.ViewId == "agent.chat").Placement);
        Assert.Equal("agent.home", snapshot.State.SelectedMiddleViewId);
    }

    [Fact]
    public void Compose_UsesViewTitleInitialWhenGlyphIsMissing()
    {
        var service = new ShellCompositionService();

        var snapshot = service.Compose(
            [CreatePackage("tools", "Tools", [CreateView("tools.files", "Files", "left-top")])],
            new ShellState(),
            systemStatus: null,
            warnings: [],
            errors: [],
            ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);

        var view = Assert.Single(snapshot.PackageViews);
        Assert.Equal("F", view.Glyph);
        Assert.Equal(RailPlacement.LeftTop, view.Placement);
    }

    [Fact]
    public void Compose_UsesPackageIconGlyphForPackageMenuMetadata()
    {
        var service = new ShellCompositionService();
        var packageIcon = new PackageIconDescriptor("P", AssetPath: "Assets/icon.png");

        var snapshot = service.Compose(
            [new ActivePackageDescriptor(
                "agent",
                "Agent",
                "1.0.0",
                PackageHostRoles.App | PackageHostRoles.Runtime,
                packageIcon,
                IsEnabled: true,
                PackageReadinessState.Ready,
                [CreateView("agent.chat", "Chat", "middle")])],
            new ShellState(),
            systemStatus: null,
            warnings: [],
            errors: [],
            ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);

        var view = Assert.Single(snapshot.PackageViews);
        Assert.Equal("P", view.PackageGlyph);
        Assert.Same(packageIcon, view.PackageIcon);
    }

    [Fact]
    public void Compose_PreservesViewIconAssetPathForHotbarRendering()
    {
        var service = new ShellCompositionService();
        var viewIcon = new PackageIconDescriptor("C", AssetPath: "Assets/chat-icon.png");

        var snapshot = service.Compose(
            [CreatePackage("agent", "Agent", [CreateView("agent.chat", "Chat", "middle", icon: viewIcon)])],
            new ShellState(),
            systemStatus: null,
            warnings: [],
            errors: [],
            ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);

        var view = Assert.Single(snapshot.PackageViews);
        Assert.Equal("C", view.Glyph);
        Assert.Same(viewIcon, view.Icon);
    }

    [Fact]
    public void Compose_HidesNewViewsThatAreNotShownInHotbarByDefault()
    {
        var service = new ShellCompositionService();

        var snapshot = service.Compose(
            [CreatePackage("agent", "Agent", [CreateView("agent.chat", "Chat", "middle"), CreateView("agent.subsessions", "Subsessions", "right-top", showInHotbarByDefault: false)])],
            new ShellState(),
            systemStatus: null,
            warnings: [],
            errors: [],
            ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);

        Assert.Contains("agent.subsessions", snapshot.State.HiddenHotbarViewIds);
        Assert.Equal("agent.chat", snapshot.State.SelectedMiddleViewId);
        Assert.Null(snapshot.State.SelectedRightTopViewId);
        Assert.Contains(snapshot.PackageViews, view => view.ViewId == "agent.subsessions" && !view.ShowInHotbarByDefault);
    }

    [Fact]
    public void Compose_DeduplicatesPackageViewsByViewId()
    {
        var service = new ShellCompositionService();

        var snapshot = service.Compose(
            [CreatePackage("agent", "Agent", [CreateView("agent.chat", "Chat", "middle"), CreateView("agent.chat", "Duplicate Chat", "right-top")])],
            new ShellState(),
            systemStatus: null,
            warnings: [],
            errors: [],
            ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);

        var view = Assert.Single(snapshot.PackageViews);
        Assert.Equal("agent.chat", view.ViewId);
        Assert.Equal("Chat", view.Title);
        Assert.Equal(RailPlacement.Middle, view.Placement);
        Assert.Single(snapshot.State.ViewPlacements);
    }

    [Fact]
    public void Compose_EmptySnapshot_AppliesExplicitNormalizationPolicy()
    {
        var service = new ShellCompositionService();
        var safeModeState = CreateSavedState();
        var runtimeState = CreateSavedState();

        service.Compose([], safeModeState, null, [], [], ShellNormalizationPolicy.SafeModePreserveLayout);
        service.Compose([], runtimeState, null, [], [], ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);

        Assert.Equal("agent.chat", safeModeState.SelectedMiddleViewId);
        Assert.True(safeModeState.ViewPlacements.ContainsKey("agent.chat"));
        Assert.Null(runtimeState.SelectedMiddleViewId);
        Assert.Empty(runtimeState.ViewPlacements);
        Assert.Empty(runtimeState.ViewOrder);
        Assert.Empty(runtimeState.HiddenHotbarViewIds);
    }

    private static ShellState CreateSavedState()
        => new()
        {
            HasInitializedLayout = true,
            ViewPlacements = new Dictionary<string, RailPlacement> { ["agent.chat"] = RailPlacement.Middle },
            ViewOrder = new Dictionary<string, int> { ["agent.chat"] = 0 },
            HiddenHotbarViewIds = ["agent.hidden"],
            SelectedMiddleViewId = "agent.chat",
        };

    private static ActivePackageDescriptor CreatePackage(
        string packageId,
        string displayName,
        IReadOnlyList<PackageViewDescriptor> views)
        => new(packageId, displayName, "1.0.0", PackageHostRoles.App | PackageHostRoles.Runtime, Icon: null, IsEnabled: true, PackageReadinessState.Ready, views);

    private static PackageViewDescriptor CreateView(
        string viewId,
        string title,
        string defaultPlacement,
        bool showInHotbarByDefault = true,
        PackageIconDescriptor? icon = null)
        => new(viewId, "package", title, icon, defaultPlacement, showInHotbarByDefault);
}
