using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Avalonia.Theming;

/// <summary>Defines stable Avalonia dynamic-resource keys supplied by the active Sunder theme.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ThemingV1)]
public static class SunderThemeKeys
{
    /// <summary>Application background brush.</summary>
    public const string BackgroundAppBrush = "Sunder.Brush.Background.App";
    /// <summary>Default content-surface brush.</summary>
    public const string SurfaceBaseBrush = "Sunder.Brush.Surface.Base";
    /// <summary>Raised content-surface brush.</summary>
    public const string SurfaceRaisedBrush = "Sunder.Brush.Surface.Raised";
    /// <summary>Popover surface brush.</summary>
    public const string SurfacePopoverBrush = "Sunder.Brush.Surface.Popover";
    /// <summary>Primary workspace surface brush.</summary>
    public const string SurfaceWorkspaceBrush = "Sunder.Brush.Surface.Workspace";
    /// <summary>Pointer-hover surface brush.</summary>
    public const string SurfaceHoverBrush = "Sunder.Brush.Surface.Hover";
    /// <summary>Selected-item surface brush.</summary>
    public const string SurfaceSelectedBrush = "Sunder.Brush.Surface.Selected";
    /// <summary>Drag-over target surface brush.</summary>
    public const string SurfaceDragOverBrush = "Sunder.Brush.Surface.DragOver";
    /// <summary>Code-block surface brush.</summary>
    public const string SurfaceCodeBrush = "Sunder.Brush.Surface.Code";
    /// <summary>Low-emphasis border brush.</summary>
    public const string BorderSubtleBrush = "Sunder.Brush.Border.Subtle";
    /// <summary>High-emphasis border brush.</summary>
    public const string BorderStrongBrush = "Sunder.Brush.Border.Strong";
    /// <summary>Warning-state border brush.</summary>
    public const string BorderWarningBrush = "Sunder.Brush.Border.Warning";
    /// <summary>Danger-state border brush.</summary>
    public const string BorderDangerBrush = "Sunder.Brush.Border.Danger";
    /// <summary>Keyboard-focus border brush.</summary>
    public const string BorderFocusBrush = "Sunder.Brush.Border.Focus";
    /// <summary>Primary text and icon brush.</summary>
    public const string ForegroundPrimaryBrush = "Sunder.Brush.Foreground.Primary";
    /// <summary>Secondary text and icon brush.</summary>
    public const string ForegroundSecondaryBrush = "Sunder.Brush.Foreground.Secondary";
    /// <summary>Muted supporting-text brush.</summary>
    public const string ForegroundMutedBrush = "Sunder.Brush.Foreground.Muted";
    /// <summary>Foreground brush readable on accent surfaces.</summary>
    public const string ForegroundOnAccentBrush = "Sunder.Brush.Foreground.OnAccent";
    /// <summary>Foreground brush readable on danger surfaces.</summary>
    public const string ForegroundOnDangerBrush = "Sunder.Brush.Foreground.OnDanger";
    /// <summary>Code text brush.</summary>
    public const string ForegroundCodeBrush = "Sunder.Brush.Foreground.Code";
    /// <summary>Primary interactive accent brush.</summary>
    public const string AccentBrush = "Sunder.Brush.Accent";
    /// <summary>Low-emphasis accent surface brush.</summary>
    public const string AccentSoftBrush = "Sunder.Brush.Accent.Soft";
    /// <summary>Success-state foreground brush.</summary>
    public const string SuccessBrush = "Sunder.Brush.Success";
    /// <summary>Success-state surface brush.</summary>
    public const string SuccessSoftBrush = "Sunder.Brush.Success.Soft";
    /// <summary>Warning-state foreground brush.</summary>
    public const string WarningBrush = "Sunder.Brush.Warning";
    /// <summary>Warning-state surface brush.</summary>
    public const string WarningSoftBrush = "Sunder.Brush.Warning.Soft";
    /// <summary>Dangerous-action foreground brush.</summary>
    public const string DangerBrush = "Sunder.Brush.Danger";
    /// <summary>Dangerous-action surface brush.</summary>
    public const string DangerSoftBrush = "Sunder.Brush.Danger.Soft";
    /// <summary>Error-state foreground brush.</summary>
    public const string ErrorBrush = "Sunder.Brush.Error";
    /// <summary>Error-state surface brush.</summary>
    public const string ErrorSoftBrush = "Sunder.Brush.Error.Soft";
    /// <summary>Informational foreground brush.</summary>
    public const string InfoBrush = "Sunder.Brush.Info";
    /// <summary>Informational surface brush.</summary>
    public const string InfoSoftBrush = "Sunder.Brush.Info.Soft";
    /// <summary>Focus-indicator brush.</summary>
    public const string FocusBrush = "Sunder.Brush.Focus";
    /// <summary>Text or item selection brush.</summary>
    public const string SelectionBrush = "Sunder.Brush.Selection";
    /// <summary>Disabled-content brush.</summary>
    public const string DisabledBrush = "Sunder.Brush.Disabled";
    /// <summary>Fully transparent brush.</summary>
    public const string TransparentBrush = "Sunder.Brush.Transparent";
    /// <summary>Standard modal backdrop brush.</summary>
    public const string OverlayBackdropBrush = "Sunder.Brush.Overlay.Backdrop";
    /// <summary>High-opacity modal backdrop brush.</summary>
    public const string OverlayBackdropStrongBrush = "Sunder.Brush.Overlay.Backdrop.Strong";
    /// <summary>Overlay content-surface brush.</summary>
    public const string OverlaySurfaceBrush = "Sunder.Brush.Overlay.Surface";
    /// <summary>Overlay boundary brush.</summary>
    public const string OverlayBorderBrush = "Sunder.Brush.Overlay.Border";
    /// <summary>Accent tint used over layered content.</summary>
    public const string AccentOverlayBrush = "Sunder.Brush.Accent.Overlay";
    /// <summary>Raw application background color for gradients and derived brushes.</summary>
    public const string BackgroundAppColor = "Sunder.Color.Background.App";
    /// <summary>Raw base-surface color.</summary>
    public const string SurfaceBaseColor = "Sunder.Color.Surface.Base";
    /// <summary>Raw raised-surface color.</summary>
    public const string SurfaceRaisedColor = "Sunder.Color.Surface.Raised";
    /// <summary>Raw workspace-surface color.</summary>
    public const string SurfaceWorkspaceColor = "Sunder.Color.Surface.Workspace";
    /// <summary>Start color for the application background gradient.</summary>
    public const string AppGradientStartColor = "Sunder.Color.AppGradient.Start";
    /// <summary>Middle color for the application background gradient.</summary>
    public const string AppGradientMiddleColor = "Sunder.Color.AppGradient.Middle";
    /// <summary>End color for the application background gradient.</summary>
    public const string AppGradientEndColor = "Sunder.Color.AppGradient.End";
    /// <summary>Start color for loading overlays.</summary>
    public const string LoadingOverlayStartColor = "Sunder.Color.Loading.Overlay.Start";
    /// <summary>Middle color for loading overlays.</summary>
    public const string LoadingOverlayMiddleColor = "Sunder.Color.Loading.Overlay.Middle";
    /// <summary>Low-opacity loading-overlay color.</summary>
    public const string LoadingOverlaySoftColor = "Sunder.Color.Loading.Overlay.Soft";
    /// <summary>End color for loading overlays.</summary>
    public const string LoadingOverlayEndColor = "Sunder.Color.Loading.Overlay.End";
    /// <summary>Box shadow for raised workspace panels.</summary>
    public const string ShadowWorkspacePanel = "Sunder.Shadow.WorkspacePanel";
    /// <summary>Box shadow for shell panels.</summary>
    public const string ShadowShellPanel = "Sunder.Shadow.ShellPanel";
    /// <summary>Box shadow for toast notifications.</summary>
    public const string ShadowToast = "Sunder.Shadow.Toast";
    /// <summary>Box shadow for prompts and dialogs.</summary>
    public const string ShadowPrompt = "Sunder.Shadow.Prompt";
    /// <summary>Drop shadow for welcome branding.</summary>
    public const string ShadowWelcomeLogo = "Sunder.Shadow.WelcomeLogo";
    /// <summary>Box shadow for package detail panels.</summary>
    public const string ShadowPackagePanel = "Sunder.Shadow.PackagePanel";
    /// <summary>Box shadow for package cards.</summary>
    public const string ShadowPackageCard = "Sunder.Shadow.PackageCard";
    /// <summary>Box shadow for modal overlay content.</summary>
    public const string ShadowOverlay = "Sunder.Shadow.Overlay";
    /// <summary>Small control corner radius.</summary>
    public const string RadiusSmall = "Sunder.Radius.Small";
    /// <summary>Standard control and card corner radius.</summary>
    public const string RadiusMedium = "Sunder.Radius.Medium";
    /// <summary>Large panel corner radius.</summary>
    public const string RadiusLarge = "Sunder.Radius.Large";
    /// <summary>Fully rounded pill or circular radius.</summary>
    public const string RadiusFull = "Sunder.Radius.Full";
    /// <summary>Extra-small layout spacing.</summary>
    public const string SpacingXSmall = "Sunder.Spacing.XSmall";
    /// <summary>Small layout spacing.</summary>
    public const string SpacingSmall = "Sunder.Spacing.Small";
    /// <summary>Standard layout spacing.</summary>
    public const string SpacingMedium = "Sunder.Spacing.Medium";
    /// <summary>Large layout spacing.</summary>
    public const string SpacingLarge = "Sunder.Spacing.Large";
    /// <summary>Extra-large layout spacing.</summary>
    public const string SpacingXLarge = "Sunder.Spacing.XLarge";
    /// <summary>Caption text size.</summary>
    public const string FontSizeCaption = "Sunder.FontSize.Caption";
    /// <summary>Standard body text size.</summary>
    public const string FontSizeBody = "Sunder.FontSize.Body";
    /// <summary>Section-heading text size.</summary>
    public const string FontSizeSectionTitle = "Sunder.FontSize.SectionTitle";
    /// <summary>Page-heading text size.</summary>
    public const string FontSizePageTitle = "Sunder.FontSize.PageTitle";

