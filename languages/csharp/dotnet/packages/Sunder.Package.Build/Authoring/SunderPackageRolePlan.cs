using Sunder.Package.Build.Rpc.Authoring.CSharp;

namespace Sunder.Package.Build.Authoring;

[Flags]
internal enum SunderPackageRoleMembership
{
    None = 0,
    Worker = 1,
    App = 2,
    Shared = Worker | App,
}

internal sealed record SunderPackageRoleType(
    string MetadataName,
    SunderPackageRoleMembership Roles,
    IReadOnlyList<string> SourcePaths,
    bool IsPortableValue,
    bool HasExplicitRole);

internal sealed record SunderPackageRoleView(
    string MetadataName,
    string? ViewModelMetadataName,
    bool IsSettings,
    string? ViewId,
    string? DisplayName,
    string? IconPath,
    int DefaultPlacement);

internal sealed record SunderPackageRolePlan(
    IReadOnlyList<SunderPackageRoleType> Types,
    IReadOnlyList<SunderPackageRoleView> Views,
    IReadOnlyList<SunderRpcCSharpCompilerDiagnostic> Diagnostics)
{
    internal bool Success => Diagnostics.Count == 0;
}
