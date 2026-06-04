using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.PackageManagement;
using Sunder.Protocol;

namespace Sunder.App.ViewModels;

public enum CreateStackWizardStep
{
    Packages,
    Items,
    Review,
}

public sealed partial class CreateStackWizardViewModel(
    LocalStackLibraryService library,
    IRuntimeApiClient runtimeApiClient) : ViewModelBase, IDisposable
{
    private IReadOnlyList<RuntimeStackExportItemDescriptor> _allItems = [];
    private bool _disposed;

    public event Action<bool?>? CloseRequested;

    public ObservableCollection<CreateStackPackageGroupViewModel> PackageGroups { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    public string? CreatedStackId { get; private set; }

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
        _ => "Create Stack",
    };

    public string StepDescription => CurrentStep switch
    {
        CreateStackWizardStep.Packages => "Pick the packages that should be installed when this Stack is used.",
        CreateStackWizardStep.Items => "Optionally choose setup items that should be imported after selected packages are installed.",
        CreateStackWizardStep.Review => "Review the exact content that will be exported and create the Stack when it looks safe.",
        _ => string.Empty,
    };

    public string SelectedSummary => $"{SelectedPackageCount} package{StackDisplayFormatters.Plural(SelectedPackageCount)} · {SelectedItemCount} setup item{StackDisplayFormatters.Plural(SelectedItemCount)} selected";

    public string PackagesStepStatus => SelectedPackageCount == 0
        ? "Choose package groups"
        : $"{SelectedPackageCount} package{StackDisplayFormatters.Plural(SelectedPackageCount)} selected";

    public string ItemsStepStatus => SelectedItemCount == 0
        ? HasSelectedPackages ? "No setup items selected" : "Select packages first"
        : $"{SelectedItemCount} setup item{StackDisplayFormatters.Plural(SelectedItemCount)} selected";

    public string ReviewStepStatus => CanCreate ? "Ready to create" : "Complete selections first";

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
    private string _stackName = "New Stack";

    [ObservableProperty]
    private string _stackId = "sunder-stack-" + DateTimeOffset.Now.ToString("yyyyMMddHHmm");

    [ObservableProperty]
    private string _stackShortDescription = string.Empty;

    [ObservableProperty]
    private string _statusText = "Choose packages to include in the Stack.";

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

        IsBusy = true;
        Warnings.Clear();
        Errors.Clear();
        PackageGroups.Clear();
        try
        {
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

            foreach (var packageId in packageIds.OrderBy(packageId => packageInfo.TryGetValue(packageId, out var info) ? info.DisplayName : packageId, StringComparer.OrdinalIgnoreCase))
            {
                packageInfo.TryGetValue(packageId, out var info);
                PackageGroups.Add(new CreateStackPackageGroupViewModel(
                    packageId,
                    _allItems.Where(item => string.Equals(item.OwnerPackageId, packageId, StringComparison.OrdinalIgnoreCase)),
                    info,
                    NotifyWizardStateChanged));
            }

            StatusText = PackageGroups.Count == 0
                ? "No active packages are available to include in a Stack."
                : $"Discovered {PackageGroups.Count} package{StackDisplayFormatters.Plural(PackageGroups.Count)} and {_allItems.Count} setup item{StackDisplayFormatters.Plural(_allItems.Count)}.";
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

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (CurrentStep != CreateStackWizardStep.Packages)
        {
            CurrentStep = CurrentStep == CreateStackWizardStep.Review
                ? CreateStackWizardStep.Items
                : CreateStackWizardStep.Packages;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (CurrentStep != CreateStackWizardStep.Review)
        {
            CurrentStep = CurrentStep == CreateStackWizardStep.Packages
                ? CreateStackWizardStep.Items
                : CreateStackWizardStep.Review;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllPackages))]
    private void SelectAllPackages()
    {
        foreach (var group in PackageGroups)
        {
            group.IsSelected = true;
        }

        NotifyWizardStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUnselectAllPackages))]
    private void UnselectAllPackages()
    {
        foreach (var group in PackageGroups)
        {
            group.IsSelected = false;
        }

        NotifyWizardStateChanged();
    }

    [RelayCommand]
    private void Cancel()
        => CloseRequested?.Invoke(false);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var selectedPackages = PackageGroups
            .Where(group => group.IsSelected)
            .Select(group => group.PackageId)
            .ToArray();
        var selectedItems = PackageGroups
            .Where(group => group.IsSelected)
            .SelectMany(group => group.Items)
            .Where(item => item.IsSelected && item.HasSelectedDetails)
            .Select(item => new RuntimeStackExportSelection(
                item.ContributorId,
                item.ItemId,
                item.Details
                    .Where(detail => detail.IsSelected)
                    .Select(detail => new RuntimeStackExportDetailSelection(
                        detail.DetailId,
                        IsSelected: true,
                        detail.ValueOverride,
                        detail.SensitivityOverride))
                    .ToArray())
            {
                OwnerPackageId = item.OwnerPackageId,
            })
            .ToArray();
        if (selectedPackages.Length == 0)
        {
            StatusText = "Select at least one package.";
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "create", Guid.NewGuid().ToString("N"), SunderStackFormat.BuildStackFileName(StackId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        IsBusy = true;
        Errors.Clear();
        Warnings.Clear();
        try
        {
            StatusText = "Saving local Stack...";
            var result = await runtimeApiClient.ExportStackAsync(new RuntimeStackExportRequest(
                StackId: StackId.Trim(),
                Name: StackName.Trim(),
                Summary: NormalizeOptionalText(StackShortDescription),
                OutputPath: path,
                SelectedItems: selectedItems,
                ReadmeMarkdown: null,
                Media: null,
                SelectedPackages: selectedPackages));

            foreach (var warning in result.Warnings)
            {
                Warnings.Add(warning);
            }

            foreach (var error in result.Errors)
            {
                Errors.Add(error);
            }

            if (!result.Success)
            {
                StatusText = Errors.FirstOrDefault() ?? "Stack export failed.";
                return;
            }

            var imported = await library.ImportAsync(result.StackPath ?? path);
            await library.UpdateDetailsAsync(imported.StackId, BuildLocalDetails(), CancellationToken.None);
            CreatedStackId = imported.StackId;
            StatusText = $"Created local Stack '{imported.Name}'.";
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            Errors.Add(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            TryDeleteDirectory(Path.GetDirectoryName(path));
            NotifyWizardStateChanged();
        }
    }

    private IReadOnlyList<LocalStackDetailPackage> BuildLocalDetails()
        => PackageGroups
            .Where(group => group.IsSelected && group.HasSelectedItems)
            .Select(group => new LocalStackDetailPackage(
                group.PackageId,
                group.DisplayName,
                group.Glyph,
                group.Items
                    .Where(item => item.IsSelected && item.HasSelectedDetails)
                    .Select(item => new LocalStackDetailItem(
                        item.ItemId,
                        item.DisplayName,
                        item.SummaryText,
                        item.Details
                            .Where(detail => detail.IsSelected)
                            .Select(detail => new LocalStackDetailValue(
                                detail.Label,
                                detail.SelectedExportBehavior == "Ask on import" ? "Importer will provide this value." : detail.Value,
                                detail.SelectedExportBehavior))
                            .ToArray(),
                        item.Kind))
                    .ToArray(),
                group.IconAssetPath))
            .ToArray();

    private void NotifyWizardStateChanged()
    {
        OnPropertyChanged(nameof(IsPackagesStep));
        OnPropertyChanged(nameof(IsItemsStep));
        OnPropertyChanged(nameof(IsReviewStep));
        OnPropertyChanged(nameof(PackagesStepComplete));
        OnPropertyChanged(nameof(ItemsStepComplete));
        OnPropertyChanged(nameof(ReviewStepReady));
        OnPropertyChanged(nameof(ShowNextButton));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(HasPackageGroups));
        OnPropertyChanged(nameof(ShowNoPackageGroups));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasSelectedPackages));
        OnPropertyChanged(nameof(HasSelectedPackagesWithItems));
        OnPropertyChanged(nameof(ShowNoItemsStepPackages));
        OnPropertyChanged(nameof(HasSelectedItems));
        OnPropertyChanged(nameof(SelectedPackageCount));
        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(CanSelectAllPackages));
        OnPropertyChanged(nameof(CanUnselectAllPackages));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(PackagesStepStatus));
        OnPropertyChanged(nameof(ItemsStepStatus));
        OnPropertyChanged(nameof(ReviewStepStatus));
        OnPropertyChanged(nameof(IncludedReviewDetailCount));
        OnPropertyChanged(nameof(ExcludedReviewDetailCount));
        OnPropertyChanged(nameof(HasExcludedReviewDetails));
        OnPropertyChanged(nameof(HasStackShortDescription));
        OnPropertyChanged(nameof(ReviewImpactSummary));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepDescription));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        SelectAllPackagesCommand.NotifyCanExecuteChanged();
        UnselectAllPackagesCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
    }

    private async Task<IReadOnlyDictionary<string, StackPackageInfo>> LoadPackageInfoAsync(CancellationToken cancellationToken)
        => await new StackPackageInfoProvider(runtimeApiClient).LoadAsync(cancellationToken);

    private async Task<IReadOnlyList<string>> LoadActivePackageIdsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await runtimeApiClient.GetActivePackagesAsync(cancellationToken))
                .Select(package => package.PackageId)
                .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var group in PackageGroups)
        {
            group.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary local Stack creation cleanup is best effort.
        }
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}

