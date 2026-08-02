using Avalonia;
using Avalonia.Media;
using Sunder.App.Services;
using Sunder.Sdk.Avalonia.Theming;
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

    [Fact]
    public void PackageStyles_UseSemanticVisibleFocusAdornerForInteractiveControls()
    {
        var path = Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Sdk",
            "Sunder.Sdk.Avalonia",
            "Themes",
            "SunderPackageStyles.axaml");
        var xaml = File.ReadAllText(path);
        const string selector = "ListBoxItem:focus-visible, Button:focus-visible, CheckBox:focus-visible";
        var styleStart = xaml.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(styleStart >= 0, "The package style must target keyboard focus for list items, buttons, and check boxes.");
        var styleEnd = xaml.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        Assert.True(styleEnd > styleStart);
        var focusStyle = xaml[styleStart..styleEnd];

        Assert.DoesNotContain("x:Null", focusStyle, StringComparison.Ordinal);
        Assert.Contains("Sunder.Brush.Border.Focus", focusStyle, StringComparison.Ordinal);
        Assert.Contains("Sunder.Brush.Focus", focusStyle, StringComparison.Ordinal);
        Assert.Contains("FocusAdornerTemplate", focusStyle, StringComparison.Ordinal);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate the Sunder Core repository root.");
    }
}
