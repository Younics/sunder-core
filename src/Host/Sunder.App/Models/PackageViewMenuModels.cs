namespace Sunder.App.Models;

public sealed record PackageViewMenuItem(
    string ViewId,
    string Title,
    RailPlacement Placement);

public sealed record PackageViewMenuGroup(
    string PackageId,
    string PackageDisplayName,
    IReadOnlyList<PackageViewMenuItem> Views);
