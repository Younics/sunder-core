using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Composition;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed class ShellStartupResult : IAsyncDisposable
{
    private ShellSession? _session;
    private DevPackageOwnerSession? _devPackageOwnerSession;

    internal ShellStartupResult(
        ShellSession session,
        DevPackageOwnerSession? devPackageOwnerSession
    )
    {
        _session = session;
        _devPackageOwnerSession = devPackageOwnerSession;
    }

    internal MainWindow MainWindow => GetSession().MainWindow;

    internal Task PrepareForRevealAsync(
        LoadingWindow loadingWindow,
        CancellationToken cancellationToken
    ) => GetSession().PrepareForRevealAsync(loadingWindow, cancellationToken);

    internal Task RevealAsync(
        Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop,
        LoadingWindow loadingWindow,
        CancellationToken cancellationToken
    ) => GetSession().RevealAsync(desktop, loadingWindow, cancellationToken);

    internal Task StartRuntimeSubscriptionAsync(
        RuntimePackageSnapshot initialSnapshot,
        CancellationToken cancellationToken
    ) =>
        GetSession()
            .StartRuntimeSubscriptionAsync(initialSnapshot, cancellationToken);

    internal ShellSession TransferOwnership(ServiceProvider serviceProvider)
    {
        var session = GetSession();
        session.AcceptServiceProvider(serviceProvider);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session))
        {
            throw new InvalidOperationException("Shell startup ownership was already transferred.");
        }
        _devPackageOwnerSession = null;
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        var devPackageOwnerSession = Interlocked.Exchange(ref _devPackageOwnerSession, null);
        if (devPackageOwnerSession is not null)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await devPackageOwnerSession.ReleaseAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppSessionLog.WriteError("Failed to release the App dev package owner from incomplete shell startup.", exception);
            }
        }
    }

    private ShellSession GetSession() =>
        Volatile.Read(ref _session)
        ?? throw new InvalidOperationException("Shell startup ownership was already transferred.");
}

public sealed class ShellStartupCoordinator
{
    private readonly ShellStateService _shellStateService;
    private readonly ShellState _shellState;
    private readonly RuntimeConnectionState _runtimeConnectionState;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory;
    private readonly RuntimeHostProcessManager _runtimeHostProcessManager;
    private readonly DevPackageOwnerSession _devPackageOwnerSession;
    private readonly NotificationCenterService _notificationCenter;
    private readonly DeveloperLogService _developerLog;
    private readonly RuntimeEventSubscriptionServiceFactory _runtimeEventSubscriptionServiceFactory;
    private readonly CliInstallationService _cliInstallationService;
    private readonly SunderUpdateService _updateService;
    private readonly PackageUpdateStartupCheckService _packageUpdateStartupCheckService;
    private readonly IThemeManager _themeManager;
    private readonly AppPackageSettingsNavigationService _settingsNavigationService;
    private readonly IShellCompositionService _shellCompositionService;
    private readonly PackageViewHostServiceFactory _packageViewHostServiceFactory;
    private readonly WindowLauncherFactory _windowLauncherFactory;
    private readonly MainWindowFactory _mainWindowFactory;
    private readonly IUiDispatcher _uiDispatcher;

    public ShellStartupCoordinator(
        ShellStateService shellStateService,
        ShellState shellState,
        RuntimeConnectionState runtimeConnectionState,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        RuntimeHostProcessManager runtimeHostProcessManager,
        DevPackageOwnerSession devPackageOwnerSession,
        NotificationCenterService notificationCenter,
        DeveloperLogService developerLog,
        RuntimeEventSubscriptionServiceFactory runtimeEventSubscriptionServiceFactory,
        CliInstallationService cliInstallationService,
        SunderUpdateService updateService,
        PackageUpdateStartupCheckService packageUpdateStartupCheckService,
        IThemeManager themeManager,
        AppPackageSettingsNavigationService settingsNavigationService,
        IShellCompositionService shellCompositionService,
        PackageViewHostServiceFactory packageViewHostServiceFactory,
        WindowLauncherFactory windowLauncherFactory,
        MainWindowFactory mainWindowFactory,
        IUiDispatcher uiDispatcher
    )
    {
        _shellStateService = shellStateService;
        _shellState = shellState;
        _runtimeConnectionState = runtimeConnectionState;
        _runtimeApiClientFactory = runtimeApiClientFactory;
        _runtimeHostProcessManager = runtimeHostProcessManager;
        _devPackageOwnerSession = devPackageOwnerSession;
        _notificationCenter = notificationCenter;
        _developerLog = developerLog;
        _runtimeEventSubscriptionServiceFactory = runtimeEventSubscriptionServiceFactory;
        _cliInstallationService = cliInstallationService;
        _updateService = updateService;
        _packageUpdateStartupCheckService = packageUpdateStartupCheckService;
        _themeManager = themeManager;
        _settingsNavigationService = settingsNavigationService;
        _shellCompositionService = shellCompositionService;
        _packageViewHostServiceFactory = packageViewHostServiceFactory;
        _windowLauncherFactory = windowLauncherFactory;
        _mainWindowFactory = mainWindowFactory;
        _uiDispatcher = uiDispatcher;
    }

