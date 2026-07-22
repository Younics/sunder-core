namespace Sunder.Package.Format;

[Flags]
internal enum PackageHostRoleMetadataValue
{
    ContractOnly = 0,
    App = 1,
    Runtime = 2,
}

internal static class PackageHostRoleMetadata
{
    public static IReadOnlyList<string> ToManifestRoles(PackageHostRoleMetadataValue roles)
    {
        if (roles == PackageHostRoleMetadataValue.ContractOnly)
        {
            return [SunderPackageFormat.ContractOnlyHostRole];
        }

        var values = new List<string>(2);
        if ((roles & PackageHostRoleMetadataValue.App) != 0) values.Add(SunderPackageFormat.AppHostRole);
        if ((roles & PackageHostRoleMetadataValue.Runtime) != 0) values.Add(SunderPackageFormat.RuntimeHostRole);
        return values;
    }

    public static bool TryParseManifestRoles(
        IReadOnlyList<string>? values,
        out PackageHostRoleMetadataValue roles,
        out string? error)
    {
        roles = PackageHostRoleMetadataValue.ContractOnly;
        error = null;
        if (values is null || values.Count == 0)
        {
            error = "must declare hostRoles";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null || !seen.Add(value))
            {
                error = "must declare distinct hostRoles";
                return false;
            }
            roles |= value switch
            {
                SunderPackageFormat.AppHostRole => PackageHostRoleMetadataValue.App,
                SunderPackageFormat.RuntimeHostRole => PackageHostRoleMetadataValue.Runtime,
                SunderPackageFormat.ContractOnlyHostRole => PackageHostRoleMetadataValue.ContractOnly,
                _ => (PackageHostRoleMetadataValue)(-1),
            };
            if (roles == (PackageHostRoleMetadataValue)(-1))
            {
                error = $"declares unknown host role '{value}'";
                return false;
            }
        }

        var contractOnly = seen.Contains(SunderPackageFormat.ContractOnlyHostRole);
        if (contractOnly && seen.Count != 1)
        {
            error = "cannot combine contract-only with app or runtime host roles";
            return false;
        }
        if (seen.Count == 2
            && (values[0] != SunderPackageFormat.AppHostRole || values[1] != SunderPackageFormat.RuntimeHostRole))
        {
            error = "must declare app before runtime in hostRoles";
            return false;
        }
        return true;
    }

}
