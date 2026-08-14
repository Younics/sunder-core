namespace Sunder.Package.Format;

[Flags]
internal enum PackageHostRoleMetadataValue
{
    None = 0,
    App = 1,
}

internal static class PackageHostRoleMetadata
{
    public static IReadOnlyList<string> ToImplementedRoles(PackageHostRoleMetadataValue roles)
    {
        var values = new List<string>(1);
        if ((roles & PackageHostRoleMetadataValue.App) != 0) values.Add(SunderPackageFormat.AppHostRole);
        return values;
    }
}
