using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;
using Sunder.App.Services;
using Sunder.PackageManagement;
using Sunder.Protocol;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

public sealed partial class UseStackWizardViewModel(
    LocalStackLibraryService library,
    LocalStackLibraryItem stack,
    IRuntimeApiClient runtimeApiClient,
    RegistryPackageInstallService registryInstallService,
    Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync,
    Func<IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor>, CancellationToken, Task<IReadOnlyList<string>>> notifyStackImportAppliedAsync,
    Func<Uri, IRegistryApiClient> registryClientFactory,
    string registryUrlText) : ViewModelBase, IDisposable
{
    private readonly MarketplacePackageProfileViewModel _stackProfile = BuildStackProfile(stack);
    private IReadOnlyList<SunderStackPackageRequirement> _packageRequirements = [];
    private bool _manifestLoaded;
    private bool _installPlanReady;
    private bool _installPlanHasErrors;
    private int _previewVersion;

    public event Action<bool?>? CloseRequested;

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? ImageGalleryRequested
    {
        add => _stackProfile.ImageGalleryRequested += value;
        remove => _stackProfile.ImageGalleryRequested -= value;
    }

    public string StackTitle { get; } = stack.Name;

    public string StackSubtitle { get; } = stack.StackId;

    public string StackSummary { get; } = string.IsNullOrWhiteSpace(stack.Summary) ? "No summary provided." : stack.Summary!;

    public string StackMetadata { get; } = $"{stack.PackageCount} package{StackDisplayFormatters.Plural(stack.PackageCount)} - {stack.FragmentCount} fragment{StackDisplayFormatters.Plural(stack.FragmentCount)}";

    public ObservableCollection<RegistryPackageMediaItemViewModel> StackProfileMedia => _stackProfile.Media;

    public ObservableStringBuilder StackReadmeMarkdownBuilder => _stackProfile.ReadmeMarkdownBuilder;

    public ObservableCollection<UseStackPackageReviewViewModel> PackageRows { get; } = [];

    public ObservableCollection<StackPackageInstallPlanItemViewModel> InstallPlanItems { get; } = [];

    public ObservableCollection<string> InstallPlanWarnings { get; } = [];

    public ObservableCollection<string> InstallPlanErrors { get; } = [];

    public ObservableCollection<UseStackSetupPackageGroupViewModel> SetupPackageGroups { get; } = [];

    public ObservableCollection<UseStackImportActionViewModel> ImportActions { get; } = [];

    public ObservableCollection<UseStackRequiredInputValueViewModel> RequiredInputs { get; } = [];

    public ObservableCollection<string> ImportWarnings { get; } = [];

    public ObservableCollection<string> ImportErrors { get; } = [];

    public ObservableCollection<string> ImportedItems { get; } = [];

    public bool CanApply => !IsBusy
                            && _manifestLoaded
                            && _installPlanReady
                            && !_installPlanHasErrors
                            && HasApplicableContent
                            && !HasMissingRequiredInputs
                            && !HasImportErrors;

    public bool HasPackages => PackageRows.Count > 0;

    public bool HasInstallPlanItems => InstallPlanItems.Count > 0;

    public bool HasInstallPlanWarnings => InstallPlanWarnings.Count > 0;

    public bool HasInstallPlanErrors => InstallPlanErrors.Count > 0;

    public bool ShowNoInstallPlanChanges => _installPlanReady && !HasInstallPlanItems && !HasInstallPlanErrors;

    public bool HasSetupPackageGroups => SetupPackageGroups.Count > 0;

    public bool HasSetupItems => SetupPackageGroups.Any(group => group.Items.Count > 0);

    public bool HasSelectedSetupItems => SetupPackageGroups.SelectMany(group => group.Items).Any(item => item.IsSelected);

    public bool HasPackageRequirements => _packageRequirements.Count > 0;

    public bool HasApplicableContent => HasPackageRequirements || HasSelectedSetupItems;

    public bool HasImportActions => ImportActions.Count > 0;

    public bool HasRequiredInputs => RequiredInputs.Count > 0;

    public bool HasMissingRequiredInputs => RequiredInputs.Any(input => input.IsMissingRequiredValue);

    public bool HasImportWarnings => ImportWarnings.Count > 0;

    public bool HasImportErrors => ImportErrors.Count > 0;

    public bool HasImportedItems => ImportedItems.Count > 0;

    public bool HasStackProfileMedia => _stackProfile.HasMedia;

    public bool HasStackReadme => _stackProfile.HasReadme;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Loading Stack setup plan...";

    partial void OnIsBusyChanged(bool value) => NotifyWizardStateChanged();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_manifestLoaded || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Reading Stack manifest...";
            var manifest = await library.ReadManifestAsync(stack.LocalPath, cancellationToken);
            var packageInfo = await LoadPackageInfoAsync(cancellationToken);
            ApplyManifest(manifest, packageInfo);
            await RefreshInstallPlanAsync(cancellationToken);
            if (!HasInstallPlanItems && !HasInstallPlanErrors)
            {
                await RefreshImportPreviewCoreAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            InstallPlanErrors.Add(ex.Message);
            _installPlanReady = true;
            _installPlanHasErrors = true;
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyWizardStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var missingRequiredInput = RequiredInputs.FirstOrDefault(input => input.IsMissingRequiredValue);
        if (missingRequiredInput is not null)
        {
            StatusText = $"Enter a value for '{missingRequiredInput.Label}' before importing this Stack.";
            NotifyWizardStateChanged();
            return;
        }

        if (!HasApplicableContent)
        {
            StatusText = "Select at least one package or setup item before using this Stack.";
            NotifyWizardStateChanged();
            return;
        }

        if (HasInstallPlanItems)
        {
            if (!TryResolveRegistryUrl(out var registryUrl))
            {
                return;
            }

            IsBusy = true;
            try
            {
                using var registryClient = registryClientFactory(registryUrl);
                var installResult = await registryInstallService.InstallPackagesAsync(
                    _packageRequirements,
                    registryClient,
                    runtimeApiClient,
                    progress => StatusText = progress.StatusText);
                ApplyInstallResult(installResult);
                if (!installResult.Success)
                {
                    NotifyWizardStateChanged();
                    return;
                }

                if (installResult.ImpactedPackageIds.Count > 0)
                {
                    await applyPackageLifecycleChangesAsync(installResult.ImpactedPackageIds, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                InstallPlanErrors.Add(ex.Message);
                _installPlanHasErrors = true;
                StatusText = ex.Message;
                NotifyWizardStateChanged();
                return;
            }
            finally
            {
                IsBusy = false;
            }

            await RefreshInstallPlanAsync();
        }

        await RefreshImportPreviewCoreAsync(CancellationToken.None);

        missingRequiredInput = RequiredInputs.FirstOrDefault(input => input.IsMissingRequiredValue);
        if (missingRequiredInput is not null)
        {
            StatusText = $"Enter a value for '{missingRequiredInput.Label}' before importing this Stack.";
            NotifyWizardStateChanged();
            return;
        }

        if (HasImportErrors)
        {
            StatusText = ImportErrors.FirstOrDefault() ?? "Resolve Stack setup preview errors before importing.";
            NotifyWizardStateChanged();
            return;
        }

        var selectedActionIds = ImportActions
            .Select(action => action.ActionId)
            .ToArray();

        if (selectedActionIds.Length == 0)
        {
            StatusText = "No Stack setup actions were needed.";
            CloseRequested?.Invoke(true);
            return;
        }

        IsBusy = true;
        ImportWarnings.Clear();
        ImportErrors.Clear();
        ImportedItems.Clear();
        try
        {
            StatusText = "Importing Stack setup...";
            var result = await runtimeApiClient.ImportStackAsync(new RuntimeStackImportRequest(
                stack.LocalPath,
                GetSelectedFragmentIds(),
                GetInputValues(),
                new Dictionary<string, string>(),
                selectedActionIds));

            foreach (var warning in result.Warnings)
            {
                ImportWarnings.Add(warning);
            }

            foreach (var error in result.Errors)
            {
                ImportErrors.Add(error);
            }

            foreach (var imported in result.ImportedItems)
            {
                ImportedItems.Add($"{imported.DisplayName} ({imported.Kind})");
            }

            await NotifyStackImportAppliedAsync(result.AppliedContributions, CancellationToken.None);

            StatusText = result.Success
                ? $"Imported {result.ImportedItems.Count} Stack setup item{StackDisplayFormatters.Plural(result.ImportedItems.Count)}."
                : result.Errors.FirstOrDefault() ?? "Stack setup import failed.";
            if (result.Success)
            {
                CloseRequested?.Invoke(true);
            }
        }
        catch (Exception ex)
        {
            ImportErrors.Add(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyWizardStateChanged();
        }
    }

    [RelayCommand]
    private void Cancel()
        => CloseRequested?.Invoke(false);

    private void ApplyManifest(
        SunderStackManifest manifest,
        IReadOnlyDictionary<string, StackPackageInfo> packageInfo)
    {
        _packageRequirements = manifest.Packages ?? [];
        PackageRows.Clear();
        foreach (var package in _packageRequirements)
        {
            var packageId = package.PackageId ?? "unknown";
            packageInfo.TryGetValue(packageId, out var info);
            PackageRows.Add(new UseStackPackageReviewViewModel(package, info));
        }

        BuildSetupPackageGroups(manifest, packageInfo);
        ApplyManifestRequiredInputs(manifest);

        _manifestLoaded = true;
        StatusText = SetupPackageGroups.SelectMany(group => group.Items).Any() == false
            ? "This Stack has no fragments to import."
            : "Stack manifest loaded.";
        NotifyWizardStateChanged();
    }

    private async Task NotifyStackImportAppliedAsync(
        IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor> appliedContributions,
        CancellationToken cancellationToken)
    {
        if (appliedContributions.Count == 0)
        {
            return;
        }

        var warnings = await notifyStackImportAppliedAsync(appliedContributions, cancellationToken);
        foreach (var warning in warnings)
        {
            ImportWarnings.Add(warning);
        }
    }

    private void BuildSetupPackageGroups(
        SunderStackManifest manifest,
        IReadOnlyDictionary<string, StackPackageInfo> packageInfo)
    {
        SetupPackageGroups.Clear();
        var localDetails = stack.Details ?? LocalStackLibraryService.BuildDetailsFromManifest(manifest);
        var detailPackages = localDetails.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        foreach (var group in (manifest.Fragments ?? []).GroupBy(fragment => fragment.OwnerPackageId ?? "unknown package", StringComparer.OrdinalIgnoreCase))
        {
            var packageId = group.Key;
            packageInfo.TryGetValue(packageId, out var info);
            detailPackages.TryGetValue(packageId, out var detailPackage);
            var displayName = string.IsNullOrWhiteSpace(detailPackage?.DisplayName) || string.Equals(detailPackage.DisplayName, packageId, StringComparison.OrdinalIgnoreCase)
                ? info?.DisplayName ?? packageId
                : detailPackage!.DisplayName;
            var glyph = !string.IsNullOrWhiteSpace(detailPackage?.Glyph) ? detailPackage!.Glyph : StackDisplayFormatters.PackageGlyph(info?.Icon, displayName, packageId);
            var iconUri = info?.IconUri ?? (!string.IsNullOrWhiteSpace(detailPackage?.IconAssetPath)
                ? runtimeApiClient.CreatePackageAssetUri(packageId, detailPackage!.IconAssetPath!)
                : null);
            var items = group
                .Select(fragment => new UseStackSetupItemViewModel(
                    fragment,
                    FindDetailItem(detailPackage, fragment),
                    OnSetupItemSelectionChanged))
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SetupPackageGroups.Add(new UseStackSetupPackageGroupViewModel(packageId, displayName, glyph, iconUri, items));
        }
    }

    private static LocalStackDetailItem? FindDetailItem(LocalStackDetailPackage? detailPackage, SunderStackFragmentManifest fragment)
    {
        if (detailPackage is null)
        {
            return null;
        }

        return detailPackage.Items.FirstOrDefault(item => string.Equals(item.ItemId, fragment.FragmentId, StringComparison.OrdinalIgnoreCase))
               ?? detailPackage.Items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(fragment.Preview?.SourceItemId)
                                                            && string.Equals(item.ItemId, fragment.Preview.SourceItemId, StringComparison.OrdinalIgnoreCase))
               ?? detailPackage.Items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(fragment.DisplayName)
                                                            && string.Equals(item.DisplayName, fragment.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyManifestRequiredInputs(SunderStackManifest manifest)
    {
        RequiredInputs.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in (manifest.Fragments ?? []).SelectMany(fragment => (fragment.RequiredInputs ?? []).Select(input => (Input: input, OwnerPackageId: fragment.OwnerPackageId, ContributorId: fragment.ContributorId ?? fragment.OwnerPackageId ?? "stack"))))
        {
            var inputId = input.Input.InputId ?? input.Input.Label ?? "input";
            if (!seen.Add(inputId))
            {
                continue;
            }

            RequiredInputs.Add(new UseStackRequiredInputValueViewModel(
                new RuntimeStackRequiredInputDescriptor(
                    inputId,
                    input.ContributorId,
                    input.Input.Label ?? inputId,
                    input.Input.Required != false,
                    input.Input.Description,
                    input.Input.DefaultValue)
                {
                    OwnerPackageId = input.OwnerPackageId,
                },
                null,
                OnRequiredInputChanged));
        }
    }

    private async Task RefreshInstallPlanAsync(CancellationToken cancellationToken = default)
    {
        InstallPlanItems.Clear();
        InstallPlanWarnings.Clear();
        InstallPlanErrors.Clear();
        _installPlanReady = false;
        _installPlanHasErrors = false;

        if (_packageRequirements.Count == 0)
        {
            _installPlanReady = true;
            StatusText = "This Stack has no package requirements.";
            NotifyWizardStateChanged();
            return;
        }

        if (!TryResolveRegistryUrl(out var registryUrl))
        {
            return;
        }

        try
        {
            StatusText = "Resolving Stack package graph...";
            using var registryClient = registryClientFactory(registryUrl);
            var plan = await registryInstallService.ResolveInstallPlanForPackagesAsync(
                _packageRequirements,
                registryClient,
                runtimeApiClient,
                progress => StatusText = progress.StatusText,
                cancellationToken);
            foreach (var item in plan.Items)
            {
                InstallPlanItems.Add(new StackPackageInstallPlanItemViewModel(item));
            }

            foreach (var warning in plan.Warnings)
            {
                InstallPlanWarnings.Add(warning);
            }

            foreach (var error in plan.Errors.Concat(plan.Conflicts.Select(conflict => conflict.Message)))
            {
                InstallPlanErrors.Add(error);
            }

            _installPlanReady = true;
            _installPlanHasErrors = !plan.Success;
            ApplyPackageInstallPlan(plan);
            StatusText = plan.Success
                ? InstallPlanItems.Count == 0
                    ? "All Stack package requirements are already satisfied."
                    : $"Stack package graph resolved {InstallPlanItems.Count} package change{StackDisplayFormatters.Plural(InstallPlanItems.Count)}."
                : InstallPlanErrors.FirstOrDefault() ?? "Stack package graph resolution failed.";
        }
        catch (Exception ex)
        {
            InstallPlanErrors.Add(ex.Message);
            _installPlanReady = true;
            _installPlanHasErrors = true;
            StatusText = ex.Message;
        }
        finally
        {
            NotifyWizardStateChanged();
        }
    }

    private async Task RefreshImportPreviewCoreAsync(CancellationToken cancellationToken)
    {
        var previewVersion = ++_previewVersion;
        var existingInputValues = GetInputValues(includeEmptyValues: true);
        ImportActions.Clear();
        RequiredInputs.Clear();
        ImportWarnings.Clear();
        ImportErrors.Clear();
        if (!HasSelectedSetupItems)
        {
            StatusText = HasPackageRequirements
                ? "No Stack setup actions are needed for package-only requirements."
                : "Select at least one setup item to preview actions.";
            NotifyWizardStateChanged();
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Previewing Stack setup actions...";
            var preview = await runtimeApiClient.PreviewStackImportAsync(new RuntimeStackImportPreviewRequest(
                stack.LocalPath,
                GetSelectedFragmentIds(),
                existingInputValues.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>()), cancellationToken);
            if (previewVersion != _previewVersion)
            {
                return;
            }

            foreach (var action in preview.Actions)
            {
                ImportActions.Add(new UseStackImportActionViewModel(action));
            }

            foreach (var input in preview.RequiredInputs)
            {
                RequiredInputs.Add(new UseStackRequiredInputValueViewModel(
                    input,
                    existingInputValues.TryGetValue(input.InputId, out var value) ? value : null,
                    OnRequiredInputChanged));
            }

            foreach (var warning in preview.Warnings)
            {
                ImportWarnings.Add(warning);
            }

            foreach (var conflict in preview.Conflicts)
            {
                var prefix = string.Equals(conflict.Severity, "Error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Warning";
                (prefix == "Error" ? ImportErrors : ImportWarnings).Add($"{prefix}: {conflict.Message}");
            }

            foreach (var error in preview.Errors)
            {
                ImportErrors.Add(error);
            }

            StatusText = preview.Success
                ? ImportActions.Count == 0
                    ? "No actions are needed for the selected fragments."
                    : $"Previewed {ImportActions.Count} setup action{StackDisplayFormatters.Plural(ImportActions.Count)}."
                : ImportErrors.FirstOrDefault() ?? "Stack setup preview failed.";
        }
        catch (Exception ex)
        {
            ImportErrors.Add(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyWizardStateChanged();
        }
    }

    private void ApplyInstallResult(RegistryPackageInstallExecutionResult result)
    {
        InstallPlanWarnings.Clear();
        foreach (var warning in result.Warnings)
        {
            InstallPlanWarnings.Add(warning);
        }

        InstallPlanErrors.Clear();
        foreach (var error in result.Errors)
        {
            InstallPlanErrors.Add(error);
        }

        StatusText = result.Success
            ? $"Installed {result.PlanItems.Count} Stack package change{StackDisplayFormatters.Plural(result.PlanItems.Count)}."
            : result.Message;
    }

    private void ApplyPackageInstallPlan(RegistryResolveInstallPlanResponse plan)
    {
        foreach (var row in PackageRows)
        {
            var item = plan.Items.FirstOrDefault(item => string.Equals(item.PackageId, row.PackageId, StringComparison.OrdinalIgnoreCase));
            row.ApplyInstallPlan(item, plan.Success);
        }
    }

    private Task<IReadOnlyDictionary<string, StackPackageInfo>> LoadPackageInfoAsync(CancellationToken cancellationToken)
        => new StackPackageInfoProvider(runtimeApiClient).LoadAsync(cancellationToken);

    private void OnSetupItemSelectionChanged()
    {
        NotifyWizardStateChanged();
        if (!IsBusy)
        {
            _ = RefreshImportPreviewCoreAsync(CancellationToken.None);
        }
    }

    private void OnRequiredInputChanged()
        => NotifyWizardStateChanged();

    private bool TryResolveRegistryUrl(out Uri registryUrl)
    {
        if (RegistryUrlHelper.TryParse(registryUrlText, out registryUrl!) && registryUrl is not null)
        {
            return true;
        }

        StatusText = "Enter a valid HTTP Registry URL before using this Stack.";
        InstallPlanErrors.Clear();
        InstallPlanErrors.Add(StatusText);
        _installPlanReady = true;
        _installPlanHasErrors = true;
        NotifyWizardStateChanged();
        return false;
    }

    private IReadOnlyList<string> GetSelectedFragmentIds()
        => SetupPackageGroups
            .SelectMany(group => group.Items)
            .Where(item => item.IsSelected)
            .Select(item => item.FragmentId)
            .ToArray();

    private Dictionary<string, string> GetInputValues(bool includeEmptyValues = false)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in RequiredInputs)
        {
            if (includeEmptyValues || !string.IsNullOrWhiteSpace(input.Value))
            {
                values[input.InputId] = input.Value;
            }
        }

        return values;
    }

    private void NotifyWizardStateChanged()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(HasPackages));
        OnPropertyChanged(nameof(HasInstallPlanItems));
        OnPropertyChanged(nameof(HasInstallPlanWarnings));
        OnPropertyChanged(nameof(HasInstallPlanErrors));
        OnPropertyChanged(nameof(ShowNoInstallPlanChanges));
        OnPropertyChanged(nameof(HasSetupPackageGroups));
        OnPropertyChanged(nameof(HasSetupItems));
        OnPropertyChanged(nameof(HasSelectedSetupItems));
        OnPropertyChanged(nameof(HasPackageRequirements));
        OnPropertyChanged(nameof(HasApplicableContent));
        OnPropertyChanged(nameof(HasImportActions));
        OnPropertyChanged(nameof(HasRequiredInputs));
        OnPropertyChanged(nameof(HasMissingRequiredInputs));
        OnPropertyChanged(nameof(HasImportWarnings));
        OnPropertyChanged(nameof(HasImportErrors));
        OnPropertyChanged(nameof(HasImportedItems));
        OnPropertyChanged(nameof(HasStackProfileMedia));
        OnPropertyChanged(nameof(HasStackReadme));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _stackProfile.Dispose();
        GC.SuppressFinalize(this);
    }

    private static MarketplacePackageProfileViewModel BuildStackProfile(LocalStackLibraryItem stack)
    {
        var profile = new MarketplacePackageProfileViewModel();
        profile.Apply(new RegistryStackProfile(
            stack.StackId,
            stack.Summary,
            stack.ReadmeMarkdown,
            WebsiteUrl: null,
            SourceUrl: null,
            IssueTrackerUrl: null,
            License: null,
            Tags: [],
            Media: (stack.Media ?? [])
                .OrderBy(media => media.SortOrder)
                .Select(media => new RegistryStackMedia(
                    Guid.NewGuid(),
                    media.FileName,
                    media.ContentType,
                    media.Size,
                    media.AltText,
                    media.SortOrder,
                    new Uri(media.LocalPath).AbsoluteUri))
                .ToArray(),
            stack.UpdatedAtUtc));
        return profile;
    }

}

public sealed partial class UseStackPackageReviewViewModel(SunderStackPackageRequirement package, StackPackageInfo? packageInfo) : PackageIconItemViewModel(packageInfo?.IconUri)
{
    public string PackageId { get; } = package.PackageId ?? "unknown";

    public string DisplayName { get; } = packageInfo?.DisplayName ?? package.PackageId ?? "Unknown package";

    public string Glyph { get; } = StackDisplayFormatters.PackageGlyph(packageInfo?.Icon, packageInfo?.DisplayName ?? package.PackageId ?? "Package", package.PackageId ?? "package");

    public string RequirementText { get; } = StackDisplayFormatters.PackageRequirementText(package);

    [ObservableProperty]
    private string _statusText = "Checking";

    public void ApplyInstallPlan(RegistryPackageInstallPlanItem? item, bool planSuccess)
    {
        StatusText = !planSuccess
            ? "Blocked"
            : item is null
                ? "Installed"
                : item.CurrentVersion is null
                    ? $"Will install {item.Version}"
                    : item.IsUpdate
                        ? $"Will update {item.CurrentVersion} -> {item.Version}"
                        : $"Installed {item.Version}";
    }

}

public sealed partial class UseStackSetupPackageGroupViewModel(
    string packageId,
    string displayName,
    string glyph,
    Uri? iconUri,
    IReadOnlyList<UseStackSetupItemViewModel> items) : PackageIconItemViewModel(iconUri)
{
    public string PackageId { get; } = packageId;

    public string DisplayName { get; } = displayName;

    public string Glyph { get; } = glyph;

    public IReadOnlyList<UseStackSetupItemViewModel> Items { get; } = items;

    public IReadOnlyList<UseStackSetupKindGroupViewModel> ItemGroups { get; } = items
        .GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => StackContentKindLabels.FormatGroupName(group.Key), StringComparer.OrdinalIgnoreCase)
        .Select(group => new UseStackSetupKindGroupViewModel(group.Key, group.ToArray()))
        .ToArray();

    public string CountText => $"{Items.Count} setup item{(Items.Count == 1 ? string.Empty : "s")}";

    public string ExpandActionText => IsExpanded ? "Hide" : "Show";

    [ObservableProperty]
    private bool _isExpanded = true;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandActionText));

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }
}

