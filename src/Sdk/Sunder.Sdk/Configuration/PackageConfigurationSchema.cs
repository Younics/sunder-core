using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Configuration;

[SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
public enum PackageConfigurationFieldKind
{
    Text = 0,
    Secret = 1,
    Boolean = 2,
    Select = 3,
}

[SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
public sealed record PackageConfigurationOption(string Value, string Label);

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

[SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
public sealed record PackageConfigurationSection(
    string SectionId,
    string Title,
    string? Description,
    IReadOnlyList<PackageConfigurationField> Fields);

[SunderSdkCapability(SunderSdkCapabilities.ConfigurationSchemaV1)]
public sealed record PackageConfigurationSchema(
    string PackageId,
    string PackageDisplayName,
    string? Summary,
    IReadOnlyList<PackageConfigurationSection> Sections);
