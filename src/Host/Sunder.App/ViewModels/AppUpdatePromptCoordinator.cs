using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class AppUpdatePromptCoordinator(SunderUpdateService updateService)
{
    public async Task<SunderUpdateInfo?> CheckForStartupPromptAsync()
    {
        try
        {
            var updateSettings = updateService.LoadSettings();
            var checkResult = await updateService.CheckForUpdatesAsync();
            if (checkResult.Update is null)
            {
                return null;
            }

            if (updateSettings.DownloadUpdatesAutomatically)
            {
                await updateService.DownloadUpdateAsync(checkResult.Update);
                return null;
            }

            return checkResult.Update;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Sunder app startup update check failed.", ex);
            return null;
        }
    }

    public async Task<string?> InstallUpdateAndRestartAsync(
        SunderUpdateInfo update,
        Action<int> progress)
    {
        try
        {
            await updateService.DownloadUpdateAndRestartAsync(update, progress);
            return null;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to install Sunder app update.", ex);
            return $"Update failed: {ex.Message}";
        }
    }
}