public sealed partial class CreateStackPackageGroupViewModel : PackageIconItemViewModel
{
    private readonly Action _changed;

    public CreateStackPackageGroupViewModel(
        string packageId,
        IEnumerable<RuntimeStackExportItemDescriptor> items,
        StackPackageInfo? packageInfo,
        Action changed)
        : base(packageInfo?.IconUri)
    {
        PackageId = packageId;
        DisplayName = packageInfo?.DisplayName ?? packageId;
        Glyph = StackDisplayFormatters.PackageGlyph(packageInfo?.Icon, DisplayName, packageId);
        IconAssetPath = packageInfo?.Icon?.AssetPath;
        _changed = changed;
        foreach (var item in items.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            Items.Add(new CreateStackExportItemViewModel(item, OnItemSelectionChanged));
        }

        ItemGroups = Items
            .GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => StackContentKindLabels.FormatGroupName(group.Key), StringComparer.OrdinalIgnoreCase)
            .Select(group => new CreateStackExportKindGroupViewModel(group.Key, group.ToArray()))
            .ToArray();

        ContentSummary = BuildContentSummary(Items);
    }

    public string PackageId { get; }

    public string DisplayName { get; }

    public string Glyph { get; }

    public string? IconAssetPath { get; }

    public string ContentSummary { get; }

    public string SecretSummary => BuildSecretSummary(Items);

    public bool HasSecretSummary => !string.IsNullOrWhiteSpace(SecretSummary);

    public ObservableCollection<CreateStackExportItemViewModel> Items { get; } = [];

    public IReadOnlyList<CreateStackExportKindGroupViewModel> ItemGroups { get; }

    public bool HasItems => Items.Count > 0;

    public bool HasSelectedItems => IsSelected && Items.Any(item => item.IsSelected && item.HasSelectedDetails);

    public bool IsVisibleInItemsStep => IsSelected && HasItems;

    public bool ShowReviewNoSelectedItems => IsSelected && !HasSelectedItems;

    public string ReviewNoSelectedItemsText => HasItems
        ? "Package will be installed. No setup items selected."
        : "Package will be installed. It does not expose setup items.";

    public int SelectedItemCount => Items.Count(item => item.IsSelected);

    public int TotalItemCount => Items.Count;

    public string CountText => $"{SelectedItemCount}/{Items.Count} items";

    public string ExpandActionText => IsExpanded ? "Hide" : "Show";

    public bool IsCollapsed => !IsExpanded;

    public bool CanSelectAllItems => Items.Any(item => !item.IsSelected);

    public bool CanUnselectAllItems => Items.Any(item => item.IsSelected);

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded = true;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandActionText));

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllItems))]
    private void SelectAllItems()
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }

        NotifyGroupChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUnselectAllItems))]
    private void UnselectAllItems()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }

        NotifyGroupChanged();
    }

    partial void OnIsSelectedChanged(bool value)
    {
        NotifyGroupChanged();
    }

    private void OnItemSelectionChanged()
    {
        NotifyGroupChanged();
    }

    private void NotifyGroupChanged()
    {
        foreach (var item in Items)
        {
            item.RefreshReviewState();
        }

        foreach (var itemGroup in ItemGroups)
        {
            itemGroup.RefreshReviewState();
        }

        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(HasSelectedItems));
        OnPropertyChanged(nameof(IsVisibleInItemsStep));
        OnPropertyChanged(nameof(ShowReviewNoSelectedItems));
        OnPropertyChanged(nameof(ReviewNoSelectedItemsText));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CanSelectAllItems));
        OnPropertyChanged(nameof(CanUnselectAllItems));
        SelectAllItemsCommand.NotifyCanExecuteChanged();
        UnselectAllItemsCommand.NotifyCanExecuteChanged();
        _changed();
    }

    private static string BuildContentSummary(IReadOnlyCollection<CreateStackExportItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return "No setup items. The package itself will be included.";
        }

        var kinds = items
            .Select(item => item.KindDisplay)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        return kinds.Length == 0
            ? $"{items.Count} setup item{StackDisplayFormatters.Plural(items.Count)}"
            : $"{items.Count} setup item{StackDisplayFormatters.Plural(items.Count)} · {string.Join(", ", kinds)}";
    }

    private static string BuildSecretSummary(IReadOnlyCollection<CreateStackExportItemViewModel> items)
    {
        var secretItemCount = items.Count(item => item.HasSecretValues);
        return secretItemCount == 0
            ? string.Empty
            : $"{secretItemCount} setup item{StackDisplayFormatters.Plural(secretItemCount)} will ask for secret values on import.";
    }

}

