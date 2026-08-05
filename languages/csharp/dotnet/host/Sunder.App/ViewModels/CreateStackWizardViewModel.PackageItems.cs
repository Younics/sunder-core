using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed record CreateStackWizardEditContext(LocalStackLibraryItem Stack);

public sealed class CreateStackPreservedFragmentViewModel(SunderStackFragmentManifest fragment)
{
    public SunderStackFragmentManifest Fragment { get; } = fragment;

    public string DisplayName { get; } = string.IsNullOrWhiteSpace(fragment.DisplayName)
        ? fragment.FragmentId ?? "Unavailable setup item"
        : fragment.DisplayName!;

    public string Subtitle { get; } = string.Join(
        " · ",
        new[]
        {
            string.IsNullOrWhiteSpace(fragment.OwnerPackageId) ? null : fragment.OwnerPackageId,
            string.IsNullOrWhiteSpace(fragment.ContributorId) ? null : fragment.ContributorId,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public string Summary { get; } = string.IsNullOrWhiteSpace(fragment.Description)
        ? "This setup item is not available from the current runtime session and will be copied from the existing Stack archive."
        : fragment.Description!;
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

