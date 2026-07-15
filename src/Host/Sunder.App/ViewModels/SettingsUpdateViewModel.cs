using CommunityToolkit.Mvvm.ComponentModel;

namespace Sunder.App.ViewModels;

public sealed partial class SettingsUpdateViewModel : ViewModelBase
{
    private readonly SettingsUpdateCoordinator _coordinator;

    internal SettingsUpdateViewModel(SettingsUpdateCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    [ObservableProperty]
    private bool _downloadUpdatesAutomatically;

    [ObservableProperty]
    private string _currentVersionText = "Unknown";

    [ObservableProperty]
    private string _sourceText = "Not configured";

    [ObservableProperty]
    private string _statusText = "Open this section to check app update status.";

    [ObservableProperty]
    private bool _canCheckForAppUpdates;

    internal void LoadSettings()
    {
        ApplyState(_coordinator.LoadSettings(), includeDownloadSetting: true);
    }

    internal async Task<string?> CheckForUpdatesAsync()
    {
        StatusText = "Checking GitHub Releases for app updates...";
        CanCheckForAppUpdates = false;
        try
        {
            var result = await _coordinator.CheckForUpdatesAsync();
            if (result.State is not null)
            {
                ApplyState(result.State, includeDownloadSetting: false);
            }

            StatusText = result.UpdateStatusText;
            return string.IsNullOrWhiteSpace(result.StatusText) ? null : result.StatusText;
        }
        finally
        {
            CanCheckForAppUpdates = _coordinator.CanCheckForUpdates();
        }
    }

    internal async Task<SettingsUpdateSaveResult> SaveSettingsAsync()
        => await _coordinator.SaveSettingsAsync(DownloadUpdatesAutomatically);

    private void ApplyState(SettingsUpdateState state, bool includeDownloadSetting)
    {
        if (includeDownloadSetting && state.DownloadUpdatesAutomatically.HasValue)
        {
            DownloadUpdatesAutomatically = state.DownloadUpdatesAutomatically.Value;
        }

        CurrentVersionText = state.CurrentVersionText;
        SourceText = state.SourceText;
        StatusText = state.StatusText;
        CanCheckForAppUpdates = state.CanCheckForUpdates;
    }
}
