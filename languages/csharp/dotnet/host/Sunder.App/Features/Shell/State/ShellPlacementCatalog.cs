using Sunder.Runtime.Contracts;
using Sunder.App.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Features.Shell.State;

internal static class ShellPlacementCatalog
{
    public static IReadOnlyList<RailPlacement> All { get; } =
    [
        RailPlacement.LeftTop,
        RailPlacement.Middle,
        RailPlacement.RightTop,
        RailPlacement.LeftBottom,
        RailPlacement.RightBottom,
    ];

    public static string ToReadinessDisplay(PackageReadinessState readiness)
        => readiness switch
        {
            PackageReadinessState.Ready => "Ready",
            PackageReadinessState.NeedsConfiguration => "Needs configuration",
            PackageReadinessState.Degraded => "Degraded",
            PackageReadinessState.Failed => "Failed",
            _ => "Unknown",
        };

    public static string ToDisplayName(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => "Left Top",
            RailPlacement.Middle => "Middle",
            RailPlacement.RightTop => "Right Top",
            RailPlacement.LeftBottom => "Left Bottom",
            RailPlacement.RightBottom => "Right Bottom",
            _ => placement.ToString(),
        };

    public static PackageViewPlacement ToPackageViewPlacement(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => PackageViewPlacement.LeftTop,
            RailPlacement.Middle => PackageViewPlacement.Middle,
            RailPlacement.RightTop => PackageViewPlacement.RightTop,
            RailPlacement.LeftBottom => PackageViewPlacement.LeftBottom,
            RailPlacement.RightBottom => PackageViewPlacement.RightBottom,
            _ => PackageViewPlacement.Middle,
        };

    public static RailPlacement ToRailPlacement(PackageViewPlacement placement)
        => placement switch
        {
            PackageViewPlacement.LeftTop => RailPlacement.LeftTop,
            PackageViewPlacement.Middle => RailPlacement.Middle,
            PackageViewPlacement.RightTop => RailPlacement.RightTop,
            PackageViewPlacement.LeftBottom => RailPlacement.LeftBottom,
            PackageViewPlacement.RightBottom => RailPlacement.RightBottom,
            _ => RailPlacement.Middle,
        };
}
