using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class RegistryStackSearchItemViewModel(RegistryStackSummary stack) : ViewModelBase
{
    public string StackId { get; } = stack.StackId;

    public string Name { get; } = stack.Name;

    public string Summary { get; } = string.IsNullOrWhiteSpace(stack.Summary) ? "No summary provided." : stack.Summary;

    public int PackageCount { get; } = stack.PackageCount;

    public int FragmentCount { get; } = stack.FragmentCount;

    public RegistryStackStats? Stats { get; } = stack.Stats;

    public string UpdatedText { get; } = $"Updated {stack.UpdatedAtUtc.LocalDateTime:g}";

    public string CountText { get; } = $"{stack.PackageCount} package{(stack.PackageCount == 1 ? string.Empty : "s")} · {stack.FragmentCount} fragment{(stack.FragmentCount == 1 ? string.Empty : "s")}";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class RegistryStackPackageRequirementViewModel(RegistryStackPackageRequirement package)
{
    public string PackageId { get; } = package.PackageId;

    public string InstallText { get; } = BuildInstallText(package);

    private static string BuildInstallText(RegistryStackPackageRequirement package)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(package.InstallTag))
        {
            parts.Add(package.InstallTag);
        }

        if (!string.IsNullOrWhiteSpace(package.MinimumVersion))
        {
            parts.Add($">= {package.MinimumVersion}");
        }

        if (!string.IsNullOrWhiteSpace(package.CreatedWithVersion))
        {
            parts.Add($"created with {package.CreatedWithVersion}");
        }

        parts.Add(package.Required ? "required" : "optional");
        return string.Join(" · ", parts);
    }
}

public sealed partial class LocalStackLibraryItemViewModel(LocalStackLibraryItem item) : ViewModelBase
{
    public LocalStackLibraryItem Item { get; } = item;

    public string StackId => Item.StackId;

    public string Name => Item.Name;

    public string Summary => string.IsNullOrWhiteSpace(Item.Summary) ? "No summary provided." : Item.Summary;

    public string LocalPath => Item.LocalPath;

    public string? RegistryUrl => Item.RegistryUrl;

    public string? PublishedStackId => Item.PublishedStackId;

    public bool IsPublished => !string.IsNullOrWhiteSpace(Item.PublishedStackId);

    public int PackageCount => Item.PackageCount;

    public int FragmentCount => Item.FragmentCount;

    public string UpdatedText => $"Updated {Item.UpdatedAtUtc.LocalDateTime:g}";

    public string CountText => $"{PackageCount} package{(PackageCount == 1 ? string.Empty : "s")} · {FragmentCount} fragment{(FragmentCount == 1 ? string.Empty : "s")}";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class LocalStackDetailPackageViewModel(LocalStackDetailPackage package, Uri? iconUri) : PackageIconItemViewModel(iconUri)
{
    public string PackageId { get; } = package.PackageId;

    public string DisplayName { get; } = package.DisplayName;

    public string Glyph { get; } = string.IsNullOrWhiteSpace(package.Glyph) ? "?" : package.Glyph;

    public IReadOnlyList<LocalStackDetailItemViewModel> Items { get; } = package.Items
        .Select(item => new LocalStackDetailItemViewModel(item, package.PackageId))
        .ToArray();

    public IReadOnlyList<LocalStackDetailKindGroupViewModel> ItemGroups { get; } = package.Items
        .Select(item => new LocalStackDetailItemViewModel(item, package.PackageId))
        .GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => StackContentKindLabels.FormatGroupName(group.Key), StringComparer.OrdinalIgnoreCase)
        .Select(group => new LocalStackDetailKindGroupViewModel(group.Key, group.ToArray()))
        .ToArray();

    public string CountText => $"{Items.Count} setup item{(Items.Count == 1 ? string.Empty : "s")}";

    public string ExpandActionText => IsExpanded ? "Hide" : "Show";

    public bool IsCollapsed => !IsExpanded;

    [ObservableProperty]
    private bool _isExpanded = true;

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
}

public sealed class LocalStackDetailKindGroupViewModel(string? kind, IReadOnlyList<LocalStackDetailItemViewModel> items)
{
    public string? Kind { get; } = kind;

