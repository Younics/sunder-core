using System.Diagnostics;
using Avalonia.Threading;
using Sunder.App.Composition;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Protocol;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed record ShellStartupResult(
    MainWindow MainWindow,
    MainWindowViewModel MainWindowViewModel,
    PackageViewHostService PackageViewHostService,
    WindowLauncher WindowLauncher,
    DevPackageHotReloadSession? DevPackageHotReloadSession);

public sealed class ShellStartupCoordinator
{
    private readonly ShellStateService _shellStateService;
    private readonly ShellState _shellState;
    private readonly RuntimeConnectionState _runtimeConnectionState;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory;
    private readonly RuntimeHostProcessManager _runtimeHostProcessManager;
    private readonly NotificationCenterService _notificationCenter;
    private readonly DeveloperLogService _developerLog;
    private readonly CliInstallationService _cliInstallationService;
    private readonly SunderUpdateService _updateService;
    private readonly IThemeManager _themeManager;
    private readonly AppPackageSettingsNavigationService _settingsNavigationService;
    private readonly AppPackageSessionService _packageSessionService;
    private readonly IShellCompositionService _shellCompositionService;
    private readonly PackageViewHostServiceFactory _packageViewHostServiceFactory;
    private readonly WindowLauncherFactory _windowLauncherFactory;
    private readonly MainWindowFactory _mainWindowFactory;

    public ShellStartupCoordinator(
        ShellStateService shellStateService,
        ShellState shellState,
        RuntimeConnectionState runtimeConnectionState,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        RuntimeHostProcessManager runtimeHostProcessManager,
        NotificationCenterService notificationCenter,
        DeveloperLogService developerLog,
        CliInstallationService cliInstallationService,
        SunderUpdateService updateService,
        IThemeManager themeManager,
        AppPackageSettingsNavigationService settingsNavigationService,
        AppPackageSessionService packageSessionService,
        IShellCompositionService shellCompositionService,
        PackageViewHostServiceFactory packageViewHostServiceFactory,
        WindowLauncherFactory windowLauncherFactory,
        MainWindowFactory mainWindowFactory)
    {
        _shellStateService = shellStateService;
        _shellState = shellState;
        _runtimeConnectionState = runtimeConnectionState;
        _runtimeApiClientFactory = runtimeApiClientFactory;
        _runtimeHostProcessManager = runtimeHostProcessManager;
        _notificationCenter = notificationCenter;
        _developerLog = developerLog;
        _cliInstallationService = cliInstallationService;
        _updateService = updateService;
        _themeManager = themeManager;
        _settingsNavigationService = settingsNavigationService;
        _packageSessionService = packageSessionService;
        _shellCompositionService = shellCompositionService;
        _packageViewHostServiceFactory = packageViewHostServiceFactory;
        _windowLauncherFactory = windowLauncherFactory;
        _mainWindowFactory = mainWindowFactory;
    }

