using Sunder.App.Features.Shell.Layout;
using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Features.Shell.Panels;

internal sealed class ShellPackagePanelCoordinator(
    IReadOnlyDictionary<string, ShellPackageView> viewsById,
    ShellState shellState,
    PackageViewHostService packageViewHostService,
    ShellSelectionPresenter selectionPresenter,
    Func<RailPlacement, PackageIconBarViewModel> getBar,
    Func<RailPlacement, ShellPanelViewModel> getPanel,
    Action<RailPlacement, string?, bool> applyPanelContent,
    Func<string, bool> isViewInHotbar,
    Func<
        string,
        bool,
        IReadOnlyDictionary<string, string?>?,
        ValueTask<bool>
    > addViewToDefaultHotbarAsync,
    Action<string> notifyViewNavigated,
    Action<bool> rebuildRailCollections,
    Action notifyLayoutStateChanged,
    Action persistShellState
)
{
    public async ValueTask<bool> ReloadPackageViewAsync(string viewId)
    {
        if (!viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        var placement = packageView.Placement;
        var panel = getPanel(placement);
        var isOpen = string.Equals(
            ShellSelectionState.GetSelectedViewId(shellState, placement),
            viewId,
            StringComparison.OrdinalIgnoreCase
        );
        panel.RemoveHostedView(viewId);
        if (!isOpen)
        {
            packageViewHostService.InvalidateView(viewId);
            return true;
        }

        var reloadedView = packageViewHostService.CreateHostedViewBoundary(
            packageView.PackageId,
            viewId,
            packageViewHostService.ReloadView(viewId)
        );
        if (isOpen)
        {
            panel.SetActiveView(viewId, reloadedView);
        }

        if (reloadedView is not null)
        {
            await packageViewHostService.NotifyViewNavigatedAsync(viewId, parameters: null);
        }

        return reloadedView is not null;
    }

    public async ValueTask<bool> OpenPackageViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null
    )
    {
        if (!viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        if (
            !isViewInHotbar(viewId) && !await addViewToDefaultHotbarAsync(viewId, false, parameters)
        )
        {
            return false;
        }

        packageView = viewsById[viewId];
        var item = getBar(packageView.Placement).Items.FirstOrDefault(x => x.Id == viewId);
        if (item is null)
        {
            rebuildRailCollections(true);
            item = getBar(packageView.Placement).Items.FirstOrDefault(x => x.Id == viewId);
        }

        if (item is null)
        {
            return false;
        }

        if (
            !string.Equals(
                ShellSelectionState.GetSelectedViewId(shellState, packageView.Placement),
                viewId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            SelectItem(item, allowToggle: false, notifyNavigation: false);
        }

        await packageViewHostService.NotifyViewNavigatedAsync(viewId, parameters);
        return true;
    }

    public bool ClosePackageViewPanel(string viewId)
    {
        if (!viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        if (
            !string.Equals(
                ShellSelectionState.GetSelectedViewId(shellState, packageView.Placement),
                viewId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return true;
        }

        packageViewHostService.CancelViewNavigation(viewId);
        var bar = getBar(packageView.Placement);
        var fallback = ShellPanelCloseSelector.FindFallbackItem(
            packageView.Placement,
            bar.Items,
            viewId
        );

        if (fallback is not null)
        {
            selectionPresenter.Select(bar, packageView.Placement, fallback);
            ShellSelectionState.SetSelectedViewId(shellState, packageView.Placement, fallback.Id);
            applyPanelContent(packageView.Placement, fallback.Id, true);
            notifyViewNavigated(fallback.Id);
        }
        else
        {
            selectionPresenter.Clear(bar, packageView.Placement);
            ShellSelectionState.SetSelectedViewId(shellState, packageView.Placement, null);
            applyPanelContent(packageView.Placement, null, true);
        }

        notifyLayoutStateChanged();
        persistShellState();
        return true;
    }

    public void SelectItem(ShellItemViewModel item, bool allowToggle, bool notifyNavigation = true)
    {
        if (!viewsById.TryGetValue(item.Id, out var packageView))
        {
            return;
        }

        var placement = packageView.Placement;
        var bar = getBar(placement);
        var selectedItem = selectionPresenter.GetSelectedItem(placement);

        if (allowToggle && ReferenceEquals(selectedItem, item))
        {
            packageViewHostService.CancelViewNavigation(item.Id);
            selectionPresenter.Clear(bar, placement);
            ShellSelectionState.SetSelectedViewId(shellState, placement, null);
            applyPanelContent(placement, null, true);
            notifyLayoutStateChanged();
            persistShellState();
            return;
        }

        var panel = getPanel(placement);
        if (
            !allowToggle
            && ReferenceEquals(selectedItem, item)
            && string.Equals(panel.ActiveViewId, item.Id, StringComparison.OrdinalIgnoreCase)
            && panel.HostedView is not null
        )
        {
            return;
        }

        if (selectedItem is not null && !ReferenceEquals(selectedItem, item))
        {
            packageViewHostService.CancelViewNavigation(selectedItem.Id);
        }
        selectionPresenter.Select(bar, placement, item);
        ShellSelectionState.SetSelectedViewId(shellState, placement, item.Id);
        applyPanelContent(placement, item.Id, true);
        notifyLayoutStateChanged();
        persistShellState();
        if (notifyNavigation)
        {
            notifyViewNavigated(item.Id);
        }
    }
}
