using System.Reflection;
using System.Security.Cryptography;

namespace Sunder.Package.Hosting;

internal static class SharedContractAssemblyPolicy
{
    public static bool IsSharedContract(string? assemblyName)
        => assemblyName?.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsReferenceSatisfiedBy(AssemblyName requested, AssemblyName loaded)
        => FamiliesMatch(requested, loaded)
           && NormalizeVersion(requested.Version).Major == NormalizeVersion(loaded.Version).Major
           && CompareVersions(loaded.Version, requested.Version) >= 0;

    public static bool FamiliesMatch(AssemblyName left, AssemblyName right)
        => string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
           && string.Equals(left.CultureName ?? string.Empty, right.CultureName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
           && (left.GetPublicKeyToken() ?? []).SequenceEqual(right.GetPublicKeyToken() ?? []);

    public static bool IdentitiesMatch(AssemblyName left, AssemblyName right)
        => FamiliesMatch(left, right) && CompareVersions(left.Version, right.Version) == 0;

    public static int CompareVersions(Version? left, Version? right)
        => NormalizeVersion(left).CompareTo(NormalizeVersion(right));

    public static bool FilesRepresentSameDefinition(string leftPath, string rightPath)
    {
        if (string.Equals(Path.GetFullPath(leftPath), Path.GetFullPath(rightPath), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        using var left = File.OpenRead(leftPath);
        using var right = File.OpenRead(rightPath);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(left), SHA256.HashData(right));
    }

    private static Version NormalizeVersion(Version? version)
        => version is null
            ? new Version(0, 0, 0, 0)
            : new Version(
                Math.Max(version.Major, 0),
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0),
                Math.Max(version.Revision, 0));
}
