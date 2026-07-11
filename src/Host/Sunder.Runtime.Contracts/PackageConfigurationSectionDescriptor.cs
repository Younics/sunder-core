namespace Sunder.Runtime.Contracts;

public sealed record PackageConfigurationSectionDescriptor(
    string SectionId,
    string Title,
    string? Description,
    IReadOnlyList<PackageConfigurationFieldDescriptor> Fields);
