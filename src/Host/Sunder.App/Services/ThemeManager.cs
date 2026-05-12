using Avalonia;
using Avalonia.Media;
using Sunder.App.Themes;
using Sunder.Sdk.Theming;

namespace Sunder.App.Services;

public sealed class ThemeManager(Application application) : IThemeManager
{
    private readonly Application _application = application;

    public IReadOnlyList<SunderThemeDefinition> AvailableThemes { get; } =
    [
        SunderThemeDefinition.GraphiteDark,
    ];

    public SunderThemeDefinition ActiveTheme { get; private set; } = SunderThemeDefinition.GraphiteDark;

    public void Initialize()
    {
        ApplyTheme(SunderThemeDefinition.GraphiteDark.Id);
    }

    public void ApplyTheme(string themeId)
    {
        var theme = AvailableThemes.FirstOrDefault(x => x.Id == themeId) ?? SunderThemeDefinition.GraphiteDark;
        ActiveTheme = theme;

        SetBrush(SunderThemeKeys.BackgroundAppBrush, theme.BackgroundApp);
        SetBrush(SunderThemeKeys.SurfaceBaseBrush, theme.SurfaceBase);
        SetBrush(SunderThemeKeys.SurfaceRaisedBrush, theme.SurfaceRaised);
        SetBrush(SunderThemeKeys.SurfacePopoverBrush, theme.SurfacePopover);
        SetBrush(SunderThemeKeys.SurfaceWorkspaceBrush, theme.SurfaceWorkspace);
        SetBrush(SunderThemeKeys.SurfaceHoverBrush, theme.SurfaceHover);
        SetBrush(SunderThemeKeys.SurfaceSelectedBrush, theme.SurfaceSelected);
        SetBrush(SunderThemeKeys.SurfaceDragOverBrush, theme.SurfaceDragOver);
        SetBrush(SunderThemeKeys.SurfaceCodeBrush, theme.SurfaceCode);
        SetBrush(SunderThemeKeys.BorderSubtleBrush, theme.BorderSubtle);
        SetBrush(SunderThemeKeys.BorderStrongBrush, theme.BorderStrong);
        SetBrush(SunderThemeKeys.BorderWarningBrush, theme.BorderWarning);
        SetBrush(SunderThemeKeys.BorderDangerBrush, theme.BorderDanger);
        SetBrush(SunderThemeKeys.BorderFocusBrush, theme.BorderFocus);
        SetBrush(SunderThemeKeys.ForegroundPrimaryBrush, theme.ForegroundPrimary);
        SetBrush(SunderThemeKeys.ForegroundSecondaryBrush, theme.ForegroundSecondary);
        SetBrush(SunderThemeKeys.ForegroundMutedBrush, theme.ForegroundMuted);
        SetBrush(SunderThemeKeys.ForegroundOnAccentBrush, theme.ForegroundOnAccent);
        SetBrush(SunderThemeKeys.ForegroundOnDangerBrush, theme.ForegroundOnDanger);
        SetBrush(SunderThemeKeys.ForegroundCodeBrush, theme.ForegroundCode);
        SetBrush(SunderThemeKeys.AccentBrush, theme.Accent);
        SetBrush(SunderThemeKeys.AccentSoftBrush, theme.AccentSoft);
        SetBrush(SunderThemeKeys.SuccessBrush, theme.Success);
        SetBrush(SunderThemeKeys.SuccessSoftBrush, theme.SuccessSoft);
        SetBrush(SunderThemeKeys.WarningBrush, theme.Warning);
        SetBrush(SunderThemeKeys.WarningSoftBrush, theme.WarningSoft);
        SetBrush(SunderThemeKeys.DangerBrush, theme.Danger);
        SetBrush(SunderThemeKeys.DangerSoftBrush, theme.DangerSoft);
        SetBrush(SunderThemeKeys.ErrorBrush, theme.Danger);
        SetBrush(SunderThemeKeys.ErrorSoftBrush, theme.DangerSoft);
        SetBrush(SunderThemeKeys.InfoBrush, theme.Info);
        SetBrush(SunderThemeKeys.InfoSoftBrush, theme.InfoSoft);
        SetBrush(SunderThemeKeys.FocusBrush, theme.Focus);
        SetBrush(SunderThemeKeys.SelectionBrush, theme.Selection);
        SetBrush(SunderThemeKeys.DisabledBrush, theme.Disabled);
        SetBrush(SunderThemeKeys.TransparentBrush, Colors.Transparent);
        SetBrush(SunderThemeKeys.OverlayBackdropBrush, WithAlpha(theme.BackgroundApp, 0xE6));
        SetBrush(SunderThemeKeys.OverlayBackdropStrongBrush, WithAlpha(theme.BackgroundApp, 0xCC));
        SetBrush(SunderThemeKeys.OverlaySurfaceBrush, WithAlpha(theme.SurfaceWorkspace, 0xB8));
        SetBrush(SunderThemeKeys.OverlayBorderBrush, WithAlpha(theme.ForegroundPrimary, 0x29));
        SetBrush(SunderThemeKeys.AccentOverlayBrush, WithAlpha(theme.Accent, 0x24));
        SetThemeColorResources(theme);
        SetThemeShadowResources(theme);
        SetFluentAccentResources(theme);
    }

