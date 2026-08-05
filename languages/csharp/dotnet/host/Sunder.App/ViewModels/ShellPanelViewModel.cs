using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Views.Controls;

namespace Sunder.App.ViewModels;

public sealed partial class ShellPanelViewModel : ViewModelBase
{
    public ObservableCollection<string> Lines { get; } = [];

    public ObservableCollection<ShellHostedViewViewModel> HostedViews { get; } = [];

    public bool HasHostedView => HostedView is not null;

    public bool HasLayoutHostedView => HostedViews.Any(view => view.IsLayoutVisible);

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

    private bool _isDockStaged;

    public bool IsDockStaged => _isDockStaged;

    public bool IsDockLayoutVisible => IsDockVisible || IsDockStaged;

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
            view.IsStaged = false;
        }

        SetHostedView(retainedView.View);
        OnPropertyChanged(nameof(HasLayoutHostedView));
    }

    public object RetainHostedView(string viewId, object hostedView)
    {
        var retainedView = HostedViews.FirstOrDefault(view =>
            string.Equals(view.ViewId, viewId, StringComparison.OrdinalIgnoreCase));
        if (retainedView is null)
        {
            retainedView = new ShellHostedViewViewModel(viewId, hostedView);
            HostedViews.Add(retainedView);
            OnPropertyChanged(nameof(HasLayoutHostedView));
            return hostedView;
        }

        if (!ReferenceEquals(retainedView.View, hostedView))
        {
            HostedPackageViewBoundary.ReleaseHostedView(hostedView);
        }

        return retainedView.View;
    }

    public bool StageHostedView(string viewId)
    {
        var stagedView = HostedViews.FirstOrDefault(view => string.Equals(
            view.ViewId,
            viewId,
            StringComparison.OrdinalIgnoreCase));
        if (stagedView is null)
        {
            return false;
        }

        foreach (var view in HostedViews)
        {
            view.IsStaged = ReferenceEquals(view, stagedView);
        }
        OnPropertyChanged(nameof(HasLayoutHostedView));
        return true;
    }

    public void UnstageHostedView(string viewId)
    {
        var stagedView = HostedViews.FirstOrDefault(view => string.Equals(
            view.ViewId,
            viewId,
            StringComparison.OrdinalIgnoreCase));
        if (stagedView?.IsStaged != true)
        {
            return;
        }

        stagedView.IsStaged = false;
        OnPropertyChanged(nameof(HasLayoutHostedView));
    }

    public void ClearActiveView()
    {
        ActiveViewId = null;
        DeactivateHostedViews();
    }

    public void SetDockVisible(bool isVisible)
    {
        if (SetProperty(ref _isDockVisible, isVisible, nameof(IsDockVisible)))
        {
            OnPropertyChanged(nameof(IsDockLayoutVisible));
        }
    }

    public void SetDockStaged(bool isStaged)
    {
        if (SetProperty(ref _isDockStaged, isStaged, nameof(IsDockStaged)))
        {
            OnPropertyChanged(nameof(IsDockLayoutVisible));
        }
    }

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
        OnPropertyChanged(nameof(HasLayoutHostedView));
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
            view.IsStaged = false;
        }

        SetHostedView(null);
        OnPropertyChanged(nameof(HasLayoutHostedView));
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
