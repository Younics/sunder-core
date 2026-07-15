using Avalonia.Media;

namespace Sunder.App.Features.Shell.Menus;

public sealed record ShellMenuItem(
    string Id,
    string Title,
    string? Glyph,
    IImage? IconImage,
    bool IsEnabled,
    IReadOnlyList<ShellMenuItem> Children,
    Func<CancellationToken, Task>? ExecuteAsync = null);
