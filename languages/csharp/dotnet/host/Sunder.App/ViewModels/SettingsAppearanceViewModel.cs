using Sunder.App.Services;
using Sunder.App.Themes;

namespace Sunder.App.ViewModels;

public sealed partial class SettingsAppearanceViewModel(IThemeManager themeManager, Action<string> persistTheme) : ViewModelBase
{
    public IReadOnlyList<SunderThemeDefinition> Themes => themeManager.AvailableThemes;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private SunderThemeDefinition? _selectedTheme = themeManager.ActiveTheme;
    private SunderThemeDefinition? _beforePreview;

    partial void OnSelectedThemeChanged(SunderThemeDefinition? value)
    {
        if (value is null || value.Id == themeManager.ActiveTheme.Id) return;
        _beforePreview ??= themeManager.ActiveTheme;
        themeManager.ApplyTheme(value.Id);
    }

    public void LoadSettings() => SelectedTheme = themeManager.ActiveTheme;

    public void CancelPreview()
    {
        if (_beforePreview is not { } original) return;
        _beforePreview = null;
        themeManager.ApplyTheme(original.Id);
        LoadSettings();
    }

    public void Save()
    {
        var selected = Themes.FirstOrDefault(theme => theme.Id == SelectedTheme?.Id)
            ?? throw new InvalidOperationException("Select an available theme.");
        try
        {
            persistTheme(selected.Id);
            _beforePreview = null;
        }
        catch
        {
            CancelPreview();
            throw;
        }
    }
}