public sealed class CreateStackExportKindGroupViewModel(string? kind, IReadOnlyList<CreateStackExportItemViewModel> items) : ViewModelBase
{
    public string? Kind { get; } = kind;

    public string DisplayName { get; } = StackContentKindLabels.FormatGroupName(kind);

    public IReadOnlyList<CreateStackExportItemViewModel> Items { get; } = items;

    public IReadOnlyList<CreateStackExportItemViewModel> ReviewItems => Items
        .Where(item => item.IsIncludedInReview)
        .ToArray();

    public bool IsIncludedInReview => ReviewItems.Count > 0;

    public string CountText => $"{Items.Count} item{(Items.Count == 1 ? string.Empty : "s")}";

    public string ReviewCountText => $"{ReviewItems.Count} item{(ReviewItems.Count == 1 ? string.Empty : "s")}";

    public void RefreshReviewState()
    {
        OnPropertyChanged(nameof(ReviewItems));
        OnPropertyChanged(nameof(IsIncludedInReview));
        OnPropertyChanged(nameof(ReviewCountText));
    }
}

public sealed partial class CreateStackExportItemViewModel(RuntimeStackExportItemDescriptor item, Action changed) : ViewModelBase
{
    public string ContributorId { get; } = item.ContributorId;

    public string OwnerPackageId { get; } = item.OwnerPackageId;

