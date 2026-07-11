using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class StacksWindowViewModel
{

    private void RebuildStackList(string? preferredStackId = null)
    {
        var query = SearchText.Trim();
        var items = _allStacks
            .Where(item => MatchesSearch(item, query))
            .Select(item => new LocalStackLibraryItemViewModel(item))
            .ToArray();

        Stacks.Clear();
        foreach (var item in items)
        {
            Stacks.Add(item);
        }

        OnPropertyChanged(nameof(HasStacks));
        OnPropertyChanged(nameof(ShowNoStacks));

        var selected = !string.IsNullOrWhiteSpace(preferredStackId)
            ? Stacks.FirstOrDefault(item => string.Equals(item.StackId, preferredStackId, StringComparison.OrdinalIgnoreCase))
            : Stacks.FirstOrDefault();
        SelectedStack = selected;
        if (selected is null)
        {
            ApplySelectedStackSummary(null);
            NotifySelectionChanged();
        }
    }

    private async Task LoadSelectedStackManifestAsync(
        LocalStackLibraryItem item,
        int selectionVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await _library.ReadManifestAsync(item.LocalPath, cancellationToken);
            if (selectionVersion != _selectionVersion)
            {
                return;
            }

            ApplySelectedStackManifest(manifest);
        }
        catch (Exception ex)
        {
            if (selectionVersion == _selectionVersion)
            {
                StatusText = ex.Message;
            }
        }
    }

    private async Task LoadSelectedPublishedStackStatsAsync(
        LocalStackLibraryItemViewModel stack,
        int selectionVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stack.PublishedStackId)
            || !RegistryUrlHelper.TryParse(stack.RegistryUrl, out var registryUrl)
            || registryUrl is null)
        {
            return;
        }

        try
        {
            using var registryClient = _registryClientFactory(registryUrl);
            var details = await registryClient.GetStackAsync(stack.PublishedStackId, cancellationToken);
            if (selectionVersion != _selectionVersion || SelectedStack?.StackId != stack.StackId)
            {
                return;
            }

            ApplySelectedStackStats(details?.Stats);
        }
        catch
        {
            // Published Stack stats are auxiliary; keep the local Stack details usable if the Registry is unavailable.
        }
    }

    private void ApplySelectedStackSummary(LocalStackLibraryItemViewModel? value)
    {
        SelectedPackages.Clear();
        DisposeSelectedLocalDetails();
        SelectedFragments.Clear();
        SelectedRequiredInputs.Clear();
        ClearSelectedInstallPlan();
        ClearSelectedImportPreview();
        _selectedPackageRequirements = [];

        if (value is null)
        {
            SelectedStackTitle = "No Stack selected";
            SelectedStackSubtitle = "Select a Stack to inspect its setup plan.";
            SelectedStackSummary = string.Empty;
            SelectedStackMetadata = string.Empty;
            SelectedStackPath = string.Empty;
            _selectedLocalProfile.Apply((RegistryStackProfile?)null);
            ClearSelectedStackStats();
            NotifySelectedDetailsChanged();
            return;
        }

        SelectedStackTitle = value.Name;
        SelectedStackSubtitle = value.StackId;
        SelectedStackSummary = value.Summary;
        SelectedStackMetadata = $"{value.PackageCount} package{(value.PackageCount == 1 ? string.Empty : "s")} · {value.FragmentCount} fragment{(value.FragmentCount == 1 ? string.Empty : "s")} · {value.UpdatedText}";
        SelectedStackPath = value.LocalPath;
        _selectedLocalProfile.Apply(ToRegistryStackProfile(value.Item));
        if (value.IsPublished)
        {
            HasSelectedStackStats = true;
            SelectedStackStatsText = "Loading Registry stats...";
            SelectedStackIsStarred = false;
            SelectedStackStarActionText = "Star";
        }
        else
        {
            ClearSelectedStackStats();
        }

        PopulateSelectedLocalDetails(value.Item, new Dictionary<string, Uri?>(StringComparer.OrdinalIgnoreCase));
        var selectionVersion = _selectionVersion;
        if (value.Item.Details?.Count > 0)
        {
            _tasks.Observe(
                RefreshSelectedLocalDetailIconsAsync(value.Item, selectionVersion, _tasks.Token),
                "loading local Stack package icons");
        }

        NotifySelectedDetailsChanged();
    }

    private void ClearSelectedStackStats()
    {
        HasSelectedStackStats = false;
        SelectedStackStatsText = string.Empty;
        SelectedStackIsStarred = false;
        SelectedStackStarActionText = "Star";
    }

    private void ApplySelectedStackStats(RegistryStackStats? stats)
    {
        if (stats is null)
        {
            return;
        }

        HasSelectedStackStats = true;
        SelectedStackStatsText = FormatStackStats(stats);
        SelectedStackIsStarred = stats.IsStarred;
        SelectedStackStarActionText = stats.IsStarred ? "Unstar" : "Star";
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private void ApplySelectedStackManifest(SunderStackManifest manifest)
    {
        _selectedPackageRequirements = manifest.Packages ?? [];
        SelectedPackages.Clear();
        foreach (var package in manifest.Packages ?? [])
        {
            SelectedPackages.Add(new StackPackageRequirementViewModel(package));
        }

        SelectedFragments.Clear();
        foreach (var fragment in manifest.Fragments ?? [])
        {
            SelectedFragments.Add(new StackFragmentViewModel(fragment));
        }

        SelectedRequiredInputs.Clear();
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private async Task RefreshSelectedLocalDetailIconsAsync(
        LocalStackLibraryItem item,
        int selectionVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var packageIcons = await LoadLocalDetailPackageIconsAsync(cancellationToken);
            if (selectionVersion != _selectionVersion || _disposed || SelectedStack?.StackId != item.StackId)
            {
                return;
            }

            PopulateSelectedLocalDetails(item, packageIcons);
            NotifySelectedDetailsChanged();
        }
        catch
        {
            // Local detail icons are decorative; keep glyph fallback if package metadata is unavailable.
        }
    }

    private void PopulateSelectedLocalDetails(LocalStackLibraryItem item, IReadOnlyDictionary<string, Uri?> packageIcons)
    {
        DisposeSelectedLocalDetails();
        foreach (var detail in StackDetailTreeProjector.ProjectLocal(
                     item.Details ?? [],
                     packageIcons,
                     _runtimeApiClient.CreatePackageAssetUri))
        {
            SelectedLocalDetails.Add(detail);
        }
    }

    private async Task<IReadOnlyDictionary<string, Uri?>> LoadLocalDetailPackageIconsAsync(CancellationToken cancellationToken)
    {
        var packages = new Dictionary<string, Uri?>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in await _runtimeApiClient.GetInstalledPackagesAsync(cancellationToken))
        {
            AddPackageIcon(packages, package.PackageId, package.Icon);
        }

        foreach (var package in await _runtimeApiClient.GetSessionPackagesAsync(cancellationToken))
        {
            AddPackageIcon(packages, package.PackageId, package.Icon);
        }

        foreach (var package in await _runtimeApiClient.GetActivePackagesAsync(cancellationToken))
        {
            AddPackageIcon(packages, package.PackageId, package.Icon);
        }

        return packages;
    }

    private void AddPackageIcon(IDictionary<string, Uri?> packages, string packageId, PackageIconDescriptor? icon)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        packages[packageId] = PackageIconUriResolver.Resolve(packageId, icon, _runtimeApiClient.CreatePackageAssetUri);
    }

    private static async Task<IReadOnlyDictionary<string, StackPackageInfo>> LoadRegistryDetailPackageInfoAsync(
        IRegistryApiClient registryClient,
        IReadOnlyList<RegistryStackFragmentSummary> fragments,
        CancellationToken cancellationToken)
    {
        var packages = new Dictionary<string, StackPackageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in fragments
                     .Select(GetRegistryFragmentPackageId)
                     .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var package = await registryClient.GetPackageAsync(packageId, cancellationToken);
                if (package is null)
                {
                    continue;
                }

                packages[packageId] = new StackPackageInfo(
                    string.IsNullOrWhiteSpace(package.Name) ? packageId : package.Name,
                    null,
                    ResolveRegistryPackageIconUri(registryClient.RegistryUrl, package.IconUrl));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Registry package metadata is decorative; keep package-id fallback details.
            }
        }

        return packages;
    }

    private void PopulateRegistrySelectedDetails(
        IReadOnlyList<RegistryStackFragmentSummary> fragments,
        IReadOnlyDictionary<string, StackPackageInfo> packageInfo)
    {
        DisposeRegistrySelectedDetails();
        foreach (var packageGroup in fragments
                     .GroupBy(GetRegistryFragmentPackageId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => ResolveRegistryPackageDisplayName(group.Key, packageInfo), StringComparer.OrdinalIgnoreCase))
        {
            var packageId = string.IsNullOrWhiteSpace(packageGroup.Key) ? "unknown" : packageGroup.Key;
            packageInfo.TryGetValue(packageId, out var info);
            var package = new LocalStackDetailPackage(
                packageId,
                string.IsNullOrWhiteSpace(info?.DisplayName) ? packageId : info.DisplayName,
                BuildPackageGlyph(packageId),
                packageGroup
                    .Select(ToRegistryLocalDetailItem)
                    .OrderBy(item => StackContentKindLabels.FormatGroupName(item.Kind), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            RegistrySelectedDetails.Add(new LocalStackDetailPackageViewModel(package, info?.IconUri));
        }
    }

    private static LocalStackDetailItem ToRegistryLocalDetailItem(RegistryStackFragmentSummary fragment)
    {
        var itemId = string.IsNullOrWhiteSpace(fragment.SourceItemId) ? fragment.FragmentId : fragment.SourceItemId!;
        var displayName = string.IsNullOrWhiteSpace(fragment.DisplayName) ? itemId : fragment.DisplayName;
        var kind = StackContentKindLabels.InferKind(
            fragment.Kind,
            fragment.OwnerPackageId,
            schemaId: fragment.SchemaId,
            itemId: itemId,
            displayName: displayName,
            summary: fragment.Description);
        return new LocalStackDetailItem(
            itemId,
            displayName,
            BuildRegistryFragmentSummary(fragment),
            (fragment.DisplayDetails ?? [])
                .Select(detail => new LocalStackDetailValue(
                    string.IsNullOrWhiteSpace(detail.Label) ? "Value" : detail.Label,
                    string.IsNullOrWhiteSpace(detail.Value) ? "No preview value." : detail.Value,
                    detail.Behavior ?? string.Empty))
                .ToArray(),
            kind);
    }

    private static string BuildRegistryFragmentSummary(RegistryStackFragmentSummary fragment)
    {
        var detailLabels = (fragment.DisplayDetails ?? [])
            .Select(detail => detail.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Take(3)
            .ToArray();
        if (detailLabels.Length > 0)
        {
            return string.Join(", ", detailLabels);
        }

        if (!string.IsNullOrWhiteSpace(fragment.Description))
        {
            return fragment.Description!;
        }

        return string.IsNullOrWhiteSpace(fragment.SchemaId) ? "Selected setup values" : fragment.SchemaId;
    }

    private static string GetRegistryFragmentPackageId(RegistryStackFragmentSummary fragment)
        => string.IsNullOrWhiteSpace(fragment.OwnerPackageId) ? "unknown" : fragment.OwnerPackageId;

    private static string ResolveRegistryPackageDisplayName(
        string packageId,
        IReadOnlyDictionary<string, StackPackageInfo> packageInfo)
        => packageInfo.TryGetValue(packageId, out var info) && !string.IsNullOrWhiteSpace(info.DisplayName)
            ? info.DisplayName
            : packageId;

    private static Uri? ResolveRegistryPackageIconUri(Uri registryUrl, string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
        {
            return null;
        }

        var trimmed = iconUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        return Uri.TryCreate(registryUrl, trimmed, out var relativeUri) ? relativeUri : null;
    }

    private static string BuildPackageGlyph(string packageId)
    {
        var first = packageId.FirstOrDefault(char.IsLetterOrDigit);
        return first == default ? "?" : char.ToUpperInvariant(first).ToString();
    }

}
