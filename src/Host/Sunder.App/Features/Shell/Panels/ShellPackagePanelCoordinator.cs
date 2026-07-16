using Avalonia.Controls;
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
    Action persistShellState,
    Func<string, IReadOnlyDictionary<string, string?>?, ValueTask<bool>>? presentViewAsync = null,
    Action<string>? cancelViewNavigation = null,
    Action<RailPlacement, string>? closePanel = null
)
{
    private readonly Action<string> _cancelViewNavigation =
        cancelViewNavigation ?? packageViewHostService.CancelViewNavigation;
    private readonly Action<RailPlacement, string>? _closePanel = closePanel;

    public async ValueTask<bool> ReloadPackageViewAsync(
        string viewId,
        Func<Func<CancellationToken, Task>, CancellationToken, Task>? runDeferredAsync = null,
        Func<bool>? canContinue = null,
        Func<ValueTask<bool>>? presentReloadedViewAsync = null,
        CancellationToken cancellationToken = default)
    {
        if (!viewsById.TryGetValue(viewId, out var packageView))
        {
            return false;
        }

        var generationId = packageViewHostService.CurrentGenerationId;
        var packageId = packageView.PackageId;
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
            if (runDeferredAsync is null)
            {
                await packageViewHostService.InvalidateViewAsync(viewId, cancellationToken);
            }
            else
            {
                await runDeferredAsync(
                    async operationCancellation =>
                    {
                        await packageViewHostService.InvalidateViewAsync(
                            viewId,
                            operationCancellation);
                    },
                    cancellationToken);
            }
            return true;
        }

        Control? reloadedView = null;
        try
        {
            if (runDeferredAsync is null)
            {
                reloadedView = await packageViewHostService.ReloadViewAsync(
                    viewId,
                    generationId,
                    cancellationToken);
            }
            else
            {
                await runDeferredAsync(
                    async operationCancellation =>
                    {
                        if (canContinue is not null && !canContinue())
                        {
                            return;
                        }

                        reloadedView = await packageViewHostService.ReloadViewAsync(
                            viewId,
                            generationId,
                            operationCancellation);
                    },
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (
            canContinue is not null
            && !canContinue()
            && !string.Equals(
                ShellSelectionState.GetSelectedViewId(shellState, placement),
                viewId,
                StringComparison.OrdinalIgnoreCase))
        {
            await packageViewHostService.InvalidateViewAsync(
                viewId,
                generationId,
                CancellationToken.None);
            return false;
        }

        if (reloadedView is null)
        {
            if (canContinue is not null
                && !canContinue()
                && !string.Equals(
                    ShellSelectionState.GetSelectedViewId(shellState, placement),
                    viewId,
                    StringComparison.OrdinalIgnoreCase))
            {
                await packageViewHostService.InvalidateViewAsync(
                    viewId,
                    generationId,
                    CancellationToken.None);
            }
            return false;
        }

        if (packageViewHostService.CurrentGenerationId != generationId
            || !viewsById.TryGetValue(viewId, out var currentView)
            || !string.Equals(currentView.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
            || canContinue is not null && !canContinue()
            || !string.Equals(
                ShellSelectionState.GetSelectedViewId(shellState, placement),
                viewId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return presentReloadedViewAsync is not null
            ? await presentReloadedViewAsync()
            : presentViewAsync is null
            ? await NavigateViewAsync(viewId, parameters: null)
            : await presentViewAsync(viewId, null);
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
            rebuildRailCollections(presentViewAsync is null);
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

        return presentViewAsync is null
            ? await NavigateViewAsync(viewId, parameters)
            : await presentViewAsync(viewId, parameters);
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
            if (presentViewAsync is null)
            {
                applyPanelContent(packageView.Placement, fallback.Id, true);
            }
            notifyLayoutStateChanged();
            notifyViewNavigated(fallback.Id);
        }
        else
        {
            selectionPresenter.Clear(bar, packageView.Placement);
            ShellSelectionState.SetSelectedViewId(shellState, packageView.Placement, null);
            if (_closePanel is not null)
            {
                _closePanel(packageView.Placement, viewId);
            }
            else
            {
                applyPanelContent(packageView.Placement, null, true);
                notifyLayoutStateChanged();
            }
        }

        persistShellState();
        _cancelViewNavigation(viewId);
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
            selectionPresenter.Clear(bar, placement);
            ShellSelectionState.SetSelectedViewId(shellState, placement, null);
            if (_closePanel is not null)
            {
                _closePanel(placement, item.Id);
            }
            else
            {
                applyPanelContent(placement, null, true);
                notifyLayoutStateChanged();
            }
            persistShellState();
            _cancelViewNavigation(item.Id);
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

        var replacedViewId = selectedItem is not null && !ReferenceEquals(selectedItem, item)
            ? selectedItem.Id
            : null;
        selectionPresenter.Select(bar, placement, item);
        ShellSelectionState.SetSelectedViewId(shellState, placement, item.Id);
        if (presentViewAsync is null)
        {
            applyPanelContent(placement, item.Id, true);
            notifyLayoutStateChanged();
        }
        persistShellState();
        if (replacedViewId is not null)
        {
            _cancelViewNavigation(replacedViewId);
        }
        if (notifyNavigation)
        {
            notifyViewNavigated(item.Id);
        }
    }

    private async ValueTask<bool> NavigateViewAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters)
    {
        await packageViewHostService.NotifyViewNavigatedAsync(viewId, parameters);
        return true;
    }
}
