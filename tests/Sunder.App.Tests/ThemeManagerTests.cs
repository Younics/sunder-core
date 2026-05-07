using Avalonia;
using Avalonia.Media;
using Sunder.App.Services;
using Sunder.Sdk.Theming;
using Xunit;

namespace Sunder.App.Tests;

public sealed class ThemeManagerTests
{
    [Fact]
    public void ApplyTheme_PublishesAllSdkBrushKeys()
    {
        var application = new Application();
        var themeManager = new ThemeManager(application);

        foreach (var theme in themeManager.AvailableThemes)
        {
            themeManager.ApplyTheme(theme.Id);

            foreach (var key in SunderThemeKeys.BrushKeys)
            {
                Assert.True(application.Resources.ContainsKey(key), $"Missing theme brush resource '{key}'.");
                Assert.IsType<SolidColorBrush>(application.Resources[key]);
            }
        }
    }
}
