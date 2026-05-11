using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Theming;

[SunderSdkCapability(SunderSdkCapabilities.ThemingV1)]
public static class SunderThemeKeys
{
    public const string BackgroundAppBrush = "Sunder.Brush.Background.App";

    public const string SurfaceBaseBrush = "Sunder.Brush.Surface.Base";
    public const string SurfaceRaisedBrush = "Sunder.Brush.Surface.Raised";
    public const string SurfacePopoverBrush = "Sunder.Brush.Surface.Popover";
    public const string SurfaceWorkspaceBrush = "Sunder.Brush.Surface.Workspace";
    public const string SurfaceHoverBrush = "Sunder.Brush.Surface.Hover";
    public const string SurfaceSelectedBrush = "Sunder.Brush.Surface.Selected";
    public const string SurfaceDragOverBrush = "Sunder.Brush.Surface.DragOver";
    public const string SurfaceCodeBrush = "Sunder.Brush.Surface.Code";

    public const string BorderSubtleBrush = "Sunder.Brush.Border.Subtle";
    public const string BorderStrongBrush = "Sunder.Brush.Border.Strong";
    public const string BorderWarningBrush = "Sunder.Brush.Border.Warning";
    public const string BorderDangerBrush = "Sunder.Brush.Border.Danger";
    public const string BorderFocusBrush = "Sunder.Brush.Border.Focus";

    public const string ForegroundPrimaryBrush = "Sunder.Brush.Foreground.Primary";
    public const string ForegroundSecondaryBrush = "Sunder.Brush.Foreground.Secondary";
    public const string ForegroundMutedBrush = "Sunder.Brush.Foreground.Muted";
    public const string ForegroundOnAccentBrush = "Sunder.Brush.Foreground.OnAccent";
    public const string ForegroundOnDangerBrush = "Sunder.Brush.Foreground.OnDanger";
    public const string ForegroundCodeBrush = "Sunder.Brush.Foreground.Code";

    public const string AccentBrush = "Sunder.Brush.Accent";
    public const string AccentSoftBrush = "Sunder.Brush.Accent.Soft";
    public const string SuccessBrush = "Sunder.Brush.Success";
    public const string SuccessSoftBrush = "Sunder.Brush.Success.Soft";
    public const string WarningBrush = "Sunder.Brush.Warning";
    public const string WarningSoftBrush = "Sunder.Brush.Warning.Soft";
    public const string DangerBrush = "Sunder.Brush.Danger";
    public const string DangerSoftBrush = "Sunder.Brush.Danger.Soft";
    public const string ErrorBrush = "Sunder.Brush.Error";
    public const string ErrorSoftBrush = "Sunder.Brush.Error.Soft";
    public const string InfoBrush = "Sunder.Brush.Info";
    public const string InfoSoftBrush = "Sunder.Brush.Info.Soft";
    public const string FocusBrush = "Sunder.Brush.Focus";
    public const string SelectionBrush = "Sunder.Brush.Selection";
    public const string DisabledBrush = "Sunder.Brush.Disabled";

    public const string RadiusSmall = "Sunder.Radius.Small";
    public const string RadiusMedium = "Sunder.Radius.Medium";
    public const string RadiusLarge = "Sunder.Radius.Large";
    public const string RadiusFull = "Sunder.Radius.Full";

    public const string SpacingXSmall = "Sunder.Spacing.XSmall";
    public const string SpacingSmall = "Sunder.Spacing.Small";
    public const string SpacingMedium = "Sunder.Spacing.Medium";
    public const string SpacingLarge = "Sunder.Spacing.Large";
    public const string SpacingXLarge = "Sunder.Spacing.XLarge";

    public const string FontSizeCaption = "Sunder.FontSize.Caption";
    public const string FontSizeBody = "Sunder.FontSize.Body";
    public const string FontSizeSectionTitle = "Sunder.FontSize.SectionTitle";
    public const string FontSizePageTitle = "Sunder.FontSize.PageTitle";

    public static IReadOnlyList<string> BrushKeys { get; } =
    [
        BackgroundAppBrush,
        SurfaceBaseBrush,
        SurfaceRaisedBrush,
        SurfacePopoverBrush,
        SurfaceWorkspaceBrush,
        SurfaceHoverBrush,
        SurfaceSelectedBrush,
        SurfaceDragOverBrush,
        SurfaceCodeBrush,
        BorderSubtleBrush,
        BorderStrongBrush,
        BorderWarningBrush,
        BorderDangerBrush,
        BorderFocusBrush,
        ForegroundPrimaryBrush,
        ForegroundSecondaryBrush,
        ForegroundMutedBrush,
        ForegroundOnAccentBrush,
        ForegroundOnDangerBrush,
        ForegroundCodeBrush,
        AccentBrush,
        AccentSoftBrush,
        SuccessBrush,
        SuccessSoftBrush,
        WarningBrush,
        WarningSoftBrush,
        DangerBrush,
        DangerSoftBrush,
        ErrorBrush,
        ErrorSoftBrush,
        InfoBrush,
        InfoSoftBrush,
        FocusBrush,
        SelectionBrush,
        DisabledBrush,
    ];
}
