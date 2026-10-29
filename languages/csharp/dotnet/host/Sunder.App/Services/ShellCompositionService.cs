using Sunder.App.Models;
using Sunder.App.Features.Shell.State;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class ShellCompositionService : IShellCompositionService
{
    public ShellSnapshot Compose(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        ShellState state,
        SystemStatusResponse? systemStatus,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        ShellNormalizationPolicy normalizationPolicy,
        IReadOnlySet<string>? unavailablePackageIds = null
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

        ShellStateNormalizer.Normalize(state, orderedViews, normalizationPolicy,
            activePackages.Select(package => package.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase),
            unavailablePackageIds);

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

    internal static IReadOnlySet<string> GetUnavailablePackageIds(
        IReadOnlyList<ActivePackageDescriptor> composedPackages,
        IReadOnlyList<ActivePackageDescriptor> runtimeActivePackages,
        IReadOnlyList<SessionPackageDescriptor>? sessionPackages = null)
    {
        var composedIds = composedPackages.Select(package => package.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Runtime does not enumerate App registrations. It only identifies enabled
        // App packages whose missing composition is a failure rather than removal.
        return runtimeActivePackages.Where(package => package.IsEnabled && (package.HostRoles & PackageHostRoles.App) != 0)
            .Select(package => package.PackageId)
            .Concat((sessionPackages ?? []).Where(package => package.IsEnabled && (package.HostRoles & PackageHostRoles.App) != 0)
                .Select(package => package.PackageId))
            .Where(id => !composedIds.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
