namespace Sunder.Package.Build.Authoring;

internal sealed record SunderPackageResourceInput(string Kind, string SourcePath, byte[] Contents,
    IReadOnlyDictionary<string, string> Metadata);

internal sealed record SunderPackageResourceEntry(string Kind, string SourcePath, string SnapshotPath, string Sha256,
    SunderPackageRoleMembership Roles, IReadOnlyDictionary<string, string> Metadata);

internal sealed record SunderPackageMarkupType(string MetadataName, string? AssemblyName, string Path, int Line, int Column)
{
    internal bool IsBindingDataType { get; init; }
}

internal sealed record SunderPackageMarkupPlan(IReadOnlyList<SunderPackageMarkupType> Types, IReadOnlyList<string> Assemblies);
