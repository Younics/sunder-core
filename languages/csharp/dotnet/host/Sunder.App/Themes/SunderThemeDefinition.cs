using Avalonia.Media;
using Avalonia.Styling;

namespace Sunder.App.Themes;

public sealed record SunderThemeDefinition
{
    public const string GraphiteDarkId = "sunder-graphite-dark";
    public const string GraphiteLightId = "sunder-graphite-light";

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public ThemeVariant Variant { get; init; } = ThemeVariant.Dark;

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
            SurfacePopover = Color.Parse("#222323"),
            SurfaceWorkspace = Color.Parse("#171818"),
            SurfaceHover = Color.Parse("#303232"),
            SurfaceSelected = Color.Parse("#34291B"),
            SurfaceDragOver = Color.Parse("#3A2D1C"),
            SurfaceCode = Color.Parse("#151616"),
            BorderSubtle = Color.Parse("#343535"),
            BorderStrong = Color.Parse("#474949"),
            BorderWarning = Color.Parse("#6F5735"),
            BorderDanger = Color.Parse("#FF6675"),
            BorderFocus = Color.Parse("#E7B765"),
            ForegroundPrimary = Color.Parse("#D8D5CE"),
            ForegroundSecondary = Color.Parse("#B9BCBE"),
            ForegroundMuted = Color.Parse("#AAA69F"),
            ForegroundOnAccent = Color.Parse("#1C1205"),
            ForegroundOnDanger = Colors.White,
            ForegroundCode = Color.Parse("#C9964D"),
            Accent = Color.Parse("#D09132"),
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

    public static SunderThemeDefinition GraphiteLight { get; } = GraphiteDark with
    {
        Id = GraphiteLightId,
        DisplayName = "Sunder Graphite Light",
        Variant = ThemeVariant.Light,
        BackgroundApp = Color.Parse("#EDEDEC"),
        SurfaceBase = Color.Parse("#F8F8F7"),
        SurfaceRaised = Color.Parse("#FFFFFF"),
        SurfacePopover = Color.Parse("#FFFFFF"),
        SurfaceWorkspace = Color.Parse("#F3F3F2"),
        SurfaceHover = Color.Parse("#E3E4E3"),
        SurfaceSelected = Color.Parse("#F2E5D0"),
        SurfaceDragOver = Color.Parse("#EFDEC0"),
        SurfaceCode = Color.Parse("#E8E9E8"),
        BorderSubtle = Color.Parse("#CFD1CF"),
        BorderStrong = Color.Parse("#A5A8A5"),
        BorderWarning = Color.Parse("#9B752F"),
        BorderDanger = Color.Parse("#B32E40"),
        BorderFocus = Color.Parse("#986013"),
        ForegroundPrimary = Color.Parse("#242625"),
        ForegroundSecondary = Color.Parse("#4B504D"),
        ForegroundMuted = Color.Parse("#60645F"),
        ForegroundOnAccent = Colors.White,
        ForegroundCode = Color.Parse("#805019"),
        Accent = Color.Parse("#986013"),
        AccentSoft = Color.Parse("#F2E5D0"),
        Success = Color.Parse("#176C50"),
        SuccessSoft = Color.Parse("#DCEFE5"),
        Warning = Color.Parse("#805E1F"),
        WarningSoft = Color.Parse("#F4E9CE"),
        Danger = Color.Parse("#B32E40"),
        DangerSoft = Color.Parse("#F8E0E4"),
        Info = Color.Parse("#805019"),
        InfoSoft = Color.Parse("#F2E5D0"),
        Focus = Color.Parse("#986013"),
        Selection = Color.Parse("#E8D3AF"),
        Disabled = Color.Parse("#91968F"),
    };
}
