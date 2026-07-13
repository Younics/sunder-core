using Sunder.App.ViewModels;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed class StackPackageInfoProvider(IRuntimeStacksClient runtimeApiClient)
{
    public async Task<IReadOnlyDictionary<string, StackPackageInfo>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var packages = new Dictionary<string, StackPackageInfo>(StringComparer.OrdinalIgnoreCase);

        async Task AddInstalledAsync()
        {
            foreach (var package in await runtimeApiClient.GetInstalledPackagesAsync(cancellationToken))
            {
                AddPackageInfo(packages, package.PackageId, package.Name, package.Icon);
            }
        }

        async Task AddSessionAsync()
        {
            foreach (var package in await runtimeApiClient.GetSessionPackagesAsync(cancellationToken))
            {
                AddPackageInfo(packages, package.PackageId, package.DisplayName, package.Icon);
            }
        }

        async Task AddActiveAsync()
        {
            foreach (var package in await runtimeApiClient.GetActivePackagesAsync(cancellationToken))
            {
                AddPackageInfo(packages, package.PackageId, package.DisplayName, package.Icon);
            }
        }

        try
        {
            await AddInstalledAsync();
            await AddSessionAsync();
            await AddActiveAsync();
        }
        catch
        {
            // Package metadata is decorative in Stack screens.
        }

        return packages;
    }

    private void AddPackageInfo(
        IDictionary<string, StackPackageInfo> packages,
        string packageId,
        string displayName,
        PackageIconDescriptor? icon)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        packages[packageId] = new StackPackageInfo(
            string.IsNullOrWhiteSpace(displayName) ? packageId : displayName,
            icon,
            PackageIconUriResolver.Resolve(packageId, icon, runtimeApiClient.CreatePackageAssetUri));
    }
}