    public string ItemId { get; } = item.ItemId;

    public string DisplayName { get; } = item.DisplayName;

    public string Kind { get; } = item.Kind;

    public string KindDisplay { get; } = StackContentKindLabels.HumanizeToken(item.Kind);

    public string Description { get; } = item.Description ?? string.Empty;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string DescriptionPreview { get; } = string.IsNullOrWhiteSpace(item.Description)
        ? BuildSummaryText(item)
        : item.Description;

    public IReadOnlyList<CreateStackExportDetailViewModel> Details { get; } = BuildDetails(item, changed);

    public bool HasDetails => Details.Count > 0;

    public bool HasSelectedDetails => Details.Any(detail => detail.IsSelected);

    public bool IsIncludedInReview => IsSelected && HasSelectedDetails;

    public IReadOnlyList<string> SecretChips { get; } = BuildSecretChips(item.Sensitivities);

    public bool HasSecretChips => SecretChips.Count > 0;

    public bool HasSecretValues { get; } = item.Sensitivities.Any(sensitivity => string.Equals(sensitivity, "Secret", StringComparison.OrdinalIgnoreCase));

    public string SummaryText { get; } = BuildSummaryText(item);

    public int IncludedDetailCount => Details.Count(detail => detail.IsIncludedByOptions);

    public int ExcludedDetailCount => Details.Count(detail => !detail.IsIncludedByOptions);

    public bool HasExcludedDetails => ExcludedDetailCount > 0;

