using Avalonia;
using Avalonia.Threading;
using Sunder.App.Services;
using Sunder.Protocol;

namespace Sunder.App.ViewModels;

internal sealed class ShellPackageLifecycleRefreshCoordinator(
    AppPackageLifecycleCoordinator packageLifecycleCoordinator,
    ShellPackageLifecyclePresenter packageLifecyclePresenter,
    ShellDeferredHostedViewActivator deferredHostedViewActivator,
    Func<bool> isDisposed)
{
    private readonly AppPackageLifecycleGate _packageLifecycleGate = new(nameof(ShellPackageLifecycleRefreshCoordinator));

    public async Task ApplyPackageLifecycleChangesAsync(
        IReadOnlyCollection<string>? impactedPackageIds = null,
        CancellationToken cancellationToken = default,
        bool deferHostedViewCreation = false)
    {
        using var lifecycle = await _packageLifecycleGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (isDisposed())
        {
            return;
        }

        var activePackages = await packageLifecycleCoordinator.ApplyPackageDeltaFromRuntimeAsync(impactedPackageIds, cancellationToken).ConfigureAwait(false);
        await ApplyPackageLifecycleChangesToShellAsync(activePackages, deferHostedViewCreation).ConfigureAwait(false);
        if (deferHostedViewCreation)
        {
            _ = deferredHostedViewActivator.ActivateAfterLifecycleAsync(cancellationToken);
        }
    }

    private async Task ApplyPackageLifecycleChangesToShellAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        bool deferHostedViewCreation)
    {
        if (Dispatcher.UIThread.CheckAccess() || Application.Current is null)
        {
            packageLifecyclePresenter.ApplyLifecycleChanges(activePackages, deferHostedViewCreation);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            () => packageLifecyclePresenter.ApplyLifecycleChanges(activePackages, deferHostedViewCreation),
            DispatcherPriority.Normal);
    }
}
