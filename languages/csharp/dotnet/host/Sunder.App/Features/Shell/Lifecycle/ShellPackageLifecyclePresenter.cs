using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Features.Shell.Lifecycle;

internal sealed class ShellPackageLifecyclePresenter(
    IShellCompositionService shellCompositionService,
    IDictionary<string, ShellPackageView> viewsById,
    ShellState shellState,
    IReadOnlyList<string> startupWarnings,
    IReadOnlyList<string> startupErrors,
    Action<string> setSyncStatusText,
    Action<bool, IReadOnlySet<string>?> rebuildRailCollections,
    Action persistShellState,
    Action<IReadOnlySet<string>>? removeRetainedPackageViews = null,
    Action<IReadOnlySet<string>>? refreshPackageMetadata = null,
    Func<string, bool>? isPackageAppUiQuarantined = null)
{
    public ShellPackageLifecyclePresentation PrepareLifecycleChanges(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<ActivePackageDescriptor>? runtimeActivePackages = null)
    {
        var projectedState = CloneProjectionState(shellState);
        var shellSnapshot = shellCompositionService.Compose(
            IncludeQuarantinedPackages(activePackages, runtimeActivePackages),
            projectedState,
            systemStatus: null,
            startupWarnings,
            startupErrors,
            ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);
        var stabilizableViewIds = activePackages
            .SelectMany(package => package.Views)
            .Select(view => view.ViewId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ShellPackageLifecyclePresentation(
            shellSnapshot,
            GetSelectedViewIds(shellSnapshot)
                .Where(stabilizableViewIds.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private IReadOnlyList<ActivePackageDescriptor> IncludeQuarantinedPackages(
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<ActivePackageDescriptor>? runtimeActivePackages)
    {
        if (isPackageAppUiQuarantined is null || runtimeActivePackages is null)
        {
            return activePackages;
        }

        var includedPackageIds = activePackages
            .Select(package => package.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var presentationPackages = activePackages.ToList();
        foreach (var package in runtimeActivePackages)
        {
            if (includedPackageIds.Contains(package.PackageId)
                || !isPackageAppUiQuarantined(package.PackageId))
            {
                continue;
            }

            var retainedViews = viewsById.Values
                .Where(view => string.Equals(
                    view.PackageId,
                    package.PackageId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(view => new PackageViewDescriptor(
                    view.ViewId,
                    view.PackageId,
                    view.Title,
                    view.Icon,
                    view.Placement.ToString(),
                    view.ShowInHotbarByDefault))
                .ToArray();
            if (retainedViews.Length > 0)
            {
                presentationPackages.Add(package with { Views = retainedViews });
            }
        }
        return presentationPackages;
    }

    public void CommitPreparedLifecycleChanges(
        ShellPackageLifecyclePresentation presentation,
        IReadOnlySet<string> stabilizedViewIds)
    {
        ApplyProjectionState(presentation.Snapshot.State, shellState);
        DetachRetainedPackageViews();
        viewsById.Clear();
        foreach (var view in presentation.Snapshot.PackageViews)
        {
            viewsById[view.ViewId] = view;
        }

        setSyncStatusText(presentation.Snapshot.SyncStatusText);
        rebuildRailCollections(true, stabilizedViewIds);
        persistShellState();
    }

    public void ApplyPackageMetadata(IReadOnlyList<ActivePackageDescriptor> activePackages)
    {
        var packages = activePackages.ToDictionary(
            static package => package.PackageId,
            StringComparer.OrdinalIgnoreCase);
        var impactedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (viewId, view) in viewsById.ToArray())
        {
            if (packages.TryGetValue(view.PackageId, out var package)
                && view.Readiness != package.Readiness)
            {
                viewsById[viewId] = view with { Readiness = package.Readiness };
                impactedPackageIds.Add(view.PackageId);
            }
        }
        if (impactedPackageIds.Count > 0)
        {
            refreshPackageMetadata?.Invoke(impactedPackageIds);
        }
    }

    public bool RemovePackageViewsFromShell(string packageId)
        => ShellPackageRemovalPruner.RemovePackageViews(packageId, viewsById, shellState);

    public void DetachRetainedPackageViews()
        => removeRetainedPackageViews?.Invoke(
            viewsById.Values
                .Select(view => view.PackageId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static ShellState CloneProjectionState(ShellState source)
        => new()
        {
            HasInitializedLayout = source.HasInitializedLayout,
            ViewPlacements = new Dictionary<string, RailPlacement>(source.ViewPlacements, StringComparer.OrdinalIgnoreCase),
            ViewOrder = new Dictionary<string, int>(source.ViewOrder, StringComparer.OrdinalIgnoreCase),
            HiddenHotbarViewIds = new HashSet<string>(source.HiddenHotbarViewIds, StringComparer.OrdinalIgnoreCase),
            SelectedLeftTopViewId = source.SelectedLeftTopViewId,
            SelectedMiddleViewId = source.SelectedMiddleViewId,
            SelectedRightTopViewId = source.SelectedRightTopViewId,
            SelectedLeftBottomViewId = source.SelectedLeftBottomViewId,
            SelectedRightBottomViewId = source.SelectedRightBottomViewId,
        };

    private static IReadOnlySet<string> GetSelectedViewIds(ShellSnapshot snapshot)
    {
        var activeViewIds = snapshot.PackageViews
            .Select(view => view.ViewId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ShellSelectionState.GetDeferredActivationSelections(snapshot.State)
            .Select(selection => selection.ViewId)
            .Where(viewId => !string.IsNullOrWhiteSpace(viewId) && activeViewIds.Contains(viewId))
            .Select(viewId => viewId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplyProjectionState(ShellState projection, ShellState target)
    {
        target.ViewPlacements = projection.ViewPlacements;
        target.ViewOrder = projection.ViewOrder;
        target.HiddenHotbarViewIds = projection.HiddenHotbarViewIds;
        target.SelectedLeftTopViewId = projection.SelectedLeftTopViewId;
        target.SelectedMiddleViewId = projection.SelectedMiddleViewId;
        target.SelectedRightTopViewId = projection.SelectedRightTopViewId;
        target.SelectedLeftBottomViewId = projection.SelectedLeftBottomViewId;
        target.SelectedRightBottomViewId = projection.SelectedRightBottomViewId;
    }

}

internal sealed record ShellPackageLifecyclePresentation(
    ShellSnapshot Snapshot,
    IReadOnlySet<string> SelectedViewIds);
