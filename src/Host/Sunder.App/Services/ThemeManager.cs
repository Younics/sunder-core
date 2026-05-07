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
        SunderThemeDefinition.DefaultDark,
        SunderThemeDefinition.GraphiteDark,
    ];

    public SunderThemeDefinition ActiveTheme { get; private set; } = SunderThemeDefinition.DefaultDark;

    public void Initialize()
    {
        ApplyTheme(SunderThemeDefinition.DefaultDark.Id);
    }

    public void ApplyTheme(string themeId)
    {
        var theme = AvailableThemes.FirstOrDefault(x => x.Id == themeId) ?? SunderThemeDefinition.DefaultDark;
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
