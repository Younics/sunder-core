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
    Action<IReadOnlySet<string>>? removeRetainedPackageViews = null)
{
    public ShellPackageLifecyclePresentation PrepareLifecycleChanges(
        IReadOnlyList<ActivePackageDescriptor> activePackages)
    {
        var projectedState = CloneProjectionState(shellState);
        var shellSnapshot = shellCompositionService.Compose(
            activePackages,
            projectedState,
            systemStatus: null,
            startupWarnings,
            startupErrors,
            ShellNormalizationPolicy.AuthoritativeRuntimeSnapshot);
        return new ShellPackageLifecyclePresentation(shellSnapshot, GetSelectedViewIds(shellSnapshot));
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
