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
        if (PathsEqual(leftPath, rightPath))
        {
            return true;
        }

        return FileMatchesDefinition(rightPath, ComputeDefinitionHash(leftPath));
    }

    public static bool PathsEqual(string leftPath, string rightPath)
        => string.Equals(Path.GetFullPath(leftPath), Path.GetFullPath(rightPath), PathComparison);

    public static byte[] ComputeDefinitionHash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    public static bool FileMatchesDefinition(string path, ReadOnlySpan<byte> definitionHash)
        => CryptographicOperations.FixedTimeEquals(ComputeDefinitionHash(path), definitionHash);

    private static Version NormalizeVersion(Version? version)
        => version is null
            ? new Version(0, 0, 0, 0)
            : new Version(
                Math.Max(version.Major, 0),
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0),
                Math.Max(version.Revision, 0));

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
