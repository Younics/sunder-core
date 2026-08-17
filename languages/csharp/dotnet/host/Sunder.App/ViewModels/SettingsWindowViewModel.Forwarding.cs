namespace Sunder.App.ViewModels;

public sealed partial class SettingsWindowViewModel
{
    private void ApplyUpdateSettings() => Updates.LoadSettings();

    private async Task<bool> SaveUpdateSettingsAsync()
    {
        IsBusy = true;
        try
        {
            var result = await Updates.SaveSettingsAsync();
            if (_disposed)
            {
                return false;
            }

            StatusText = result.StatusText;
            return result.Success;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    private async Task<bool> SaveRuntimeResourceSettingsAsync()
    {
        IsBusy = true;
        try
        {
            var result = await RuntimeResources.SaveSettingsAsync(_disposeCts.Token);
            if (_disposed)
            {
                return false;
            }

            StatusText = result.StatusText;
            return result.Success;
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }
}
