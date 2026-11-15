using Sunder.App.Services;
using Sunder.App.Themes;

namespace Sunder.App.ViewModels;

public sealed partial class SettingsAppearanceViewModel(IThemeManager themeManager, Action<string> persistTheme) : ViewModelBase
{
    public IReadOnlyList<SunderThemeDefinition> Themes => themeManager.AvailableThemes;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private SunderThemeDefinition? _selectedTheme = themeManager.ActiveTheme;

    public void LoadSettings() => SelectedTheme = themeManager.ActiveTheme;

    public void Save()
    {
        var selected = Themes.FirstOrDefault(theme => theme.Id == SelectedTheme?.Id)
            ?? throw new InvalidOperationException("Select an available theme.");
        persistTheme(selected.Id);
        themeManager.ApplyTheme(selected.Id);
    }
}
