namespace Sunder.Runtime.Contracts;

public enum PackageSettingsFieldKind
{
    Text = 0,
    Secret = 1,
    Boolean = 2,
    Select = 3,
}

public sealed record PackageSettingsOptionDescriptor(string Value, string Label);

public sealed record PackageSettingsFieldDescriptor(
    string Key,
    string Label,
    PackageSettingsFieldKind Kind,
    string? Description,
    bool IsRequired,
    string? Placeholder,
    string? DefaultValue,
    IReadOnlyList<PackageSettingsOptionDescriptor> Options)
{
    public IReadOnlyList<PackageSettingsOptionDescriptor> Options { get; }
        = Array.AsReadOnly(Options.ToArray());
}

public sealed record PackageSettingsSectionDescriptor(
    string SectionId,
    string Title,
    string? Description,
    IReadOnlyList<PackageSettingsFieldDescriptor> Fields)
{
    public IReadOnlyList<PackageSettingsFieldDescriptor> Fields { get; }
        = Array.AsReadOnly(Fields.ToArray());
}

public sealed record PackageSettingsSchemaDescriptor(
    string PackageId,
    string PackageDisplayName,
    string? Summary,
    IReadOnlyList<PackageSettingsSectionDescriptor> Sections)
{
    public IReadOnlyList<PackageSettingsSectionDescriptor> Sections { get; }
        = Array.AsReadOnly(Sections.ToArray());
}