    public int SelectedDetailCount => Details.Count(detail => detail.IsSelected);

    public string DetailCountText => $"{SelectedDetailCount}/{Details.Count} values";

    public bool CanSelectAllDetails => Details.Any(detail => !detail.IsSelected);

    public bool CanUnselectAllDetails => Details.Any(detail => detail.IsSelected);

    public string ExpandActionText => IsExpanded ? "Hide details" : "Show details";

    public bool IsVisible => true;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIncludedInReview));
        changed();
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandActionText));

    public void RefreshOptionState()
    {
        foreach (var detail in Details)
        {
            detail.RefreshOptionState();
        }

        OnPropertyChanged(nameof(IncludedDetailCount));
        OnPropertyChanged(nameof(ExcludedDetailCount));
        OnPropertyChanged(nameof(HasExcludedDetails));
        OnPropertyChanged(nameof(HasSelectedDetails));
        OnPropertyChanged(nameof(IsIncludedInReview));
        OnPropertyChanged(nameof(SelectedDetailCount));
        OnPropertyChanged(nameof(DetailCountText));
    }

    public void RefreshReviewState()
    {
        OnPropertyChanged(nameof(HasSelectedDetails));
        OnPropertyChanged(nameof(IsIncludedInReview));
        OnPropertyChanged(nameof(SelectedDetailCount));
        OnPropertyChanged(nameof(DetailCountText));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllDetails))]
    private void SelectAllDetails()
    {
        foreach (var detail in Details)
        {
            detail.IsSelected = true;
        }

        NotifyDetailSelectionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUnselectAllDetails))]
    private void UnselectAllDetails()
    {
        foreach (var detail in Details)
        {
            detail.IsSelected = false;
        }

        NotifyDetailSelectionChanged();
    }

    private void NotifyDetailSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelectedDetails));
        OnPropertyChanged(nameof(SelectedDetailCount));
        OnPropertyChanged(nameof(DetailCountText));
        OnPropertyChanged(nameof(CanSelectAllDetails));
        OnPropertyChanged(nameof(CanUnselectAllDetails));
        SelectAllDetailsCommand.NotifyCanExecuteChanged();
        UnselectAllDetailsCommand.NotifyCanExecuteChanged();
        changed();
    }

    private static IReadOnlyList<CreateStackExportDetailViewModel> BuildDetails(RuntimeStackExportItemDescriptor item, Action changed)
    {
        var details = (item.Details ?? [])
            .Where(detail => !string.IsNullOrWhiteSpace(detail.Label) && !string.IsNullOrWhiteSpace(detail.Value))
            .Select(detail => new CreateStackExportDetailViewModel(detail, changed))
            .ToArray();
        if (details.Length > 0)
        {
            return details;
        }

        return [new CreateStackExportDetailViewModel(new RuntimeStackExportItemDetail(
            "Setup item",
            string.IsNullOrWhiteSpace(item.Description) ? $"{StackContentKindLabels.HumanizeToken(item.Kind)} configuration" : item.Description,
            item.Sensitivities.FirstOrDefault()), changed)] ;
    }

    private static IReadOnlyList<string> BuildSecretChips(IReadOnlyList<string> sensitivities)
    {
        return sensitivities.Any(sensitivity => string.Equals(sensitivity, "Secret", StringComparison.OrdinalIgnoreCase))
            ? ["secret prompt"]
            : [];
    }

    private static string BuildSummaryText(RuntimeStackExportItemDescriptor item)
    {
        var details = item.Details ?? [];
        var names = details
            .Where(detail => !string.IsNullOrWhiteSpace(detail.Label))
            .Select(detail => detail.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        return names.Length == 0
            ? StackContentKindLabels.HumanizeToken(item.Kind)
            : string.Join(", ", names);
    }

    private static string Shorten(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(4, maxLength - 3)] + "...";
    }
}

public sealed partial class CreateStackExportDetailViewModel(RuntimeStackExportItemDetail detail, Action changed) : ViewModelBase
{
    private readonly string _originalValue = detail.Value;
    private readonly bool _defaultAskOnImport = string.Equals(detail.Sensitivity, "Secret", StringComparison.OrdinalIgnoreCase);
    private const string IncludeValueBehavior = "Include value";
    private const string AskOnImportBehavior = "Ask on import";

