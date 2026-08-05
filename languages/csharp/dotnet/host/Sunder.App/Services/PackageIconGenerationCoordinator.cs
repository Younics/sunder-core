using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class PackageIconGenerationCoordinator : IDisposable
{
    public PackageIconGenerationCoordinator(
        Func<AppPackageGeneration> getCurrentGeneration,
        Func<string, CancellationToken, Task<PackageIconImageLoadResult>>? loadPackageIconImageAsync)
    {
        Cache = loadPackageIconImageAsync is null
            ? new PackageIconCache(packageId => getCurrentGeneration().State.GetLoadedPackage(packageId)?.Folder)
            : new PackageIconCache(
                packageId => getCurrentGeneration().State.GetLoadedPackage(packageId)?.Folder,
                loadPackageIconImageAsync);
    }

    public PackageIconCache Cache { get; }

    public Task<PackageIconGeneration> PrepareAsync(
        AppPackageGeneration generation,
        RuntimePackageSnapshot snapshot,
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        CancellationToken cancellationToken)
        => Cache.PrepareGenerationAsync(
            snapshot.RuntimeInstanceId,
            snapshot.SessionGeneration,
            EnumeratePackageIcons(activePackages),
            packageId => generation.State.GetLoadedPackage(packageId)?.Folder,
            cancellationToken);

    public PackageIconGeneration Publish(PackageIconGeneration generation)
        => Cache.Publish(generation);

    public void Restore(PackageIconGeneration generation)
        => Cache.Restore(generation);

    public void Dispose() => Cache.Dispose();

    private static IEnumerable<(string PackageId, PackageIconDescriptor? Icon)> EnumeratePackageIcons(
        IEnumerable<ActivePackageDescriptor> activePackages)
        => activePackages.SelectMany(package =>
            new[] { (package.PackageId, package.Icon) }
                .Concat(package.Views.Select(view => (view.PackageId, view.Icon))));
}
