namespace Sunder.Runtime.Contracts;

public sealed record PackageLifecycleLoadRequest(
    IReadOnlyList<string> PackageIds,
    PackageLifecycleOverlayOwner OverlayOwner = PackageLifecycleOverlayOwner.Startup)
{
    public PackageLifecycleLoadRequest(
        IReadOnlyList<PackageSessionLoadRequest> packages,
        PackageLifecycleOverlayOwner overlayOwner = PackageLifecycleOverlayOwner.Startup)
        : this(packages.Select(package => package.PackageId).ToArray(), overlayOwner)
    {
    }
}