    public IReadOnlyList<string> AvailableExportBehaviors { get; } = (detail.SupportsAskOnImport || string.Equals(detail.Sensitivity, "Secret", StringComparison.OrdinalIgnoreCase))
        ? [IncludeValueBehavior, AskOnImportBehavior]
        : [IncludeValueBehavior];

    public string DetailId { get; } = string.IsNullOrWhiteSpace(detail.DetailId)
        ? BuildDetailId(detail.Label)
        : detail.DetailId!;

    public string Label { get; } = detail.Label;

    public string Value => EditedValue;

    public string EffectiveValue => SelectedExportBehavior == AskOnImportBehavior
        ? "Importer will provide this value."
        : IsIncludedByOptions ? Value : ValueWhenExcluded;

    public string EffectiveValuePreview => StackDisplayFormatters.ShortenSingleLine(SelectedExportBehavior == AskOnImportBehavior ? "Importer will provide this value." : EffectiveValue, 180);

    public string ValueWhenExcluded { get; } = string.IsNullOrWhiteSpace(detail.ValueWhenExcluded) ? "Not included" : detail.ValueWhenExcluded!;

    public string? ValueOverride => SelectedExportBehavior == AskOnImportBehavior || string.Equals(EditedValue, _originalValue, StringComparison.Ordinal)
        ? null
        : EditedValue;

    public string? SensitivityOverride
    {
        get
        {
            var asksOnImport = SelectedExportBehavior == AskOnImportBehavior;
            return asksOnImport == _defaultAskOnImport
                ? null
                : asksOnImport ? "Secret" : "Public";
        }
    }

    public string FlagText => SelectedExportBehavior == AskOnImportBehavior ? "ask on import" : string.Empty;

    public bool HasFlagText => !string.IsNullOrWhiteSpace(FlagText)
        && !string.Equals(FlagText, "none", StringComparison.OrdinalIgnoreCase);

    public bool IsIncludedByOptions => true;

    public bool IsExcludedByOptions => false;

    public string ExpandActionText => IsExpanded ? "Hide" : "Show";

    public bool IsCollapsed => !IsExpanded;

    public bool IsEditable { get; } = detail.IsEditable;

    public bool CanEditValue => IsEditable && SelectedExportBehavior == IncludeValueBehavior;

    [ObservableProperty]
    private bool _isSelected = detail.DefaultSelected;

    [ObservableProperty]
    private string _editedValue = detail.Value;

    [ObservableProperty]
    private string _selectedExportBehavior = string.Equals(detail.Sensitivity, "Secret", StringComparison.OrdinalIgnoreCase)
        ? AskOnImportBehavior
        : IncludeValueBehavior;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsSelectedChanged(bool value) => changed();

    partial void OnEditedValueChanged(string value)
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(EffectiveValue));
        OnPropertyChanged(nameof(EffectiveValuePreview));
        OnPropertyChanged(nameof(ValueOverride));
        changed();
    }

    partial void OnSelectedExportBehaviorChanged(string value)
    {
        OnPropertyChanged(nameof(FlagText));
        OnPropertyChanged(nameof(HasFlagText));
        OnPropertyChanged(nameof(CanEditValue));
        OnPropertyChanged(nameof(EffectiveValuePreview));
        OnPropertyChanged(nameof(IsIncludedByOptions));
        OnPropertyChanged(nameof(IsExcludedByOptions));
        OnPropertyChanged(nameof(SensitivityOverride));
        changed();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandActionText));
        OnPropertyChanged(nameof(IsCollapsed));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    public void RefreshOptionState()
    {
        OnPropertyChanged(nameof(EffectiveValue));
        OnPropertyChanged(nameof(EffectiveValuePreview));
        OnPropertyChanged(nameof(IsIncludedByOptions));
        OnPropertyChanged(nameof(IsExcludedByOptions));
    }

    private static string BuildDetailId(string label)
    {
        var builder = new System.Text.StringBuilder(label.Length);
        var pendingSeparator = false;
        foreach (var character in label.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                pendingSeparator = false;
                continue;
            }

            if (builder.Length > 0 && !pendingSeparator)
            {
                builder.Append('-');
                pendingSeparator = true;
            }
        }

        var detailId = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(detailId) ? "detail" : detailId;
    }
}
