using Sunder.App.Themes;

namespace Sunder.App.Services;

public interface IThemeManager
{
    IReadOnlyList<SunderThemeDefinition> AvailableThemes { get; }

    SunderThemeDefinition ActiveTheme { get; }

    void Initialize();

    void ApplyTheme(string themeId);
}
