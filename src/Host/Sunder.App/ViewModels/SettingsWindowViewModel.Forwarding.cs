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
}