    public async Task<ShellStartupResult> StartAsync(
        AppStartupOptions startupOptions,
        LoadingWindowViewModel loadingViewModel,
        bool openCoreShell = false,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await StartCoreAsync(
                    startupOptions,
                    loadingViewModel,
                    openCoreShell,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _devPackageOwnerSession.ReleaseAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppSessionLog.WriteError(
                    "Failed to release the App dev package owner after startup failure.",
                    exception
                );
            }
            throw;
        }
    }

    private async Task<ShellStartupResult> StartCoreAsync(
        AppStartupOptions startupOptions,
        LoadingWindowViewModel loadingViewModel,
        bool openCoreShell,
        CancellationToken cancellationToken
    )
    {
        ValidateStartupOptions(startupOptions, openCoreShell);
        cancellationToken.ThrowIfCancellationRequested();
        var shellStateService = _shellStateService;
        var shellState = _shellState;
        IReadOnlyList<ActivePackageDescriptor> activePackages = [];
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources = [];
        RuntimePackageSnapshot? runtimePackageSnapshot = null;
        var warnings = new List<string>();
        var errors = startupOptions.ParseErrors.ToList();
        SystemStatusResponse? systemStatus = null;
        var runtimeUrl = ResolveRuntimeUrl(startupOptions, shellState, warnings);
        var runtimeConnectionState = _runtimeConnectionState;
        runtimeConnectionState.RuntimeUrl = runtimeUrl;
        var runtimeApiClientFactory = _runtimeApiClientFactory;
        var runtimeHostProcessManager = _runtimeHostProcessManager;
        var notificationCenter = _notificationCenter;
        var developerLog = _developerLog;
        var cliInstallationService = _cliInstallationService;
        var updateService = _updateService;
        var startupStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        await SetProgressAsync(loadingViewModel, "Loading theme...", 96, cancellationToken);

        var themeManager = await _uiDispatcher.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manager = _themeManager;
                manager.Initialize();
                manager.ApplyTheme(shellState.ThemeId);
                return manager;
            },
            cancellationToken
        );
        LogStartupPhase("theme", phaseStopwatch);

        await SetProgressAsync(loadingViewModel, "Checking CLI...", 136, cancellationToken);
        await EnsureCliInstalledForStartupAsync(
                cliInstallationService,
                notificationCenter,
                warnings,
                cancellationToken
            )
            .ConfigureAwait(false);
        LogStartupPhase("cli", phaseStopwatch);

        if (!openCoreShell)
        {
            if (errors.Count == 0)
            {
                try
                {
                    await SetProgressAsync(
                            loadingViewModel,
                            "Starting runtime...",
                            176,
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                    await runtimeHostProcessManager
                        .EnsureStartedAsync(runtimeConnectionState.RuntimeUrl, cancellationToken)
                        .ConfigureAwait(false);

                    using var runtimeApiClient =
                        runtimeApiClientFactory.CreateClient<IRuntimeStartupClient>();
                    await SetProgressAsync(
                            loadingViewModel,
                            startupOptions.DevPackageFolders.Count > 0
                                ? "Loading dev packages..."
                                : "Loading active packages...",
                            248,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    if (startupOptions.DevPackageFolders.Count > 0)
                    {
                        await _devPackageOwnerSession
                            .AcquireAsync(
                                startupOptions.DevPackageFolders,
                                startupOptions.WatchDevPackages,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                    runtimePackageSnapshot = await WaitForReadyRuntimeSnapshotAsync(
                            runtimeApiClient,
                            cancellationToken: cancellationToken
                        )
                        .ConfigureAwait(false);
                    systemStatus = await runtimeApiClient
                        .GetSystemStatusAsync(cancellationToken)
                        .ConfigureAwait(false);
                    activePackages = runtimePackageSnapshot.ActivePackages;
                    packageSources = runtimePackageSnapshot.PackageUiSnapshots;
                    warnings.AddRange(runtimePackageSnapshot.Warnings);
                    errors.AddRange(runtimePackageSnapshot.Errors);
                    LogStartupPhase("runtime package snapshot", phaseStopwatch);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "Sunder Runtime failed to initialize.",
                        exception
                    );
                }
            }
        }
        else
        {
            warnings.Add("Sunder opened the Core Shell without Runtime packages.");
        }

        await SetProgressAsync(loadingViewModel, "Composing shell...", 318, cancellationToken)
            .ConfigureAwait(false);

        PackageViewHostService packageViewHostService;
        var settingsNavigationService = _settingsNavigationService;
        try
        {
            packageViewHostService = await _packageViewHostServiceFactory
                .CreateForPackagesAsync(activePackages, packageSources, cancellationToken)
                .ConfigureAwait(false);
            activePackages = packageViewHostService.FilterEnabledPackages(activePackages);
            LogStartupPhase("app package activation", phaseStopwatch);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to create the app package host service.", ex);
            throw new InvalidOperationException(
                "Sunder could not activate App package modules.",
                ex
            );
        }

        ShellStartupResult? result = null;
        RuntimeEventSubscriptionService? runtimeEventSubscription = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shellSnapshot = _shellCompositionService.Compose(
                activePackages,
                shellState,
                systemStatus,
                warnings,
                errors,
                runtimePackageSnapshot is null
                    ? ShellNormalizationPolicy.SafeModePreserveLayout
                    : ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot
            );
            cancellationToken.ThrowIfCancellationRequested();
            if (runtimePackageSnapshot is not null)
            {
                await packageViewHostService
                    .PrewarmPackageIconsAsync(
                        runtimePackageSnapshot,
                        activePackages,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            LogStartupPhase("shell composition", phaseStopwatch);
            if (startupOptions.DevPackageFolders.Count > 0)
            {
                developerLog.Enable(startPackageLogStreaming: false);
            }

            runtimeEventSubscription = runtimePackageSnapshot is null
                ? null
                : _runtimeEventSubscriptionServiceFactory.Create();
            result = await _uiDispatcher
                .InvokeAsync(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        themeManager.ApplyTheme(shellSnapshot.State.ThemeId);

                        var windowLauncher = _windowLauncherFactory.Create(
                            packageViewHostService,
                            runtimeEventSubscription
                        );
                        MainWindow? mainWindow = null;
                        MainWindowViewModel? mainWindowViewModel = null;
                        try
                        {
                            settingsNavigationService.Attach(windowLauncher);
                            (mainWindow, mainWindowViewModel) = _mainWindowFactory.Create(
                                windowLauncher,
                                shellSnapshot,
                                packageViewHostService,
                                systemStatus,
                                deferInitialHostedViews: true
                            );
                            cancellationToken.ThrowIfCancellationRequested();

                            return new ShellStartupResult(
                                new ShellSession(
                                    mainWindow,
                                    mainWindowViewModel,
                                    windowLauncher,
                                    runtimeEventSubscription,
                                    settingsNavigationService,
                                    packageViewHostService,
                                    _packageUpdateStartupCheckService,
                                    _uiDispatcher,
                                    developerLog: developerLog
                                ),
                                startupOptions.DevPackageFolders.Count > 0
                                    ? _devPackageOwnerSession
                                    : null
                            );
                        }
                        catch
                        {
                            settingsNavigationService.Detach(windowLauncher);
                            mainWindowViewModel?.Dispose();
                            mainWindow?.Close();
                            windowLauncher.CloseForShutdown();
                            throw;
                        }
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            LogStartupPhase("main window creation", phaseStopwatch);
            if (startupOptions.DevPackageFolders.Count > 0)
            {
                developerLog.Info(
                    "dev",
                    $"Developer mode enabled for {startupOptions.DevPackageFolders.Count} dev package folder(s)."
                );
            }

            if (runtimePackageSnapshot is not null)
            {
                await result
                    .StartRuntimeSubscriptionAsync(
                        runtimePackageSnapshot,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AppSessionLog.WriteInfo(
                $"Sunder startup composition completed in {startupStopwatch.ElapsedMilliseconds} ms."
            );
            return result;
        }
        catch
        {
            if (result is not null)
            {
                await result.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                if (runtimeEventSubscription is not null)
                {
                    try
                    {
                        await runtimeEventSubscription.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppSessionLog.WriteError(
                            "Failed to dispose an incomplete startup Runtime event subscription.",
                            ex
                        );
                    }
                }

                try
                {
                    await packageViewHostService.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppSessionLog.WriteError(
                        "Failed to dispose an incomplete startup package view host.",
                        ex
                    );
                }
            }
            throw;
        }
    }

    internal static bool ShouldCheckPackageUpdates(bool openCoreShell) => !openCoreShell;

    internal static async Task<RuntimePackageSnapshot> WaitForReadyRuntimeSnapshotAsync(
        IRuntimeSnapshotClient runtimeApiClient,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        int maxAttempts = 600,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(runtimeApiClient);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        delayAsync ??= Task.Delay;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await runtimeApiClient
                .GetRuntimePackageSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            switch (snapshot.BootstrapState)
            {
                case RuntimeBootstrapState.Ready:
                    if (snapshot.RuntimeInstanceId == Guid.Empty)
                    {
                        throw new InvalidOperationException(
                            "Runtime package snapshot did not provide a valid instance id."
                        );
                    }
                    return snapshot;
                case RuntimeBootstrapState.Failed:
                    throw RuntimeEventSubscriptionService.CreateBootstrapFailure(snapshot);
                case RuntimeBootstrapState.ShuttingDown:
                    throw new InvalidOperationException(
                        "Runtime shut down before package bootstrap completed."
                    );
                case RuntimeBootstrapState.Starting:
                    if (attempt + 1 < maxAttempts)
                    {
                        await delayAsync(TimeSpan.FromMilliseconds(100), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Runtime returned unsupported bootstrap state '{snapshot.BootstrapState}'."
                    );
            }
        }

        throw new TimeoutException("Runtime package bootstrap did not become ready in time.");
    }

    private async Task SetProgressAsync(
        LoadingWindowViewModel loadingViewModel,
        string statusMessage,
        double progressWidth,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_uiDispatcher.CheckAccess())
        {
            ApplyProgress(loadingViewModel, statusMessage, progressWidth);
            return;
        }

        await _uiDispatcher
            .InvokeAsync(
                () => ApplyProgress(loadingViewModel, statusMessage, progressWidth),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static void ApplyProgress(
        LoadingWindowViewModel loadingViewModel,
        string statusMessage,
        double progressWidth
    )
    {
        loadingViewModel.StatusMessage = statusMessage;
        loadingViewModel.ProgressWidth = progressWidth;
    }

    private void LogStartupPhase(string phaseName, Stopwatch phaseStopwatch)
    {
        AppSessionLog.WriteInfo(
            $"Sunder startup phase '{phaseName}' completed in {phaseStopwatch.ElapsedMilliseconds} ms. UI thread: {_uiDispatcher.CheckAccess()}."
        );
        phaseStopwatch.Restart();
    }

    private static async Task EnsureCliInstalledForStartupAsync(
        CliInstallationService cliInstallationService,
        NotificationCenterService notificationCenter,
        ICollection<string> warnings,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await cliInstallationService
                .EnsureInstalledAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!CliStartupNotificationPolicy.TryCreateWarning(result, out var warning))
            {
                return;
            }

            warnings.Add($"CLI: {warning}");
            await notificationCenter
                .PublishAsync(
                    "sunder.app",
                    "Sunder",
                    new PackageNotificationRequest(
                        "Sunder CLI needs attention",
                        warning,
                        PackageNotificationDisplayMode.TrayOnly,
                        PackageNotificationSeverity.Warning
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppSessionLog.WriteError("Failed to install or repair the Sunder CLI.", ex);
            warnings.Add($"CLI: {ex.Message}");
            await notificationCenter
                .PublishAsync(
                    "sunder.app",
                    "Sunder",
                    new PackageNotificationRequest(
                        "Sunder CLI install failed",
                        ex.Message,
                        PackageNotificationDisplayMode.TrayOnly,
                        PackageNotificationSeverity.Warning
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    internal static void ValidateStartupOptions(
        AppStartupOptions startupOptions,
        bool openCoreShell
    )
    {
        ArgumentNullException.ThrowIfNull(startupOptions);
        if (openCoreShell || startupOptions.ParseErrors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Sunder could not use the startup arguments: {string.Join(" ", startupOptions.ParseErrors)}"
        );
    }

    private static Uri ResolveRuntimeUrl(
        AppStartupOptions startupOptions,
        ShellState shellState,
        ICollection<string> warnings
    )
    {
        if (startupOptions.HasExplicitRuntimeUrl)
        {
            return startupOptions.RuntimeUrl;
        }

        if (
            RuntimeUrlHelper.TryParse(shellState.PreferredRuntimeUrl, out var preferredRuntimeUrl)
            && preferredRuntimeUrl is not null
        )
        {
            return preferredRuntimeUrl;
        }

        if (!string.IsNullOrWhiteSpace(shellState.PreferredRuntimeUrl))
        {
            warnings.Add(
                $"Saved runtime URL '{shellState.PreferredRuntimeUrl}' is invalid, so the default runtime address is being used."
            );
        }

        return startupOptions.RuntimeUrl;
    }
}
