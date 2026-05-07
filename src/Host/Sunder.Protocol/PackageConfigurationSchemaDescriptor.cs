namespace Sunder.Protocol;

public sealed record PackageConfigurationSchemaDescriptor(
    string PackageId,
    string PackageDisplayName,
    string? Summary,
    IReadOnlyList<PackageConfigurationSectionDescriptor> Sections);
