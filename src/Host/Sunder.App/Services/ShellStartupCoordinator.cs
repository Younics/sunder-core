using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
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
    WindowLauncher WindowLauncher);

public sealed class ShellStartupCoordinator(Application application)
{
    private readonly Application _application = application;

    public async Task<ShellStartupResult> StartAsync(
        AppStartupOptions startupOptions,
        LoadingWindowViewModel loadingViewModel)
    {
        var shellStateService = new ShellStateService();
        var shellState = shellStateService.Load();
        IReadOnlyList<ActivePackageDescriptor> activePackages = [];
        IReadOnlyList<PackageSourceDescriptor> packageSources = [];
        var warnings = new List<string>();
        var errors = startupOptions.ParseErrors.ToList();
        SystemStatusResponse? systemStatus = null;
        var runtimeUrl = ResolveRuntimeUrl(startupOptions, shellState, warnings);
        var runtimeConnectionState = new RuntimeConnectionState(runtimeUrl);
        var runtimeApiClientFactory = new RuntimeApiClientFactory(runtimeConnectionState);
        var runtimeHostProcessManager = new RuntimeHostProcessManager(startupOptions);
        var notificationCenter = new NotificationCenterService();
        var cliInstallationService = new CliInstallationService();
        var updateService = new SunderUpdateService(SunderAppSettings.Load(), new AppUpdateSettingsService());
        var startupStopwatch = Stopwatch.StartNew();
        var phaseStopwatch = Stopwatch.StartNew();

        await SetProgressAsync(loadingViewModel, "Loading theme...", 96);

        var themeManager = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var manager = new ThemeManager(_application);
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
        var shellViewService = new AppPackageShellViewService();
        var settingsNavigationService = new AppPackageSettingsNavigationService();
        var backgroundProcessQueue = new BackgroundProcessQueueService();
        try
        {
            var packageFaultReporter = new PackageRuntimeFaultReporter(runtimeApiClientFactory);
            packageViewHostService = await PackageViewHostService.CreateForPackagesAsync(
                activePackages,
                packageSources,
                packageFaultReporter,
                shellViewService,
                settingsNavigationService,
                notificationCenter,
                backgroundProcessQueue).ConfigureAwait(false);
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

        IShellCompositionService shellCompositionService = new ShellCompositionService();
        var shellSnapshot = shellCompositionService.Compose(
            activePackages,
            shellState,
            systemStatus,
            warnings,
            errors);
        LogStartupPhase("shell composition", phaseStopwatch);

        var result = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            themeManager.ApplyTheme(shellSnapshot.State.ThemeId);

            var windowLauncher = new WindowLauncher(packageViewHostService, runtimeApiClientFactory, cliInstallationService, notificationCenter, shellStateService, shellState, updateService, backgroundProcessQueue);
            settingsNavigationService.Attach(windowLauncher);
            var mainWindowViewModel = new MainWindowViewModel(
                windowLauncher,
                shellStateService,
                shellSnapshot,
                packageViewHostService,
                runtimeConnectionState,
                runtimeApiClientFactory,
                runtimeHostProcessManager,
                systemStatus,
                notificationCenter,
                shellViewService,
                updateService,
                deferInitialHostedViews: true,
                backgroundProcessQueue: windowLauncher.BackgroundProcesses);
            var mainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };

            return new ShellStartupResult(mainWindow, mainWindowViewModel, packageViewHostService, windowLauncher);
        });
        LogStartupPhase("main window creation", phaseStopwatch);
        AppSessionLog.WriteInfo($"Sunder startup composition completed in {startupStopwatch.ElapsedMilliseconds} ms.");
        _ = result.MainWindowViewModel.CheckForAppUpdatesOnStartupAsync();

        return result;
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
