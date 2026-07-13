using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class AppUpdatePromptCoordinator(SunderUpdateService updateService)
{
    public async Task<SunderUpdateInfo?> CheckForStartupPromptAsync(CancellationToken cancellationToken)
    {
        try
        {
            var updateSettings = updateService.LoadSettings();
            var checkResult = await updateService.CheckForUpdatesAsync(cancellationToken);
            if (checkResult.Update is null)
            {
                return null;
            }

            if (updateSettings.DownloadUpdatesAutomatically)
            {
                await updateService.DownloadUpdateAsync(checkResult.Update, cancellationToken: cancellationToken);
                return null;
            }

            return checkResult.Update;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Sunder app startup update check failed.", ex);
            return null;
        }
    }

    public async Task<string?> InstallUpdateAndRestartAsync(
        SunderUpdateInfo update,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await updateService.DownloadUpdateAndRestartAsync(update, progress, cancellationToken);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to install Sunder app update.", ex);
            return $"Update failed: {ex.Message}";
        }
    }
}
