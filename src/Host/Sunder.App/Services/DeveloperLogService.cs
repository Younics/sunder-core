using Sunder.Sdk.Logging;

namespace Sunder.App.Services;

public sealed record DeveloperLogEntry(
    DateTimeOffset Timestamp,
    PackageLogLevel Level,
    string Source,
    string Message);

public sealed class DeveloperLogService : IDisposable
{
    private const int MaxEntries = 2000;
    private static readonly string PackageLogsRootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunder",
        "Packages");

    private readonly object _syncRoot = new();
    private readonly List<DeveloperLogEntry> _entries = [];
    private readonly Dictionary<string, long> _logFileOffsets = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _packageLogWatcher;
    private bool _disposed;

    public event Action? EntriesChanged;

    public bool IsEnabled { get; private set; }

    public IReadOnlyList<DeveloperLogEntry> Snapshot()
    {
        lock (_syncRoot)
        {
            return _entries.ToArray();
        }
    }

    public void Enable()
    {
        IsEnabled = true;
        StartPackageLogWatcher();
    }

    public void Write(PackageLogLevel level, string source, string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        var entry = new DeveloperLogEntry(DateTimeOffset.Now, level, Normalize(source, "developer"), Normalize(message, string.Empty));
        lock (_syncRoot)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }
        }

        if (level >= PackageLogLevel.Error)
        {
            AppSessionLog.WriteError($"[{entry.Source}] {entry.Message}");
        }
        else
        {
            AppSessionLog.WriteInfo($"[{entry.Source}] {entry.Message}");
        }

        EntriesChanged?.Invoke();
    }

    public void Info(string source, string message)
        => Write(PackageLogLevel.Information, source, message);

    public void Warning(string source, string message)
        => Write(PackageLogLevel.Warning, source, message);

    public void Error(string source, string message)
        => Write(PackageLogLevel.Error, source, message);

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private void StartPackageLogWatcher()
    {
        lock (_syncRoot)
        {
            if (_disposed || _packageLogWatcher is not null)
            {
                return;
            }

            Directory.CreateDirectory(PackageLogsRootPath);
            foreach (var filePath in Directory.EnumerateFiles(PackageLogsRootPath, "*.log", SearchOption.AllDirectories))
            {
                TryInitializeOffset(filePath);
            }

            _packageLogWatcher = new FileSystemWatcher(PackageLogsRootPath, "*.log")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _packageLogWatcher.Created += PackageLogWatcher_OnChanged;
            _packageLogWatcher.Changed += PackageLogWatcher_OnChanged;
            _packageLogWatcher.Renamed += PackageLogWatcher_OnRenamed;
        }
    }

    private void PackageLogWatcher_OnChanged(object sender, FileSystemEventArgs e)
        => TryReadNewLogLines(e.FullPath);

    private void PackageLogWatcher_OnRenamed(object sender, RenamedEventArgs e)
        => TryReadNewLogLines(e.FullPath);

    private void TryInitializeOffset(string filePath)
    {
        try
        {
            _logFileOffsets[filePath] = new FileInfo(filePath).Length;
        }
        catch
        {
            _logFileOffsets[filePath] = 0;
        }
    }

    private void TryReadNewLogLines(string filePath)
    {
        if (!IsEnabled || _disposed)
        {
            return;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long offset;
            lock (_syncRoot)
            {
                offset = _logFileOffsets.TryGetValue(filePath, out var existingOffset) ? existingOffset : 0;
                if (offset > stream.Length)
                {
                    offset = 0;
                }

                _logFileOffsets[filePath] = stream.Length;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Write(ParseLevel(line), ResolveLogSource(filePath), line);
                }
            }
        }
        catch
        {
            // Package log tailing must never interrupt package execution or shell UI.
        }
    }

    private static string ResolveLogSource(string filePath)
    {
        var relativePath = Path.GetRelativePath(PackageLogsRootPath, filePath);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Length > 0 && !string.IsNullOrWhiteSpace(segments[0])
            ? segments[0]
            : "package.log";
    }

    private static PackageLogLevel ParseLevel(string line)
    {
        if (line.Contains(" critical", StringComparison.OrdinalIgnoreCase))
        {
            return PackageLogLevel.Critical;
        }

        if (line.Contains(" error", StringComparison.OrdinalIgnoreCase))
        {
            return PackageLogLevel.Error;
        }

        if (line.Contains(" warning", StringComparison.OrdinalIgnoreCase))
        {
            return PackageLogLevel.Warning;
        }

        if (line.Contains(" debug", StringComparison.OrdinalIgnoreCase))
        {
            return PackageLogLevel.Debug;
        }

        if (line.Contains(" trace", StringComparison.OrdinalIgnoreCase))
        {
            return PackageLogLevel.Trace;
        }

        return PackageLogLevel.Information;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_packageLogWatcher is not null)
            {
                _packageLogWatcher.Created -= PackageLogWatcher_OnChanged;
                _packageLogWatcher.Changed -= PackageLogWatcher_OnChanged;
                _packageLogWatcher.Renamed -= PackageLogWatcher_OnRenamed;
                _packageLogWatcher.Dispose();
                _packageLogWatcher = null;
            }
        }
    }
}
