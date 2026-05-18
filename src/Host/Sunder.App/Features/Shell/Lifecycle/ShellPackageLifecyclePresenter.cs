using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Protocol;

namespace Sunder.App.Features.Shell.Lifecycle;

internal sealed class ShellPackageLifecyclePresenter(
    IShellCompositionService shellCompositionService,
    IDictionary<string, ShellPackageView> viewsById,
    ShellState shellState,
    IReadOnlyList<string> startupWarnings,
    IReadOnlyList<string> startupErrors,
    Action<string> setSyncStatusText,
    Action<bool> rebuildRailCollections,
    Action persistShellState)
{
    public void ApplyLifecycleChanges(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        bool deferHostedViewCreation)
    {
        var activePackageIds = activePackages
            .Select(package => package.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedPackageIds = viewsById.Values
            .Select(view => view.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(packageId => !activePackageIds.Contains(packageId))
            .ToArray();
        foreach (var packageId in removedPackageIds)
        {
            RemovePackageViewsFromShell(packageId);
        }

        ApplyActivePackages(activePackages, deferHostedViewCreation);
    }

    public bool RemovePackageViewsFromShell(string packageId)
        => ShellPackageRemovalPruner.RemovePackageViews(packageId, viewsById, shellState);

    private void ApplyActivePackages(IReadOnlyList<ActivePackageDescriptor> activePackages, bool deferHostedViewCreation)
    {
        var shellSnapshot = shellCompositionService.Compose(
            activePackages,
            shellState,
            systemStatus: null,
            startupWarnings,
            startupErrors);

        viewsById.Clear();
        foreach (var view in shellSnapshot.PackageViews)
        {
            viewsById[view.ViewId] = view;
        }

        setSyncStatusText(shellSnapshot.SyncStatusText);
        rebuildRailCollections(!deferHostedViewCreation);
        persistShellState();
    }
}
