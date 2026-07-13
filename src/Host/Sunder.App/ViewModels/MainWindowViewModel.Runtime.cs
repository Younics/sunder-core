using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace Sunder.App.ViewModels;

public partial class MainWindowViewModel
{
    public string SystemStatusText
    {
        get => _runtimeStatus.SystemStatusText;
        set => _runtimeStatus.SystemStatusText = value;
    }

    public string RuntimeAddressText
    {
        get => _runtimeStatus.RuntimeAddressText;
        set => _runtimeStatus.RuntimeAddressText = value;
    }

    public string RuntimeName => _runtimeStatus.RuntimeName;

    public string RuntimeVersion => _runtimeStatus.RuntimeVersion;

    public string RuntimeStatusText => _runtimeStatus.RuntimeStatusText;

    public string RuntimeLastError => _runtimeStatus.RuntimeLastError;

    public bool IsRuntimeRunning => _runtimeStatus.IsRuntimeRunning;

    public bool IsRuntimeReady => _runtimeStatus.IsRuntimeReady;

    public bool IsRuntimeBusy => _runtimeStatus.IsRuntimeBusy;

    public IBrush? RuntimeStatusBrush => _runtimeStatus.RuntimeStatusBrush;

    public bool CanManageRuntime => _runtimeStatus.CanManageRuntime;

    public bool ShowRuntimeAddressEditor => _runtimeStatus.ShowRuntimeAddressEditor;

    public bool ShowApplyRuntimeButton => _runtimeStatus.ShowApplyRuntimeButton;

    public bool ShowStartRuntimeButton => _runtimeStatus.ShowStartRuntimeButton;

    public bool ShowStopRuntimeButton => _runtimeStatus.ShowStopRuntimeButton;

    public bool ShowRuntimeError => _runtimeStatus.ShowRuntimeError;

    [RelayCommand]
    private async Task RefreshRuntimeAsync()
    {
        if (!_disposed)
        {
            await _runtimeStatus.RefreshRuntimeAsync(_tasks.Token);
        }
    }

    [RelayCommand]
    private async Task ApplyRuntimeAddressAsync()
    {
        if (!_disposed)
        {
            await _runtimeStatus.ApplyRuntimeAddressAsync(_tasks.Token);
        }
    }

    [RelayCommand]
    private async Task StartRuntimeAsync()
    {
        if (!_disposed)
        {
            await _runtimeStatus.StartRuntimeAsync(_tasks.Token);
        }
    }

    [RelayCommand]
    private async Task StopRuntimeAsync()
    {
        if (!_disposed)
        {
            await _runtimeStatus.StopRuntimeAsync(_tasks.Token);
        }
    }

    private void RuntimeStatus_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }
}
