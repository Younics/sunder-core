using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App;

public partial class App : Application
{
    private PackageViewHostService _packageViewHostService = PackageViewHostService.Empty;
    private WindowLauncher? _windowLauncher;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RegisterExceptionHandlers();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += OnDesktopExit;

            var loadingViewModel = new LoadingWindowViewModel();
            var loadingWindow = new LoadingWindow { DataContext = loadingViewModel };

            loadingWindow.Opened += async (_, _) =>
            {
                try
                {
                    await CompleteStartupAsync(desktop, loadingWindow, loadingViewModel);
                }
                catch (Exception ex)
                {
                    AppSessionLog.WriteError("Sunder startup failed.", ex);
                    loadingViewModel.StatusMessage =
                        "Startup failed. Check the Sunder app log for details.";
                }
            };
            desktop.MainWindow = loadingWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task CompleteStartupAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        LoadingWindow loadingWindow,
        LoadingWindowViewModel loadingViewModel
    )
    {
        var startup = await new ShellStartupCoordinator(this).StartAsync(
            Program.StartupOptions,
            loadingViewModel
        );
        _packageViewHostService = startup.PackageViewHostService;
        _windowLauncher = startup.WindowLauncher;
        var mainWindow = startup.MainWindow;
        var mainWindowViewModel = startup.MainWindowViewModel;

        desktop.MainWindow = mainWindow;
        try
        {
            await ShowMainWindowWhenReadyAsync(mainWindow);
        }
        finally
        {
            if (loadingWindow.IsVisible)
            {
                loadingWindow.Close();
            }
        }

        _ = ActivateDeferredInitialHostedViewsAsync(mainWindowViewModel);
    }

    private static async Task ShowMainWindowWhenReadyAsync(MainWindow mainWindow)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            mainWindow.Opacity = 0;
            mainWindow.Show();
        });

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            mainWindow.Opacity = 1;
            mainWindow.Activate();
        });
    }

    private static async Task ActivateDeferredInitialHostedViewsAsync(MainWindowViewModel mainWindowViewModel)
    {
        try
        {
            await mainWindowViewModel.ActivateDeferredInitialHostedViewsAsync();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to activate deferred package views after startup.", ex);
        }
    }

    private void RegisterExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppSessionLog.WriteError("Unhandled UI exception.", e.Exception);
            if (_packageViewHostService.TryHandleUnhandledException(e.Exception))
            {
                e.Handled = true;
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
            AppSessionLog.WriteError("Unobserved task exception.", e.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppSessionLog.WriteError(
                "Unhandled application-domain exception.",
                e.ExceptionObject as Exception
            );
    }

    private async void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        var windowLauncher = _windowLauncher;
        _windowLauncher = null;
        if (windowLauncher is not null)
        {
            try
            {
                await windowLauncher.CancelBackgroundProcessesAsync();
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to cancel background processes during shutdown.", ex);
            }

            windowLauncher.CloseForShutdown();
        }

        var hostService = _packageViewHostService;
        _packageViewHostService = PackageViewHostService.Empty;

        try
        {
            await hostService.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to dispose the package view host service.", ex);
        }
    }
}
