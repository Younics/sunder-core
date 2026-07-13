using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Composition;
using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.App.Views;

namespace Sunder.App;

public partial class App : Application
{
    private PackageViewHostService _packageViewHostService = PackageViewHostService.Empty;
    private readonly AppPackageResourceAssemblyRegistry _packageResourceAssemblyRegistry = new();
    private readonly OwnedTaskObserver _tasks = new("Sunder application");
    private readonly AppShutdownCoordinator _shutdown;
    private ServiceProvider? _serviceProvider;
    private WindowLauncher? _windowLauncher;
    private RuntimeEventSubscriptionService? _runtimeEventSubscription;
    private AboutSunderWindow? _aboutSunderWindow;

    public App()
    {
        _shutdown = new AppShutdownCoordinator(_tasks);
    }

    public override void Initialize()
    {
        AppPackageAvaloniaAssetLoader.Install(_packageResourceAssemblyRegistry);
        SunderAsyncImageLoader.Install();
        AvaloniaXamlLoader.Load(this);

#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RegisterExceptionHandlers();
        _serviceProvider = SunderAppComposition.CreateServiceProvider(this, Program.StartupOptions, _packageResourceAssemblyRegistry);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += OnDesktopExit;

            var loadingViewModel = new LoadingWindowViewModel();
            var loadingWindow = new LoadingWindow { DataContext = loadingViewModel };

            loadingWindow.Opened += (_, _) => _tasks.Run(
                _ => CompleteStartupSafelyAsync(desktop, loadingWindow, loadingViewModel),
                "completing startup");
            desktop.MainWindow = loadingWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task CompleteStartupSafelyAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        LoadingWindow loadingWindow,
        LoadingWindowViewModel loadingViewModel)
    {
        try
        {
            await CompleteStartupAsync(desktop, loadingWindow, loadingViewModel);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Sunder startup failed.", ex);
            loadingViewModel.StatusMessage = "Startup failed. Check the Sunder app log for details.";
        }
    }

    private async Task CompleteStartupAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        LoadingWindow loadingWindow,
        LoadingWindowViewModel loadingViewModel
    )
    {
        var startupCoordinator = (_serviceProvider ?? throw new InvalidOperationException("App services are not initialized."))
            .GetRequiredService<ShellStartupCoordinator>();
        var startup = await startupCoordinator.StartAsync(
            Program.StartupOptions,
            loadingViewModel
        );
        _packageViewHostService = startup.PackageViewHostService;
        _windowLauncher = startup.WindowLauncher;
        _runtimeEventSubscription = startup.RuntimeEventSubscription;
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

        _tasks.Run(_ => ActivateDeferredInitialHostedViewsAsync(mainWindowViewModel), "activating initial package views");
        Program.SingleInstanceCoordinator?.SetLaunchRequestHandler(HandleForwardedLaunchRequestAsync);
        _tasks.Run(_ => HandleInitialLaunchRequestAsync(Program.StartupOptions.LaunchRequest), "handling the initial launch request");
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

    private async Task HandleInitialLaunchRequestAsync(AppLaunchRequest request)
    {
        if (request.Kind == AppLaunchRequestKind.None || _windowLauncher is null)
        {
            return;
        }

        try
        {
            await _windowLauncher.HandleLaunchRequestAsync(request);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to handle startup launch request.", ex);
        }
    }

    private async Task HandleForwardedLaunchRequestAsync(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_windowLauncher is not null)
                {
                    await _windowLauncher.HandleLaunchRequestAsync(request, cancellationToken);
                }

                completion.SetResult();
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to handle forwarded Sunder launch request.", ex);
                completion.SetException(ex);
            }
        });

        await completion.Task.WaitAsync(cancellationToken);
    }

    private void AboutSunderMenuItem_OnClick(object? sender, EventArgs e)
        => ShowAboutSunderWindow();

    private void SettingsMenuItem_OnClick(object? sender, EventArgs e)
        => _windowLauncher?.ShowSettings();

    private void StacksMenuItem_OnClick(object? sender, EventArgs e)
        => _windowLauncher?.ShowStacks();

    private void PackagesMenuItem_OnClick(object? sender, EventArgs e)
        => _windowLauncher?.ShowPackages();

    private void ShowAboutSunderWindow()
    {
        if (_aboutSunderWindow is not null)
        {
            _aboutSunderWindow.Activate();
            return;
        }

        _aboutSunderWindow = new AboutSunderWindow();
        _aboutSunderWindow.Closed += AboutSunderWindow_OnClosed;
        var owner = GetCurrentOwnerWindow();
        if (owner is null)
        {
            _aboutSunderWindow.Show();
            return;
        }

        _aboutSunderWindow.Show(owner);
    }

    private void AboutSunderWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_aboutSunderWindow is not null)
        {
            _aboutSunderWindow.Closed -= AboutSunderWindow_OnClosed;
            _aboutSunderWindow = null;
        }
    }

    private Window? GetCurrentOwnerWindow()
        => ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow }
            ? mainWindow
            : null;

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
        => await _shutdown.ShutdownAsync(ShutdownCoreAsync);

    private async Task ShutdownCoreAsync()
    {
        var windowLauncher = _windowLauncher;
        _windowLauncher = null;
        var runtimeEventSubscription = _runtimeEventSubscription;
        _runtimeEventSubscription = null;
        if (runtimeEventSubscription is not null)
        {
            try
            {
                await runtimeEventSubscription.DisposeAsync();
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to stop Runtime event subscription during shutdown.", ex);
            }
        }

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

            try
            {
                windowLauncher.CloseForShutdown();
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to close app windows during shutdown.", ex);
            }
        }

        try
        {
            _aboutSunderWindow?.Close();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to close the About window during shutdown.", ex);
        }
        _aboutSunderWindow = null;

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

        var serviceProvider = _serviceProvider;
        _serviceProvider = null;
        if (serviceProvider is not null)
        {
            try
            {
                await serviceProvider.DisposeAsync();
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to dispose app services.", ex);
            }
        }

        try
        {
            Program.SingleInstanceCoordinator?.Dispose();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to dispose single-instance coordination during shutdown.", ex);
        }
        _tasks.Dispose();

        try
        {
            await AppSessionLog.FlushAsync();
        }
        catch
        {
            // Logging must never block shutdown.
        }
    }
}
