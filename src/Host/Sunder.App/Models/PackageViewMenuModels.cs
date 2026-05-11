namespace Sunder.App.Models;

public sealed record PackageViewMenuItem(
    string ViewId,
    string Title,
    string Glyph,
    Uri? IconUri,
    RailPlacement Placement,
    bool IsInHotbar);

public sealed record PackageViewMenuGroup(
    string PackageId,
    string PackageDisplayName,
    string PackageGlyph,
    Uri? PackageIconUri,
    IReadOnlyList<PackageViewMenuItem> Views);
