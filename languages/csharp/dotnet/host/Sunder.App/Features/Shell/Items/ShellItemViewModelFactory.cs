using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Features.Shell.Items;

internal sealed class ShellItemViewModelFactory(PackageIconCache packageIconCache)
{
    public ShellItemViewModel Create(ShellPackageView packageView, Action<ShellItemViewModel> onSelect)
    {
        var tooltip = $"{packageView.PackageDisplayName} · {packageView.Title}";
        return new ShellItemViewModel(
            packageView.ViewId,
            packageView.Glyph,
            iconUri: null,
            packageView.Title,
            packageView.PackageDisplayName,
            tooltip,
            packageView.Placement,
            onSelect,
            iconImage: GetPackageIcon(packageView.PackageId, packageView.Icon),
            ownsIconImage: false);
    }

    public Avalonia.Media.IImage? GetPackageIcon(string packageId, Sunder.Runtime.Contracts.PackageIconDescriptor? icon)
        => packageIconCache.GetImage(packageId, icon);
}
