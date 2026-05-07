using Sunder.Protocol;

namespace Sunder.App.Models;

public sealed record ShellPackageView(
    string ViewId,
    string PackageId,
    string PackageDisplayName,
    string PackageVersion,
    string Title,
    string Glyph,
    RailPlacement Placement,
    PackageReadinessState Readiness,
    bool ShowInHotbarByDefault);

public sealed record ShellSnapshot(
    IReadOnlyList<ShellPackageView> PackageViews,
    ShellState State,
    IReadOnlyList<string> StartupWarnings,
    IReadOnlyList<string> StartupErrors,
    string SystemStatusText,
    string SyncStatusText);
