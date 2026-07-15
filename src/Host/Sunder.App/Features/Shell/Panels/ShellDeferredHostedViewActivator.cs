using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Features.Shell.Panels;

internal sealed class ShellDeferredHostedViewActivator(
    ShellState shellState,
    Func<bool> isDisposed,
    Func<RailPlacement, ShellPanelViewModel> getPanel,
    Action<RailPlacement, string?, bool> applyPanelContent,
    Func<string, CancellationToken, Task> notifyViewNavigatedAsync,
    Action notifyLayoutStateChanged,
    IUiDispatcher? uiDispatcher = null
)
{
    private readonly IUiDispatcher _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;

    public async Task ActivateInitialHostedViewsAsync(
        Func<Task>? waitForAttachmentAsync = null,
        CancellationToken cancellationToken = default
    )
    {
        var activatedViewIds = new List<string>();
        foreach (
            var (placement, viewId) in ShellSelectionState.GetDeferredActivationSelections(
                shellState
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(viewId))
            {
                continue;
            }

            if (
                await _uiDispatcher
                    .InvokeAsync(() => ActivateHostedView(placement, viewId), cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                activatedViewIds.Add(viewId);
            }
        }

        if (waitForAttachmentAsync is not null)
        {
            await waitForAttachmentAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var viewId in activatedViewIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppSessionLog.WriteInfo($"Initial package view '{viewId}' is ready for navigation.");
            await notifyViewNavigatedAsync(viewId, cancellationToken).ConfigureAwait(false);
            AppSessionLog.WriteInfo($"Initial package view '{viewId}' navigation completed.");
        }
    }

    private bool ActivateHostedView(RailPlacement placement, string viewId)
    {
        if (
            isDisposed()
            || !string.Equals(
                ShellSelectionState.GetSelectedViewId(shellState, placement),
                viewId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }

        var panel = getPanel(placement);
        if (panel.HostedView is not null)
        {
            return false;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        applyPanelContent(placement, viewId, true);
        AppSessionLog.WriteInfo(
            $"Deferred package view '{viewId}' activated in {stopwatch.ElapsedMilliseconds} ms."
        );
        notifyLayoutStateChanged();
        return panel.HostedView is not null;
    }
}
