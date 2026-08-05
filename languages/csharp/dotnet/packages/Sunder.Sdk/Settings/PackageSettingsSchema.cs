using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Settings;

/// <summary>Specifies how the host renders and stores a package setting.</summary>
[SunderSdkCapability(SunderSdkCapabilities.SettingsSchemaV1)]
public enum PackageSettingsFieldKind
{
    /// <summary>A visible single-line text value.</summary>
    Text = 0,
    /// <summary>A masked value stored through the host secret facility.</summary>
    Secret = 1,
    /// <summary>A boolean value serialized as <c>true</c> or <c>false</c>.</summary>
    Boolean = 2,
    /// <summary>A value selected from the field's declared options.</summary>
    Select = 3,
}

/// <summary>Defines one persisted value and user-facing label for a selection field.</summary>
[SunderSdkCapability(SunderSdkCapabilities.SettingsSchemaV1)]
public sealed record PackageSettingsOption
{
    /// <summary>Creates a validated selection option.</summary>
    public PackageSettingsOption(string value, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (!PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException(
                $"Settings option values cannot exceed {PackageStorageValidation.MaximumValueUtf8Bytes} UTF-8 bytes.",
                nameof(value));
        }
        Value = value;
        Label = label;
    }

    /// <summary>Gets the exact persisted value.</summary>
    public string Value { get; }
    /// <summary>Gets the user-facing label.</summary>
    public string Label { get; }
}

/// <summary>Defines one host-rendered package settings field.</summary>
[SunderSdkCapability(SunderSdkCapabilities.SettingsSchemaV1)]
public sealed record PackageSettingsField
{
    /// <summary>Creates and validates a settings field.</summary>
    public PackageSettingsField(
        string key,
        string label,
        PackageSettingsFieldKind kind,
        string? description = null,
        bool isRequired = false,
        string? placeholder = null,
        string? defaultValue = null,
        IReadOnlyList<PackageSettingsOption>? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (!PackageStorageValidation.IsValidKey(key))
        {
            throw new ArgumentException(
                $"Settings keys must be portable ASCII tokens of at most {PackageStorageValidation.MaximumKeyLength} characters.",
                nameof(key));
        }
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The settings field kind is invalid.");
        }
        var copiedOptions = Array.AsReadOnly((options ?? []).ToArray());
        if (copiedOptions.Any(static option => option is null))
        {
            throw new ArgumentException($"Setting '{key}' contains a null option.", nameof(options));
        }
        if (kind == PackageSettingsFieldKind.Secret && defaultValue is not null)
        {
            throw new ArgumentException($"Secret setting '{key}' must not declare a default value.", nameof(defaultValue));
        }
        if (defaultValue is not null && !PackageStorageValidation.IsValidValue(defaultValue))
        {
            throw new ArgumentException(
                $"Setting '{key}' default cannot exceed {PackageStorageValidation.MaximumValueUtf8Bytes} UTF-8 bytes.",
                nameof(defaultValue));
        }
        if (kind == PackageSettingsFieldKind.Boolean
            && defaultValue is not null
            && !bool.TryParse(defaultValue, out _))
        {
            throw new ArgumentException($"Boolean setting '{key}' has invalid default '{defaultValue}'.", nameof(defaultValue));
        }
        if (kind == PackageSettingsFieldKind.Select)
        {
            if (copiedOptions.Count == 0)
            {
                throw new ArgumentException($"Select setting '{key}' must declare at least one option.", nameof(options));
            }
            EnsureDistinct(copiedOptions.Select(static option => option.Value), $"Select setting '{key}' option value", nameof(options));
            if (defaultValue is not null
                && !copiedOptions.Any(option => string.Equals(option.Value, defaultValue, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Select setting '{key}' default is not a declared option.", nameof(defaultValue));
            }
        }
        else if (copiedOptions.Count > 0)
        {
            throw new ArgumentException($"Non-select setting '{key}' must not declare options.", nameof(options));
        }

        Key = key;
        Label = label;
        Kind = kind;
        Description = description;
        IsRequired = isRequired;
        Placeholder = placeholder;
        DefaultValue = defaultValue;
        Options = copiedOptions;
    }

    /// <summary>Gets the case-sensitive portable ASCII settings key.</summary>
    public string Key { get; }
    /// <summary>Gets the user-facing label.</summary>
    public string Label { get; }
    /// <summary>Gets the rendering and storage behavior.</summary>
    public PackageSettingsFieldKind Kind { get; }
    /// <summary>Gets optional explanatory text.</summary>
    public string? Description { get; }
    /// <summary>Gets whether an empty value is rejected.</summary>
    public bool IsRequired { get; }
    /// <summary>Gets the optional non-persisted input hint.</summary>
    public string? Placeholder { get; }
    /// <summary>Gets the optional effective value used when no value is stored.</summary>
    public string? DefaultValue { get; }
    /// <summary>Gets immutable selection options.</summary>
    public IReadOnlyList<PackageSettingsOption> Options { get; }

    private static void EnsureDistinct(IEnumerable<string> values, string label, string parameterName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw new ArgumentException($"{label} '{value}' is declared more than once.", parameterName);
            }
        }
    }
}

