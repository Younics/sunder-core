using Avalonia;
using System;
using Sunder.App.Models;
using Sunder.App.Services;
using Velopack;

namespace Sunder.App;

sealed class Program
{
    public static AppStartupOptions StartupOptions { get; private set; } = new();

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        StartupOptions = AppStartupOptionsParser.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions
            {
                RenderingMode =
                [
                    // put OpenGL first, to have higher priority over Metal (remove later when Metal issue with resize flickering is fixed)
                    AvaloniaNativeRenderingMode.OpenGl,
                    AvaloniaNativeRenderingMode.Metal,
                    AvaloniaNativeRenderingMode.Software
                ]
            })
            .WithInterFont()
            .WithDeveloperTools()
            .LogToTrace();
}
