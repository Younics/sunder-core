using System.Security.Cryptography;
using System.Text;
using Sunder.Host.Contracts;

namespace Sunder.Host.Supervisor;

internal static class HostDeploymentIdentityVerifier
{
    private const string PayloadMarkerFileName = ".sunder-host-payload.json";

    public static void Validate(string directory, string? expectedIdentity)
    {
        if (expectedIdentity is null)
        {
            return;
        }
        var actualIdentity = Compute(directory);
        if (!string.Equals(actualIdentity, expectedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The Sunder Host deployment content does not match identity '{expectedIdentity}'.");
        }
    }

    internal static string Compute(string directory)
        => HostDeploymentIdentity.FromSha256(ComputeContentSha256(Path.GetFullPath(directory)));

    private static string ComputeContentSha256(string directory)
    {
        EnsureSafeDirectory(directory);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var markerPath = Path.Combine(directory, PayloadMarkerFileName);
        var buffer = new byte[81920];
        foreach (var filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => !PathEquals(path, markerPath))
                     .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(directory, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            AppendHashText(hash, relativePath);
            AppendHashText(
                hash,
                OperatingSystem.IsWindows()
                    ? "windows"
                    : ((int)File.GetUnixFileMode(filePath)).ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
            AppendHashText(
                hash,
                new FileInfo(filePath).Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void EnsureSafeDirectory(string directory)
    {
        var root = new DirectoryInfo(directory);
        EnsureNotLink(root);
        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            EnsureNotLink(entry);
        }
    }

    private static void EnsureNotLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null
            || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"The Sunder Host deployment contains an unsupported link at '{entry.FullName}'.");
        }
    }

    private static void AppendHashText(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
