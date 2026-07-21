using System.Text;
using System.Text.RegularExpressions;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageLogStreamService : IAsyncDisposable
{
    internal const int ReplayCapacity = 2000;
    internal const int SubscriberCapacity = 128;
    internal const int MaxLineBytes = 16 * 1024;
    internal const int MaxMessageCharacters = 4096;
    internal const int MaxCategoryCharacters = 256;
    internal const int MaxInitialReplayFiles = 128;
    internal const int MaxInitialReplayBytes = 8 * 1024 * 1024;
    private const int MaxInitialFileBytes = 2 * 1024 * 1024;
    private const int MaxReadBytesPerChange = 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _packageRootPath;
    private readonly Guid _runtimeInstanceId;
    private readonly BoundedReplayFeed<PackageLogEntryDescriptor> _feed = new(ReplayCapacity, SubscriberCapacity);
    private readonly Dictionary<string, FileTailState> _files = new(PathComparer);
    private FileSystemWatcher? _watcher;
    private bool _starting;
    private bool _disposed;

    public PackageLogStreamService()
        : this(new RuntimePackagePaths().PackageDataRootPath, Guid.NewGuid())
    {
    }

    internal PackageLogStreamService(string packageRootPath, Guid? runtimeInstanceId = null)
    {
        _packageRootPath = Path.GetFullPath(packageRootPath);
        _runtimeInstanceId = runtimeInstanceId is { } value && value != Guid.Empty
            ? value
            : Guid.NewGuid();
    }

    public void Start(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is not null || _starting)
            {
                return;
            }
            _starting = true;
        }

        try
        {
            Directory.CreateDirectory(_packageRootPath);
            foreach (var file in SelectInitialReplayFiles(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    ReadFile(file.Path, initialRead: true, file.MaxBytes, cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _watcher = new FileSystemWatcher(_packageRootPath, "*.log")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                _watcher.Created += OnChanged;
                _watcher.Changed += OnChanged;
                _watcher.Renamed += OnRenamed;
                _watcher.Deleted += OnDeleted;
                _watcher.Error += OnError;
                _watcher.EnableRaisingEvents = true;
            }
        }
        finally
        {
            lock (_gate)
            {
                _starting = false;
            }
        }
    }

    public PackageLogSnapshot GetSnapshot(long afterSequenceId = 0, int limit = 500)
    {
        var replay = _feed.Snapshot(afterSequenceId, Math.Clamp(limit, 1, 1000));
        return new PackageLogSnapshot(replay.SequenceId, replay.Items, replay.HistoryGap)
        {
            RuntimeInstanceId = _runtimeInstanceId,
        };
    }

    public BoundedReplayFeed<PackageLogEntryDescriptor>.ReplayFeedSubscription<PackageLogEntryDescriptor> Subscribe(long afterSequenceId)
        => _feed.Subscribe(afterSequenceId);

    internal void ProcessLineForTest(string packageId, string line)
        => PublishLine(packageId, Encoding.UTF8.GetBytes(line), oversized: false);

    internal void ProcessFileForTest(string filePath)
    {
        lock (_gate)
        {
            ReadFile(filePath, initialRead: false);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnChanged;
                _watcher.Changed -= OnChanged;
                _watcher.Renamed -= OnRenamed;
                _watcher.Deleted -= OnDeleted;
                _watcher.Error -= OnError;
                _watcher.Dispose();
                _watcher = null;
            }

            _files.Clear();
            _feed.Complete();
        }

        return ValueTask.CompletedTask;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                ReadFile(e.FullPath, initialRead: false);
            }
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        lock (_gate)
        {
            _files.Remove(e.OldFullPath);
            if (!_disposed)
            {
                ReadFile(e.FullPath, initialRead: false);
            }
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _files.Remove(e.FullPath);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (!Directory.Exists(_packageRootPath))
        {
            return;
        }

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(_packageRootPath, "*.log", SearchOption.AllDirectories))
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    ReadFile(filePath, initialRead: false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private IReadOnlyList<InitialReplayFile> SelectInitialReplayFiles(CancellationToken cancellationToken)
    {
        var newest = new PriorityQueue<InitialReplayCandidate, DateTime>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_packageRootPath, "*.log", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var file = new FileInfo(path);
                    var candidate = new InitialReplayCandidate(file.FullName, file.LastWriteTimeUtc, file.Length);
                    if (newest.Count < MaxInitialReplayFiles)
                    {
                        newest.Enqueue(candidate, candidate.LastWriteTimeUtc);
                    }
                    else if (newest.TryPeek(out var oldest, out var oldestTimestamp)
                             && candidate.LastWriteTimeUtc > oldestTimestamp)
                    {
                        newest.Dequeue();
                        TrackSkippedFile(oldest);
                        newest.Enqueue(candidate, candidate.LastWriteTimeUtc);
                    }
                    else
                    {
                        TrackSkippedFile(candidate);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        var selected = new List<InitialReplayFile>();
        var remainingBytes = (long)MaxInitialReplayBytes;
        foreach (var file in newest.UnorderedItems
                     .Select(static item => item.Element)
                     .OrderByDescending(static file => file.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingBytes <= 0)
            {
                TrackSkippedFile(file);
                continue;
            }

            var maxBytes = Math.Min(Math.Min(file.Length, MaxInitialFileBytes), remainingBytes);
            selected.Add(new InitialReplayFile(file.Path, maxBytes));
            remainingBytes -= maxBytes;
        }

        selected.Reverse();
        return selected;
    }

    private void TrackSkippedFile(InitialReplayCandidate file)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _files[file.Path] = new FileTailState { Offset = file.Length };
        }
    }

    private long ReadFile(
        string filePath,
        bool initialRead,
        long maxReadBytes = MaxReadBytesPerChange,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsLogPath(filePath))
        {
            return 0;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!_files.TryGetValue(filePath, out var state) || state.Offset > stream.Length)
            {
                state = new FileTailState();
                _files[filePath] = state;
            }

            var readLimit = initialRead
                ? Math.Min(MaxInitialFileBytes, maxReadBytes)
                : Math.Min(MaxReadBytesPerChange, maxReadBytes);
            if (readLimit <= 0)
            {
                return 0;
            }

            if (initialRead && state.Offset == 0 && stream.Length > readLimit)
            {
                state.Offset = stream.Length - readLimit;
                state.DiscardUntilNewline = true;
            }

            stream.Position = state.Offset;
            var initialOffset = state.Offset;
            var remaining = Math.Min(stream.Length - state.Offset, readLimit);
            var buffer = new byte[8192];
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    break;
                }

                ConsumeBytes(ResolvePackageId(filePath), state, buffer.AsSpan(0, read));
                state.Offset += read;
                remaining -= read;
            }
            return state.Offset - initialOffset;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return 0;
    }

    private void ConsumeBytes(string packageId, FileTailState state, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (state.DiscardUntilNewline)
            {
                if (value == (byte)'\n')
                {
                    state.DiscardUntilNewline = false;
                }

                continue;
            }

            if (value == (byte)'\n')
            {
                PublishLine(packageId, state.Line.ToArray(), state.Oversized);
                state.Line.Clear();
                state.Oversized = false;
                continue;
            }

            if (value == (byte)'\r')
            {
                continue;
            }

            if (state.Line.Count < MaxLineBytes)
            {
                state.Line.Add(value);
            }
            else
            {
                state.Oversized = true;
            }
        }
    }

    private void PublishLine(string packageId, ReadOnlySpan<byte> bytes, bool oversized)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var line = Encoding.UTF8.GetString(bytes);
        var level = ParseLevel(ReadField(line, "level"));
        var category = SanitizeText(ReadField(line, "category") ?? "package", MaxCategoryCharacters, out var categoryTruncated);
        var rawMessage = ReadField(line, "msg") ?? line;
        var message = SanitizeText(rawMessage, MaxMessageCharacters, out var messageTruncated);
        var timestamp = ParseTimestamp(line);
        _feed.Publish(sequenceId => new PackageLogEntryDescriptor(
            sequenceId,
            NormalizePackageId(packageId),
            timestamp,
            level,
            category,
            message,
            oversized || categoryTruncated || messageTruncated)
        {
            RuntimeInstanceId = _runtimeInstanceId,
        });
    }

    private bool IsLogPath(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".log", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(filePath);
        var rootPrefix = _packageRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootPrefix, PathComparison);
    }

    private string ResolvePackageId(string filePath)
    {
        var relativePath = Path.GetRelativePath(_packageRootPath, filePath);
        var segment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
        return NormalizePackageId(segment);
    }

    private static string NormalizePackageId(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return "package";
        }

        var normalized = new string(packageId.Trim()
            .Where(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_')
            .Take(128)
            .ToArray());
        return normalized.Length == 0 ? "package" : normalized;
    }

    private string SanitizeText(string value, int maxCharacters, out bool truncated)
    {
        var text = value.Replace("\\r", " ", StringComparison.Ordinal)
            .Replace("\\n", " ", StringComparison.Ordinal)
            .Replace("\\t", " ", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace(_packageRootPath, "[PATH]", PathComparison);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            text = text.Replace(home, "[PATH]", PathComparison);
        }

        text = SensitiveAssignmentRegex().Replace(text, match => $"{match.Groups[1].Value}=[REDACTED]");
        text = WindowsPathRegex().Replace(text, "[PATH]");
        text = UnixPathRegex().Replace(text, "[PATH]");
        text = new string(text.Where(character => !char.IsControl(character)).ToArray()).Trim();
        truncated = text.Length > maxCharacters;
        return truncated ? text[..maxCharacters] : text;
    }

    private static RuntimePackageLogLevel ParseLevel(string? value)
        => value?.ToLowerInvariant() switch
        {
            "trace" => RuntimePackageLogLevel.Trace,
            "debug" => RuntimePackageLogLevel.Debug,
            "warning" or "warn" => RuntimePackageLogLevel.Warning,
            "error" or "fail" => RuntimePackageLogLevel.Error,
            "critical" or "crit" => RuntimePackageLogLevel.Critical,
            _ => RuntimePackageLogLevel.Information,
        };

    private static DateTimeOffset ParseTimestamp(string line)
    {
        if (line.Length >= 15
            && DateTime.TryParseExact(
                $"{DateTimeOffset.Now.Year} {line[..15]}",
                "yyyy MMM dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal,
                out var timestamp))
        {
            return new DateTimeOffset(timestamp);
        }

        return DateTimeOffset.UtcNow;
    }

    private static string? ReadField(string line, string name)
    {
        var marker = name + "=";
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        index += marker.Length;
        if (index >= line.Length)
        {
            return string.Empty;
        }

        if (line[index] != '"')
        {
            var end = line.IndexOf(' ', index);
            return end < 0 ? line[index..] : line[index..end];
        }

        var builder = new StringBuilder();
        for (index++; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                break;
            }

            if (character == '\\' && index + 1 < line.Length)
            {
                builder.Append(line[++index] switch
                {
                    'r' => ' ',
                    'n' => ' ',
                    't' => ' ',
                    var escaped => escaped,
                });
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex("""(?i)\b([a-z0-9_.-]*(?:token|secret|password|authorization|cookie|api[_-]?key)[a-z0-9_.-]*)\s*=\s*(?:"(?:\\.|[^"])*"|\S+)""", RegexOptions.NonBacktracking)]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex("""\b[A-Za-z]:\\[^\s"]+""", RegexOptions.NonBacktracking)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("""/(?:[^\s"/]+/)*[^\s"]+""", RegexOptions.NonBacktracking)]
    private static partial Regex UnixPathRegex();

    private sealed class FileTailState
    {
        public long Offset { get; set; }

        public bool DiscardUntilNewline { get; set; }

        public bool Oversized { get; set; }

        public List<byte> Line { get; } = new(MaxLineBytes);
    }

    private sealed record InitialReplayFile(string Path, long MaxBytes);

    private sealed record InitialReplayCandidate(string Path, DateTime LastWriteTimeUtc, long Length);
}
