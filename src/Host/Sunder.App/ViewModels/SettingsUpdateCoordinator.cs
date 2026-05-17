using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class SettingsUpdateCoordinator(SunderUpdateService updateService)
{
    public SettingsUpdateState LoadSettings()
    {
        var settings = updateService.LoadSettings();
        return SettingsUpdateState.FromStatus(settings.DownloadUpdatesAutomatically, updateService.GetRuntimeStatus());
    }

    public async Task<SettingsUpdateCheckResult> CheckForUpdatesAsync()
    {
        try
        {
            var result = await updateService.CheckForUpdatesAsync();
            return new SettingsUpdateCheckResult(
                SettingsUpdateState.FromStatus(null, result.RuntimeStatus),
                result.Message,
                StatusText: null);
        }
        catch (Exception ex)
        {
            return new SettingsUpdateCheckResult(State: null, UpdateStatusText: ex.Message, StatusText: ex.Message);
        }
    }

    public async Task<SettingsUpdateSaveResult> SaveSettingsAsync(bool downloadUpdatesAutomatically)
    {
        try
        {
            await updateService.SaveSettingsAsync(new AppUpdateSettings
            {
                DownloadUpdatesAutomatically = downloadUpdatesAutomatically,
            });
            return new SettingsUpdateSaveResult(true, downloadUpdatesAutomatically
                ? "Sunder will download app updates in the background and apply them on the next start."
                : "Automatic app update downloads are disabled.");
        }
        catch (Exception ex)
        {
            return new SettingsUpdateSaveResult(false, ex.Message);
        }
    }

    public bool CanCheckForUpdates()
        => updateService.GetRuntimeStatus().CanCheckForUpdates;
}

internal sealed record SettingsUpdateState(
    bool? DownloadUpdatesAutomatically,
    string CurrentVersionText,
    string SourceText,
    string StatusText,
    bool CanCheckForUpdates)
{
    public static SettingsUpdateState FromStatus(bool? downloadUpdatesAutomatically, SunderUpdateRuntimeStatus status)
        => new(
            downloadUpdatesAutomatically,
            status.CurrentVersion,
            status.Source,
            status.Message,
            status.CanCheckForUpdates);
}

internal sealed record SettingsUpdateCheckResult(
    SettingsUpdateState? State,
    string UpdateStatusText,
    string? StatusText);

internal sealed record SettingsUpdateSaveResult(bool Success, string StatusText);
