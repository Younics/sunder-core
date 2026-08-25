using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Sunder.Core.TestSupport;

public sealed class PackageArchiveFixtureBuilder
{
    private static readonly DateTimeOffset EntryTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

    public PackageArchiveFixtureBuilder AddBytes(string path, ReadOnlySpan<byte> content)
    {
        _entries.Add(NormalizeArchivePath(path), content.ToArray());
        return this;
    }

    public PackageArchiveFixtureBuilder AddText(string path, string content)
        => AddBytes(path, Encoding.UTF8.GetBytes(content));

    public PackageArchiveFixtureBuilder AddJson<T>(string path, T value, JsonSerializerOptions? options = null)
        => AddBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, options));

    public PackageArchiveFixtureBuilder Remove(string path)
    {
        _entries.Remove(NormalizeArchivePath(path));
        return this;
    }

    public PackageArchiveFixtureBuilder Mutate(string path, Func<byte[], byte[]> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var normalized = NormalizeArchivePath(path);
        if (!_entries.TryGetValue(normalized, out var content))
        {
            throw new KeyNotFoundException($"Archive fixture entry '{normalized}' does not exist.");
        }
        _entries[normalized] = mutation(content.ToArray());
        return this;
    }

    public string WriteArchive(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var (entryPath, content) in _entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
            entry.LastWriteTime = EntryTimestamp;
            using var output = entry.Open();
            output.Write(content);
        }
        return fullPath;
    }

    public string MaterializeDirectory(string path)
    {
        var root = Path.GetFullPath(path);
        Directory.CreateDirectory(root);
        foreach (var (entryPath, content) in _entries)
        {
            var output = Path.Combine(root, entryPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllBytes(output, content);
        }
        return root;
    }

    public IReadOnlyDictionary<string, byte[]> Snapshot()
        => _entries.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal);

    private static string NormalizeArchivePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Archive fixture paths must be canonical contained paths.", nameof(path));
        }
        return normalized;
    }
}
