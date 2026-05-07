namespace Sunder.Protocol;

public sealed record PackageConfigurationSectionDescriptor(
    string SectionId,
    string Title,
    string? Description,
    IReadOnlyList<PackageConfigurationFieldDescriptor> Fields);
