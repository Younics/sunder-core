using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Views.Controls;

namespace Sunder.App.ViewModels;

public sealed partial class ShellPanelViewModel : ViewModelBase
{
    public ObservableCollection<string> Lines { get; } = [];

    public ObservableCollection<ShellHostedViewViewModel> HostedViews { get; } = [];

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

    private bool _isDockVisible;

    public bool IsDockVisible => _isDockVisible;

    private object? _hostedView;

    public object? HostedView => _hostedView;

    public void SetActiveView(string viewId, object? hostedView)
    {
        ActiveViewId = viewId;
        if (hostedView is null)
        {
            DeactivateHostedViews();
            return;
        }

        var retainedView = HostedViews.FirstOrDefault(view => string.Equals(view.ViewId, viewId, StringComparison.OrdinalIgnoreCase));
        if (retainedView is null)
        {
            retainedView = new ShellHostedViewViewModel(viewId, hostedView);
            HostedViews.Add(retainedView);
        }
        else if (!ReferenceEquals(retainedView.View, hostedView))
        {
            HostedPackageViewBoundary.ReleaseHostedView(retainedView.View);
            retainedView.View = hostedView;
        }

        foreach (var view in HostedViews)
        {
            view.IsActive = ReferenceEquals(view, retainedView);
        }

        SetHostedView(retainedView.View);
    }

    public object RetainHostedView(string viewId, object hostedView)
    {
        var retainedView = HostedViews.FirstOrDefault(view =>
            string.Equals(view.ViewId, viewId, StringComparison.OrdinalIgnoreCase));
        if (retainedView is null)
        {
            retainedView = new ShellHostedViewViewModel(viewId, hostedView);
            HostedViews.Add(retainedView);
            return hostedView;
        }

        if (!ReferenceEquals(retainedView.View, hostedView))
        {
            HostedPackageViewBoundary.ReleaseHostedView(hostedView);
        }

        return retainedView.View;
    }

    public void ClearActiveView()
    {
        ActiveViewId = null;
        DeactivateHostedViews();
    }

    public void SetDockVisible(bool isVisible)
        => SetProperty(ref _isDockVisible, isVisible, nameof(IsDockVisible));

    public object? GetRetainedView(string viewId)
        => HostedViews.FirstOrDefault(view => string.Equals(view.ViewId, viewId, StringComparison.OrdinalIgnoreCase))?.View;

    public void RemoveHostedView(string viewId)
    {
        var retainedView = HostedViews.FirstOrDefault(view => string.Equals(view.ViewId, viewId, StringComparison.OrdinalIgnoreCase));
        if (retainedView is null)
        {
            return;
        }

        var wasActive = retainedView.IsActive || ReferenceEquals(_hostedView, retainedView.View);
        HostedViews.Remove(retainedView);
        HostedPackageViewBoundary.ReleaseHostedView(retainedView.View);
        if (wasActive)
        {
            ActiveViewId = null;
            SetHostedView(null);
        }
    }

    public void ClearRetainedViews()
    {
        foreach (var retainedView in HostedViews.ToArray())
        {
            RemoveHostedView(retainedView.ViewId);
        }
    }

    public void PruneHostedViews(ISet<string> retainedViewIds)
    {
        foreach (var retainedView in HostedViews
            .Where(view => !retainedViewIds.Contains(view.ViewId))
            .ToArray())
        {
            RemoveHostedView(retainedView.ViewId);
        }
    }

    private void DeactivateHostedViews()
    {
        foreach (var view in HostedViews)
        {
            view.IsActive = false;
        }

        SetHostedView(null);
    }

    private void SetHostedView(object? hostedView)
    {
        if (ReferenceEquals(_hostedView, hostedView))
        {
            return;
        }

        if (!SetProperty(ref _hostedView, hostedView, nameof(HostedView)))
        {
            return;
        }

        OnPropertyChanged(nameof(HasHostedView));
        OnPropertyChanged(nameof(ShowFallbackLines));
    }
}
