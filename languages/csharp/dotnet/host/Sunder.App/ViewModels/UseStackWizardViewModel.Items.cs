using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class UseStackPackageReviewViewModel(SunderStackPackageRequirement package, StackPackageInfo? packageInfo)
    : PackageIconItemViewModel(
        packageInfo?.IconUri,
        iconTransport: packageInfo?.IconTransport ?? PackageIconTransport.RuntimeAsset)
{
    public string PackageId { get; } = package.PackageId ?? "unknown";

    public string DisplayName { get; } = packageInfo?.DisplayName ?? package.PackageId ?? "Unknown package";

    public string Glyph { get; } = StackDisplayFormatters.PackageGlyph(packageInfo?.Icon, packageInfo?.DisplayName ?? package.PackageId ?? "Package", package.PackageId ?? "package");

    public string RequirementText { get; } = StackDisplayFormatters.PackageRequirementText(package);

    [ObservableProperty]
    private string _statusText = "Checking";

    public void ApplyInstallPlan(RuntimeRegistryPackageInstallPlanItem? item, bool planSuccess)
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
    PackageIconTransport iconTransport,
    IReadOnlyList<UseStackSetupItemViewModel> items)
    : PackageIconItemViewModel(iconUri, iconTransport: iconTransport)
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
        _isSelected = fragment.DefaultSelected != false;
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

public sealed partial class UseStackImportActionViewModel : ViewModelBase
{
    private readonly Action _changed;

    public UseStackImportActionViewModel(RuntimeStackImportActionDescriptor action, Action changed)
    {
        _changed = changed;
        ActionId = action.ActionId;
        DisplayName = action.DisplayName;
        Subtitle = $"{action.ContributorId} - {action.Kind}";
        Description = string.IsNullOrWhiteSpace(action.Description) ? "No description provided." : action.Description;
        _isSelected = action.DefaultSelected;
    }

    public string ActionId { get; }

    public string DisplayName { get; }

    public string Subtitle { get; }

    public string Description { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

public sealed partial class UseStackRequiredInputValueViewModel(RuntimeStackRequiredInputDescriptor input, string? currentValue, Action valueChanged) : ViewModelBase
{
    public string InputId { get; } = input.InputId;

    public string OwnerPackageId { get; } = input.OwnerPackageId;

    public string LocalInputId { get; } = input.LocalInputId;

    public string Label { get; } = input.Label;

    public string ContributorId { get; } = input.ContributorId;

    public bool IsSecret { get; } = input.Sensitivity == RuntimeStackInputSensitivity.Secret;

    public char PasswordCharacter => IsSecret ? '*' : '\0';

    public bool IsHostScoped => !string.IsNullOrWhiteSpace(InputId);

    public bool Matches(RuntimeStackRequiredInputDescriptor input)
        => string.Equals(OwnerPackageId, input.OwnerPackageId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(ContributorId, input.ContributorId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(LocalInputId, input.LocalInputId, StringComparison.OrdinalIgnoreCase);

    public bool Required { get; } = input.Required;

    public string Description { get; } = string.IsNullOrWhiteSpace(input.Description) ? "Provide this value locally before import." : input.Description;

    public string Placeholder { get; } = input.Required ? "Required" : "Optional";

    public bool IsMissingRequiredValue => Required && string.IsNullOrWhiteSpace(Value);

    public string ReviewValue => string.IsNullOrWhiteSpace(Value)
        ? "Not provided"
        : IsSecret ? "Provided locally" : Value;

    [ObservableProperty]
    private string _value = currentValue ?? input.DefaultValue ?? string.Empty;

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsMissingRequiredValue));
        OnPropertyChanged(nameof(ReviewValue));
        valueChanged();
    }
}
