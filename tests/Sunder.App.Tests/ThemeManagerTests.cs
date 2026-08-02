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
    public void Initialize_PublishesResourcesRequiredByLoadingWindow()
    {
        var application = new Application();
        var themeManager = new ThemeManager(application);

        themeManager.Initialize();

        var primaryForeground = Assert.IsType<SolidColorBrush>(
            application.Resources[SunderThemeKeys.ForegroundPrimaryBrush]);
        Assert.Equal(Color.Parse("#D8D5CE"), primaryForeground.Color);
        Assert.IsType<SolidColorBrush>(application.Resources[SunderThemeKeys.ForegroundSecondaryBrush]);
        Assert.IsType<SolidColorBrush>(application.Resources[SunderThemeKeys.ForegroundMutedBrush]);
        Assert.IsType<SolidColorBrush>(application.Resources[SunderThemeKeys.SurfaceWorkspaceBrush]);
        Assert.IsType<SolidColorBrush>(application.Resources[SunderThemeKeys.SurfaceRaisedBrush]);
        Assert.IsType<SolidColorBrush>(application.Resources[SunderThemeKeys.AccentBrush]);
        Assert.IsType<Color>(application.Resources[SunderThemeKeys.LoadingOverlayStartColor]);
        Assert.IsType<Color>(application.Resources[SunderThemeKeys.LoadingOverlayMiddleColor]);
        Assert.IsType<Color>(application.Resources[SunderThemeKeys.LoadingOverlaySoftColor]);
        Assert.IsType<Color>(application.Resources[SunderThemeKeys.LoadingOverlayEndColor]);
    }

    [Fact]
    public void AppStartup_InitializesThemeBeforeConstructingLoadingWindow()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "Host",
            "Sunder.App",
            "App.axaml.cs"));

        var themeInitialization = source.IndexOf(
            "GetRequiredService<IThemeManager>().Initialize()",
            StringComparison.Ordinal);
        var loadingWindowConstruction = source.IndexOf(
            "new LoadingWindow",
            StringComparison.Ordinal);

        Assert.True(themeInitialization >= 0);
        Assert.True(loadingWindowConstruction > themeInitialization);
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
