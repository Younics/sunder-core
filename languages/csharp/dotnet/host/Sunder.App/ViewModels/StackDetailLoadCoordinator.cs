using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

internal sealed class StackDetailLoadCoordinator(
    LocalStackLibraryService library,
    IRuntimeStacksClient runtimeClient)
{
    public Task<SunderStackManifest> LoadLocalManifestAsync(
        LocalStackLibraryItem item,
        CancellationToken cancellationToken)
        => library.ReadManifestAsync(item.LocalPath, cancellationToken);

    public async Task<RegistryStackStats?> LoadPublishedStatsAsync(
        LocalStackLibraryItemViewModel stack,
        Func<Uri, IRegistryStackDetailClient> registryClientFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stack.PublishedStackId)
            || !RegistryUrlHelper.TryParse(stack.RegistryUrl, out var registryUrl)
            || registryUrl is null)
        {
            return null;
        }

        using var registryClient = registryClientFactory(registryUrl);
        return (await registryClient.GetStackAsync(stack.PublishedStackId, cancellationToken))?.Stats;
    }

    public async Task<IReadOnlyDictionary<string, Uri?>> LoadLocalPackageIconsAsync(CancellationToken cancellationToken)
    {
        var packages = new Dictionary<string, Uri?>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in await runtimeClient.GetInstalledPackagesAsync(cancellationToken))
        {
            AddPackageIcon(packages, package.PackageId, package.Icon);
        }

        foreach (var package in await runtimeClient.GetSessionPackagesAsync(cancellationToken))
        {
            AddPackageIcon(packages, package.PackageId, package.Icon);
        }

        foreach (var package in await runtimeClient.GetActivePackagesAsync(cancellationToken))
        {
            AddPackageIcon(packages, package.PackageId, package.Icon);
        }

        return packages;
    }

    public async Task<RegistryStackDetailLoadResult?> LoadRegistryAsync(
        string stackId,
        IRegistryStackDetailClient registryClient,
        CancellationToken cancellationToken)
    {
        var details = await registryClient.GetStackAsync(stackId, cancellationToken);
        if (details is null)
        {
            return null;
        }

        var packageInfo = new Dictionary<string, StackPackageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in details.Fragments
                     .Select(static fragment => string.IsNullOrWhiteSpace(fragment.OwnerPackageId) ? "unknown" : fragment.OwnerPackageId)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var package = await registryClient.GetPackageAsync(packageId, cancellationToken);
                if (package is not null)
                {
                    packageInfo[packageId] = new StackPackageInfo(
                        string.IsNullOrWhiteSpace(package.Name) ? packageId : package.Name,
                        null,
                        ResolveRegistryPackageIconUri(registryClient.RegistryUrl, package.IconUrl),
                        PackageIconTransport.AnonymousMedia);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Package metadata is decorative; fragment details retain package-id fallback.
            }
        }

        return new RegistryStackDetailLoadResult(details, packageInfo);
    }

    private void AddPackageIcon(IDictionary<string, Uri?> packages, string packageId, PackageIconDescriptor? icon)
    {
        if (!string.IsNullOrWhiteSpace(packageId))
        {
            packages[packageId] = PackageIconUriResolver.Resolve(packageId, icon, runtimeClient.CreatePackageAssetUri);
        }
    }

    private static Uri? ResolveRegistryPackageIconUri(Uri registryUrl, string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
        {
            return null;
        }

        var trimmed = iconUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return HttpMediaUriValidator.IsValid(absoluteUri) ? absoluteUri : null;
        }

        return Uri.TryCreate(registryUrl, trimmed, out var relativeUri)
               && HttpMediaUriValidator.IsValid(relativeUri)
            ? relativeUri
            : null;
    }
}

internal sealed record RegistryStackDetailLoadResult(
    RegistryStackDetails Details,
    IReadOnlyDictionary<string, StackPackageInfo> PackageInfo);
