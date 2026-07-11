namespace Sunder.Runtime.Contracts;

public sealed record PackageLifecycleStageRequest(
    IReadOnlyList<string> PackageIds,
    PackageLifecycleOverlayOwner OverlayOwner = PackageLifecycleOverlayOwner.HotReload)
{
    public PackageLifecycleStageRequest(
        IReadOnlyList<PackageSessionLoadRequest> packages,
        PackageLifecycleOverlayOwner overlayOwner = PackageLifecycleOverlayOwner.HotReload)
        : this(packages.Select(package => package.PackageId).ToArray(), overlayOwner)
    {
    }
}
