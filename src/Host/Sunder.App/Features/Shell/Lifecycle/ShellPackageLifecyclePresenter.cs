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
    Action<IReadOnlySet<RailPlacement>, IReadOnlySet<string>, bool> updateRailCollections,
    Action persistShellState)
{
    public void ApplyLifecycleChanges(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyCollection<string>? impactedPackageIds,
        bool deferHostedViewCreation)
    {
        if (impactedPackageIds is not null)
        {
            ApplyTargetedLifecycleChanges(activePackages, impactedPackageIds, deferHostedViewCreation);
            return;
        }

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

    public void ApplyLifecycleChanges(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        bool deferHostedViewCreation)
        => ApplyLifecycleChanges(activePackages, impactedPackageIds: null, deferHostedViewCreation);

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

    private void ApplyTargetedLifecycleChanges(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyCollection<string> impactedPackageIds,
        bool deferHostedViewCreation)
    {
        var shellSnapshot = shellCompositionService.Compose(
            activePackages,
            shellState,
            systemStatus: null,
            startupWarnings,
            startupErrors);
        var impactedPackages = BuildEffectiveImpactedPackageIds(activePackages, impactedPackageIds);

        setSyncStatusText(shellSnapshot.SyncStatusText);
        if (impactedPackages.Count == 0)
        {
            persistShellState();
            return;
        }

        var oldViews = viewsById.Values
            .Where(view => impactedPackages.Contains(view.PackageId))
            .ToArray();
        var newViews = shellSnapshot.PackageViews
            .Where(view => impactedPackages.Contains(view.PackageId))
            .ToArray();
        var affectedPlacements = oldViews
            .Select(view => view.Placement)
            .Concat(newViews.Select(view => view.Placement))
            .ToHashSet();

        foreach (var viewId in viewsById
                     .Where(entry => impactedPackages.Contains(entry.Value.PackageId))
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            viewsById.Remove(viewId);
        }

        foreach (var view in newViews)
        {
            viewsById[view.ViewId] = view;
        }

        updateRailCollections(affectedPlacements, impactedPackages, !deferHostedViewCreation);
        persistShellState();
    }

    private HashSet<string> BuildEffectiveImpactedPackageIds(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyCollection<string> impactedPackageIds)
    {
        var impactedPackages = new HashSet<string>(impactedPackageIds, StringComparer.OrdinalIgnoreCase);
        var activePackageIds = activePackages
            .Select(package => package.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingPackageIds = viewsById.Values
            .Select(view => view.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var removedPackageId in existingPackageIds.Where(packageId => !activePackageIds.Contains(packageId)))
        {
            impactedPackages.Add(removedPackageId);
        }

        foreach (var addedPackageId in activePackageIds.Where(packageId => !existingPackageIds.Contains(packageId)))
        {
            impactedPackages.Add(addedPackageId);
        }

        return impactedPackages;
    }
}
