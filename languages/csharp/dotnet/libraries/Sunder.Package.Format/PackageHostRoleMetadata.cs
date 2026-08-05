namespace Sunder.Package.Format;

[Flags]
internal enum PackageHostRoleMetadataValue
{
    None = 0,
    App = 1,
    Runtime = 2,
}

internal static class PackageHostRoleMetadata
{
    public static IReadOnlyList<string> ToImplementedRoles(PackageHostRoleMetadataValue roles)
    {
        var values = new List<string>(2);
        if ((roles & PackageHostRoleMetadataValue.App) != 0) values.Add(SunderPackageFormat.AppHostRole);
        if ((roles & PackageHostRoleMetadataValue.Runtime) != 0) values.Add(SunderPackageFormat.RuntimeHostRole);
        return values;
    }
}
