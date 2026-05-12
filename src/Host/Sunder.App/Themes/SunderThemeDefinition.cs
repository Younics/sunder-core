using Avalonia.Media;

namespace Sunder.App.Themes;

public sealed record SunderThemeDefinition
{
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

    public static SunderThemeDefinition DefaultDark { get; } =
        new()
        {
            Id = "sunder-default-dark",
            DisplayName = "Sunder Default Dark",
            BackgroundApp = Color.Parse("#15171A"),
            SurfaceBase = Color.Parse("#1D2025"),
            SurfaceRaised = Color.Parse("#21252A"),
            SurfacePopover = Color.Parse("#262A30"),
            SurfaceWorkspace = Color.Parse("#1A1D21"),
            SurfaceHover = Color.Parse("#2C3137"),
            SurfaceSelected = Color.Parse("#3B3022"),
            SurfaceDragOver = Color.Parse("#433625"),
            SurfaceCode = Color.Parse("#101214"),
            BorderSubtle = Color.Parse("#353A41"),
            BorderStrong = Color.Parse("#474D55"),
            BorderWarning = Color.Parse("#A9762F"),
            BorderDanger = Color.Parse("#FF6675"),
            BorderFocus = Color.Parse("#E7B765"),
            ForegroundPrimary = Color.Parse("#DEDAD3"),
            ForegroundSecondary = Color.Parse("#C2C5C8"),
            ForegroundMuted = Color.Parse("#CCC7BE"),
            ForegroundOnAccent = Color.Parse("#1C1205"),
            ForegroundOnDanger = Colors.White,
            ForegroundCode = Color.Parse("#E7B765"),
            Accent = Color.Parse("#D99A3A"),
            AccentSoft = Color.Parse("#30271C"),
            Success = Color.Parse("#6EE7B7"),
            SuccessSoft = Color.Parse("#10312D"),
            Warning = Color.Parse("#D99A3A"),
            WarningSoft = Color.Parse("#3B3022"),
            Danger = Color.Parse("#FF6675"),
            DangerSoft = Color.Parse("#3A171F"),
            Info = Color.Parse("#E7B765"),
            InfoSoft = Color.Parse("#30271C"),
            Focus = Color.Parse("#E7B765"),
            Selection = Color.Parse("#493824"),
            Disabled = Color.Parse("#595D64"),
        };

    public static SunderThemeDefinition GraphiteDark { get; } =
        new()
        {
            Id = "sunder-graphite-dark",
            DisplayName = "Sunder Graphite Dark",
            BackgroundApp = Color.Parse("#15171A"),
            SurfaceBase = Color.Parse("#1D2025"),
            SurfaceRaised = Color.Parse("#21252A"),
            SurfacePopover = Color.Parse("#262A30"),
            SurfaceWorkspace = Color.Parse("#1A1D21"),
            SurfaceHover = Color.Parse("#2C3137"),
            SurfaceSelected = Color.Parse("#3B3022"),
            SurfaceDragOver = Color.Parse("#433625"),
            SurfaceCode = Color.Parse("#101214"),
            BorderSubtle = Color.Parse("#353A41"),
            BorderStrong = Color.Parse("#474D55"),
            BorderWarning = Color.Parse("#A9762F"),
            BorderDanger = Color.Parse("#FF6675"),
            BorderFocus = Color.Parse("#E7B765"),
            ForegroundPrimary = Color.Parse("#DEDAD3"),
            ForegroundSecondary = Color.Parse("#C2C5C8"),
            ForegroundMuted = Color.Parse("#CCC7BE"),
            ForegroundOnAccent = Color.Parse("#1C1205"),
            ForegroundOnDanger = Colors.White,
            ForegroundCode = Color.Parse("#E7B765"),
            Accent = Color.Parse("#D99A3A"),
            AccentSoft = Color.Parse("#30271C"),
            Success = Color.Parse("#6EE7B7"),
            SuccessSoft = Color.Parse("#10312D"),
            Warning = Color.Parse("#D99A3A"),
            WarningSoft = Color.Parse("#3B3022"),
            Danger = Color.Parse("#FF6675"),
            DangerSoft = Color.Parse("#3A171F"),
            Info = Color.Parse("#E7B765"),
            InfoSoft = Color.Parse("#30271C"),
            Focus = Color.Parse("#E7B765"),
            Selection = Color.Parse("#493824"),
            Disabled = Color.Parse("#595D64"),
        };
}