    private void SetBrush(string key, Color color)
    {
        _application.Resources[key] = new SolidColorBrush(color);
    }

    private void SetColor(string key, Color color)
    {
        _application.Resources[key] = color;
    }

    private void SetShadow(string key, string value)
    {
        _application.Resources[key] = BoxShadows.Parse(value);
    }

    private void SetThemeColorResources(SunderThemeDefinition theme)
    {
        SetColor(SunderThemeKeys.BackgroundAppColor, theme.BackgroundApp);
        SetColor(SunderThemeKeys.SurfaceBaseColor, theme.SurfaceBase);
        SetColor(SunderThemeKeys.SurfaceRaisedColor, theme.SurfaceRaised);
        SetColor(SunderThemeKeys.SurfaceWorkspaceColor, theme.SurfaceWorkspace);
        SetColor(SunderThemeKeys.AppGradientStartColor, theme.SurfaceRaised);
        SetColor(SunderThemeKeys.AppGradientMiddleColor, theme.SurfaceWorkspace);
        SetColor(SunderThemeKeys.AppGradientEndColor, theme.BackgroundApp);
        SetColor(SunderThemeKeys.LoadingOverlayStartColor, WithAlpha(theme.SurfaceRaised, 0xD2));
        SetColor(SunderThemeKeys.LoadingOverlayMiddleColor, WithAlpha(theme.SurfaceRaised, 0xAA));
        SetColor(SunderThemeKeys.LoadingOverlaySoftColor, WithAlpha(theme.SurfaceRaised, 0x42));
        SetColor(SunderThemeKeys.LoadingOverlayEndColor, WithAlpha(theme.SurfaceRaised, 0x14));
    }

    private void SetThemeShadowResources(SunderThemeDefinition theme)
    {
        SetShadow(SunderThemeKeys.ShadowWorkspacePanel, CreateShadow(0, 22, 70, 0, WithAlpha(theme.BackgroundApp, 0x54)));
        SetShadow(SunderThemeKeys.ShadowShellPanel, CreateShadow(0, 18, 52, 0, WithAlpha(theme.BackgroundApp, 0x42)));
        SetShadow(SunderThemeKeys.ShadowToast, CreateShadow(0, 10, 28, 0, WithAlpha(theme.BackgroundApp, 0x7A)));
        SetShadow(SunderThemeKeys.ShadowPrompt, CreateShadow(0, 18, 42, 0, WithAlpha(theme.BackgroundApp, 0x92)));
        SetShadow(SunderThemeKeys.ShadowWelcomeLogo, CreateShadow(0, 14, 36, 0, WithAlpha(theme.BackgroundApp, 0x70)));
        SetShadow(SunderThemeKeys.ShadowPackagePanel, CreateShadow(0, 18, 48, 0, WithAlpha(theme.BackgroundApp, 0x58)));
        SetShadow(SunderThemeKeys.ShadowPackageCard, CreateShadow(0, 10, 30, 0, WithAlpha(theme.BackgroundApp, 0x42)));
        SetShadow(SunderThemeKeys.ShadowOverlay, CreateShadow(0, 28, 100, 0, WithAlpha(theme.BackgroundApp, 0x90)));
    }

    private static string CreateShadow(int offsetX, int offsetY, int blur, int spread, Color color)
        => $"{offsetX} {offsetY} {blur} {spread} {FormatColor(color)}";

    private static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static string FormatColor(Color color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private void SetFluentAccentResources(SunderThemeDefinition theme)
    {
        SetColor("SystemAccentColor", theme.Accent);
        SetColor("SystemAccentColorLight1", theme.Accent);
        SetColor("SystemAccentColorLight2", theme.Accent);
        SetColor("SystemAccentColorLight3", theme.Accent);
        SetColor("SystemAccentColorDark1", theme.Accent);
        SetColor("SystemAccentColorDark2", theme.Accent);
        SetColor("SystemAccentColorDark3", theme.Accent);
    }
}
