using Avalonia;
using Sunder.App.Features.Shell.Panels;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Features.Shell.Lifecycle;

internal sealed class ShellPackageLifecycleRefreshCoordinator(
    AppPackageLifecycleCoordinator packageLifecycleCoordinator,
    ShellPackageLifecyclePresenter packageLifecyclePresenter,
    ShellDeferredHostedViewActivator deferredHostedViewActivator,
    Func<bool> isDisposed,
    IUiDispatcher? uiDispatcher = null)
{
    private readonly AppPackageLifecycleGate _packageLifecycleGate = new(nameof(ShellPackageLifecycleRefreshCoordinator));
    private readonly IUiDispatcher _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;

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
        await ApplyPackageLifecycleChangesToShellAsync(activePackages, impactedPackageIds, deferHostedViewCreation).ConfigureAwait(false);
        if (deferHostedViewCreation)
        {
            await deferredHostedViewActivator.ActivateAfterLifecycleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyPackageLifecycleChangesToShellAsync(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyCollection<string>? impactedPackageIds,
        bool deferHostedViewCreation)
    {
        if (_uiDispatcher.CheckAccess() || Application.Current is null)
        {
            packageLifecyclePresenter.ApplyLifecycleChanges(activePackages, impactedPackageIds, deferHostedViewCreation);
            return;
        }

        await _uiDispatcher.InvokeAsync(
            () => packageLifecyclePresenter.ApplyLifecycleChanges(activePackages, impactedPackageIds, deferHostedViewCreation));
    }
}
