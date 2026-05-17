using Avalonia.Threading;
using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class ShellDeferredHostedViewActivator(
    ShellState shellState,
    Func<bool> isDisposed,
    Func<RailPlacement, ShellPanelViewModel> getPanel,
    Action<RailPlacement, string?, bool> applyPanelContent,
    Action notifyLayoutStateChanged)
{
    public async Task ActivateInitialHostedViewsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (placement, viewId) in ShellSelectionState.GetDeferredActivationSelections(shellState))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(viewId))
            {
                continue;
            }

            await Dispatcher.UIThread.InvokeAsync(
                () => ActivateHostedView(placement, viewId),
                DispatcherPriority.Background);
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ActivateAfterLifecycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ActivateInitialHostedViewsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to activate deferred package views after package lifecycle changes.", ex);
        }
    }

    private void ActivateHostedView(RailPlacement placement, string viewId)
    {
        if (isDisposed() || !string.Equals(ShellSelectionState.GetSelectedViewId(shellState, placement), viewId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var panel = getPanel(placement);
        if (panel.HostedView is not null)
        {
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        applyPanelContent(placement, viewId, true);
        AppSessionLog.WriteInfo(
            $"Deferred package view '{viewId}' activated in {stopwatch.ElapsedMilliseconds} ms.");
        notifyLayoutStateChanged();
    }
}
