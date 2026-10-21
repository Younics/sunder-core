namespace Sunder.Package.Build.Tasks;

// A synchronous analysis pass owns bytes, never path/mtime-based authority.
// Complete rereads every retained file before the caller may publish anything.
// No entries survive the operation or come from a persistent cache.
internal sealed class SunderBuildReadSnapshot : IDisposable
{
    [ThreadStatic] private static SunderBuildReadSnapshot? _current;
    private readonly SunderBuildReadSnapshot? _parent;
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<string, byte[]> _files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private const long MaximumBytes = 128L * 1024 * 1024;
    private long _bytes;
    private bool _completed;
    private bool _reading;

    // Only captured file reads share ancestry work. Each verification pass starts
    // empty, and every observed directory is checked again at the end of that pass.
    internal static bool HasDirectory(string path) => _current is { _reading: true } snapshot && snapshot._directories.Contains(path);
    internal static void RecordDirectory(string path)
    {
        if (_current is { _reading: true } snapshot && snapshot._directories.Count < 65536) snapshot._directories.Add(path);
    }

    internal SunderBuildReadSnapshot(CancellationToken cancellationToken = default)
    {
        _cancellation = cancellationToken;
        _parent = _current;
        _current = this;
    }

    internal static byte[] Read(string path, int maximumBytes, string label)
    {
        if (_current is not { } snapshot) return SunderRpcDescriptorFileReader.ReadCurrentBytes(path, maximumBytes, label);
        snapshot._cancellation.ThrowIfCancellationRequested();
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        path = Path.GetFullPath(path);
        if (snapshot._files.TryGetValue(path, out var captured))
        {
            if (captured.Length > maximumBytes) throw new InvalidDataException($"The {label} '{path}' exceeds the {maximumBytes}-byte limit.");
            return captured.ToArray();
        }
        var bytes = snapshot.ReadCurrent(path, maximumBytes, label);
        if (snapshot._files.Count < 4096 && snapshot._bytes + bytes.Length <= MaximumBytes)
        {
            snapshot._files.Add(path, bytes.ToArray());
            snapshot._bytes += bytes.Length;
        }
        else
        {
            // Unretained bytes cannot borrow the pass's directory proofs.
            bytes = SunderRpcDescriptorFileReader.ReadCurrentBytes(path, maximumBytes, label);
        }
        return bytes;
    }

    private byte[] ReadCurrent(string path, int maximumBytes, string label)
    {
        _reading = true;
        try { return SunderRpcDescriptorFileReader.ReadCurrentBytes(path, maximumBytes, label); }
        finally { _reading = false; }
    }

    internal void Complete(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        _completed = true;
        if (!cancellationToken.CanBeCanceled) cancellationToken = _cancellation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDirectories(cancellationToken);
            _directories.Clear();
            foreach (var (path, expected) in _files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadCurrent(path, expected.Length, "captured build input").AsSpan().SequenceEqual(expected))
                    throw new IOException($"The captured build input '{path}' changed during analysis. No output may be published.");
            }
            ValidateDirectories(cancellationToken);
        }
        finally { _current = _parent; _files.Clear(); _directories.Clear(); }
    }

    private void ValidateDirectories(CancellationToken cancellationToken)
    {
        foreach (var directory in _directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(directory);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException($"The captured build input ancestor '{directory}' is no longer a regular non-reparse directory.");
        }
    }

    public void Dispose() => Complete();
}
