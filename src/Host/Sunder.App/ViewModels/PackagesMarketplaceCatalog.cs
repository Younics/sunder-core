using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

internal sealed class PackagesMarketplaceCatalog(
    PackageRegistryClientProvider registryClientProvider,
    Func<Uri, RegistryAuthToken?> registryTokenProvider)
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
            var token = registryClientProvider.TryResolve(out var registryUrl, out _) && registryUrl is not null
                ? registryTokenProvider(registryUrl)
                : null;
            var package = token is not null && !string.IsNullOrWhiteSpace(token.Token)
                ? await registryClient.GetPackageAsync(packageId, token.Token, cancellationToken).ConfigureAwait(false)
                : await registryClient.GetPackageAsync(packageId, cancellationToken).ConfigureAwait(false);
            var versions = package?.Versions
                .OrderByDescending(version => RegistryPackageVersionOrdering.TryParse(version.Version), RegistryPackageVersionOrdering.Comparer)
                .ThenByDescending(version => version.PublishedAtUtc)
                .Select(version => new RegistryPackageVersionItemViewModel(version, selectVersion))
                .ToArray() ?? [];
            return PackagesMarketplaceDetailsResult.Succeeded(package?.Profile, package?.Stats, package?.Creator, package?.Maintainers ?? [], versions, package is not null);
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
    RegistryPackageProfile? Profile,
    RegistryPackageStats? Stats,
    RegistryUserAttribution? Creator,
    IReadOnlyList<RegistryUserAttribution> Maintainers,
    IReadOnlyList<RegistryPackageVersionItemViewModel> Versions,
    bool PackageFound,
    string? ErrorMessage)
{
    public static PackagesMarketplaceDetailsResult Succeeded(
        RegistryPackageProfile? profile,
        RegistryPackageStats? stats,
        RegistryUserAttribution? creator,
        IReadOnlyList<RegistryUserAttribution> maintainers,
        IReadOnlyList<RegistryPackageVersionItemViewModel> versions,
        bool packageFound)
        => new(true, profile, stats, creator, maintainers, versions, packageFound, null);

    public static PackagesMarketplaceDetailsResult Failed(string errorMessage)
        => new(false, null, null, null, [], [], false, errorMessage);
}
