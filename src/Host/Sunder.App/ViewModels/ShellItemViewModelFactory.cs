using Sunder.App.Models;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

internal sealed class ShellItemViewModelFactory(IRuntimeApiClientFactory runtimeApiClientFactory)
{
    public ShellItemViewModel Create(ShellPackageView packageView, Action<ShellItemViewModel> onSelect)
    {
        var tooltip = $"{packageView.PackageDisplayName} · {packageView.Title}";
        return new ShellItemViewModel(
            packageView.ViewId,
            packageView.Glyph,
            CreatePackageIconUri(packageView.PackageId, packageView.Icon),
            packageView.Title,
            packageView.PackageDisplayName,
            tooltip,
            packageView.Placement,
            onSelect);
    }

    public Uri? CreatePackageIconUri(string packageId, Sunder.Protocol.PackageIconDescriptor? icon)
    {
        return PackageIconUriResolver.Resolve(packageId, icon, (resolvedPackageId, assetPath) =>
        {
            using var runtimeApiClient = runtimeApiClientFactory.CreateClient();
            return runtimeApiClient.CreatePackageAssetUri(resolvedPackageId, assetPath);
        });
    }
}
