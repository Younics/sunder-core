using Sunder.App.Models;

namespace Sunder.App.Features.Shell.Menus;

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
