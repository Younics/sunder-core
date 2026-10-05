using System.Text.Json.Serialization;

namespace Sunder.Package.Build.Authoring;

internal sealed record SunderPackageFileEvidence(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record SunderPackageRoleReference(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sha256")] string Sha256);

internal sealed record SunderPackageDependencyFile(string Path, byte[] Contents);

internal sealed record SunderPackageDependencyPlan(
    string[] Libraries,
    string[] ExcludedProjects,
    string[] CompilePaths,
    string[] RuntimePaths,
    IReadOnlyList<SunderPackageFileEvidence> Inputs,
    IReadOnlyList<SunderPackageDependencyFile> Files);
