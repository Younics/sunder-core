using System.Collections.ObjectModel;
using LiveMarkdown.Avalonia;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public sealed class MarketplacePackagesPaneViewModel : ViewModelBase, IDisposable
{
    private readonly MarketplacePackageProfileViewModel _profile = new();
    private bool _rpcAccessLoaded;
    private bool _rpcAccessLoading;
    private string _rpcAccessError = string.Empty;

    internal MarketplacePackagesPaneViewModel(PackagesMarketplaceCatalog catalog)
    {
        Catalog = catalog;
        SelectionLoader = new MarketplacePackageSelectionLoader(catalog);
    }

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? ImageGalleryRequested
    {
        add => _profile.ImageGalleryRequested += value;
        remove => _profile.ImageGalleryRequested -= value;
    }

    internal PackagesMarketplaceCatalog Catalog { get; }

    internal MarketplacePackageSelectionLoader SelectionLoader { get; }

    public ObservableCollection<RegistryPackageSearchItemViewModel> Packages { get; } = [];

    public string SearchText { get; set; } = string.Empty;

    public RegistryPackageSearchItemViewModel? SelectedPackage { get; internal set; }

    public RegistryPackageVersionItemViewModel? SelectedVersion { get; internal set; }

    public ObservableCollection<RegistryPackageVersionItemViewModel> Versions { get; } = [];

    public ObservableCollection<RegistryPackageProfileLinkViewModel> ProfileLinks => _profile.Links;

    public ObservableCollection<RegistryPackageProfileMetadataItemViewModel> ProfileMetadata => _profile.Metadata;

    public ObservableCollection<string> ProfileTags => _profile.Tags;

    public ObservableCollection<RegistryPackageMediaItemViewModel> ProfileMedia => _profile.Media;

    public ObservableCollection<RegistryUserAttributionViewModel> Attributions { get; } = [];

    public ObservableCollection<RegistryUserAttributionViewModel> Creators { get; } = [];

    public ObservableCollection<RegistryUserAttributionViewModel> Maintainers { get; } = [];

    public ObservableCollection<PackageRpcAccessItemViewModel> RpcContractUses { get; } = [];

    public ObservableStringBuilder ReadmeMarkdownBuilder => _profile.ReadmeMarkdownBuilder;

    public bool HasPackages => Packages.Count > 0;

    public bool HasVersions => Versions.Count > 0;

    public bool HasReadme => _profile.HasReadme;

    public bool HasProfileLinks => _profile.HasLinks;

    public bool HasProfileMetadata => _profile.HasMetadata;

    public bool HasProfileTags => _profile.HasTags;

    public bool HasProfile => _profile.HasProfile;

    public bool HasProfileMedia => _profile.HasMedia;

    public bool HasAttributions => Attributions.Count > 0;

    public bool HasCreators => Creators.Count > 0;

    public bool HasMaintainers => Maintainers.Count > 0;

    public bool RpcAccessLoaded => _rpcAccessLoaded;

    public bool RpcAccessLoading => _rpcAccessLoading;

    public bool HasRpcAccessError => !string.IsNullOrWhiteSpace(_rpcAccessError);

    public string RpcAccessError => _rpcAccessError;

    public bool HasRpcContractUses => RpcAccessLoaded && RpcContractUses.Count > 0;

    public bool HasNoRpcContractUses => RpcAccessLoaded && !HasRpcContractUses;

    public string RpcAccessSummary => RpcAccessLoading
        ? "Loading declared access..."
        : HasRpcAccessError
            ? "Declared access unavailable"
            : RpcAccessLoaded
                ? PackageRpcAccessProjection.Summary(RpcContractUses)
                : "Select a package version";

    public void ReplacePackages(IReadOnlyList<RegistryPackageSearchItemViewModel> packages)
    {
        DisposePackageItems();
        Packages.ReplaceWith(packages);
    }

    public void KeepOnlyPackage(RegistryPackageSearchItemViewModel package)
    {
        for (var index = Packages.Count - 1; index >= 0; index--)
        {
            var item = Packages[index];
            if (ReferenceEquals(item, package))
            {
                continue;
            }

            Packages.RemoveAt(index);
            item.Dispose();
        }
    }

    public void ReplaceVersions(IReadOnlyList<RegistryPackageVersionItemViewModel> versions)
    {
        Versions.ReplaceWith(versions);
    }

    public void ClearVersions()
    {
        Versions.Clear();
    }

    public void BeginRpcContractUseLoad()
    {
        SetRpcContractUses([], loaded: false, loading: true, error: null);
    }

    public void CompleteRpcContractUseLoad(IReadOnlyList<PackageRpcAccessItemViewModel> uses)
    {
        SetRpcContractUses(uses, loaded: true, loading: false, error: null);
    }

    public void FailRpcContractUseLoad(string error)
    {
        SetRpcContractUses([], loaded: false, loading: false, error);
    }

    public void ClearRpcContractUses()
    {
        SetRpcContractUses([], loaded: false, loading: false, error: null);
    }

    private void SetRpcContractUses(
        IReadOnlyList<PackageRpcAccessItemViewModel> uses,
        bool loaded,
        bool loading,
        string? error)
    {
        RpcContractUses.ReplaceWith(uses);
        _rpcAccessLoaded = loaded;
        _rpcAccessLoading = loading;
        _rpcAccessError = error ?? string.Empty;
        OnPropertyChanged(nameof(RpcAccessLoaded));
        OnPropertyChanged(nameof(RpcAccessLoading));
        OnPropertyChanged(nameof(HasRpcAccessError));
        OnPropertyChanged(nameof(RpcAccessError));
        OnPropertyChanged(nameof(HasRpcContractUses));
        OnPropertyChanged(nameof(HasNoRpcContractUses));
        OnPropertyChanged(nameof(RpcAccessSummary));
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