    /// <summary>Gets all semantic brush keys for validation or resource inspection.</summary>
    public static IReadOnlyList<string> BrushKeys { get; } =
    [
        BackgroundAppBrush, SurfaceBaseBrush, SurfaceRaisedBrush, SurfacePopoverBrush, SurfaceWorkspaceBrush,
        SurfaceHoverBrush, SurfaceSelectedBrush, SurfaceDragOverBrush, SurfaceCodeBrush, BorderSubtleBrush,
        BorderStrongBrush, BorderWarningBrush, BorderDangerBrush, BorderFocusBrush, ForegroundPrimaryBrush,
        ForegroundSecondaryBrush, ForegroundMutedBrush, ForegroundOnAccentBrush, ForegroundOnDangerBrush,
        ForegroundCodeBrush, AccentBrush, AccentSoftBrush, SuccessBrush, SuccessSoftBrush, WarningBrush,
        WarningSoftBrush, DangerBrush, DangerSoftBrush, ErrorBrush, ErrorSoftBrush, InfoBrush, InfoSoftBrush,
        FocusBrush, SelectionBrush, DisabledBrush, TransparentBrush, OverlayBackdropBrush,
        OverlayBackdropStrongBrush, OverlaySurfaceBrush, OverlayBorderBrush, AccentOverlayBrush,
    ];
}
