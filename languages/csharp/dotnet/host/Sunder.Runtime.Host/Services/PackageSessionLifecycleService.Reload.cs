using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageSessionLifecycleService
{
    internal Task<PackageLifecycleStageResult> StageOwnerReloadAsync(string ownerId, string packageId,
        string contentIdentity, CancellationToken token)
        => StageCoreAsync(new([packageId]), developmentWorkerContinuation: false, token,
            new(ownerId, packageId, contentIdentity));

    private static void RequireOwnedReloadFolder(PackageSessionSourceSnapshot sources, DevelopmentOwnerReload reload)
    {
        var owned = sources.DevOverlays.SingleOrDefault(overlay => overlay.Owner == PackageSessionOverlayOwner.AppInvocation
            && overlay.OwnerId == reload.OwnerId && overlay.PackageId == reload.PackageId);
        var active = sources.ActiveDevOverlays.SingleOrDefault(overlay => overlay.PackageId == reload.PackageId);
        if (owned is null || active is null || !PathsEqual(owned.Folder, active.Folder))
            throw new RuntimeConflictException("This owner no longer selects the active development package folder.");
        // Capture the authorized folder, not a later lookup after preparation.
        reload.Folder = owned.Folder;
    }

    private static bool MatchesOwnedReload(IReadOnlyList<RuntimePackageSource> sources, DevelopmentOwnerReload reload)
        => sources.Any(source => source.Kind == PackageSourceKind.Dev && source.PackageId == reload.PackageId
            && PathsEqual(source.SourceFolder, reload.Folder!) && source.ContentIdentity == reload.ContentIdentity);

    private sealed record DevelopmentOwnerReload(string OwnerId, string PackageId, string ContentIdentity)
    {
        internal string? Folder { get; set; }
    }
}
