using System.Collections.ObjectModel;
using LiveMarkdown.Avalonia;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed class MarketplacePackagesPaneViewModel : ViewModelBase, IDisposable
{
    private readonly MarketplacePackageProfileViewModel _profile;
    private bool _rpcAccessLoaded;
    private bool _rpcAccessLoading;
    private string _rpcAccessError = string.Empty;
    private bool _runtimePreflightLoaded;
    private bool _runtimePreflightLoading;
    private string _runtimePreflightError = string.Empty;
    private RegistryPackageChangePreflight? _runtimePreflight;
    private long _packageDownloadBytes;

    internal MarketplacePackagesPaneViewModel(
        PackagesMarketplaceCatalog catalog,
        RemoteImageService? remoteImageService)
    {
        Catalog = catalog;
        SelectionLoader = new MarketplacePackageSelectionLoader(catalog);
        _profile = new MarketplacePackageProfileViewModel(remoteImageService);
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

    public ObservableCollection<MarketplaceExecutionRuntimeRequirementViewModel> ExecutionRuntimeRequirements { get; } = [];

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

    public string CurrentRid => AppPackageTargetEnvironment.CurrentRid;

    public bool RuntimePreflightLoaded => _runtimePreflightLoaded;

    public bool RuntimePreflightLoading => _runtimePreflightLoading;

    public bool HasRuntimePreflightError => !string.IsNullOrWhiteSpace(_runtimePreflightError);

    public string RuntimePreflightError => _runtimePreflightError;

    public bool HasExecutionRuntimeRequirements => ExecutionRuntimeRequirements.Count > 0;

    public bool RuntimePlanCanApply => RuntimePreflightLoaded
                                       && (_runtimePreflight?.Plan.Success == true || _runtimePreflight?.Plan.RuntimePrerequisites is { Satisfied: false, Requirements.Count: > 0 })
                                       && _runtimePreflight?.Plan.Conflicts.Count == 0
                                       && ExecutionRuntimeRequirements.All(static runtime => !runtime.IsBlocked);

    public string PackageDownloadText => RuntimePreflightLoading
        ? "Calculating..."
        : HasRuntimePreflightError
            ? "Unavailable"
            : RegistryPackageChangePreflight.FormatBytes(_packageDownloadBytes);

    public string SharedRuntimeDownloadText => RuntimePreflightLoading
        ? "Calculating..."
        : _runtimePreflight?.Plan.RuntimePrerequisites is { Satisfied: false }
            ? RegistryPackageChangePreflight.FormatBytes(_runtimePreflight.SharedRuntimeDownloadBytes) + " (runtime first)"
        : HasRuntimePreflightError || _runtimePreflight?.Plan.Success != true
            ? "Unavailable"
            : _runtimePreflight.SharedRuntimeDownloadBytes == 0 && _runtimePreflight.ExecutionRuntimes.Any(item => item.AlreadyInstalled)
                ? "0 bytes (already installed)"
                : RegistryPackageChangePreflight.FormatBytes(_runtimePreflight.SharedRuntimeDownloadBytes);

    public string RuntimeCompatibilitySummary
    {
        get
        {
            if (RuntimePreflightLoading) return $"Checking compatibility for {CurrentRid}...";
            if (HasRuntimePreflightError) return $"Runtime compatibility could not be checked: {RuntimePreflightError}";
            if (_runtimePreflight is null) return "Select a package version.";
            if (_runtimePreflight.Plan.RuntimePrerequisites is { Satisfied: false })
                return "One review covers the runtimes and package. Runtimes install first; the package waits in the queue.";
            if (!_runtimePreflight.Plan.Success)
            {
                return _runtimePreflight.Plan.Errors.FirstOrDefault()
                       ?? $"This package is not compatible with {CurrentRid}.";
            }
            if (!HasExecutionRuntimeRequirements) return "No shared execution runtime is required for this version.";
            var warnings = ExecutionRuntimeRequirements.Count(static runtime => runtime.ShowWarning);
            if (warnings > 0) return $"Available for {CurrentRid} with {warnings} lifecycle warning(s).";
            return _runtimePreflight.SharedRuntimeDownloadBytes == 0
                ? $"Available for {CurrentRid}; the exact shared runtime is already installed."
                : $"Available for {CurrentRid}; a shared runtime download is required.";
        }
    }

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

    public void BeginRuntimePreflightLoad()
    {
        SetRuntimePreflight(
            preflight: null,
            requirements: [],
            packageDownloadBytes: 0,
            loaded: false,
            loading: true,
            error: null);
    }

    public void CompleteRuntimePreflightLoad(
        RegistryPackageVersionDetails details,
        RegistryPackageChangePreflight preflight)
    {
        var runtimeTargets = details.Targets
            .Where(static target => target.Role == SunderPackageFormat.RuntimeHostRole && target.ExecutionRuntime is not null)
            .ToArray();
        var currentTargets = runtimeTargets
            .Where(target => string.Equals(target.Rid, CurrentRid, StringComparison.Ordinal))
            .ToArray();
        IReadOnlyList<MarketplaceExecutionRuntimeRequirementViewModel> requirements = currentTargets.Length == 0 && runtimeTargets.Length > 0
            ? [MarketplaceExecutionRuntimeRequirementViewModel.Unavailable(
                runtimeTargets[0].ExecutionRuntime!,
                CurrentRid,
                $"This version has no Runtime target for current RID '{CurrentRid}'.")]
            : currentTargets.Select(target => CreateRuntimeRequirement(details, target, preflight)).ToArray();
        var packageBytes = preflight.PackageDownloadBytes > 0
            ? preflight.PackageDownloadBytes
            : details.CanonicalArtifact.Size;
        SetRuntimePreflight(
            preflight,
            requirements,
            packageBytes,
            loaded: true,
            loading: false,
            error: null);
    }

    public void FailRuntimePreflightLoad(string error)
    {
        SetRuntimePreflight(
            preflight: null,
            requirements: [],
            packageDownloadBytes: 0,
            loaded: false,
            loading: false,
            error);
    }

    public void ClearRuntimePreflight()
    {
        SetRuntimePreflight(
            preflight: null,
            requirements: [],
            packageDownloadBytes: 0,
            loaded: false,
            loading: false,
            error: null);
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

    private MarketplaceExecutionRuntimeRequirementViewModel CreateRuntimeRequirement(
        RegistryPackageVersionDetails details,
        RegistryPackageTarget target,
        RegistryPackageChangePreflight preflight)
    {
        var requirement = target.ExecutionRuntime!;
        var prerequisite = preflight.Plan.RuntimePrerequisites?.Requirements.FirstOrDefault(item =>
            item.Package.PackageId == details.PackageId && item.Package.Role == target.Role && item.Package.Rid == target.Rid);
        if (prerequisite is { Satisfied: false })
        {
            var resolution = prerequisite.Installation?.Resolution;
            return new(requirement.RuntimeId, requirement.VersionRange, requirement.ModuleAbi, target.Rid,
                resolution is null ? "Not installed" : resolution.Artifact.Identity.VendorVersion,
                resolution?.Artifact.Support.State.ToString() ?? "Required",
                "Runtime and package installation will be queued in order.",
                prerequisite.Installation?.Message ?? "Review the required runtime with this package.",
                ShowWarning: true, IsBlocked: false);
        }
        if (prerequisite?.InstalledRuntime is not { } identity)
        {
            var message = preflight.Plan.Errors.FirstOrDefault()
                          ?? $"No compatible execution runtime could be resolved for RID '{target.Rid}'.";
            return MarketplaceExecutionRuntimeRequirementViewModel.Unavailable(requirement, target.Rid, message);
        }

        return new MarketplaceExecutionRuntimeRequirementViewModel(
            requirement.RuntimeId,
            requirement.VersionRange,
            requirement.ModuleAbi,
            target.Rid,
            identity.VendorVersion,
            "Installed",
            $"Available for {target.Rid}; exact runtime already installed.",
            RuntimeManagementUi.Source(identity),
            false,
            IsBlocked: false);
    }

    private void SetRuntimePreflight(
        RegistryPackageChangePreflight? preflight,
        IReadOnlyList<MarketplaceExecutionRuntimeRequirementViewModel> requirements,
        long packageDownloadBytes,
        bool loaded,
        bool loading,
        string? error)
    {
        _runtimePreflight = preflight;
        ExecutionRuntimeRequirements.ReplaceWith(requirements);
        _packageDownloadBytes = packageDownloadBytes;
        _runtimePreflightLoaded = loaded;
        _runtimePreflightLoading = loading;
        _runtimePreflightError = error ?? string.Empty;
        OnPropertyChanged(nameof(RuntimePreflightLoaded));
        OnPropertyChanged(nameof(RuntimePreflightLoading));
        OnPropertyChanged(nameof(HasRuntimePreflightError));
        OnPropertyChanged(nameof(RuntimePreflightError));
        OnPropertyChanged(nameof(HasExecutionRuntimeRequirements));
        OnPropertyChanged(nameof(RuntimePlanCanApply));
        OnPropertyChanged(nameof(PackageDownloadText));
        OnPropertyChanged(nameof(SharedRuntimeDownloadText));
        OnPropertyChanged(nameof(RuntimeCompatibilitySummary));
    }

    public RegistryPackageSearchItemViewModel? ResolvePackageSelection(string? selectedPackageId)
        => Packages
               .FirstOrDefault(package => string.Equals(package.PackageId, selectedPackageId, StringComparison.OrdinalIgnoreCase))
           ?? Packages.FirstOrDefault();

    public string? ApplyProfile(RegistryPackageProfile? profile, Uri? registryOrigin)
        => _profile.Apply(profile, registryOrigin);

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

public sealed record MarketplaceExecutionRuntimeRequirementViewModel(
    string RuntimeId,
    string VersionRange,
    string ModuleAbi,
    string Rid,
    string ResolvedRuntime,
    string StatusText,
    string Availability,
    string DetailText,
    bool ShowWarning,
    bool IsBlocked)
{
    public static MarketplaceExecutionRuntimeRequirementViewModel Unavailable(
        RegistryExecutionRuntimeRequirement requirement,
        string rid,
        string message)
        => new(
            requirement.RuntimeId,
            requirement.VersionRange,
            requirement.ModuleAbi,
            rid,
            "Not resolved",
            "Blocked",
            message,
            string.Empty,
            true,
            true);
}
