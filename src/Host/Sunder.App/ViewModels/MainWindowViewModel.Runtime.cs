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
        => await _runtimeStatus.RefreshRuntimeAsync();

    [RelayCommand]
    private async Task ApplyRuntimeAddressAsync()
        => await _runtimeStatus.ApplyRuntimeAddressAsync();

    [RelayCommand]
    private async Task StartRuntimeAsync()
        => await _runtimeStatus.StartRuntimeAsync();

    [RelayCommand]
    private async Task StopRuntimeAsync()
        => await _runtimeStatus.StopRuntimeAsync();

    private void RuntimeStatus_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }
}