public sealed class UseStackSetupKindGroupViewModel(string? kind, IReadOnlyList<UseStackSetupItemViewModel> items)
{
    public string? Kind { get; } = kind;

    public string DisplayName { get; } = StackContentKindLabels.FormatGroupName(kind);

    public IReadOnlyList<UseStackSetupItemViewModel> Items { get; } = items;

    public string CountText => $"{Items.Count} item{(Items.Count == 1 ? string.Empty : "s")}";
}

public sealed partial class UseStackSetupItemViewModel : ViewModelBase
{
    private readonly Action _changed;

    public UseStackSetupItemViewModel(SunderStackFragmentManifest fragment, LocalStackDetailItem? details, Action changed)
    {
        _changed = changed;
        FragmentId = fragment.FragmentId ?? string.Empty;
        ContributorId = fragment.ContributorId ?? string.Empty;
        DisplayName = details?.DisplayName ?? fragment.DisplayName ?? fragment.FragmentId ?? "Setup item";
        Summary = details?.Summary ?? BuildSummary(fragment);
        Description = string.IsNullOrWhiteSpace(fragment.Description) ? string.Empty : fragment.Description!;
        Kind = StackContentKindLabels.InferKind(
            fragment.Preview?.Kind ?? details?.Kind,
            fragment.OwnerPackageId,
            fragment.SchemaId,
            fragment.Preview?.SourceItemId ?? fragment.FragmentId,
            DisplayName,
            Summary);
        Details = (details?.Values ?? [])
            .Select(value => new LocalStackDetailValueViewModel(value))
            .ToArray();
    }

