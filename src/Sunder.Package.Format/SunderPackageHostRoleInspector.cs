namespace Sunder.Package.Format;

public static class SunderPackageHostRoleInspector
{
    public static IReadOnlyList<string> ReadManifestRoles(string assemblyPath)
        => PackageHostRoleMetadata.ReadManifestRoles(assemblyPath);
}
