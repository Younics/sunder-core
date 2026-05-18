using Sunder.Protocol;
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

    public static PackageHotbarPlacement ToPackageHotbarPlacement(RailPlacement placement)
        => placement switch
        {
            RailPlacement.LeftTop => PackageHotbarPlacement.LeftTop,
            RailPlacement.Middle => PackageHotbarPlacement.Middle,
            RailPlacement.RightTop => PackageHotbarPlacement.RightTop,
            RailPlacement.LeftBottom => PackageHotbarPlacement.LeftBottom,
            RailPlacement.RightBottom => PackageHotbarPlacement.RightBottom,
            _ => PackageHotbarPlacement.Middle,
        };

    public static RailPlacement ToRailPlacement(PackageHotbarPlacement placement)
        => placement switch
        {
            PackageHotbarPlacement.LeftTop => RailPlacement.LeftTop,
            PackageHotbarPlacement.Middle => RailPlacement.Middle,
            PackageHotbarPlacement.RightTop => RailPlacement.RightTop,
            PackageHotbarPlacement.LeftBottom => RailPlacement.LeftBottom,
            PackageHotbarPlacement.RightBottom => RailPlacement.RightBottom,
            _ => RailPlacement.Middle,
        };
}