    public string FragmentId { get; }

    public string ContributorId { get; }

    public string DisplayName { get; }

    public string Summary { get; }

    public string Description { get; }

    public string? Kind { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public IReadOnlyList<LocalStackDetailValueViewModel> Details { get; }

    public bool HasDetails => Details.Count > 0;

    public string DetailCountText => $"{Details.Count} value{(Details.Count == 1 ? string.Empty : "s")}";

    [ObservableProperty]
    private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => _changed();

    private static string BuildSummary(SunderStackFragmentManifest fragment)
    {
        var schema = string.IsNullOrWhiteSpace(fragment.SchemaId) ? null : fragment.SchemaId;
        var owner = string.IsNullOrWhiteSpace(fragment.OwnerPackageId) ? null : fragment.OwnerPackageId;
        return string.Join(" - ", new[] { schema, owner }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

}

public sealed class UseStackImportActionViewModel(RuntimeStackImportActionDescriptor action)
{
    public string ActionId { get; } = action.ActionId;

    public string DisplayName { get; } = action.DisplayName;

    public string Subtitle { get; } = $"{action.ContributorId} - {action.Kind}";

    public string Description { get; } = string.IsNullOrWhiteSpace(action.Description) ? "No description provided." : action.Description;
}

public sealed partial class UseStackRequiredInputValueViewModel(RuntimeStackRequiredInputDescriptor input, string? currentValue, Action valueChanged) : ViewModelBase
{
    public string InputId { get; } = input.InputId;

    public string Label { get; } = input.Label;

    public string ContributorId { get; } = input.ContributorId;

    public bool Required { get; } = input.Required;

    public string Description { get; } = string.IsNullOrWhiteSpace(input.Description) ? "Provide this value locally before import." : input.Description;

    public string Placeholder { get; } = input.Required ? "Required" : "Optional";

    public bool IsMissingRequiredValue => Required && string.IsNullOrWhiteSpace(Value);

    public string ReviewValue => string.IsNullOrWhiteSpace(Value) ? "Not provided" : "Provided locally";

    [ObservableProperty]
    private string _value = currentValue ?? input.DefaultValue ?? string.Empty;

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsMissingRequiredValue));
        OnPropertyChanged(nameof(ReviewValue));
        valueChanged();
    }
}
