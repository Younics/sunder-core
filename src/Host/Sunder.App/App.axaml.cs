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
using Sunder.Sdk.Notifications;

namespace Sunder.App;

public partial class App : Application
{
    private readonly AppPackageResourceAssemblyRegistry _packageResourceAssemblyRegistry = new();
    private readonly OwnedTaskObserver _tasks = new("Sunder application");
    private readonly AppShutdownCoordinator _shutdown;
    private ServiceProvider? _serviceProvider;
    private ShellSession? _session;
    private IClassicDesktopStyleApplicationLifetime? _desktopLifetime;
    private Window? _shutdownWindow;
    private Task? _desktopShutdownTask;
    private int _requestedExitCode;
    private bool _allowWindowClose;
    private bool _allowDesktopShutdown;

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
        _serviceProvider = SunderAppComposition.CreateServiceProvider(
            this,
            Program.StartupOptions,
            _packageResourceAssemblyRegistry
        );
        _serviceProvider.GetRequiredService<IThemeManager>().Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktopLifetime = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += OnDesktopShutdownRequested;
            if (
                OperatingSystem.IsMacOS()
                && this.TryGetFeature<IActivatableLifetime>() is { } activatableLifetime
            )
            {
                activatableLifetime.Activated += OnMacOsActivated;
            }

            var loadingViewModel = new LoadingWindowViewModel();
            var loadingWindow = new LoadingWindow { DataContext = loadingViewModel };
            _shutdownWindow = loadingWindow;
            loadingWindow.Closing += OnDesktopMainWindowClosing;

