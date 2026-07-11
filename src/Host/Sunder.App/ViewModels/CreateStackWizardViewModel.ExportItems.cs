using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

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

