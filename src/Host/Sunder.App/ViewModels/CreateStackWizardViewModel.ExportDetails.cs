using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

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
