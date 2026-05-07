namespace Sunder.Protocol;

public sealed record PackageConfigurationFieldDescriptor(
    string Key,
    string Label,
    PackageConfigurationFieldKind Kind,
    string? Description,
    bool IsRequired,
    string? Placeholder,
    string? DefaultValue,
    IReadOnlyList<PackageConfigurationOptionDescriptor> Options);
