using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Views.Controls;

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

    private object? _hostedView;

    public object? HostedView => _hostedView;

    public void SetActiveView(string viewId, object? hostedView)
    {
        ActiveViewId = viewId;
        SetHostedView(hostedView);
    }

    public void ClearActiveView()
    {
        ActiveViewId = null;
        SetHostedView(null);
    }

    private void SetHostedView(object? hostedView)
    {
        if (ReferenceEquals(_hostedView, hostedView))
        {
            return;
        }

        HostedPackageViewBoundary.ReleaseHostedView(_hostedView);
        if (!SetProperty(ref _hostedView, hostedView, nameof(HostedView)))
        {
            return;
        }

        OnPropertyChanged(nameof(HasHostedView));
        OnPropertyChanged(nameof(ShowFallbackLines));
    }
}
