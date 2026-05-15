using Sunder.App.Models;
using Sunder.Protocol;

namespace Sunder.App.Services;

public sealed class ShellCompositionService : IShellCompositionService
{
    public ShellSnapshot Compose(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        ShellState state,
        SystemStatusResponse? systemStatus,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors
    )
    {
        var packageViews = new List<ShellPackageView>();

        foreach (var activePackage in activePackages)
        {
            foreach (var view in activePackage.Views)
            {
                packageViews.Add(
                    new ShellPackageView(
                        view.ViewId,
                        view.PackageId,
                        activePackage.DisplayName,
                        activePackage.Version,
                        view.Title,
                        ResolveGlyph(view.Icon, view.Title),
                        ResolvePlacement(view, state),
                        activePackage.Readiness,
                        view.ShowInHotbarByDefault,
                        view.Icon,
                        ResolveGlyph(activePackage.Icon, activePackage.DisplayName),
                        activePackage.Icon
                    )
                );
            }
        }

        var orderedViews = packageViews
            .GroupBy(view => view.ViewId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(x => x.PackageDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        NormalizeState(state, orderedViews);
        NormalizeSelections(state, orderedViews);

        var systemStatusText = errors.Count > 0
            ? "Runtime loaded with errors"
            : systemStatus?.IsReady == true
                ? "Runtime Ready"
                : "Runtime Unavailable";

        var syncStatusText = activePackages.Count == 0
            ? "No packages loaded"
            : $"{activePackages.Count} package(s) active";

        return new ShellSnapshot(orderedViews, state, warnings, errors, systemStatusText, syncStatusText);
    }

    private static void NormalizeState(ShellState state, IReadOnlyList<ShellPackageView> packageViews)
    {
        if (packageViews.Count == 0)
        {
            return;
        }

        var activeViewIds = packageViews.Select(view => view.ViewId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleViewId in state.ViewPlacements.Keys.Where(viewId => !activeViewIds.Contains(viewId)).ToArray())
        {
            state.ViewPlacements.Remove(staleViewId);
        }

        foreach (var staleViewId in state.ViewOrder.Keys.Where(viewId => !activeViewIds.Contains(viewId)).ToArray())
        {
            state.ViewOrder.Remove(staleViewId);
        }

        foreach (var staleViewId in state.HiddenHotbarViewIds.Where(viewId => !activeViewIds.Contains(viewId)).ToArray())
        {
            state.HiddenHotbarViewIds.Remove(staleViewId);
        }

        foreach (var view in packageViews)
        {
            var isNewView = !state.ViewPlacements.ContainsKey(view.ViewId);
            if (isNewView && !view.ShowInHotbarByDefault)
            {
                state.HiddenHotbarViewIds.Add(view.ViewId);
            }

            state.ViewPlacements[view.ViewId] = view.Placement;
        }
    }

    private static void NormalizeSelections(ShellState state, IReadOnlyList<ShellPackageView> packageViews)
    {
        if (packageViews.Count == 0)
        {
            return;
        }

        var visibleViews = packageViews
            .Where(view => !state.HiddenHotbarViewIds.Contains(view.ViewId))
            .ToArray();
        var leftTopViews = visibleViews.Where(x => x.Placement == RailPlacement.LeftTop).ToArray();
        var middleViews = visibleViews.Where(x => x.Placement == RailPlacement.Middle).ToArray();
        var rightTopViews = visibleViews.Where(x => x.Placement == RailPlacement.RightTop).ToArray();
        var leftBottomViews = visibleViews.Where(x => x.Placement == RailPlacement.LeftBottom).ToArray();
        var rightBottomViews = visibleViews.Where(x => x.Placement == RailPlacement.RightBottom).ToArray();

        state.SelectedLeftTopViewId = leftTopViews.Any(x => x.ViewId == state.SelectedLeftTopViewId)
            ? state.SelectedLeftTopViewId
            : null;

        state.SelectedMiddleViewId = middleViews.Any(x => x.ViewId == state.SelectedMiddleViewId)
            ? state.SelectedMiddleViewId
            : state.HasInitializedLayout ? null : middleViews.FirstOrDefault()?.ViewId;

        state.SelectedRightTopViewId = rightTopViews.Any(x => x.ViewId == state.SelectedRightTopViewId)
            ? state.SelectedRightTopViewId
            : null;

        state.SelectedLeftBottomViewId = leftBottomViews.Any(x => x.ViewId == state.SelectedLeftBottomViewId)
            ? state.SelectedLeftBottomViewId
            : null;

        state.SelectedRightBottomViewId = rightBottomViews.Any(x => x.ViewId == state.SelectedRightBottomViewId)
            ? state.SelectedRightBottomViewId
            : null;
    }

    private static RailPlacement ResolvePlacement(PackageViewDescriptor view, ShellState state)
    {
        if (state.ViewPlacements.TryGetValue(view.ViewId, out var savedPlacement))
        {
            return savedPlacement;
        }

        return ParsePlacement(view.DefaultPlacement) ?? RailPlacement.Middle;
    }

    private static RailPlacement? ParsePlacement(string? placement)
    {
        if (string.IsNullOrWhiteSpace(placement))
        {
            return null;
        }

        var normalized = placement
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "lefttop" => RailPlacement.LeftTop,
            "middle" => RailPlacement.Middle,
            "righttop" => RailPlacement.RightTop,
            "leftbottom" => RailPlacement.LeftBottom,
            "rightbottom" => RailPlacement.RightBottom,
            _ => null,
        };
    }

    private static string ResolveGlyph(PackageIconDescriptor? icon, string? fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(icon?.Glyph))
        {
            return icon.Glyph!;
        }

        return string.IsNullOrWhiteSpace(fallbackName)
            ? "?"
            : fallbackName.Trim()[0].ToString().ToUpperInvariant();
    }
}
