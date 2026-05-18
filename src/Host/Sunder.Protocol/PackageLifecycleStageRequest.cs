namespace Sunder.Protocol;

public sealed record PackageLifecycleStageRequest(
    IReadOnlyList<PackageSessionLoadRequest> Packages,
    PackageLifecycleOverlayOwner OverlayOwner = PackageLifecycleOverlayOwner.HotReload);
