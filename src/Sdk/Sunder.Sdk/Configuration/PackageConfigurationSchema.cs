using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Configuration;

/// <summary>Specifies how the host renders and stores a configuration value.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
public enum PackageConfigurationFieldKind
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
/// <param name="Value">Exact value supplied to package configuration.</param>
/// <param name="Label">User-facing option label.</param>
[SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
public sealed record PackageConfigurationOption(string Value, string Label);

/// <summary>Defines one host-rendered package configuration field.</summary>
/// <param name="Key">Case-sensitive key used by <see cref="Abstractions.IPackageSettings"/>.</param>
/// <param name="Label">User-facing field label.</param>
/// <param name="Kind">Rendering and storage behavior.</param>
/// <param name="Description">Optional explanatory text; <see langword="null"/> omits it.</param>
/// <param name="IsRequired">Whether the host rejects an empty value; defaults to optional.</param>
/// <param name="Placeholder">Optional non-persisted input hint.</param>
/// <param name="DefaultValue">Optional effective value returned when no setting has been persisted.</param>
/// <param name="Options">Options for <see cref="PackageConfigurationFieldKind.Select"/>; otherwise normally <see langword="null"/>.</param>
[SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
public sealed record PackageConfigurationField(
    string Key,
    string Label,
    PackageConfigurationFieldKind Kind,
    string? Description = null,
    bool IsRequired = false,
    string? Placeholder = null,
    string? DefaultValue = null,
    IReadOnlyList<PackageConfigurationOption>? Options = null);

/// <summary>Groups related fields in host-rendered package settings.</summary>
/// <param name="SectionId">Stable package-scoped section identifier.</param>
/// <param name="Title">User-facing section title.</param>
/// <param name="Description">Optional section explanation.</param>
/// <param name="Fields">Ordered immutable field definitions.</param>
[SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
public sealed record PackageConfigurationSection(
    string SectionId,
    string Title,
    string? Description,
    IReadOnlyList<PackageConfigurationField> Fields);

/// <summary>Defines the complete host-rendered settings schema owned by one package.</summary>
/// <param name="PackageId">Package identifier that must match the contributing package.</param>
/// <param name="PackageDisplayName">User-facing package name.</param>
/// <param name="Summary">Optional settings-page summary.</param>
/// <param name="Sections">Ordered immutable sections.</param>
[SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
public sealed record PackageConfigurationSchema(
    string PackageId,
    string PackageDisplayName,
    string? Summary,
    IReadOnlyList<PackageConfigurationSection> Sections);
