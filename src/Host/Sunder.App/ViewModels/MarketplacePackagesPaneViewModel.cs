using System.Collections.ObjectModel;
using LiveMarkdown.Avalonia;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

internal sealed class MarketplacePackagesPaneViewModel(PackagesMarketplaceCatalog catalog) : IDisposable
{
    private readonly MarketplacePackageProfileViewModel _profile = new();

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? ImageGalleryRequested
    {
        add => _profile.ImageGalleryRequested += value;
        remove => _profile.ImageGalleryRequested -= value;
    }

    public PackagesMarketplaceCatalog Catalog { get; } = catalog;

    public MarketplacePackageSelectionLoader SelectionLoader { get; } = new(catalog);

    public ObservableCollection<RegistryPackageSearchItemViewModel> Packages { get; } = [];

    public ObservableCollection<RegistryPackageVersionItemViewModel> Versions { get; } = [];

    public ObservableCollection<RegistryPackageProfileLinkViewModel> ProfileLinks => _profile.Links;

    public ObservableCollection<RegistryPackageProfileMetadataItemViewModel> ProfileMetadata => _profile.Metadata;

    public ObservableCollection<string> ProfileTags => _profile.Tags;

    public ObservableCollection<RegistryPackageMediaItemViewModel> ProfileMedia => _profile.Media;

    public ObservableStringBuilder ReadmeMarkdownBuilder => _profile.ReadmeMarkdownBuilder;

    public bool HasPackages => Packages.Count > 0;

    public bool HasVersions => Versions.Count > 0;

    public bool HasReadme => _profile.HasReadme;

    public bool HasProfileLinks => _profile.HasLinks;

    public bool HasProfileMetadata => _profile.HasMetadata;

    public bool HasProfileTags => _profile.HasTags;

    public bool HasProfile => _profile.HasProfile;

    public bool HasProfileMedia => _profile.HasMedia;

    public void ReplacePackages(IReadOnlyList<RegistryPackageSearchItemViewModel> packages)
    {
        DisposePackageItems();
        Packages.ReplaceWith(packages);
    }

    public void ReplaceVersions(IReadOnlyList<RegistryPackageVersionItemViewModel> versions)
    {
        Versions.ReplaceWith(versions);
    }

    public void ClearVersions()
    {
        Versions.Clear();
    }

    public RegistryPackageSearchItemViewModel? ResolvePackageSelection(string? selectedPackageId)
        => Packages
               .FirstOrDefault(package => string.Equals(package.PackageId, selectedPackageId, StringComparison.OrdinalIgnoreCase))
           ?? Packages.FirstOrDefault();

    public string? ApplyProfile(RegistryPackageProfile? profile)
        => _profile.Apply(profile);

    public void Dispose()
    {
        SelectionLoader.Dispose();
        DisposePackageItems();
        _profile.Dispose();
    }

    private void DisposePackageItems()
    {
        foreach (var package in Packages)
        {
            package.Dispose();
        }
    }
}
