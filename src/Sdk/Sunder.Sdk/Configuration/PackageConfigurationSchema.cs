namespace Sunder.Sdk.Configuration;

public enum PackageConfigurationFieldKind
{
    Text = 0,
    Secret = 1,
    Boolean = 2,
    Select = 3,
}

public sealed record PackageConfigurationOption(string Value, string Label);

public sealed record PackageConfigurationField(
    string Key,
    string Label,
    PackageConfigurationFieldKind Kind,
    string? Description = null,
    bool IsRequired = false,
    string? Placeholder = null,
    string? DefaultValue = null,
    IReadOnlyList<PackageConfigurationOption>? Options = null);

public sealed record PackageConfigurationSection(
    string SectionId,
    string Title,
    string? Description,
    IReadOnlyList<PackageConfigurationField> Fields);

public sealed record PackageConfigurationSchema(
    string PackageId,
    string PackageDisplayName,
    string? Summary,
    IReadOnlyList<PackageConfigurationSection> Sections);