    public async Task<ShellStartupResult> StartAsync(
        AppStartupOptions startupOptions,
        LoadingWindowViewModel loadingViewModel)
    {
        var shellStateService = _shellStateService;
        var shellState = _shellState;
        IReadOnlyList<ActivePackageDescriptor> activePackages = [];
        IReadOnlyList<PackageSourceDescriptor> packageSources = [];
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

        await SetProgressAsync(loadingViewModel, "Loading theme...", 96);

        var themeManager = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var manager = _themeManager;
            manager.Initialize();
            manager.ApplyTheme(shellState.ThemeId);
            return manager;
        });
        LogStartupPhase("theme", phaseStopwatch);

        await SetProgressAsync(loadingViewModel, "Checking CLI...", 136);
        await EnsureCliInstalledForStartupAsync(cliInstallationService, notificationCenter, warnings).ConfigureAwait(false);
        LogStartupPhase("cli", phaseStopwatch);

        try
        {
            if (errors.Count == 0)
            {
                await SetProgressAsync(loadingViewModel, "Starting runtime...", 176).ConfigureAwait(false);

                await runtimeHostProcessManager.EnsureStartedAsync(runtimeConnectionState.RuntimeUrl).ConfigureAwait(false);

                using var runtimeApiClient = runtimeApiClientFactory.CreateClient();
                systemStatus = await runtimeApiClient.GetSystemStatusAsync().ConfigureAwait(false);
                LogStartupPhase("runtime", phaseStopwatch);

                if (startupOptions.DevPackageFolders.Count > 0)
                {
                    await SetProgressAsync(loadingViewModel, "Loading dev packages...", 248).ConfigureAwait(false);

                    var loadResult = await runtimeApiClient.LoadDevPackagesAsync(startupOptions.DevPackageFolders).ConfigureAwait(false);
                    activePackages = loadResult.LoadedPackages;
                    packageSources = await runtimeApiClient.GetActivePackageSourcesAsync().ConfigureAwait(false);
                    warnings.AddRange(loadResult.Warnings);
                    errors.AddRange(loadResult.Errors);
                    LogStartupPhase("runtime dev packages", phaseStopwatch);
                }
                else
                {
                    await SetProgressAsync(loadingViewModel, "Loading active packages...", 248).ConfigureAwait(false);
                    activePackages = await runtimeApiClient.GetActivePackagesAsync().ConfigureAwait(false);
                    packageSources = await runtimeApiClient.GetActivePackageSourcesAsync().ConfigureAwait(false);
                    LogStartupPhase("runtime active packages", phaseStopwatch);
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        await SetProgressAsync(loadingViewModel, "Composing shell...", 318).ConfigureAwait(false);

        PackageViewHostService packageViewHostService;
        var settingsNavigationService = _settingsNavigationService;
        var packageSessionService = _packageSessionService;
        try
        {
            packageViewHostService = await _packageViewHostServiceFactory.CreateForPackagesAsync(
                activePackages,
                packageSources).ConfigureAwait(false);
            activePackages = packageViewHostService.FilterEnabledPackages(activePackages);
            LogStartupPhase("app package activation", phaseStopwatch);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to create the app package host service.", ex);
            errors.Add(ex.Message);
            packageViewHostService = PackageViewHostService.Empty;
            activePackages = [];
        }

        var shellCompositionService = _shellCompositionService;
        var shellSnapshot = shellCompositionService.Compose(
            activePackages,
            shellState,
            systemStatus,
            warnings,
            errors);
        LogStartupPhase("shell composition", phaseStopwatch);
        if (startupOptions.DevPackageFolders.Count > 0)
        {
            developerLog.Enable();
        }

        var result = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            themeManager.ApplyTheme(shellSnapshot.State.ThemeId);

            var windowLauncher = _windowLauncherFactory.Create(packageViewHostService);
            settingsNavigationService.Attach(windowLauncher);
            windowLauncher.AttachPackageSessionService(packageSessionService);
            var (mainWindow, mainWindowViewModel) = _mainWindowFactory.Create(
                windowLauncher,
                shellSnapshot,
                packageViewHostService,
                systemStatus,
                deferInitialHostedViews: true);

            return new ShellStartupResult(mainWindow, mainWindowViewModel, packageViewHostService, windowLauncher, DevPackageHotReloadSession: null);
        });
        LogStartupPhase("main window creation", phaseStopwatch);
        DevPackageHotReloadSession? devPackageHotReloadSession = null;
        if (startupOptions.DevPackageFolders.Count > 0)
        {
            developerLog.Info("dev", $"Developer mode enabled for {startupOptions.DevPackageFolders.Count} dev package folder(s).");
            if (startupOptions.WatchDevPackages)
            {
                devPackageHotReloadSession = new DevPackageHotReloadSession(
                    startupOptions.DevPackageFolders,
                    runtimeApiClientFactory,
                    result.WindowLauncher,
                    developerLog,
                    notificationCenter);
                devPackageHotReloadSession.Start();
            }
        }

        AppSessionLog.WriteInfo($"Sunder startup composition completed in {startupStopwatch.ElapsedMilliseconds} ms.");
        _ = result.MainWindowViewModel.CheckForAppUpdatesOnStartupAsync();

        return result with { DevPackageHotReloadSession = devPackageHotReloadSession };
    }

    private static async Task SetProgressAsync(
        LoadingWindowViewModel loadingViewModel,
        string statusMessage,
        double progressWidth)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyProgress(loadingViewModel, statusMessage, progressWidth);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => ApplyProgress(loadingViewModel, statusMessage, progressWidth));
    }

    private static void ApplyProgress(
        LoadingWindowViewModel loadingViewModel,
        string statusMessage,
        double progressWidth)
    {
        loadingViewModel.StatusMessage = statusMessage;
        loadingViewModel.ProgressWidth = progressWidth;
    }

    private static void LogStartupPhase(string phaseName, Stopwatch phaseStopwatch)
    {
        AppSessionLog.WriteInfo(
            $"Sunder startup phase '{phaseName}' completed in {phaseStopwatch.ElapsedMilliseconds} ms. UI thread: {Dispatcher.UIThread.CheckAccess()}.");
        phaseStopwatch.Restart();
    }

    private static async Task EnsureCliInstalledForStartupAsync(
        CliInstallationService cliInstallationService,
        NotificationCenterService notificationCenter,
        ICollection<string> warnings)
    {
        try
        {
            var result = await cliInstallationService.EnsureInstalledAsync().ConfigureAwait(false);
            if (!CliStartupNotificationPolicy.TryCreateWarning(result, out var warning))
            {
                return;
            }

            warnings.Add($"CLI: {warning}");
            await notificationCenter.PublishAsync(
                "sunder.app",
                "Sunder",
                new PackageNotificationRequest(
                    "Sunder CLI needs attention",
                    warning,
                    PackageNotificationDisplayMode.TrayOnly,
                    PackageNotificationSeverity.Warning)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to install or repair the Sunder CLI.", ex);
            warnings.Add($"CLI: {ex.Message}");
            await notificationCenter.PublishAsync(
                "sunder.app",
                "Sunder",
                new PackageNotificationRequest(
                    "Sunder CLI install failed",
                    ex.Message,
                    PackageNotificationDisplayMode.TrayOnly,
                    PackageNotificationSeverity.Warning)).ConfigureAwait(false);
        }
    }

    private static Uri ResolveRuntimeUrl(AppStartupOptions startupOptions, ShellState shellState, ICollection<string> warnings)
    {
        if (startupOptions.HasExplicitRuntimeUrl)
        {
            return startupOptions.RuntimeUrl;
        }

        if (RuntimeUrlHelper.TryParse(shellState.PreferredRuntimeUrl, out var preferredRuntimeUrl) && preferredRuntimeUrl is not null)
        {
            return preferredRuntimeUrl;
        }

        if (!string.IsNullOrWhiteSpace(shellState.PreferredRuntimeUrl))
        {
            warnings.Add($"Saved runtime URL '{shellState.PreferredRuntimeUrl}' is invalid, so the default runtime address is being used.");
        }

        return startupOptions.RuntimeUrl;
    }
}
