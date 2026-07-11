using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionCommandService(
    PackageSessionLifecycleService sessions,
    InstalledPackageLifecycleService installedPackages)
{
    public async Task<PackageSessionOperationResult> LoadAsync(PackageSessionLoadRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId)) return PackageSessionOperationResult.Failed("A package id is required.");
        if (request.SourceKind != PackageSourceKind.Installed) return PackageSessionOperationResult.Failed("Dev package paths are Runtime startup inputs and cannot be loaded through the Runtime API.");
        return await MapAsync(await installedPackages.SetEnabledAsync(request.PackageId.Trim(), true, cancellationToken), request.PackageId, cancellationToken);
    }

    public async Task<PackageSessionOperationResult> UnloadAsync(string packageId, PackageSourceKind sourceKind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return PackageSessionOperationResult.Failed("A package id is required.");
        if (sourceKind == PackageSourceKind.Dev) return await sessions.UnloadDevPackageAsync(packageId, cancellationToken);
        if (sourceKind != PackageSourceKind.Installed) return PackageSessionOperationResult.Failed($"Unsupported package session source kind '{sourceKind}'.");
        return await MapAsync(await installedPackages.SetEnabledAsync(packageId, false, cancellationToken), packageId, cancellationToken);
    }

    private async Task<PackageSessionOperationResult> MapAsync(PackageOperationResult result, string packageId, CancellationToken cancellationToken)
        => new(
            result.Success,
            result.Message,
            result.Warnings,
            result.Errors,
            result.ImpactedPackageIds.Count == 0 ? [packageId] : result.ImpactedPackageIds,
            await sessions.GetStatusAsync(packageId, cancellationToken));
}
