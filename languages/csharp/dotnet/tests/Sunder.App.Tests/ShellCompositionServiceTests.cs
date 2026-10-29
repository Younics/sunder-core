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

    [Fact]
    public void CompletedCatalogRemovesOwnerlessLegacyEntriesAndUsesSavedMiddleOrder()
    {
        var state = CreateSavedState();
        state.SelectedMiddleViewId = "local.test.default";
        state.ViewPlacements["local.test.default"] = RailPlacement.Middle;
        state.ViewOrder["local.test.default"] = 3;
        state.ViewOrder["tools.z"] = 0;
        state.ViewOrder["tools.a"] = 1;
        state.SelectedRightTopViewId = "selection-only";
        state.ViewOrder["order-only"] = 4;
        state.ViewPackageIds["owner-only"] = "removed";
        state.HiddenHotbarViewIds.Add("tools.hidden");
        state.ViewPlacements["tools.hidden"] = RailPlacement.Middle;
        state.ViewOrder["tools.hidden"] = -1;
        new ShellCompositionService().Compose([CreatePackage("tools", "Tools",
            [CreateView("tools.a", "A", "middle"), CreateView("tools.z", "Z", "middle"),
                CreateView("tools.hidden", "Hidden", "middle"), CreateView("tools.side", "Side", "right-top")])],
            state, null, [], [], ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);
        Assert.Equal("tools.z", state.SelectedMiddleViewId);
        Assert.Null(state.SelectedRightTopViewId);
        Assert.False(state.ViewPlacements.ContainsKey("local.test.default"));
        Assert.False(state.ViewOrder.ContainsKey("local.test.default"));
        Assert.False(state.ViewPlacements.ContainsKey("agent.chat"));
        Assert.False(state.ViewOrder.ContainsKey("order-only"));
        Assert.False(state.ViewPackageIds.ContainsKey("owner-only"));
        Assert.DoesNotContain("agent.hidden", state.HiddenHotbarViewIds);
        Assert.Contains("tools.hidden", state.HiddenHotbarViewIds);
    }

    [Fact]
    public void ActivationFailurePreservesUncertainViewsButNotConfirmedRemovals()
    {
        var state = CreateSavedState();
        state.ViewPackageIds["agent.chat"] = "agent";
        state.ViewOrder["agent.chat"] = 7;
        state.SelectedRightTopViewId = "legacy-opaque-view";
        state.ViewPlacements["legacy-opaque-view"] = RailPlacement.RightTop;
        state.SelectedLeftBottomViewId = "removed.view";
        state.ViewPackageIds["removed.view"] = "removed";
        var tools = CreatePackage("tools", "Tools", [CreateView("tools.home", "Home", "middle")]);
        var failed = CreatePackage("agent", "Agent", []);
        var unavailable = ShellCompositionService.GetUnavailablePackageIds([tools], [tools, failed]);
        var service = new ShellCompositionService();
        service.Compose([tools], state, null, [], [], ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot, unavailable);
        Assert.Equal("agent.chat", state.SelectedMiddleViewId);
        Assert.Equal("legacy-opaque-view", state.SelectedRightTopViewId);
        Assert.Equal(7, state.ViewOrder["agent.chat"]);
        Assert.Null(state.SelectedLeftBottomViewId);
        Assert.False(state.ViewPackageIds.ContainsKey("removed.view"));

        // A later completed catalog resolves the legacy record without inferring
        // an owner from its id; the failed package itself is now confirmed absent.
        service.Compose([tools], state, null, [], [], ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);
        Assert.Equal("tools.home", state.SelectedMiddleViewId);
        Assert.Null(state.SelectedRightTopViewId);
        Assert.False(state.ViewPlacements.ContainsKey("legacy-opaque-view"));
    }

    [Fact]
    public void UnavailableCatalogIdentifiesFailedAppPackagesNotDisabledOrRuntimeOnlyPackages()
    {
        var composed = CreatePackage("composed", "Composed", []);
        var missingSource = CreatePackage("missing-source", "Missing", []);
        var disabled = CreatePackage("disabled", "Disabled", []) with { IsEnabled = false };
        var runtimeOnly = CreatePackage("runtime", "Runtime", []) with { HostRoles = PackageHostRoles.Runtime };
        var failed = new SessionPackageDescriptor("failed", "Failed", "1.0.0", PackageHostRoles.App, null,
            true, PackageReadinessState.Failed, [], null, "activation failed", null, 1);
        var unavailable = ShellCompositionService.GetUnavailablePackageIds([composed, runtimeOnly],
            [composed, missingSource, disabled, runtimeOnly], [failed]);
        Assert.Equal(["failed", "missing-source"], unavailable.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedMiddleSelectionWithoutEligibleReplacementUsesTheDefaultMiddle(bool onlyQuarantinedMiddle)
    {
        var state = CreateSavedState();
        state.ViewPackageIds["agent.chat"] = "removed";
        var snapshot = new ShellCompositionService().Compose(
            [CreatePackage("tools", "Tools", [CreateView("tools.view", "View", onlyQuarantinedMiddle ? "middle" : "right-top")])],
            state, null, [], [], ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot,
            onlyQuarantinedMiddle ? new HashSet<string>(["tools"], StringComparer.OrdinalIgnoreCase) : null);
        Assert.Null(snapshot.State.SelectedMiddleViewId);
        Assert.Null(snapshot.State.SelectedRightTopViewId);
        Assert.False(state.ViewPlacements.ContainsKey("agent.chat"));
    }

    [Fact]
    public void ExplicitPackageRemovalAlsoPrunesSavedViewsAbsentFromTheLiveViewDictionary()
    {
        var state = CreateSavedState();
        state.ViewPackageIds["agent.chat"] = "agent";
        var views = new ShellCompositionService().Compose(
            [CreatePackage("tools", "Tools", [CreateView("tools.home", "Home", "middle")])], state,
            null, [], [], ShellNormalizationPolicy.SafeModePreserveLayout).PackageViews.ToDictionary(view => view.ViewId);
        Assert.True(ShellPackageRemovalPruner.RemovePackageViews("agent", views, state));
        Assert.Equal("tools.home", state.SelectedMiddleViewId);
        Assert.False(state.ViewPlacements.ContainsKey("agent.chat"));
        Assert.False(state.ViewOrder.ContainsKey("agent.chat"));
        Assert.False(state.ViewPackageIds.ContainsKey("agent.chat"));
        Assert.False(ShellPackageRemovalPruner.RemovePackageViews("agent", views, state));
    }

    [Fact]
    public void InitializedEmptyMiddleDoesNotSelectFirstAvailableView()
    {
        var state = new ShellState { HasInitializedLayout = true };
        new ShellCompositionService().Compose([CreatePackage("agent", "Agent", [CreateView("agent.chat", "Chat", "middle")])],
            state, null, [], [], ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);
        Assert.Null(state.SelectedMiddleViewId);
    }

    [Fact]
    public void SuccessfullyLoadedPackageWithNoViewsRemovesItsOldRegistrations()
    {
        var state = CreateSavedState();
        state.ViewPackageIds["agent.chat"] = "agent";
        new ShellCompositionService().Compose([CreatePackage("agent", "Agent", [])], state,
            null, [], [], ShellNormalizationPolicy.SafeModePreserveLayout);
        Assert.Null(state.SelectedMiddleViewId);
        Assert.False(state.ViewPlacements.ContainsKey("agent.chat"));
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
        => new(packageId, displayName, "1.0.0", PackageHostRoles.App | PackageHostRoles.Runtime, Icon: null, IsEnabled: true,
            PackageReadinessState.Ready, views.Select(view => view with { PackageId = packageId }).ToArray());

    private static PackageViewDescriptor CreateView(
        string viewId,
        string title,
        string defaultPlacement,
        bool showInHotbarByDefault = true,
        PackageIconDescriptor? icon = null)
        => new(viewId, "package", title, icon, defaultPlacement, showInHotbarByDefault);
}
