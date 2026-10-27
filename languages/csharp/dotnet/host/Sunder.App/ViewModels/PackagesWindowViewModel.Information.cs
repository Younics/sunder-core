using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Sdk.Packaging;

namespace Sunder.App.ViewModels;

public sealed partial class PackagesWindowViewModel
{
    private readonly LatestAsyncRequest _informationRequest = new();
    private readonly Stack<PackageDetailsLocation> _informationHistory = [];
    private sealed record PackageDetailsLocation(PackageWindowMode Mode, string PackageId, string Search, string? Version);
    private (string Id, string Version, string? Identity, DateTimeOffset? InstalledAt)? _informationSource;
    private Sunder.Package.Format.SunderPackageInformation? _informationFacts;
    private string? _informationDependencyState;
    private string? _informationSummary;

    [ObservableProperty]
    private PackageInformationViewModel _information = PackageInformationViewModel.Unavailable();

    public bool CanGoBackInPackageDetails => _informationHistory.Count > 0;

    private async Task LoadInstalledInformationAsync(PackageCatalogItemViewModel item)
    {
        var identity = _installedPackages.Catalog.SessionPackages.FirstOrDefault(value => value.PackageId == item.PackageId)?.SourceContentIdentity;
        var source = (item.PackageId, item.Version, identity, _installedPackages.GetInstalledPackage(item.PackageId)?.InstalledAtUtc);
        var dependencyState = string.Join('\n', _installedPackages.Catalog.SessionPackages.Select(value => $"{value.PackageId}:{value.Version}:{value.IsEnabled}:{value.Readiness}"))
            + string.Join('\n', _installedPackages.Catalog.InstalledPackages.Select(value => $"{value.PackageId}:{value.Version}:{value.IsEnabled}"));
        if (_informationSource == source && (Information.IsLoaded || _informationRequest.IsBusy))
        {
            if (Information.IsLoaded) SelectedPackageSummary = _informationSummary ?? "Package features, dependencies and public integrations.";
            if (Information.IsLoaded && _informationFacts is not null && _informationDependencyState != dependencyState)
                Information = PackageInformationViewModel.Create(_informationFacts, AppPackageTargetEnvironment.CurrentRid, ResolveDependencyState, NavigateToDependency);
            _informationDependencyState = dependencyState;
            return;
        }
        using var request = _informationRequest.Start(_tasks.Token);
        _informationSource = source;
        Information = PackageInformationViewModel.Loading();
        try
        {
            var details = await _runtimeApiClient.GetPackageInformationAsync(item.PackageId, request.Token);
            if (_disposed || !request.IsCurrent || !ReferenceEquals(Installed.SelectedPackage, item) || !IsInstalledMode) return;
            if (details is null || details.PackageId != item.PackageId || details.Version != item.Version
                || identity is not null && details.ContentIdentity != identity)
            {
                Information = PackageInformationViewModel.Unavailable();
                return;
            }
            _informationFacts = PackageInformationViewModel.FromRuntime(details);
            _informationDependencyState = dependencyState;
            _informationSummary = details.Summary;
            SelectedPackageSummary = _informationSummary ?? "Package features, dependencies and public integrations.";
            Information = PackageInformationViewModel.Create(_informationFacts, AppPackageTargetEnvironment.CurrentRid,
                ResolveDependencyState, NavigateToDependency);
            request.Complete();
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed && request.IsCurrent && ReferenceEquals(Installed.SelectedPackage, item))
                Information = PackageInformationViewModel.Unavailable();
            AppSessionLog.WriteError($"Could not load package information for '{item.PackageId}'.", error);
        }
    }

    private PackageDependencyState ResolveDependencyState(string packageId, string range)
    {
        var session = _installedPackages.Catalog.SessionPackages.FirstOrDefault(value => value.PackageId == packageId);
        var installed = _installedPackages.GetInstalledPackage(packageId);
        var name = session?.DisplayName ?? installed?.Name ?? packageId;
        var version = session?.Version ?? installed?.Version;
        if (version is null) return new(name, "Not installed");
        if (!PackageVersionRange.IsSatisfiedBy(version, range)) return new(name, $"Version {version} is outside the required range");
        if (session is { IsEnabled: false } || session is null && installed is { IsEnabled: false }) return new(name, $"v{version} · Disabled");
        return new(name, $"v{version} · " + (session?.Readiness switch
        {
            Sunder.Runtime.Contracts.PackageReadinessState.Ready => "Ready",
            Sunder.Runtime.Contracts.PackageReadinessState.NeedsConfiguration => "Needs configuration",
            Sunder.Runtime.Contracts.PackageReadinessState.Degraded => "Degraded",
            Sunder.Runtime.Contracts.PackageReadinessState.Failed => "Failed",
            _ => "Installed",
        }));
    }

    private void NavigateToDependency(string packageId)
        => _tasks.Observe(NavigateToDependencyAsync(packageId), "opening package dependency");

    private async Task NavigateToDependencyAsync(string packageId)
    {
        var currentId = IsInstalledMode ? Installed.SelectedPackage?.PackageId : Marketplace.SelectedPackage?.PackageId;
        if (currentId is null || currentId == packageId) return;
        if (_informationHistory.Count >= 128) _informationHistory.Clear();
        _informationHistory.Push(new(Mode, currentId, SearchText, Marketplace.SelectedVersion?.Version));
        NotifyInformationHistory();
        // Prefer the actual local package when there is one. Missing packages are
        // browsed at the current Registry, never auto-installed by navigation.
        var local = _installedPackages.Catalog.SessionPackages.Any(value => value.PackageId == packageId)
            || _installedPackages.GetInstalledPackage(packageId) is not null;
        if (local)
        {
            Mode = PackageWindowMode.Installed;
            SearchText = string.Empty;
            RebuildInstalledPackageList(packageId);
        }
        else
        {
            Mode = PackageWindowMode.Marketplace;
            SearchText = packageId;
            CancelQueuedMarketplaceSearch();
            await RefreshMarketplaceAsync(_tasks.Token);
            var package = Marketplace.Packages.FirstOrDefault(value => value.PackageId == packageId);
            if (package is not null && !ReferenceEquals(Marketplace.SelectedPackage, package)) await SelectMarketplacePackageAsync(package, _tasks.Token);
        }
    }

    [RelayCommand]
    private async Task GoBackInPackageDetailsAsync()
    {
        if (!_informationHistory.TryPop(out var location)) return;
        NotifyInformationHistory();
        Mode = location.Mode;
        SearchText = location.Search;
        if (IsInstalledMode) RebuildInstalledPackageList(location.PackageId);
        else
        {
            CancelQueuedMarketplaceSearch();
            await RefreshMarketplaceAsync(_tasks.Token);
            var package = Marketplace.Packages.FirstOrDefault(value => value.PackageId == location.PackageId);
            if (package is not null) await SelectMarketplacePackageAsync(package, _tasks.Token);
            var version = Marketplace.Versions.FirstOrDefault(value => value.Version == location.Version);
            if (version is not null) SelectMarketplaceVersion(version);
        }
    }

    private void NotifyInformationHistory() => OnPropertyChanged(nameof(CanGoBackInPackageDetails));
}
