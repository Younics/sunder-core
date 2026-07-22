using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class ShellSession : IAsyncDisposable
{
    private readonly AppPackageSettingsNavigationService _settingsNavigationService;
    private readonly PackageUpdateStartupCheckService _packageUpdateStartupCheckService;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly InitialWindowReveal _initialWindowReveal;
    private readonly InitialShellRenderWaiter _initialShellRenderWaiter;
    private readonly ShellUiWorkScheduler _shellUiWorkScheduler;
    private readonly DeveloperLogService? _developerLog;
    private readonly OwnedTaskObserver _tasks = new(nameof(ShellSession));
    private ServiceProvider? _serviceProvider;
    private AppSingleInstanceCoordinator? _singleInstanceCoordinator;
    private AboutSunderWindow? _aboutSunderWindow;
    private int _disposed;

    internal ShellSession(
        MainWindow mainWindow,
        MainWindowViewModel mainWindowViewModel,
        WindowLauncher windowLauncher,
        RuntimeEventSubscriptionService? runtimeEventSubscription,
        AppPackageSettingsNavigationService settingsNavigationService,
        PackageViewHostService packageViewHostService,
        PackageUpdateStartupCheckService packageUpdateStartupCheckService,
        IUiDispatcher uiDispatcher,
        InitialWindowReveal? initialWindowReveal = null,
        InitialShellRenderWaiter? initialShellRenderWaiter = null,
        DeveloperLogService? developerLog = null
    )
    {
        MainWindow = mainWindow;
        MainWindowViewModel = mainWindowViewModel;
        WindowLauncher = windowLauncher;
        RuntimeEventSubscription = runtimeEventSubscription;
        _settingsNavigationService = settingsNavigationService;
        PackageViewHostService = packageViewHostService;
        _packageUpdateStartupCheckService = packageUpdateStartupCheckService;
        _uiDispatcher = uiDispatcher;
        _initialWindowReveal = initialWindowReveal ?? new InitialWindowReveal();
        _initialShellRenderWaiter =
            initialShellRenderWaiter ?? new InitialShellRenderWaiter(uiDispatcher);
        _shellUiWorkScheduler = new ShellUiWorkScheduler();
        _developerLog = developerLog;
    }

    internal MainWindow MainWindow { get; }

    internal MainWindowViewModel MainWindowViewModel { get; }

    internal WindowLauncher WindowLauncher { get; }

    internal RuntimeEventSubscriptionService? RuntimeEventSubscription { get; }

    internal PackageViewHostService PackageViewHostService { get; }

    internal void AcceptServiceProvider(ServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _serviceProvider, serviceProvider, null) is not null)
        {
            throw new InvalidOperationException(
                "The shell session already owns an application service provider."
            );
        }
    }

    internal void AcceptSingleInstanceCoordinator(AppSingleInstanceCoordinator? coordinator)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _singleInstanceCoordinator = coordinator;
        coordinator?.SetLaunchRequestHandler(HandleForwardedLaunchRequestAsync);
    }

    internal Task StartRuntimeSubscriptionAsync(
        RuntimePackageSnapshot initialSnapshot,
        CancellationToken cancellationToken
    ) =>
        RuntimeEventSubscription?.StartAsync(
            WindowLauncher,
            initialSnapshot,
            cancellationToken
        ) ?? Task.CompletedTask;

    internal async Task PrepareForRevealAsync(
        LoadingWindow loadingWindow,
        CancellationToken cancellationToken
    )
    {
        await _uiDispatcher
            .InvokeAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _initialWindowReveal.ShowConcealed(MainWindow, loadingWindow);
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        await MainWindowViewModel
            .ActivateDeferredInitialHostedViewsAsync(
                () =>
                    _initialShellRenderWaiter.WaitForInitialViewsReadyAsync(
                        MainWindow,
                        cancellationToken
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);
        await _initialShellRenderWaiter
            .WaitForRenderedAsync(MainWindow, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task RevealAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        LoadingWindow loadingWindow,
        CancellationToken cancellationToken
    )
    {
        await _uiDispatcher
            .InvokeAsync(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MainWindowViewModel.CompleteInitialReveal();
                    _initialWindowReveal.Reveal(
                        MainWindow,
                        loadingWindow,
                        () => desktop.MainWindow = MainWindow,
                        ReleaseRuntimePresentation
                    );
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        LogStartupVisibleTiming();
    }

    private void ReleaseRuntimePresentation()
    {
        try
        {
            RuntimeEventSubscription?.ReleasePresentation();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to release Runtime presentation after shell reveal.", ex);
        }
    }

    private static void LogStartupVisibleTiming()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var processElapsed = DateTimeOffset.Now - process.StartTime;
            var startupElapsed = Stopwatch.GetElapsedTime(Program.StartupTimestamp);
            AppSessionLog.WriteInfo(
                $"Sunder main window became visible after {processElapsed.TotalMilliseconds:0} ms process time and {startupElapsed.TotalMilliseconds:0} ms startup time."
            );
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to record shell visible timing.", ex);
        }
    }

    internal void StartPostRevealWork(bool openCoreShell, AppLaunchRequest initialLaunchRequest)
    {
        MainWindowViewModel.StartPackageViewPreloading(
            _shellUiWorkScheduler.RunBelowInputPriorityAsync,
            _shellUiWorkScheduler.RunBelowInputPriorityAsync);
        var developerLog = _developerLog;
        if (developerLog is not null
            && ShouldStreamPackageLogs(openCoreShell, developerLog.IsEnabled))
        {
            developerLog.StartPackageLogStreaming();
        }
        _tasks.Run(
            cancellationToken => PackageViewHostService.CollectContentCacheGarbageAsync(cancellationToken),
            "cleaning the App package content cache"
        );
        _tasks.Observe(
            MainWindowViewModel.CheckForAppUpdatesOnStartupAsync(),
            "checking for app updates"
        );
        if (!openCoreShell)
        {
            try
            {
                _packageUpdateStartupCheckService.EnqueueStartupCheck();
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Failed to enqueue the startup package update check.", ex);
            }
        }

        if (initialLaunchRequest.Kind != AppLaunchRequestKind.None)
        {
            _tasks.Run(
                cancellationToken =>
                    WindowLauncher.HandleLaunchRequestAsync(
                        initialLaunchRequest,
                        cancellationToken
                    ),
                "handling the initial launch request"
            );
        }
    }

    internal static bool ShouldStreamPackageLogs(bool openCoreShell, bool developerLogEnabled)
        => !openCoreShell && developerLogEnabled;

    internal bool TryHandleUnhandledException(Exception exception) =>
        Volatile.Read(ref _disposed) == 0
        && PackageViewHostService.TryHandleUnhandledException(exception);

    internal void ShowSettings() => WindowLauncher.ShowSettings();

    internal void ShowStacks() => WindowLauncher.ShowStacks();

    internal void ShowPackages() => WindowLauncher.ShowPackages();

    internal void ShowAboutSunderWindow()
    {
        if (_aboutSunderWindow is not null)
        {
            _aboutSunderWindow.Activate();
            return;
        }

        _aboutSunderWindow = new AboutSunderWindow();
        _aboutSunderWindow.Closed += AboutSunderWindow_OnClosed;
        if (MainWindow.IsVisible)
        {
            _aboutSunderWindow.Show(MainWindow);
        }
        else
        {
            _aboutSunderWindow.Show();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var ownedTasks = StartCleanup(_tasks.StopAsync);
        if (
            !await BoundedCleanup
                .RunAsync(
                    "stopping shell-owned tasks",
                    () => ownedTasks,
                    AppShutdownBudgets.OwnedTasks
                )
                .ConfigureAwait(false)
        )
        {
            DeferRemainingCleanup(ownedTasks, DisposeRuntimeSubscriptionAsync);
            return;
        }

        await DisposeRuntimeSubscriptionAsync().ConfigureAwait(false);
    }

    private async Task DisposeRuntimeSubscriptionAsync()
    {
        if (RuntimeEventSubscription is not null)
        {
            var subscription = StartCleanup(() => RuntimeEventSubscription.DisposeAsync().AsTask());
            if (
                !await BoundedCleanup
                    .RunAsync(
                        "stopping the Runtime event subscription",
                        () => subscription,
                        AppShutdownBudgets.RuntimeSubscription
                    )
                    .ConfigureAwait(false)
            )
            {
                DeferRemainingCleanup(subscription, DisposeBackgroundProcessesAsync);
                return;
            }
        }

        await DisposeBackgroundProcessesAsync().ConfigureAwait(false);
    }

    private async Task DisposeBackgroundProcessesAsync()
    {
        var backgroundProcesses = StartCleanup(() =>
            WindowLauncher.CancelBackgroundProcessesAsync()
        );
        if (
            !await BoundedCleanup
                .RunAsync(
                    "cancelling shell background processes",
                    () => backgroundProcesses,
                    AppShutdownBudgets.BackgroundProcesses
                )
                .ConfigureAwait(false)
        )
        {
            DeferRemainingCleanup(backgroundProcesses, DisposePresentationAsync);
            return;
        }

        await DisposePresentationAsync().ConfigureAwait(false);
    }

    private async Task DisposePresentationAsync()
    {
        var presentation = StartCleanup(() => _uiDispatcher.InvokeAsync(ClosePresentation));
        if (
            !await BoundedCleanup
                .RunAsync(
                    "closing shell windows and detaching presentation",
                    () => presentation,
                    AppShutdownBudgets.UiCleanup
                )
                .ConfigureAwait(false)
        )
        {
            DeferRemainingCleanup(presentation, DisposePackageHostAsync);
            return;
        }

        await DisposePackageHostAsync().ConfigureAwait(false);
    }

    private async Task DisposePackageHostAsync()
    {
        var packageHost = StartCleanup(() => PackageViewHostService.DisposeAsync().AsTask());
        if (
            !await BoundedCleanup
                .RunAsync(
                    "disposing the App package host",
                    () => packageHost,
                    AppShutdownBudgets.PackageHost
                )
                .ConfigureAwait(false)
        )
        {
            DeferRemainingCleanup(packageHost, DisposeServiceProviderAsync);
            return;
        }

        await DisposeServiceProviderAsync().ConfigureAwait(false);
    }

    private async Task DisposeServiceProviderAsync()
    {
        var serviceProvider = Interlocked.Exchange(ref _serviceProvider, null);
        if (serviceProvider is not null)
        {
            await BoundedCleanup
                .RunAsync(
                    "disposing application services",
                    async () => await serviceProvider.DisposeAsync().ConfigureAwait(false),
                    AppShutdownBudgets.ServiceProvider
                )
                .ConfigureAwait(false);
        }

        CompleteCleanup();
    }

    private void CompleteCleanup()
    {
        try
        {
            _singleInstanceCoordinator?.Dispose();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError(
                "Failed to dispose single-instance coordination during shutdown.",
                ex
            );
        }
        _singleInstanceCoordinator = null;
        _tasks.Dispose();
        GC.SuppressFinalize(this);
    }

    private void DeferRemainingCleanup(Task blockedCleanup, Func<Task> continueCleanup)
    {
        var deferredCleanup = ContinueCleanupAsync(blockedCleanup, continueCleanup);
        AppCleanupQuarantine.Retain(
            deferredCleanup,
            "completing ordered shell cleanup after an earlier budget expired"
        );
    }

    private static async Task ContinueCleanupAsync(Task blockedCleanup, Func<Task> continueCleanup)
    {
        try
        {
            await blockedCleanup.ConfigureAwait(false);
        }
        catch
        {
            // The bounded step already logged the failure; ordering can continue once it is terminal.
        }

        await continueCleanup().ConfigureAwait(false);
    }

    private static Task StartCleanup(Func<Task> cleanup)
    {
        try
        {
            return cleanup();
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private Task HandleForwardedLaunchRequestAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken
    )
    {
        if (_uiDispatcher.CheckAccess())
        {
            return WindowLauncher.HandleLaunchRequestAsync(request, cancellationToken);
        }

        return _uiDispatcher.InvokeAsync(async () =>
        {
            await WindowLauncher.HandleLaunchRequestAsync(request, cancellationToken);
            return true;
        });
    }

    private void ClosePresentation()
    {
        _settingsNavigationService.Detach(WindowLauncher);
        MainWindowViewModel.Dispose();
        try
        {
            MainWindow.Close();
        }
        finally
        {
            MainWindow.DataContext = null;
        }

        WindowLauncher.CloseForShutdown();
        if (_aboutSunderWindow is not null)
        {
            _aboutSunderWindow.Closed -= AboutSunderWindow_OnClosed;
            _aboutSunderWindow.Close();
            _aboutSunderWindow = null;
        }
    }

    private void AboutSunderWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_aboutSunderWindow is not null)
        {
            _aboutSunderWindow.Closed -= AboutSunderWindow_OnClosed;
            _aboutSunderWindow = null;
        }
    }
}