            loadingWindow.Opened += (_, _) =>
            {
                var shutdownAfterStartup = false;
                var startupTask = _tasks.RunTracked(
                    async cancellationToken =>
                    {
                        shutdownAfterStartup = await CompleteStartupSafelyAsync(
                            desktop,
                            loadingWindow,
                            loadingViewModel,
                            cancellationToken
                        );
                    },
                    "completing startup"
                );
                _ = startupTask.ContinueWith(
                    _ =>
                    {
                        if (shutdownAfterStartup)
                        {
                            Dispatcher.UIThread.Post(
                                () => RequestDesktopShutdown(desktop, exitCode: 1)
                            );
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
            };
            desktop.MainWindow = loadingWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task<bool> CompleteStartupSafelyAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        LoadingWindow loadingWindow,
        LoadingWindowViewModel loadingViewModel,
        CancellationToken cancellationToken = default
    )
    {
        if (!loadingViewModel.TryBeginAttempt())
        {
            return false;
        }

        try
        {
            var notificationService = ResolveStartupNotificationService(_serviceProvider);
            var outcome = await StartupFallbackRunner.RunAsync(
                async (openCoreShell, attemptCancellationToken) =>
                {
                    if (openCoreShell)
                    {
                        loadingViewModel.BeginCoreShellFallback();
                    }

                    await CompleteStartupAttemptAsync(
                        desktop,
                        loadingWindow,
                        loadingViewModel,
                        openCoreShell,
                        attemptCancellationToken
                    );
                },
                (failure, notificationCancellationToken) =>
                    PublishStartupFailureAsync(
                        notificationService,
                        failure,
                        notificationCancellationToken
                    ),
                cancellationToken
            );

            return outcome.CoreShellFailure is not null;
        }
        finally
        {
            loadingViewModel.CompleteAttempt();
        }
    }

    internal static IPackageNotificationService ResolveStartupNotificationService(
        IServiceProvider? serviceProvider
    )
    {
        try
        {
            return serviceProvider?.GetService<IPackageNotificationService>()
                ?? NullPackageNotificationService.Instance;
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError(
                "Failed to resolve the Sunder SDK notification service.",
                exception
            );
            return NullPackageNotificationService.Instance;
        }
    }

    private async Task CompleteStartupAttemptAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        LoadingWindow loadingWindow,
        LoadingWindowViewModel loadingViewModel,
        bool openCoreShell,
        CancellationToken cancellationToken
    )
    {
        using var deadline = new StartupAttemptDeadline(cancellationToken);
        try
        {
            await CompleteStartupAsync(
                desktop,
                loadingWindow,
                loadingViewModel,
                openCoreShell,
                deadline
            );
        }
        catch (OperationCanceledException exception) when (deadline.HasExpired)
        {
            var timeoutException = deadline.CreateTimeoutException(exception);
            AppSessionLog.WriteError(
                openCoreShell
                    ? "Sunder Core Shell startup timed out."
                    : "Sunder startup timed out; opening the Core Shell.",
                timeoutException
            );
            throw timeoutException;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError(
                openCoreShell
                    ? "Sunder Core Shell startup failed."
                    : "Sunder startup failed; opening the Core Shell.",
                exception
            );
            throw;
        }
    }

    internal static async Task PublishStartupFailureAsync(
        IPackageNotificationService notificationService,
        Exception startupFailure,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(notificationService);
        ArgumentNullException.ThrowIfNull(startupFailure);
        try
        {
            await notificationService.PublishAsync(
                new PackageNotificationRequest(
                    "Sunder startup failed",
                    startupFailure.Message,
                    PackageNotificationDisplayMode.ToastAndTray,
                    PackageNotificationSeverity.Error
                ),
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError(
                "Failed to publish the Sunder startup error notification.",
                exception
            );
        }
    }

    private async Task CompleteStartupAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        LoadingWindow loadingWindow,
        LoadingWindowViewModel loadingViewModel,
        bool openCoreShell,
        StartupAttemptDeadline startupAttempt
    )
    {
        var cancellationToken = startupAttempt.Token;
        var startupCoordinator = (
            _serviceProvider
            ?? throw new InvalidOperationException("App services are not initialized.")
        ).GetRequiredService<ShellStartupCoordinator>();
        ShellStartupResult? startup = null;
        try
        {
            startup = await startupCoordinator.StartAsync(
                Program.StartupOptions,
                loadingViewModel,
                startupAttempt,
                openCoreShell
            );
            startupAttempt.EnterPhase(StartupPhase.InitialViewActivation);
            await startup.PrepareForRevealAsync(loadingWindow, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _shutdownWindow = startup.MainWindow;
            startup.MainWindow.Closing += OnDesktopMainWindowClosing;
            startupAttempt.EnterPhase(StartupPhase.Reveal);
            await startup.RevealAsync(
                desktop,
                loadingWindow,
                () =>
                {
                    if (!startupAttempt.TryCommit())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException(cancellationToken);
                    }
                },
                cancellationToken);

            var serviceProvider =
                Interlocked.Exchange(ref _serviceProvider, null)
                ?? throw new InvalidOperationException("App services are not initialized.");
            var session = startup.TransferOwnership(serviceProvider);
            _session = session;
            startup = null;

            try
            {
                session.AcceptSingleInstanceCoordinator(Program.SingleInstanceCoordinator);
                session.StartPostRevealWork(openCoreShell, Program.StartupOptions.LaunchRequest);
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to schedule post-startup work.", ex);
            }
        }
        finally
        {
            if (startup is not null)
            {
                _shutdownWindow = loadingWindow;
                await startup.DisposeAsync();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (loadingWindow.IsVisible)
                    {
                        desktop.MainWindow = loadingWindow;
                    }
                });
            }
        }
    }

    private void AboutSunderMenuItem_OnClick(object? sender, EventArgs e) =>
        _session?.ShowAboutSunderWindow();

    private void SettingsMenuItem_OnClick(object? sender, EventArgs e) => _session?.ShowSettings();

    private void StacksMenuItem_OnClick(object? sender, EventArgs e) => _session?.ShowStacks();

    private void PackagesMenuItem_OnClick(object? sender, EventArgs e) => _session?.ShowPackages();

    private void RegisterExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppSessionLog.WriteError("Unhandled UI exception.", e.Exception);
            if (_session?.TryHandleUnhandledException(e.Exception) == true)
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

    private void OnDesktopMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowWindowClose || !ReferenceEquals(sender, _shutdownWindow))
        {
            return;
        }

        e.Cancel = true;
        if (OperatingSystem.IsMacOS() && sender is MainWindow)
        {
            return;
        }

        if (_desktopLifetime is { } desktop)
        {
            RequestDesktopShutdown(desktop);
        }
    }

    private void OnMacOsActivated(object? sender, ActivatedEventArgs e)
    {
        if (
            e.Kind == ActivationKind.Reopen
            && _session is { } session
            && Volatile.Read(ref _desktopShutdownTask) is null
        )
        {
            session.WindowLauncher.ActivateMainWindow();
        }
    }

    private void OnDesktopShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_allowDesktopShutdown)
        {
            return;
        }

        e.Cancel = true;
        if (sender is IClassicDesktopStyleApplicationLifetime desktop)
        {
            RequestDesktopShutdown(desktop);
            return;
        }
        if (_desktopLifetime is { } currentDesktop)
        {
            RequestDesktopShutdown(currentDesktop);
        }
    }

    private void RequestDesktopShutdown(
        IClassicDesktopStyleApplicationLifetime desktop,
        int exitCode = 0
    )
    {
        if (exitCode != 0)
        {
            Interlocked.Exchange(ref _requestedExitCode, exitCode);
        }

        var shutdownTask = _shutdown.ShutdownAsync(ShutdownCoreAsync);
        if (Interlocked.CompareExchange(ref _desktopShutdownTask, shutdownTask, null) is not null)
        {
            return;
        }

        shutdownTask.ContinueWith(
            completed =>
                Dispatcher.UIThread.Post(() => CompleteDesktopShutdown(desktop, completed)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private void CompleteDesktopShutdown(
        IClassicDesktopStyleApplicationLifetime desktop,
        Task shutdownTask
    )
    {
        var exitCode = Volatile.Read(ref _requestedExitCode);
        if (shutdownTask.Exception is { } exception)
        {
            exitCode = 1;
            AppSessionLog.WriteError("Sunder shutdown failed.", exception.Flatten());
        }

        _allowDesktopShutdown = true;
        _allowWindowClose = true;
        desktop.Shutdown(exitCode);
    }

    private async Task ShutdownCoreAsync()
    {
        _allowWindowClose = true;
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            var serviceProvider = Interlocked.Exchange(ref _serviceProvider, null);
            if (serviceProvider is not null && _shutdown.OwnedTasksDrained)
            {
                await BoundedCleanup
                    .RunAsync(
                        "disposing application services after incomplete startup",
                        async () => await serviceProvider.DisposeAsync().ConfigureAwait(false),
                        AppShutdownBudgets.ServiceProvider
                    )
                    .ConfigureAwait(false);
            }
            else if (serviceProvider is not null)
            {
                AppCleanupQuarantine.Retain(
                    serviceProvider,
                    "Startup work did not stop before application shutdown."
                );
                AppSessionLog.WriteError(
                    "Application services were quarantined because startup work did not stop within its shutdown budget."
                );
            }

            try
            {
                Program.SingleInstanceCoordinator?.Dispose();
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError(
                    "Failed to dispose single-instance coordination during shutdown.",
                    ex
                );
            }
        }

        _tasks.Dispose();
        await BoundedCleanup
            .RunAsync(
                "flushing the application session log",
                () => AppSessionLog.FlushAsync(),
                AppShutdownBudgets.LogFlush
            )
            .ConfigureAwait(false);
    }
}