    public string DisplayName { get; } = StackContentKindLabels.FormatGroupName(kind);

    public IReadOnlyList<LocalStackDetailItemViewModel> Items { get; } = items;

    public string CountText => $"{Items.Count} item{(Items.Count == 1 ? string.Empty : "s")}";
}

public sealed partial class LocalStackDetailItemViewModel(LocalStackDetailItem item, string packageId) : ViewModelBase
{
    public string ItemId { get; } = item.ItemId;

    public string DisplayName { get; } = item.DisplayName;

    public string Summary { get; } = string.IsNullOrWhiteSpace(item.Summary) ? "Selected setup values" : item.Summary;

    public string? Kind { get; } = StackContentKindLabels.InferKind(
        item.Kind,
        packageId,
        itemId: item.ItemId,
        displayName: item.DisplayName,
        summary: item.Summary);

    public IReadOnlyList<LocalStackDetailValueViewModel> Values { get; } = item.Values
        .Select(value => new LocalStackDetailValueViewModel(value))
        .ToArray();

    public bool HasValues => Values.Count > 0;

    public string DetailCountText => $"{Values.Count} value{(Values.Count == 1 ? string.Empty : "s")}";

    public string ExpandActionText => IsExpanded ? "Hide details" : "Show details";

    public bool IsCollapsed => !IsExpanded;

    [ObservableProperty]
    private bool _isExpanded;

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
}

public sealed partial class LocalStackDetailValueViewModel(LocalStackDetailValue value) : ViewModelBase
{
    public string Label { get; } = value.Label;

    public string Value { get; } = value.Value;

    public string ValuePreview { get; } = StackDisplayFormatters.ShortenSingleLine(value.Value, 180);

    public string Behavior { get; } = value.Behavior;

    public bool HasBehavior => !string.IsNullOrWhiteSpace(Behavior);

    public string ExpandActionText => IsExpanded ? "Hide" : "Show";

    public bool IsCollapsed => !IsExpanded;

    [ObservableProperty]
    private bool _isExpanded;

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

}

public sealed class StackPackageRequirementViewModel(SunderStackPackageRequirement package)
{
    public string PackageId { get; } = package.PackageId ?? "unknown";

    public string InstallText { get; } = StackDisplayFormatters.PackageRequirementText(package);
}

public sealed class StackPackageInstallPlanItemViewModel(RegistryPackageInstallPlanItem item)
{
    public string PackageId { get; } = item.PackageId;

    public string VersionText { get; } = item.CurrentVersion is null
        ? $"Install {item.Version}"
        : item.IsUpdate
            ? $"Update {item.CurrentVersion} -> {item.Version}"
            : $"Keep {item.Version}";

    public string DependencyText { get; } = item.DependsOn.Count == 0
        ? "No package dependencies"
        : string.Join(", ", item.DependsOn.Select(dependency => $"{dependency.PackageId} {dependency.VersionRange}"));

    public bool HasDeprecatedMessage { get; } = !string.IsNullOrWhiteSpace(item.DeprecatedMessage);

    public string DeprecatedMessage { get; } = item.DeprecatedMessage ?? string.Empty;
}

public sealed partial class StackFragmentViewModel(SunderStackFragmentManifest fragment) : ViewModelBase
{
    public string FragmentId { get; } = fragment.FragmentId ?? string.Empty;

    public string DisplayName { get; } = fragment.DisplayName ?? fragment.FragmentId ?? "Stack fragment";

    public string Subtitle { get; } = BuildSubtitle(fragment);

    public string Description { get; } = string.IsNullOrWhiteSpace(fragment.Description) ? "No description provided." : fragment.Description;

    [ObservableProperty]
    private bool _isSelected = fragment.DefaultSelected != false;

    private static string BuildSubtitle(SunderStackFragmentManifest fragment)
    {
        var owner = fragment.OwnerPackageId ?? "unknown package";
        var selected = fragment.DefaultSelected == false ? "off by default" : "selected by default";
        return $"{owner} · {selected}";
    }
}
