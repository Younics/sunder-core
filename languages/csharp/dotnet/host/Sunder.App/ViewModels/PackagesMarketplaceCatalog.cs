using Sunder.App.Services;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

internal sealed class PackagesMarketplaceCatalog(
    PackageRegistryClientProvider registryClientProvider,
    RemoteImageService? remoteImageService)
{
    internal const int PageSize = 30;
    private readonly Dictionary<(Uri Origin, string Id, string? Version), (DateTimeOffset Expires, RegistryPackageDetails Package)> _overviews = [];

    internal void InvalidateOverviews()
    {
        lock (_overviews) _overviews.Clear();
    }

    public async Task<PackagesMarketplaceSearchResult> SearchAsync(
        string searchText,
        RegistrySearchSort sort,
        PackagesInstalledCatalog installedCatalog,
        Func<RegistryPackageSearchItemViewModel, Task> selectPackageAsync,
        CancellationToken cancellationToken,
        int skip = 0)
    {
        if (!registryClientProvider.TryCreate(out var registryClient, out var errorMessage))
        {
            return PackagesMarketplaceSearchResult.Failed(errorMessage ?? "Enter a valid HTTP or HTTPS registry URL.");
        }

        using (registryClient)
        {
            var query = string.IsNullOrWhiteSpace(searchText) ? null : searchText.Trim();
            var packages = await registryClient.SearchAsync(query, skip, PageSize, sort, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var packageItems = MarketplacePackageSearchProjector.Build(
                packages,
                installedCatalog.InstalledPackages,
                installedCatalog.AvailableUpdates,
                selectPackageAsync,
                registryClient.RegistryUrl,
                remoteImageService);
            return PackagesMarketplaceSearchResult.Succeeded(packageItems);
        }
    }

    public async Task<PackagesMarketplaceDetailsResult> LoadDetailsAsync(
        string packageId,
        string? version,
        Action<RegistryPackageVersionItemViewModel> selectVersion,
        CancellationToken cancellationToken)
    {
        if (!registryClientProvider.TryCreate(out var registryClient, out var errorMessage))
        {
            return PackagesMarketplaceDetailsResult.Failed(errorMessage ?? "Enter a valid HTTP or HTTPS registry URL.");
        }

        using (registryClient)
        {
            var package = await LoadOverviewAsync(registryClient, packageId, version, cancellationToken).ConfigureAwait(false);
            IEnumerable<RegistryPackageVersionSummary> history = package?.Versions ?? [];
            if (package?.SelectedRelease is { } selected)
            {
                // The selected release can fall outside the Registry's bounded version history.
                history = history.Where(version => version.Version != selected.Version)
                    .Append(new(selected.Version, selected.IsYanked, selected.DeprecatedMessage, selected.PublishedAtUtc));
            }
            var versions = history
                .OrderByDescending(version => version.Version, RegistryPackageVersionOrdering.Comparer)
                .Select(version => new RegistryPackageVersionItemViewModel(version, selectVersion, package?.LatestVersion))
                .ToArray();
            return PackagesMarketplaceDetailsResult.Succeeded(package, versions);
        }
    }

    public async Task<RegistryPackageDeviceRelease?> LoadVersionDetailsAsync(
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
            var package = await LoadOverviewAsync(registryClient, packageId, version, cancellationToken).ConfigureAwait(false);
            return package?.SelectedRelease;
        }
    }

    private async Task<RegistryPackageDetails?> LoadOverviewAsync(IRegistryPackageBrowseClient client, string id, string? version, CancellationToken token)
    {
        var rid = AppPackageTargetEnvironment.CurrentRid;
        var key = (client.RegistryUrl, id, version);
        lock (_overviews)
        {
            if (_overviews.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
                return cached.Package;
        }
        var package = await client.GetPackageOverviewAsync(id, version, rid, token).ConfigureAwait(false);
        if (package is null) return null;
        var expected = version ?? package.LatestVersion ?? package.Versions.FirstOrDefault()?.Version;
        var release = package.SelectedRelease;
        if (expected is not null && release is null)
        {
            // Registries predating device overviews ignore the rid query. Keep both reads in the same selection.
            var details = await client.GetVersionAsync(id, expected, token).ConfigureAwait(false);
            if (details is not null) release = RegistryPackageDeviceRelease.From(details, rid);
        }
        if (package.PackageId != id || expected is not null && (release?.PackageId != id || release.Version != expected || release.Rid != rid))
            throw new InvalidDataException($"Registry details for '{id}' version '{expected}' were unavailable or mismatched.");
        package = package with { SelectedRelease = release };
        lock (_overviews)
        {
            token.ThrowIfCancellationRequested();
            _overviews[key] = (DateTimeOffset.UtcNow.AddMinutes(2), package);
            if (release is not null) _overviews[(client.RegistryUrl, id, release.Version)] = _overviews[key];
            while (_overviews.Count > 32) _overviews.Remove(_overviews.MinBy(entry => entry.Value.Expires).Key);
        }
        return package;
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
