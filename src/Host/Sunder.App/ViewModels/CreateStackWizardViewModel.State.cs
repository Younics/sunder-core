using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public enum CreateStackWizardStep
{
    Packages,
    Items,
    Review,
}

public sealed partial class CreateStackWizardViewModel(
    LocalStackLibraryService library,
    IRuntimeApiClient runtimeApiClient,
    CreateStackWizardEditContext? editContext = null) : ViewModelBase, IDisposable
{
    private readonly CreateStackWizardEditContext? _editContext = editContext;
    private IReadOnlyList<RuntimeStackExportItemDescriptor> _allItems = [];
    private SunderStackManifest? _editManifest;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public event Action<bool?>? CloseRequested;

    public ObservableCollection<CreateStackPackageGroupViewModel> PackageGroups { get; } = [];

    public ObservableCollection<CreateStackPreservedFragmentViewModel> PreservedFragments { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    public string? CreatedStackId { get; private set; }

    public bool IsEditMode => _editContext is not null;

    public bool IsCreateMode => !IsEditMode;

    public string WindowTitle => IsEditMode ? "Edit Stack" : "Create Stack";

    public string WizardTitle => IsEditMode ? "Edit Stack" : "Stack Wizard";

    public string WizardDescription => IsEditMode
        ? "Update the packages and setup items in this local Stack."
        : "Build a focused setup preset one step at a time.";

    public string PrimaryActionText => IsEditMode ? "Save Changes" : "Create Stack";

    public bool CanEditStackId => !IsEditMode;

    public bool IsStackIdReadOnly => IsEditMode;

    public bool IsPackagesStep => CurrentStep == CreateStackWizardStep.Packages;

    public bool IsItemsStep => CurrentStep == CreateStackWizardStep.Items;

    public bool IsReviewStep => CurrentStep == CreateStackWizardStep.Review;

    public bool PackagesStepComplete => HasSelectedPackages;

    public bool ItemsStepComplete => HasSelectedPackages;

    public bool ReviewStepReady => CanCreate;

    public bool ShowNextButton => !IsReviewStep;

    public bool CanGoBack => !IsBusy && CurrentStep != CreateStackWizardStep.Packages;

    public bool CanGoNext => !IsBusy
                             && CurrentStep != CreateStackWizardStep.Review
                             && HasSelectedPackages;

    public bool CanCreate => !IsBusy
                             && IsReviewStep
                             && HasSelectedPackages
                             && !string.IsNullOrWhiteSpace(StackId)
                             && !string.IsNullOrWhiteSpace(StackName);

    public bool HasPackageGroups => PackageGroups.Count > 0;

    public bool ShowNoPackageGroups => !HasPackageGroups && !IsBusy && Errors.Count == 0;

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasErrors => Errors.Count > 0;

    public bool HasSelectedPackages => PackageGroups.Any(group => group.IsSelected);

    public bool HasPreservedFragments => PreservedFragments.Count > 0;

    public int SelectedPreservedFragmentCount => GetSelectedPreservedFragments().Count;

    public string PreservedFragmentsSummary => SelectedPreservedFragmentCount == 0
        ? "Existing setup items from unavailable packages will be removed unless their package remains selected."
        : $"{SelectedPreservedFragmentCount} unavailable setup item{StackDisplayFormatters.Plural(SelectedPreservedFragmentCount)} will be preserved because their package remains selected.";

    public bool HasSelectedPackagesWithItems => PackageGroups.Any(group => group.IsVisibleInItemsStep);

    public bool ShowNoItemsStepPackages => HasSelectedPackages && !HasSelectedPackagesWithItems;

    public bool HasSelectedItems => PackageGroups
        .Where(group => group.IsSelected)
        .SelectMany(group => group.Items)
        .Any(item => item.IsSelected && item.HasSelectedDetails && item.IsVisible);

    public int SelectedPackageCount => PackageGroups.Count(group => group.IsSelected);

    public int SelectedItemCount => PackageGroups
        .Where(group => group.IsSelected)
        .SelectMany(group => group.Items)
        .Count(item => item.IsSelected && item.HasSelectedDetails && item.IsVisible);

    public bool CanSelectAllPackages => PackageGroups.Any(group => !group.IsSelected);

    public bool CanUnselectAllPackages => PackageGroups.Any(group => group.IsSelected);

    public string StepTitle => CurrentStep switch
    {
        CreateStackWizardStep.Packages => "Choose Packages",
        CreateStackWizardStep.Items => "Choose Setup Items",
        CreateStackWizardStep.Review => "Review Stack",
        _ => WindowTitle,
    };

    public string StepDescription => CurrentStep switch
    {
        CreateStackWizardStep.Packages => "Pick the packages that should be installed when this Stack is used.",
        CreateStackWizardStep.Items => "Optionally choose setup items that should be imported after selected packages are installed.",
        CreateStackWizardStep.Review => IsEditMode
            ? "Review the exact content that will be saved into this local Stack."
            : "Review the exact content that will be exported and create the Stack when it looks safe.",
        _ => string.Empty,
    };

    public string SelectedSummary => SelectedPreservedFragmentCount == 0
        ? $"{SelectedPackageCount} package{StackDisplayFormatters.Plural(SelectedPackageCount)} · {SelectedItemCount} setup item{StackDisplayFormatters.Plural(SelectedItemCount)} selected"
        : $"{SelectedPackageCount} package{StackDisplayFormatters.Plural(SelectedPackageCount)} · {SelectedItemCount} setup item{StackDisplayFormatters.Plural(SelectedItemCount)} selected · {SelectedPreservedFragmentCount} preserved";

    public string PackagesStepStatus => SelectedPackageCount == 0
        ? "Choose package groups"
        : $"{SelectedPackageCount} package{StackDisplayFormatters.Plural(SelectedPackageCount)} selected";

    public string ItemsStepStatus => SelectedItemCount == 0
        ? HasSelectedPackages ? "No setup items selected" : "Select packages first"
        : $"{SelectedItemCount} setup item{StackDisplayFormatters.Plural(SelectedItemCount)} selected";

    public string ReviewStepStatus => CanCreate
        ? (IsEditMode ? "Ready to save" : "Ready to create")
        : "Complete selections first";

    public int IncludedReviewDetailCount => SelectedDetails.Count(detail => detail.IsIncludedByOptions);

    public int ExcludedReviewDetailCount => SelectedDetails.Count(detail => !detail.IsIncludedByOptions);

    public bool HasExcludedReviewDetails => ExcludedReviewDetailCount > 0;

    public bool HasStackShortDescription => !string.IsNullOrWhiteSpace(StackShortDescription);

    public string ReviewImpactSummary => HasExcludedReviewDetails
        ? $"{SelectedPackageCount} package{StackDisplayFormatters.Plural(SelectedPackageCount)} · {IncludedReviewDetailCount} setup part{StackDisplayFormatters.Plural(IncludedReviewDetailCount)} included · {ExcludedReviewDetailCount} affected by current options"
        : $"{SelectedPackageCount} package{StackDisplayFormatters.Plural(SelectedPackageCount)} · {IncludedReviewDetailCount} setup part{StackDisplayFormatters.Plural(IncludedReviewDetailCount)} included";

    private IEnumerable<CreateStackExportDetailViewModel> SelectedDetails => PackageGroups
        .Where(group => group.IsSelected)
        .SelectMany(group => group.Items)
        .Where(item => item.IsSelected)
        .SelectMany(item => item.Details)
        .Where(detail => detail.IsSelected);

    [ObservableProperty]
    private CreateStackWizardStep _currentStep = CreateStackWizardStep.Packages;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _stackName = editContext?.Stack.Name ?? "New Stack";

    [ObservableProperty]
    private string _stackId = editContext?.Stack.StackId ?? "sunder-stack-" + DateTimeOffset.Now.ToString("yyyyMMddHHmm");

    [ObservableProperty]
    private string _stackShortDescription = editContext?.Stack.Summary ?? string.Empty;

    [ObservableProperty]
    private string _statusText = editContext is null ? "Choose packages to include in the Stack." : "Loading existing Stack content.";

    partial void OnCurrentStepChanged(CreateStackWizardStep value) => NotifyWizardStateChanged();

    partial void OnIsBusyChanged(bool value) => NotifyWizardStateChanged();

    partial void OnStackNameChanged(string value) => NotifyWizardStateChanged();

    partial void OnStackIdChanged(string value) => NotifyWizardStateChanged();

    partial void OnStackShortDescriptionChanged(string value)
        => OnPropertyChanged(nameof(HasStackShortDescription));

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_allItems.Count > 0 || IsBusy)
        {
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linkedCancellation.Token;
        IsBusy = true;
        Warnings.Clear();
        Errors.Clear();
        PackageGroups.Clear();
        PreservedFragments.Clear();
        try
        {
            var editManifest = await LoadEditManifestAsync(cancellationToken);
            StatusText = "Discovering setup items...";
            var discovery = await runtimeApiClient.ListStackExportItemsAsync(cancellationToken);
            _allItems = discovery.Items;
            foreach (var warning in discovery.Warnings)
            {
                Warnings.Add(warning);
            }

            foreach (var error in discovery.Errors)
            {
                Errors.Add(error);
            }

            var packageInfo = await LoadPackageInfoAsync(cancellationToken);
            var packageIds = _allItems
                .Select(item => item.OwnerPackageId)
                .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var packageId in await LoadActivePackageIdsAsync(cancellationToken))
            {
                packageIds.Add(packageId);
            }

            foreach (var packageId in GetEditPackageIds(editManifest))
            {
                packageIds.Add(packageId);
            }

            foreach (var packageId in packageIds.OrderBy(packageId => packageInfo.TryGetValue(packageId, out var info) ? info.DisplayName : packageId, StringComparer.OrdinalIgnoreCase))
            {
                packageInfo.TryGetValue(packageId, out var info);
                PackageGroups.Add(new CreateStackPackageGroupViewModel(
                    packageId,
                    _allItems.Where(item => string.Equals(item.OwnerPackageId, packageId, StringComparison.OrdinalIgnoreCase)),
                    info,
                    NotifyWizardStateChanged));
            }

            ApplyEditManifestSelections(editManifest);

            StatusText = PackageGroups.Count == 0
                ? "No active packages are available to include in a Stack."
                : IsEditMode
                    ? $"Loaded {PackageGroups.Count} package{StackDisplayFormatters.Plural(PackageGroups.Count)} and {_allItems.Count} available setup item{StackDisplayFormatters.Plural(_allItems.Count)}."
                    : $"Discovered {PackageGroups.Count} package{StackDisplayFormatters.Plural(PackageGroups.Count)} and {_allItems.Count} setup item{StackDisplayFormatters.Plural(_allItems.Count)}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Errors.Add(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyWizardStateChanged();
        }
    }

    private async Task<SunderStackManifest?> LoadEditManifestAsync(CancellationToken cancellationToken)
    {
        if (_editContext is null)
        {
            return null;
        }

        var manifest = await library.ReadManifestAsync(_editContext.Stack.LocalPath, cancellationToken);
        _editManifest = manifest;
        StackId = string.IsNullOrWhiteSpace(manifest.StackId) ? _editContext.Stack.StackId : manifest.StackId!;
        StackName = string.IsNullOrWhiteSpace(manifest.Name) ? _editContext.Stack.Name : manifest.Name!;
        StackShortDescription = manifest.Summary ?? string.Empty;
        return manifest;
    }

    private static IReadOnlyList<string> GetEditPackageIds(SunderStackManifest? manifest)
    {
        if (manifest is null)
        {
            return [];
        }

        return (manifest.Packages ?? [])
            .Select(package => package.PackageId)
            .Concat((manifest.Fragments ?? []).Select(fragment => fragment.OwnerPackageId))
            .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
            .Select(packageId => packageId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ApplyEditManifestSelections(SunderStackManifest? manifest)
    {
        if (manifest is null)
        {
            return;
        }

        var selectedPackageIds = GetEditPackageIds(manifest).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in PackageGroups)
        {
            group.IsSelected = selectedPackageIds.Contains(group.PackageId);
        }

        foreach (var fragment in manifest.Fragments ?? [])
        {
            var item = FindMatchingItem(fragment);
            if (item is null)
            {
                PreservedFragments.Add(new CreateStackPreservedFragmentViewModel(fragment));
                continue;
            }

            item.IsSelected = true;
            item.IsExpanded = true;
            ApplyFragmentDetailSelections(item, fragment);
        }

        NotifyWizardStateChanged();
    }

    private CreateStackExportItemViewModel? FindMatchingItem(SunderStackFragmentManifest fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment.OwnerPackageId) || string.IsNullOrWhiteSpace(fragment.ContributorId))
        {
            return null;
        }

        var sourceItemId = fragment.Preview?.SourceItemId;
        foreach (var item in PackageGroups
                     .Where(group => string.Equals(group.PackageId, fragment.OwnerPackageId, StringComparison.OrdinalIgnoreCase))
                     .SelectMany(group => group.Items)
                     .Where(item => string.Equals(item.ContributorId, fragment.ContributorId, StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrWhiteSpace(sourceItemId)
                && string.Equals(item.ItemId, sourceItemId, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            if (string.IsNullOrWhiteSpace(sourceItemId)
                && string.Equals(item.ItemId, fragment.FragmentId, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private static void ApplyFragmentDetailSelections(CreateStackExportItemViewModel item, SunderStackFragmentManifest fragment)
    {
        var displayDetails = fragment.Preview?.DisplayDetails ?? [];
        if (displayDetails.Count == 0)
        {
            return;
        }

        foreach (var detail in item.Details)
        {
            detail.IsSelected = false;
        }

        foreach (var displayDetail in displayDetails)
        {
            if (string.IsNullOrWhiteSpace(displayDetail.Label))
            {
                continue;
            }

            var detail = item.Details.FirstOrDefault(candidate => string.Equals(candidate.Label, displayDetail.Label, StringComparison.OrdinalIgnoreCase));
            if (detail is null)
            {
                continue;
            }

            detail.IsSelected = true;
            if (string.Equals(displayDetail.Behavior, "Ask on import", StringComparison.OrdinalIgnoreCase)
                && detail.AvailableExportBehaviors.Contains("Ask on import", StringComparer.OrdinalIgnoreCase))
            {
                detail.SelectedExportBehavior = "Ask on import";
                continue;
            }

            detail.SelectedExportBehavior = "Include value";
            if (detail.CanEditValue && !string.IsNullOrWhiteSpace(displayDetail.Value))
            {
                detail.EditedValue = displayDetail.Value!;
            }
        }

        item.RefreshOptionState();
        item.RefreshReviewState();
    }

}
