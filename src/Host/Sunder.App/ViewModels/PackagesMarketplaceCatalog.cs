using Sunder.App.Services;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

internal sealed class PackagesMarketplaceCatalog(
    PackageRegistryClientProvider registryClientProvider)
{
    public async Task<PackagesMarketplaceSearchResult> SearchAsync(
        string searchText,
        RegistrySearchSort sort,
        PackagesInstalledCatalog installedCatalog,
        Func<RegistryPackageSearchItemViewModel, Task> selectPackageAsync,
        CancellationToken cancellationToken)
    {
        if (!registryClientProvider.TryCreate(out var registryClient, out var errorMessage))
        {
            return PackagesMarketplaceSearchResult.Failed(errorMessage ?? "Enter a valid HTTP or HTTPS registry URL.");
        }

        using (registryClient)
        {
            var query = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
            var packages = await registryClient.SearchAsync(query, skip: 0, take: 50, sort, cancellationToken).ConfigureAwait(false);
            var packageItems = MarketplacePackageSearchProjector.Build(
                packages,
                installedCatalog.InstalledPackages,
                installedCatalog.AvailableUpdates,
                selectPackageAsync);
            return PackagesMarketplaceSearchResult.Succeeded(packageItems);
        }
    }

    public async Task<PackagesMarketplaceDetailsResult> LoadDetailsAsync(
        string packageId,
        Action<RegistryPackageVersionItemViewModel> selectVersion,
        CancellationToken cancellationToken)
    {
        if (!registryClientProvider.TryCreate(out var registryClient, out var errorMessage))
        {
            return PackagesMarketplaceDetailsResult.Failed(errorMessage ?? "Enter a valid HTTP or HTTPS registry URL.");
        }

        using (registryClient)
        {
            var package = await registryClient.GetPackageAsync(packageId, cancellationToken).ConfigureAwait(false);
            var versions = package?.Versions
                .OrderByDescending(version => version.Version, RegistryPackageVersionOrdering.Comparer)
                .Select(version => new RegistryPackageVersionItemViewModel(version, selectVersion))
                .ToArray() ?? [];
            return PackagesMarketplaceDetailsResult.Succeeded(package, versions);
        }
    }

    public async Task<RegistryPackageVersionDetails?> LoadVersionDetailsAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken)
    {
        if (!registryClientProvider.TryCreate(out var registryClient, out _))
        {
            return null;
        }

        using (registryClient)
        {
            return await registryClient
                .GetVersionAsync(packageId, version, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

internal sealed record PackagesMarketplaceSearchResult(
    bool Success,
    IReadOnlyList<RegistryPackageSearchItemViewModel> Packages,
    string? ErrorMessage)
{
    public static PackagesMarketplaceSearchResult Succeeded(IReadOnlyList<RegistryPackageSearchItemViewModel> packages)
        => new(true, packages, null);

    public static PackagesMarketplaceSearchResult Failed(string errorMessage)
        => new(false, [], errorMessage);
}

internal sealed record PackagesMarketplaceDetailsResult(
    bool Success,
    RegistryPackageDetails? Package,
    RegistryPackageProfile? Profile,
    RegistryPackageStats? Stats,
    RegistryUserAttribution? Creator,
    IReadOnlyList<RegistryUserAttribution> Maintainers,
    IReadOnlyList<RegistryPackageVersionItemViewModel> Versions,
    bool PackageFound,
    string? ErrorMessage)
{
    public static PackagesMarketplaceDetailsResult Succeeded(
        RegistryPackageDetails? package,
        IReadOnlyList<RegistryPackageVersionItemViewModel> versions,
        bool packageFound)
        => new(true, package, package?.Profile, package?.Stats, package?.Creator, package?.Maintainers ?? [], versions, packageFound, null);

    public static PackagesMarketplaceDetailsResult Succeeded(
        RegistryPackageDetails? package,
        IReadOnlyList<RegistryPackageVersionItemViewModel> versions)
        => Succeeded(package, versions, package is not null);

    public static PackagesMarketplaceDetailsResult Failed(string errorMessage)
        => new(false, null, null, null, null, [], [], false, errorMessage);
}
