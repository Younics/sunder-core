using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sunder.App.ViewModels;

public sealed partial class ShellPanelViewModel : ViewModelBase
{
    public ObservableCollection<string> Lines { get; } = [];

    public bool HasHostedView => HostedView is not null;

    public bool ShowFallbackLines => HostedView is null;

    private string? _activeViewId;

    public string? ActiveViewId
    {
        get => _activeViewId;
        private set => SetProperty(ref _activeViewId, value);
    }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private object? _hostedView;

    partial void OnHostedViewChanged(object? value)
    {
        OnPropertyChanged(nameof(HasHostedView));
        OnPropertyChanged(nameof(ShowFallbackLines));
    }

    public void SetActiveView(string viewId, object? hostedView)
    {
        ActiveViewId = viewId;
        HostedView = hostedView;
    }

    public void ClearActiveView()
    {
        ActiveViewId = null;
        HostedView = null;
    }
}
