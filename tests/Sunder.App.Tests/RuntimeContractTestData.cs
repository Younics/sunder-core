using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.IO.Compression;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Tests;

internal static class RuntimeContractTestData
{
    private static readonly ConcurrentDictionary<string, byte[]> SnapshotContent = new(StringComparer.Ordinal);

    public static PackageUiSnapshotDescriptor Snapshot(
        string packageId,
        PackageSourceKind sourceKind,
        string revision)
    {
        var content = Directory.Exists(revision) ? CreateSnapshotArchive(revision) : Encoding.UTF8.GetBytes(revision);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var snapshotId = hash[..32];
        SnapshotContent[snapshotId] = content;
        return new PackageUiSnapshotDescriptor(
            packageId,
            sourceKind,
            1,
            hash,
            snapshotId,
            $"packages/ui-snapshots/{snapshotId}");
    }

    public static async Task DownloadSnapshotAsync(
        PackageUiSnapshotDescriptor snapshot,
        Stream destination,
        CancellationToken cancellationToken)
    {
        if (!SnapshotContent.TryGetValue(snapshot.SnapshotId, out var content))
        {
            throw new InvalidOperationException($"Test snapshot '{snapshot.SnapshotId}' is unavailable.");
        }

        await destination.WriteAsync(content, cancellationToken);
    }

    private static byte[] CreateSnapshotArchive(string folder)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(Path.GetRelativePath(folder, file).Replace('\\', '/'));
                using var input = File.OpenRead(file);
                using var destination = entry.Open();
                input.CopyTo(destination);
            }
        }

        return output.ToArray();
    }
}
