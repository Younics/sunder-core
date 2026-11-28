using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Sunder.App.Services;

namespace Sunder.App.Tests;

public sealed class HeadlessTestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApplication>()
        .UseSkia().UseHeadless(new() { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        var app = new Uri("avares://Sunder.App/");
        Resources.MergedDictionaries.Add(new ResourceInclude(app)
            { Source = new Uri("avares://Sunder.App.Bridge/Themes/SunderThemeResources.axaml") });
        Resources.MergedDictionaries.Add(new ResourceInclude(app) { Source = new Uri(app, "Styles/IconResources.axaml") });
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(app) { Source = new Uri("avares://Sunder.App.Bridge/Themes/SunderPackageStyles.axaml") });
        Styles.Add(new StyleInclude(app) { Source = new Uri(app, "Styles/SunderShellStyles.axaml") });
        Styles.Add(new StyleInclude(app) { Source = new Uri(app, "Styles/PackageWindowStyles.axaml") });
        Styles.Add(new StyleInclude(app) { Source = new Uri(app, "Styles/ManagementWindowStyles.axaml") });
        Styles.Add(new StyleInclude(app) { Source = new Uri(app, "Styles/ProcessMonitorStyles.axaml") });
        new ThemeManager(this).Initialize();
    }
}
