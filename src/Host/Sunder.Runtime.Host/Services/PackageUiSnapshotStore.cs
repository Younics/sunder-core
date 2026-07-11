using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record PackageUiSnapshotLease(string FilePath, string ContentHash, long Length);

internal sealed class PackageUiSnapshotStore(RuntimePackagePaths paths) : IDisposable
{
    internal const long MaxUncompressedBytes = 256L * 1024 * 1024;
    internal const int MaxFileCount = 20_000;
    private readonly ConcurrentDictionary<string, SnapshotEntry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyList<PackageUiSnapshotDescriptor> CreateSnapshots(
        IReadOnlyList<RuntimePackageSource> sources,
        long generation,
        string? stageId = null)
        => sources.Select(source => CreateSnapshot(source, generation, stageId)).ToArray();

    public PackageUiSnapshotLease? Acquire(string snapshotId, long generation, string? stageId)
    {
        if (!_entries.TryGetValue(snapshotId, out var entry)
            || entry.Generation != generation
            || !string.Equals(entry.StageId, stageId, StringComparison.Ordinal)
            || !File.Exists(entry.FilePath))
        {
            Remove(snapshotId);
            return null;
        }

        return new PackageUiSnapshotLease(entry.FilePath, entry.ContentHash, entry.Length);
    }

    public void RemoveStage(string stageId)
    {
        foreach (var (id, entry) in _entries)
        {
            if (string.Equals(entry.StageId, stageId, StringComparison.Ordinal))
            {
                Remove(id);
            }
        }
    }

    public void RemoveOlderGenerations(long generation)
    {
        foreach (var (id, entry) in _entries)
        {
            if (entry.StageId is null && entry.Generation != generation)
            {
                Remove(id);
            }
        }
    }

    public void Dispose()
    {
        foreach (var id in _entries.Keys)
        {
            Remove(id);
        }
    }

    private PackageUiSnapshotDescriptor CreateSnapshot(RuntimePackageSource source, long generation, string? stageId)
    {
        foreach (var (existingId, existing) in _entries)
        {
            if (existing.Generation == generation
                && string.Equals(existing.StageId, stageId, StringComparison.Ordinal)
                && string.Equals(existing.PackageId, source.PackageId, StringComparison.OrdinalIgnoreCase)
                && existing.SourceKind == source.Kind
                && File.Exists(existing.FilePath))
            {
                var existingUri = stageId is null
                    ? $"packages/ui-snapshots/{existingId}"
                    : $"packages/session/stage/{stageId}/ui-snapshots/{existingId}";
                return new PackageUiSnapshotDescriptor(source.PackageId, source.Kind, generation, existing.ContentHash, existingId, existingUri);
            }
        }

        var files = Directory.EnumerateFiles(source.EffectiveSnapshotFolder, "*", SearchOption.AllDirectories)
            .Select(file => (File: file, Relative: Path.GetRelativePath(source.EffectiveSnapshotFolder, file).Replace('\\', '/')))
            .Where(item => item.Relative == "sunder-package.json"
                           || item.Relative.StartsWith("lib/", StringComparison.Ordinal)
                           || item.Relative.StartsWith("assets/", StringComparison.Ordinal))
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0 || files.Length > MaxFileCount)
        {
            throw new InvalidDataException($"Package UI snapshot contains an invalid number of files ({files.Length}).");
        }

        var totalLength = files.Sum(item => new FileInfo(item.File).Length);
        if (totalLength > MaxUncompressedBytes)
        {
            throw new InvalidDataException($"Package UI snapshot exceeds the {MaxUncompressedBytes} byte limit.");
        }

        Directory.CreateDirectory(paths.TransferRootPath);
        var snapshotId = Guid.NewGuid().ToString("N");
        var snapshotPath = Path.Combine(paths.TransferRootPath, snapshotId + ".snapshot");
        try
        {
            using (var output = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var item in files)
                {
                    var entry = archive.CreateEntry(item.Relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var input = new FileStream(item.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var destination = entry.Open();
                    input.CopyTo(destination);
                }
            }

            var info = new FileInfo(snapshotPath);
            if (info.Length > MaxUncompressedBytes)
            {
                throw new InvalidDataException($"Package UI snapshot exceeds the {MaxUncompressedBytes} byte archive limit.");
            }

            using var snapshotStream = File.OpenRead(snapshotPath);
            var hash = Convert.ToHexString(SHA256.HashData(snapshotStream)).ToLowerInvariant();
            _entries[snapshotId] = new SnapshotEntry(snapshotPath, hash, info.Length, generation, stageId, source.PackageId, source.Kind);
            var uri = stageId is null
                ? $"packages/ui-snapshots/{snapshotId}"
                : $"packages/session/stage/{stageId}/ui-snapshots/{snapshotId}";
            return new PackageUiSnapshotDescriptor(source.PackageId, source.Kind, generation, hash, snapshotId, uri);
        }
        catch
        {
            File.Delete(snapshotPath);
            throw;
        }
    }

    private void Remove(string id)
    {
        if (_entries.TryRemove(id, out var entry))
        {
            try
            {
                File.Delete(entry.FilePath);
            }
            catch
            {
            }
        }
    }

    private sealed record SnapshotEntry(
        string FilePath,
        string ContentHash,
        long Length,
        long Generation,
        string? StageId,
        string PackageId,
        PackageSourceKind SourceKind);
}
