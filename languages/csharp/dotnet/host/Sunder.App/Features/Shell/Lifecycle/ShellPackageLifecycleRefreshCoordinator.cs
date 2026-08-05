using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Features.Shell.Lifecycle;

internal sealed class ShellPackageLifecycleRefreshCoordinator(
    AppPackageLifecycleCoordinator packageLifecycleCoordinator,
    ShellPackageLifecyclePresenter packageLifecyclePresenter,
    Func<bool> isDisposed,
    Action<Control> stageCandidateView,
    Action detachStagedCandidateViews,
    IUiDispatcher uiDispatcher,
    Action? beginPresentationCommit = null,
    Action? completePresentationCommit = null)
{
    private readonly AppPackageLifecycleGate _packageLifecycleGate = new(nameof(ShellPackageLifecycleRefreshCoordinator));

    public async Task ApplyPackageLifecycleChangesAsync(
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<PackageUiSnapshotDescriptor> packageSources,
        IReadOnlyCollection<string>? retryDisabledPackageIds = null,
        CancellationToken cancellationToken = default,
        Action? detachAuxiliaryPackageViews = null)
    {
        using var lifecycle = await _packageLifecycleGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (isDisposed())
        {
            return;
        }

        try
        {
            await packageLifecycleCoordinator.ApplyPackageSnapshotAsync(
                snapshot,
                packageSources,
                retryDisabledPackageIds,
                async (candidate, activePackages, prepareCancellationToken) =>
                {
                    var presentation = uiDispatcher.CheckAccess()
                        ? packageLifecyclePresenter.PrepareLifecycleChanges(activePackages)
                        : await uiDispatcher.InvokeAsync(
                            () => packageLifecyclePresenter.PrepareLifecycleChanges(activePackages));
                    var stabilizedViewIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var viewId in presentation.SelectedViewIds)
                    {
                        prepareCancellationToken.ThrowIfCancellationRequested();
                        await candidate.StabilizeViewAsync(
                            viewId,
                            parameters: null,
                            stageCandidateView,
                            prepareCancellationToken).ConfigureAwait(false);
                        stabilizedViewIds.Add(viewId);
                    }

                    return () =>
                    {
                        beginPresentationCommit?.Invoke();
                        try
                        {
                            detachStagedCandidateViews();
                            detachAuxiliaryPackageViews?.Invoke();
                            packageLifecyclePresenter.CommitPreparedLifecycleChanges(presentation, stabilizedViewIds);
                        }
                        finally
                        {
                            completePresentationCommit?.Invoke();
                        }
                    };
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (uiDispatcher.CheckAccess())
            {
                detachStagedCandidateViews();
            }
            else
            {
                await uiDispatcher.InvokeAsync(detachStagedCandidateViews).ConfigureAwait(false);
            }
        }
    }

}
