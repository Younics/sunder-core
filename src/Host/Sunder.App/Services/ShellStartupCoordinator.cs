using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Composition;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

internal sealed record RuntimePackageStartupResult(
    RuntimePackageSnapshot Snapshot,
    bool DevPackageOwnerAcquired,
    string? Warning,
    Exception? DevPackageFailure);

internal sealed record RuntimePackageUiStartupResult(
    RuntimePackageSnapshot Snapshot,
    IReadOnlyList<PackageUiSnapshotDescriptor> PackageSources);

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
        Action commitStartup,
        CancellationToken cancellationToken
    ) => GetSession().RevealAsync(desktop, loadingWindow, commitStartup, cancellationToken);

    internal Task StartRuntimeSubscriptionAsync(
        RuntimePackageSnapshot initialSnapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> initialPackageSources,
        CancellationToken cancellationToken
    ) =>
        GetSession()
            .StartRuntimeSubscriptionAsync(initialSnapshot, initialPackageSources, cancellationToken);

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

    internal async Task<ShellStartupResult> StartAsync(
        AppStartupOptions startupOptions,
        LoadingWindowViewModel loadingViewModel,
        StartupAttemptDeadline startupAttempt,
        bool openCoreShell = false
    )
    {
        ArgumentNullException.ThrowIfNull(startupAttempt);
        try
        {
            return await StartCoreAsync(
                    startupOptions,
                    loadingViewModel,
                    openCoreShell,
                    startupAttempt
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
        StartupAttemptDeadline startupAttempt
    )
    {
        var cancellationToken = startupAttempt.Token;
        ValidateStartupOptions(startupOptions, openCoreShell);
        cancellationToken.ThrowIfCancellationRequested();
        var shellStateService = _shellStateService;
        var shellState = _shellState;
        IReadOnlyList<ActivePackageDescriptor> activePackages = [];
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources = [];
        RuntimePackageSnapshot? runtimePackageSnapshot = null;
        var devPackageOwnerAcquired = false;
        Guid? devPackageOwnerRuntimeInstanceId = null;
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

        startupAttempt.EnterPhase(StartupPhase.Theme);
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
        LogStartupPhase(StartupPhase.Theme, phaseStopwatch);

        startupAttempt.EnterPhase(StartupPhase.Cli);
        await SetProgressAsync(loadingViewModel, "Checking CLI...", 136, cancellationToken);
        await EnsureCliInstalledForStartupAsync(
                cliInstallationService,
                notificationCenter,
                warnings,
                cancellationToken
            )
            .ConfigureAwait(false);
        LogStartupPhase(StartupPhase.Cli, phaseStopwatch);

        if (!openCoreShell)
        {
            if (errors.Count == 0)
            {
                try
                {
                    startupAttempt.EnterPhase(StartupPhase.RuntimeHost);
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
                    LogStartupPhase(StartupPhase.RuntimeHost, phaseStopwatch);

                    using var runtimeApiClient =
                        runtimeApiClientFactory.CreateClient<IRuntimeStartupClient>();
                    startupAttempt.EnterPhase(StartupPhase.RuntimePackages);
                    await SetProgressAsync(
                            loadingViewModel,
                            startupOptions.DevPackageFolders.Count > 0
                                ? "Loading dev packages..."
                                : "Loading active packages...",
                            248,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    var runtimePackageStartup = await LoadRuntimePackagesForStartupAsync(
                            runtimeApiClient,
                            startupOptions.DevPackageFolders.Count > 0,
                            token => _devPackageOwnerSession.AcquireAsync(
                                startupOptions.DevPackageFolders,
                                startupOptions.WatchDevPackages,
                                token),
                            token => _devPackageOwnerSession.ReleaseAsync(token),
                            cancellationToken)
                        .ConfigureAwait(false);
                    runtimePackageSnapshot = runtimePackageStartup.Snapshot;
                    devPackageOwnerAcquired = runtimePackageStartup.DevPackageOwnerAcquired;
                    devPackageOwnerRuntimeInstanceId = devPackageOwnerAcquired
                        ? runtimePackageSnapshot.RuntimeInstanceId
                        : null;
                    if (runtimePackageStartup.Warning is { } devPackageWarning)
                    {
                        warnings.Add(devPackageWarning);
                        AppSessionLog.WriteError(
                            "Dev package startup failed; Sunder restored the active Runtime package set.",
                            runtimePackageStartup.DevPackageFailure!);
                        await PublishDevPackageWarningAsync(
                                notificationCenter,
                                devPackageWarning,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    var runtimePackageUi = await LoadConsistentPackageUiStateAsync(
                            runtimeApiClient,
                            runtimePackageSnapshot,
                            token => runtimeApiClient.GetActivePackageUiSnapshotsAsync(
                                AppPackageTargetEnvironment.CurrentRid,
                                token),
                            requiredRuntimeInstanceId: devPackageOwnerRuntimeInstanceId,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    runtimePackageSnapshot = runtimePackageUi.Snapshot;
                    packageSources = runtimePackageUi.PackageSources;
                    activePackages = runtimePackageSnapshot.ActivePackages;
                    systemStatus = await runtimeApiClient
                        .GetSystemStatusAsync(cancellationToken)
                        .ConfigureAwait(false);
                    LogStartupPhase(StartupPhase.RuntimePackages, phaseStopwatch);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Sunder Runtime failed to initialize: {exception.Message}",
                        exception
                    );
                }
            }
        }
        else
        {
            warnings.Add("Sunder opened the Core Shell without Runtime packages.");
        }

        startupAttempt.EnterPhase(StartupPhase.AppPackageActivation);
        await SetProgressAsync(loadingViewModel, "Composing shell...", 318, cancellationToken)
            .ConfigureAwait(false);

        PackageViewHostService packageViewHostService;
        var settingsNavigationService = _settingsNavigationService;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                PackageViewHostService? candidatePackageViewHostService = null;
                try
                {
                    candidatePackageViewHostService = await _packageViewHostServiceFactory
                        .CreateForPackagesAsync(activePackages, packageSources, cancellationToken)
                        .ConfigureAwait(false);
                    if (runtimePackageSnapshot is not null)
                    {
                        using var confirmationClient =
                            runtimeApiClientFactory.CreateClient<IRuntimeStartupClient>();
                        var confirmation = await WaitForReadyRuntimeSnapshotAsync(
                                confirmationClient,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                        ValidateRequiredRuntimeInstance(
                            confirmation,
                            devPackageOwnerRuntimeInstanceId);
                        if (confirmation.RuntimeInstanceId != runtimePackageSnapshot.RuntimeInstanceId
                            || confirmation.SessionGeneration != runtimePackageSnapshot.SessionGeneration)
                        {
                            if (attempt >= 2)
                            {
                                throw new InvalidDataException(
                                    "The Runtime package generation kept changing while initial App package UI was being activated.");
                            }
                            var refreshedState = await LoadConsistentPackageUiStateAsync(
                                    confirmationClient,
                                    confirmation,
                                    token => confirmationClient.GetActivePackageUiSnapshotsAsync(
                                        AppPackageTargetEnvironment.CurrentRid,
                                        token),
                                    requiredRuntimeInstanceId: devPackageOwnerRuntimeInstanceId,
                                    cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                            runtimePackageSnapshot = refreshedState.Snapshot;
                            packageSources = refreshedState.PackageSources;
                            activePackages = runtimePackageSnapshot.ActivePackages;
                            systemStatus = await confirmationClient
                                .GetSystemStatusAsync(cancellationToken)
                                .ConfigureAwait(false);
                            continue;
                        }
                    }
                    packageViewHostService = candidatePackageViewHostService;
                    candidatePackageViewHostService = null;
                    break;
                }
                catch (StalePackageUiSnapshotException exception) when (
                    runtimePackageSnapshot is not null && attempt < 2)
                {
                    AppSessionLog.WriteInfo(
                        $"Retrying initial App package activation after stale Runtime snapshots: {exception.Message}");
                    using var runtimeApiClient =
                        runtimeApiClientFactory.CreateClient<IRuntimeStartupClient>();
                    var refreshedSnapshot = await WaitForReadyRuntimeSnapshotAsync(
                            runtimeApiClient,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (devPackageOwnerAcquired
                        && refreshedSnapshot.RuntimeInstanceId
                        != devPackageOwnerRuntimeInstanceId)
                    {
                        throw new InvalidOperationException(
                            "The Runtime worker changed while dev package UI was being activated; retry startup so the App can reacquire its dev package owner lease.",
                            exception);
                    }
                    var refreshedState = await LoadConsistentPackageUiStateAsync(
                            runtimeApiClient,
                            refreshedSnapshot,
                            token => runtimeApiClient.GetActivePackageUiSnapshotsAsync(
                                AppPackageTargetEnvironment.CurrentRid,
                                token),
                            requiredRuntimeInstanceId: devPackageOwnerRuntimeInstanceId,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    runtimePackageSnapshot = refreshedState.Snapshot;
                    packageSources = refreshedState.PackageSources;
                    activePackages = runtimePackageSnapshot.ActivePackages;
                    systemStatus = await runtimeApiClient
                        .GetSystemStatusAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (candidatePackageViewHostService is not null)
                    {
                        await candidatePackageViewHostService.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            activePackages = packageViewHostService.FilterEnabledPackages(activePackages);
            if (runtimePackageSnapshot is not null)
            {
                warnings.AddRange(runtimePackageSnapshot.Warnings);
                errors.AddRange(runtimePackageSnapshot.Errors);
            }
            LogStartupPhase(StartupPhase.AppPackageActivation, phaseStopwatch);
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
            startupAttempt.EnterPhase(StartupPhase.ShellComposition);
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
            LogStartupPhase(StartupPhase.ShellComposition, phaseStopwatch);
            if (startupOptions.DevPackageFolders.Count > 0)
            {
                developerLog.Enable(startPackageLogStreaming: false);
            }

            runtimeEventSubscription = runtimePackageSnapshot is null
                ? null
                : _runtimeEventSubscriptionServiceFactory.Create();
            startupAttempt.EnterPhase(StartupPhase.MainWindowCreation);
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
                                devPackageOwnerAcquired
                                    ? _devPackageOwnerSession
                                    : null
                            );
                        }
                        catch
                        {
                            settingsNavigationService.Detach(windowLauncher);
                            mainWindowViewModel?.Dispose();
                            mainWindow?.CloseForShutdown();
                            windowLauncher.CloseForShutdown();
                            throw;
                        }
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            LogStartupPhase(StartupPhase.MainWindowCreation, phaseStopwatch);
            if (devPackageOwnerAcquired)
            {
                developerLog.Info(
                    "dev",
                    $"Developer mode enabled for {startupOptions.DevPackageFolders.Count} dev package folder(s)."
                );
            }
            else if (startupOptions.DevPackageFolders.Count > 0)
            {
                developerLog.Warning(
                    "dev",
                    "Developer packages were not loaded; Sunder continued with the active Runtime package set."
                );
            }

            if (runtimePackageSnapshot is not null)
            {
                startupAttempt.EnterPhase(StartupPhase.RuntimeSubscription);
                await result
                    .StartRuntimeSubscriptionAsync(
                        runtimePackageSnapshot,
                        packageSources,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                LogStartupPhase(StartupPhase.RuntimeSubscription, phaseStopwatch);
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

    internal static void ValidatePackageSourceGeneration(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources)
    {
        if (packageSources.Any(source => source.SessionGeneration != snapshot.SessionGeneration))
        {
            throw new InvalidDataException(
                $"App package snapshots do not belong to Runtime generation {snapshot.SessionGeneration}.");
        }
    }

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

    internal static async Task<RuntimePackageStartupResult> LoadRuntimePackagesForStartupAsync(
        IRuntimeSnapshotClient runtimeApiClient,
        bool loadDevPackages,
        Func<CancellationToken, Task<DevPackageOwnerLeaseResponse>> acquireDevPackagesAsync,
        Func<CancellationToken, Task> releaseDevPackagesAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(runtimeApiClient);
        ArgumentNullException.ThrowIfNull(acquireDevPackagesAsync);
        ArgumentNullException.ThrowIfNull(releaseDevPackagesAsync);

        var baseline = await WaitForReadyRuntimeSnapshotAsync(
                runtimeApiClient,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!loadDevPackages)
        {
            return new RuntimePackageStartupResult(baseline, false, null, null);
        }

        DevPackageOwnerLeaseResponse lease;
        try
        {
            lease = await acquireDevPackagesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception acquisitionException) when (acquisitionException is not OperationCanceledException)
        {
            if (!CanSafelyFallbackFromDevPackageFailure(acquisitionException))
            {
                throw new InvalidOperationException(
                    "Sunder could not confirm whether the Runtime applied the dev package request. Startup stopped without activating package UI; wait for the dev owner lease to expire, then retry.",
                    acquisitionException);
            }
            try
            {
                await releaseDevPackagesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception releaseException) when (releaseException is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Dev packages could not be loaded ({acquisitionException.Message}), and Sunder could not restore the active Runtime package set ({releaseException.Message}).",
                    new AggregateException(acquisitionException, releaseException));
            }

            var restoredSnapshot = await WaitForReadyRuntimeSnapshotAsync(
                    runtimeApiClient,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new RuntimePackageStartupResult(
                restoredSnapshot,
                false,
                $"Dev packages were not loaded: {acquisitionException.Message} Sunder continued with the active Runtime package set; fix the package and relaunch to retry.",
                acquisitionException);
        }

        var devSnapshot = await WaitForReadyRuntimeSnapshotAsync(
                runtimeApiClient,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (devSnapshot.RuntimeInstanceId != lease.RuntimeInstanceId
            || devSnapshot.SessionGeneration < lease.SessionGeneration)
        {
            throw new InvalidDataException(
                "Runtime returned a package snapshot older than the acquired dev package owner lease.");
        }
        return new RuntimePackageStartupResult(devSnapshot, true, null, null);
    }

    private static bool CanSafelyFallbackFromDevPackageFailure(Exception exception)
        => exception is RuntimeClientException
        {
            StatusCode: HttpStatusCode.BadRequest
                or HttpStatusCode.Unauthorized
                or HttpStatusCode.Conflict
                or HttpStatusCode.UnprocessableEntity,
            ErrorCode: "runtime.v1.validation"
                or "runtime.v1.authentication"
                or "runtime.v1.conflict"
                or "runtime.v1.stale-generation"
                or "runtime.v1.package-validation",
        };

    internal static async Task<RuntimePackageUiStartupResult> LoadConsistentPackageUiStateAsync(
        IRuntimeSnapshotClient runtimeApiClient,
        RuntimePackageSnapshot initialSnapshot,
        Func<CancellationToken, Task<IReadOnlyList<PackageUiSnapshotDescriptor>>> loadPackageSourcesAsync,
        Guid? requiredRuntimeInstanceId = null,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeApiClient);
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        ArgumentNullException.ThrowIfNull(loadPackageSourcesAsync);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ValidateRequiredRuntimeInstance(initialSnapshot, requiredRuntimeInstanceId);

        var snapshot = initialSnapshot;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageSources = await loadPackageSourcesAsync(cancellationToken).ConfigureAwait(false);
            var confirmation = await WaitForReadyRuntimeSnapshotAsync(
                    runtimeApiClient,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            ValidateRequiredRuntimeInstance(confirmation, requiredRuntimeInstanceId);
            if (confirmation.RuntimeInstanceId == snapshot.RuntimeInstanceId
                && confirmation.SessionGeneration == snapshot.SessionGeneration
                && packageSources.All(source => source.SessionGeneration == snapshot.SessionGeneration))
            {
                return new RuntimePackageUiStartupResult(snapshot, packageSources);
            }
            snapshot = confirmation;
        }

        throw new InvalidDataException(
            "The Runtime worker or package generation changed while App package snapshots were being loaded.");
    }

    private static void ValidateRequiredRuntimeInstance(
        RuntimePackageSnapshot snapshot,
        Guid? requiredRuntimeInstanceId)
    {
        if (requiredRuntimeInstanceId is { } required
            && snapshot.RuntimeInstanceId != required)
        {
            throw new InvalidOperationException(
                "The Runtime worker changed while dev package UI was being loaded; retry startup so the App can reacquire its dev package owner lease.");
        }
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

    private void LogStartupPhase(StartupPhase phase, Stopwatch phaseStopwatch)
    {
        AppSessionLog.WriteInfo(
            $"Sunder startup phase '{StartupAttemptDeadline.DescribePhase(phase)}' completed in {phaseStopwatch.ElapsedMilliseconds} ms. UI thread: {_uiDispatcher.CheckAccess()}."
        );
        phaseStopwatch.Restart();
    }

    private static async Task PublishDevPackageWarningAsync(
        NotificationCenterService notificationCenter,
        string warning,
        CancellationToken cancellationToken)
    {
        try
        {
            await notificationCenter
                .PublishAsync(
                    "sunder.app",
                    "Sunder",
                    new PackageNotificationRequest(
                        "Dev packages were not loaded",
                        warning,
                        PackageNotificationDisplayMode.TrayOnly,
                        PackageNotificationSeverity.Warning
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppSessionLog.WriteError("Failed to publish the dev package startup warning.", exception);
        }
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
