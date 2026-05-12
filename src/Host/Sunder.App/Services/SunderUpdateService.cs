using Velopack;
using Velopack.Sources;

namespace Sunder.App.Services;

public sealed class SunderUpdateService
{
    private readonly SunderAppSettings _appSettings;
    private readonly AppUpdateSettingsService _settingsService;
    private readonly object _syncRoot = new();
    private UpdateManager? _updateManager;

    public SunderUpdateService(
        SunderAppSettings? appSettings = null,
        AppUpdateSettingsService? settingsService = null)
    {
        _appSettings = appSettings ?? SunderAppSettings.Load();
        _settingsService = settingsService ?? new AppUpdateSettingsService();
    }

    public AppUpdateSettings LoadSettings() => _settingsService.Load();

    public Task SaveSettingsAsync(AppUpdateSettings settings, CancellationToken cancellationToken = default)
        => _settingsService.SaveAsync(settings, cancellationToken);

    public SunderUpdateRuntimeStatus GetRuntimeStatus()
    {
        var manager = GetUpdateManager();
        var source = string.IsNullOrWhiteSpace(_appSettings.UpdateGitHubRepositoryUrl)
            ? "Not configured"
            : _appSettings.UpdateGitHubRepositoryUrl.Trim();
        var currentVersion = manager?.CurrentVersion?.ToString() ?? SunderAppVersion.CurrentText;

        if (manager is null)
        {
            return new SunderUpdateRuntimeStatus(
                IsConfigured: false,
                IsInstalled: false,
                CanCheckForUpdates: false,
                CurrentVersion: currentVersion,
                Source: source,
                Message: "GitHub Releases update source is not configured.");
        }

        if (!manager.IsInstalled)
        {
            return new SunderUpdateRuntimeStatus(
                IsConfigured: true,
                IsInstalled: false,
                CanCheckForUpdates: false,
                CurrentVersion: currentVersion,
                Source: source,
                Message: "App updates are available after installing Sunder with the Velopack installer.");
        }

        return new SunderUpdateRuntimeStatus(
            IsConfigured: true,
            IsInstalled: true,
            CanCheckForUpdates: true,
            CurrentVersion: currentVersion,
            Source: source,
            Message: "Ready to check GitHub Releases for Sunder updates.");
    }

    public async Task<SunderUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = GetRuntimeStatus();
        if (!status.CanCheckForUpdates)
        {
            return new SunderUpdateCheckResult(status, Update: null, status.Message);
        }

        var manager = GetUpdateManager();
        if (manager is null)
        {
            return new SunderUpdateCheckResult(status, Update: null, status.Message);
        }

        try
        {
            var update = await manager.CheckForUpdatesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (update is null)
            {
                return new SunderUpdateCheckResult(status, Update: null, "Sunder is up to date.");
            }

            var availableUpdate = new SunderUpdateInfo(update);
            return new SunderUpdateCheckResult(
                status,
                availableUpdate,
                $"Sunder {availableUpdate.Version} is available.");
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to check for Sunder app updates.", ex);
            return new SunderUpdateCheckResult(status, Update: null, $"Update check failed: {ex.Message}");
        }
    }

    public async Task<SunderUpdateDownloadResult> DownloadUpdateAsync(
        SunderUpdateInfo update,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manager = GetUpdateManager() ?? throw new InvalidOperationException("Sunder updates are not configured.");
        await manager.DownloadUpdatesAsync(update.UpdateInfo, progress, cancellationToken);
        return new SunderUpdateDownloadResult(update, "Update downloaded. It will be applied the next time Sunder starts.");
    }

    public async Task DownloadUpdateAndRestartAsync(
        SunderUpdateInfo update,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manager = GetUpdateManager() ?? throw new InvalidOperationException("Sunder updates are not configured.");
        await manager.DownloadUpdatesAsync(update.UpdateInfo, progress, cancellationToken);
        manager.ApplyUpdatesAndRestart(update.UpdateInfo.TargetFullRelease, []);
    }

    private UpdateManager? GetUpdateManager()
    {
        if (string.IsNullOrWhiteSpace(_appSettings.UpdateGitHubRepositoryUrl))
        {
            return null;
        }

        lock (_syncRoot)
        {
            if (_updateManager is not null)
            {
                return _updateManager;
            }

            _updateManager = new UpdateManager(new GithubSource(
                _appSettings.UpdateGitHubRepositoryUrl.Trim(),
                accessToken: null,
                prerelease: _appSettings.IncludePrereleaseUpdates == true));
            return _updateManager;
        }
    }
}

public sealed record SunderUpdateRuntimeStatus(
    bool IsConfigured,
    bool IsInstalled,
    bool CanCheckForUpdates,
    string CurrentVersion,
    string Source,
    string Message);

public sealed record SunderUpdateCheckResult(
    SunderUpdateRuntimeStatus RuntimeStatus,
    SunderUpdateInfo? Update,
    string Message)
{
    public bool HasUpdate => Update is not null;
}

public sealed record SunderUpdateDownloadResult(SunderUpdateInfo Update, string Message);

public sealed class SunderUpdateInfo
{
    internal SunderUpdateInfo(UpdateInfo updateInfo)
    {
        UpdateInfo = updateInfo;
    }

    internal UpdateInfo UpdateInfo { get; }

    public string Version => UpdateInfo.TargetFullRelease.Version.ToString();

    public long SizeBytes => UpdateInfo.TargetFullRelease.Size;
}
