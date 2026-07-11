using Avalonia;
using Sunder.App.Models;
using Sunder.App.Services;
using Velopack;

namespace Sunder.App;

sealed class Program
{
    public static AppStartupOptions StartupOptions { get; private set; } = new();

    internal static AppSingleInstanceCoordinator? SingleInstanceCoordinator { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static async Task Main(string[] args)
    {
        var velopackApp = VelopackApp.Build()
            .OnFirstRun(_ => RegisterShellAssociationsForCurrentUser());

        if (OperatingSystem.IsWindows())
        {
            velopackApp
                .OnAfterInstallFastCallback(_ => WindowsShellAssociationService.RegisterForCurrentUser())
                .OnBeforeUninstallFastCallback(_ => WindowsShellAssociationService.UnregisterForCurrentUser());
        }

        velopackApp.Run();
        StartupOptions = AppStartupOptionsParser.Parse(args);
        AppLocalState.EnsureInitializedForStartup();
        if (await TryForwardLaunchToPrimaryInstanceAsync(args))
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static async Task<bool> TryForwardLaunchToPrimaryInstanceAsync(string[] args)
    {
        try
        {
            SingleInstanceCoordinator = AppSingleInstanceCoordinator.CreateForCurrentUser();
            if (SingleInstanceCoordinator.IsPrimary)
            {
                SingleInstanceCoordinator.StartListening();
                return false;
            }

            var forwarded = await SingleInstanceCoordinator.TryForwardLaunchArgumentsAsync(args);
            if (!forwarded)
            {
                AppSessionLog.WriteInfo("Another Sunder instance is running, but the launch request could not be forwarded.");
            }

            SingleInstanceCoordinator.Dispose();
            SingleInstanceCoordinator = null;
            return true;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to initialize Sunder single-instance launch handoff.", ex);
            SingleInstanceCoordinator?.Dispose();
            SingleInstanceCoordinator = null;
            return false;
        }
    }

    private static void RegisterShellAssociationsForCurrentUser()
    {
        WindowsShellAssociationService.RegisterForCurrentUser();
        LinuxShellAssociationService.RegisterForCurrentUser();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .With(
                new AvaloniaNativePlatformOptions
                {
                    RenderingMode =
                    [
                        // put OpenGL first, to have higher priority over Metal (remove later when Metal issue with resize flickering is fixed)
                        AvaloniaNativeRenderingMode.OpenGl,
                        AvaloniaNativeRenderingMode.Metal,
                        AvaloniaNativeRenderingMode.Software,
                    ],
                }
            )
            .WithInterFont()
            .LogToTrace();
}