/// <summary>Groups related fields in host-rendered package settings.</summary>
[SunderSdkCapability(SunderSdkCapabilities.SettingsSchemaV1)]
public sealed record PackageSettingsSection
{
    /// <summary>Creates and validates a settings section.</summary>
    public PackageSettingsSection(
        string sectionId,
        string title,
        string? description,
        IReadOnlyList<PackageSettingsField> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (!PackageStorageValidation.IsValidKey(sectionId))
        {
            throw new ArgumentException(
                $"Settings section ids must be portable ASCII tokens of at most {PackageStorageValidation.MaximumKeyLength} characters.",
                nameof(sectionId));
        }
        ArgumentNullException.ThrowIfNull(fields);
        var copiedFields = Array.AsReadOnly(fields.ToArray());
        if (copiedFields.Count == 0)
        {
            throw new ArgumentException($"Settings section '{sectionId}' must declare at least one field.", nameof(fields));
        }
        if (copiedFields.Any(static field => field is null))
        {
            throw new ArgumentException($"Settings section '{sectionId}' contains a null field.", nameof(fields));
        }
        EnsureDistinct(copiedFields.Select(static field => field.Key), $"Settings section '{sectionId}' field key", nameof(fields));

        SectionId = sectionId;
        Title = title;
        Description = description;
        Fields = copiedFields;
    }

    /// <summary>Gets the stable package-scoped portable ASCII section id.</summary>
    public string SectionId { get; }
    /// <summary>Gets the user-facing title.</summary>
    public string Title { get; }
    /// <summary>Gets optional explanatory text.</summary>
    public string? Description { get; }
    /// <summary>Gets immutable field definitions.</summary>
    public IReadOnlyList<PackageSettingsField> Fields { get; }

    private static void EnsureDistinct(IEnumerable<string> values, string label, string parameterName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw new ArgumentException($"{label} '{value}' is declared more than once.", parameterName);
            }
        }
    }
}

/// <summary>Defines host-rendered settings owned by the currently activating package.</summary>
/// <remarks>Package identity and display name are host-stamped from activation metadata and are intentionally not author-controlled here.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.SettingsSchemaV1)]
public sealed record PackageSettingsSchema
{
    /// <summary>Creates and validates a complete package settings schema.</summary>
    public PackageSettingsSchema(string? summary, IReadOnlyList<PackageSettingsSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var copiedSections = Array.AsReadOnly(sections.ToArray());
        if (copiedSections.Count == 0)
        {
            throw new ArgumentException("A package settings schema must declare at least one section.", nameof(sections));
        }
        if (copiedSections.Any(static section => section is null))
        {
            throw new ArgumentException("A package settings schema contains a null section.", nameof(sections));
        }

        EnsureDistinct(copiedSections.Select(static section => section.SectionId), "Settings section id", nameof(sections));
        EnsureDistinct(
            copiedSections.SelectMany(static section => section.Fields).Select(static field => field.Key),
            "Settings field key",
            nameof(sections));
        Summary = summary;
        Sections = copiedSections;
    }

    /// <summary>Gets the optional settings-page summary.</summary>
    public string? Summary { get; }
    /// <summary>Gets immutable settings sections.</summary>
    public IReadOnlyList<PackageSettingsSection> Sections { get; }

    private static void EnsureDistinct(IEnumerable<string> values, string label, string parameterName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw new ArgumentException($"{label} '{value}' is declared more than once.", parameterName);
            }
        }
    }
}
