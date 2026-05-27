using Avalonia.Media;

namespace Sunder.App.Themes;

public sealed record SunderThemeDefinition
{
    public const string GraphiteDarkId = "sunder-graphite-dark";

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required Color BackgroundApp { get; init; }

    public required Color SurfaceBase { get; init; }

    public required Color SurfaceRaised { get; init; }

    public required Color SurfacePopover { get; init; }

    public required Color SurfaceWorkspace { get; init; }

    public required Color SurfaceHover { get; init; }

    public required Color SurfaceSelected { get; init; }

    public required Color SurfaceDragOver { get; init; }

    public required Color SurfaceCode { get; init; }

    public required Color BorderSubtle { get; init; }

    public required Color BorderStrong { get; init; }

    public required Color BorderWarning { get; init; }

    public required Color BorderDanger { get; init; }

    public required Color BorderFocus { get; init; }

    public required Color ForegroundPrimary { get; init; }

    public required Color ForegroundSecondary { get; init; }

    public required Color ForegroundMuted { get; init; }

    public required Color ForegroundOnAccent { get; init; }

    public required Color ForegroundOnDanger { get; init; }

    public required Color ForegroundCode { get; init; }

    public required Color Accent { get; init; }

    public required Color AccentSoft { get; init; }

    public required Color Success { get; init; }

    public required Color SuccessSoft { get; init; }

    public required Color Warning { get; init; }

    public required Color WarningSoft { get; init; }

    public required Color Danger { get; init; }

    public required Color DangerSoft { get; init; }

    public required Color Info { get; init; }

    public required Color InfoSoft { get; init; }

    public required Color Focus { get; init; }

    public required Color Selection { get; init; }

    public required Color Disabled { get; init; }

    public static SunderThemeDefinition GraphiteDark { get; } =
        new()
        {
            Id = GraphiteDarkId,
            DisplayName = "Sunder Graphite Dark",
            BackgroundApp = Color.Parse("#121313"),
            SurfaceBase = Color.Parse("#1A1B1B"),
            SurfaceRaised = Color.Parse("#1E1F1F"),
            SurfacePopover = Color.Parse("#242525"),
            SurfaceWorkspace = Color.Parse("#171818"),
            SurfaceHover = Color.Parse("#303232"),
            SurfaceSelected = Color.Parse("#34291B"),
            SurfaceDragOver = Color.Parse("#3A2D1C"),
            SurfaceCode = Color.Parse("#101214"),
            BorderSubtle = Color.Parse("#383A3A"),
            BorderStrong = Color.Parse("#535555"),
            BorderWarning = Color.Parse("#6F5735"),
            BorderDanger = Color.Parse("#FF6675"),
            BorderFocus = Color.Parse("#E7B765"),
            ForegroundPrimary = Color.Parse("#DEDAD3"),
            ForegroundSecondary = Color.Parse("#C2C5C8"),
            ForegroundMuted = Color.Parse("#B7B2AA"),
            ForegroundOnAccent = Color.Parse("#1C1205"),
            ForegroundOnDanger = Colors.White,
            ForegroundCode = Color.Parse("#E7B765"),
            Accent = Color.Parse("#D99A3A"),
            AccentSoft = Color.Parse("#2B2319"),
            Success = Color.Parse("#6EE7B7"),
            SuccessSoft = Color.Parse("#10312D"),
            Warning = Color.Parse("#B8925E"),
            WarningSoft = Color.Parse("#2B251C"),
            Danger = Color.Parse("#FF6675"),
            DangerSoft = Color.Parse("#3A171F"),
            Info = Color.Parse("#E7B765"),
            InfoSoft = Color.Parse("#2B2319"),
            Focus = Color.Parse("#E7B765"),
            Selection = Color.Parse("#3F301F"),
            Disabled = Color.Parse("#595D64"),
        };
}
