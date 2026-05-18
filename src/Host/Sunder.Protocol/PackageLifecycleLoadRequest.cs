namespace Sunder.Protocol;

public sealed record PackageLifecycleLoadRequest(
    IReadOnlyList<PackageSessionLoadRequest> Packages,
    PackageLifecycleOverlayOwner OverlayOwner = PackageLifecycleOverlayOwner.Startup);
