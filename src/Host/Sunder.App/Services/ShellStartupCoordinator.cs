using Avalonia;
using Sunder.App.Models;
using Sunder.App.ViewModels;
using Sunder.App.Views;
using Sunder.Protocol;
using Sunder.Sdk.Notifications;

namespace Sunder.App.Services;

public sealed record ShellStartupResult(
    MainWindow MainWindow,
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

        loadingViewModel.StatusMessage = "Loading theme...";
        loadingViewModel.ProgressWidth = 96;

        var themeManager = new ThemeManager(_application);
        themeManager.Initialize();
        themeManager.ApplyTheme(shellState.ThemeId);

        loadingViewModel.StatusMessage = "Checking CLI...";
        loadingViewModel.ProgressWidth = 136;
        await EnsureCliInstalledForStartupAsync(cliInstallationService, notificationCenter, warnings);

        try
        {
            if (errors.Count == 0)
            {
                loadingViewModel.StatusMessage = "Starting runtime...";
                loadingViewModel.ProgressWidth = 176;

                await runtimeHostProcessManager.EnsureStartedAsync(runtimeConnectionState.RuntimeUrl);

                using var runtimeApiClient = runtimeApiClientFactory.CreateClient();
                systemStatus = await runtimeApiClient.GetSystemStatusAsync();

                if (startupOptions.DevPackageFolders.Count > 0)
                {
                    loadingViewModel.StatusMessage = "Loading dev packages...";
                    loadingViewModel.ProgressWidth = 248;

                    var loadResult = await runtimeApiClient.LoadDevPackagesAsync(startupOptions.DevPackageFolders);
                    activePackages = loadResult.LoadedPackages;
                    packageSources = await runtimeApiClient.GetActivePackageSourcesAsync();
                    warnings.AddRange(loadResult.Warnings);
                    errors.AddRange(loadResult.Errors);
                }
                else
                {
                    loadingViewModel.StatusMessage = "Loading active packages...";
                    loadingViewModel.ProgressWidth = 248;
                    activePackages = await runtimeApiClient.GetActivePackagesAsync();
                    packageSources = await runtimeApiClient.GetActivePackageSourcesAsync();
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        loadingViewModel.StatusMessage = "Composing shell...";
        loadingViewModel.ProgressWidth = 318;

        PackageViewHostService packageViewHostService;
        var shellViewService = new AppPackageShellViewService();
        try
        {
            var packageFaultReporter = new PackageRuntimeFaultReporter(runtimeApiClientFactory);
            packageViewHostService = await PackageViewHostService.CreateForPackagesAsync(
                activePackages,
                packageSources,
                packageFaultReporter,
                shellViewService,
                notificationCenter);
            activePackages = packageViewHostService.FilterEnabledPackages(activePackages);
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
        themeManager.ApplyTheme(shellSnapshot.State.ThemeId);

        var windowLauncher = new WindowLauncher(packageViewHostService, runtimeApiClientFactory, cliInstallationService, notificationCenter, shellStateService, shellState, updateService);
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
            updateService);
        var mainWindow = new MainWindow
        {
            DataContext = mainWindowViewModel,
        };
        _ = mainWindowViewModel.CheckForAppUpdatesOnStartupAsync();

        return new ShellStartupResult(mainWindow, packageViewHostService, windowLauncher);
    }

    private static async Task EnsureCliInstalledForStartupAsync(
        CliInstallationService cliInstallationService,
        NotificationCenterService notificationCenter,
        ICollection<string> warnings)
    {
        try
        {
            var result = await cliInstallationService.EnsureInstalledAsync();
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
                    PackageNotificationSeverity.Warning));
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
                    PackageNotificationSeverity.Warning));
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
