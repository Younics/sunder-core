using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sunder.App.ViewModels;

public abstract partial class SettingsFieldViewModel(string key, string label, string? description, bool isRequired) : ViewModelBase
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    public string? Description { get; } = description;

    public bool IsRequired { get; } = isRequired;

    [ObservableProperty]
    private bool _isVisible = true;

    public abstract string? GetPersistedValue();
}

public sealed partial class TextSettingsFieldViewModel(
    string key,
    string label,
    string? description,
    bool isRequired,
    string? placeholder,
    string? value) : SettingsFieldViewModel(key, label, description, isRequired)
{
    public string? Placeholder { get; } = placeholder;

    [ObservableProperty]
    private string? _value = value;

    public override string? GetPersistedValue() => string.IsNullOrWhiteSpace(Value) ? null : Value;
}

public sealed partial class SecretSettingsFieldViewModel(
    string key,
    string label,
    string? description,
    bool isRequired,
    string? placeholder,
    bool hasStoredSecretValue,
    string? value) : SettingsFieldViewModel(key, label, description, isRequired)
{
    public string? Placeholder { get; } = hasStoredSecretValue ? "Stored securely" : placeholder;

    public bool HasStoredSecretValue { get; } = hasStoredSecretValue;

    [ObservableProperty]
    private string? _value = value;

    public override string? GetPersistedValue() => string.IsNullOrWhiteSpace(Value) ? null : Value;
}

public sealed partial class BooleanSettingsFieldViewModel(
    string key,
    string label,
    string? description,
    bool isRequired,
    bool value) : SettingsFieldViewModel(key, label, description, isRequired)
{
    [ObservableProperty]
    private bool _value = value;

    public override string? GetPersistedValue() => Value ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant();
}

public sealed record SettingsOptionItem(string Value, string Label);

public sealed partial class SelectSettingsFieldViewModel(
    string key,
    string label,
    string? description,
    bool isRequired,
    IReadOnlyList<SettingsOptionItem> options,
    string? selectedValue) : SettingsFieldViewModel(key, label, description, isRequired)
{
    public ObservableCollection<SettingsOptionItem> Options { get; } = new(options);

    [ObservableProperty]
    private SettingsOptionItem? _selectedOption = options.FirstOrDefault(option => option.Value == selectedValue)
                                                ?? options.FirstOrDefault();

    public override string? GetPersistedValue() => SelectedOption?.Value;
}

public sealed class SettingsFieldSectionViewModel(string title, string? description, IReadOnlyList<SettingsFieldViewModel> fields)
{
    public string Title { get; } = title;

    public string? Description { get; } = description;

    public ObservableCollection<SettingsFieldViewModel> Fields { get; } = new(fields);
}

public sealed partial class PackageAuthActionViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _canAuthorize;

    [ObservableProperty]
    private bool _canDisconnect;

    [ObservableProperty]
    private string? _pendingAuthSessionId;

    [ObservableProperty]
    private string? _launchUrl;
}

public sealed partial class SettingsSectionItemViewModel(
    string id,
    string title,
    string description,
    bool isPackage,
    string? packageId = null) : ViewModelBase
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Description { get; } = description;

    public bool IsPackage { get; } = isPackage;

    public string? PackageId { get; } = packageId;

    [ObservableProperty]
    private bool _isSelected;
}
