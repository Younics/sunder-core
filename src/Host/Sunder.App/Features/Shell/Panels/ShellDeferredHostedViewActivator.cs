using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Features.Shell.Panels;

internal sealed class ShellDeferredHostedViewActivator(
    ShellState shellState,
    Func<bool> isDisposed,
    Func<RailPlacement, string, CancellationToken, Task<bool>> prepareHostedViewAsync,
    Func<string, CancellationToken, Task> notifyViewNavigatedAsync,
    Action notifyLayoutStateChanged
)
{
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

            if (!isDisposed()
                && string.Equals(
                    ShellSelectionState.GetSelectedViewId(shellState, placement),
                    viewId,
                    StringComparison.OrdinalIgnoreCase)
                && await prepareHostedViewAsync(placement, viewId, cancellationToken)
                    .ConfigureAwait(false))
            {
                activatedViewIds.Add(viewId);
                notifyLayoutStateChanged();
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

}
