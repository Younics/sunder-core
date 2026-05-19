using Sunder.Sdk.Logging;

namespace Sunder.App.Services;

public sealed record DeveloperLogEntry(
    DateTimeOffset Timestamp,
    PackageLogLevel Level,
    DeveloperLogEntryScope Scope,
    string Source,
    string Message);

public enum DeveloperLogEntryScope
{
    Application = 0,
    Package = 1,
}

public sealed class DeveloperLogEntriesChangedEventArgs : EventArgs
{
    private DeveloperLogEntriesChangedEventArgs(bool reset, IReadOnlyList<DeveloperLogEntry> addedEntries)
    {
        Reset = reset;
        AddedEntries = addedEntries;
    }

    public bool Reset { get; }

    public IReadOnlyList<DeveloperLogEntry> AddedEntries { get; }

    public static DeveloperLogEntriesChangedEventArgs ForReset()
        => new(reset: true, []);

    public static DeveloperLogEntriesChangedEventArgs ForAdd(IReadOnlyList<DeveloperLogEntry> addedEntries)
        => new(reset: false, addedEntries);
}

public sealed class DeveloperLogService : IDisposable
{
    private const int MaxEntries = 50000;
    private static readonly string PackageLogsRootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunder",
        "Packages");

    private readonly object _syncRoot = new();
    private readonly List<DeveloperLogEntry> _entries = [];
    private readonly Dictionary<string, long> _logFileOffsets = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _packageLogWatcher;
    private bool _disposed;

    public event EventHandler<DeveloperLogEntriesChangedEventArgs>? EntriesChanged;

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
        if (IsEnabled)
        {
            return;
        }

        IsEnabled = true;
        AppSessionLog.EntryWritten += AppSessionLog_OnEntryWritten;
        AddEntries(AppSessionLog.Snapshot().Select(CreateMirroredLogEntry).ToArray(), forceReset: true);
        StartPackageLogWatcher();
    }

    public void Write(PackageLogLevel level, string source, string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        var entry = new DeveloperLogEntry(
            DateTimeOffset.Now,
            level,
            DeveloperLogEntryScope.Application,
            Normalize(source, "application"),
            Normalize(message, string.Empty));
        AddEntry(entry);

        if (level >= PackageLogLevel.Error)
        {
            AppSessionLog.WriteError($"[{entry.Source}] {entry.Message}", visibleInDeveloperLog: false);
        }
        else
        {
            AppSessionLog.WriteInfo($"[{entry.Source}] {entry.Message}", visibleInDeveloperLog: false);
        }
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
                    WritePackageLog(ParseLevel(line), ResolveLogSource(filePath), line);
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
        if (line.Contains("level=critical", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" critical", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("crit:", StringComparison.OrdinalIgnoreCase))
        {
            return PackageLogLevel.Critical;
        }

        if (line.Contains("level=error", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" error", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("fail:", StringComparison.OrdinalIgnoreCase))
        {
            return PackageLogLevel.Error;
        }

        if (line.Contains("level=warning", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" warning", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("warn:", StringComparison.OrdinalIgnoreCase))
        {
            return PackageLogLevel.Warning;
        }

        if (line.Contains("level=debug", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" debug", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("dbug:", StringComparison.OrdinalIgnoreCase))
        {
            return PackageLogLevel.Debug;
        }

        if (line.Contains("level=trace", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" trace", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("trce:", StringComparison.OrdinalIgnoreCase))
        {
            return PackageLogLevel.Trace;
        }

        return PackageLogLevel.Information;
    }

    internal void WritePackageLog(PackageLogLevel level, string packageId, string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        var entry = new DeveloperLogEntry(
            DateTimeOffset.Now,
            level,
            DeveloperLogEntryScope.Package,
            Normalize(packageId, "package.log"),
            Normalize(message, string.Empty));
        AddEntry(entry);

        if (level >= PackageLogLevel.Error)
        {
            AppSessionLog.WriteError($"[{entry.Source}] {entry.Message}", visibleInDeveloperLog: false);
        }
        else
        {
            AppSessionLog.WriteInfo($"[{entry.Source}] {entry.Message}", visibleInDeveloperLog: false);
        }
    }

    private void AppSessionLog_OnEntryWritten(AppSessionLogSnapshotEntry entry)
    {
        if (!IsEnabled || _disposed)
        {
            return;
        }

        AddEntry(CreateMirroredLogEntry(entry));
    }

    private void AddEntry(DeveloperLogEntry entry)
        => AddEntries([entry], forceReset: false);

    private void AddEntries(IReadOnlyList<DeveloperLogEntry> entries, bool forceReset)
    {
        if (entries.Count == 0)
        {
            return;
        }

        DeveloperLogEntriesChangedEventArgs eventArgs;
        lock (_syncRoot)
        {
            _entries.AddRange(entries);
            var trimmed = false;
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
                trimmed = true;
            }

            eventArgs = forceReset || trimmed
                ? DeveloperLogEntriesChangedEventArgs.ForReset()
                : DeveloperLogEntriesChangedEventArgs.ForAdd(entries);
        }

        EntriesChanged?.Invoke(this, eventArgs);
    }

    private static DeveloperLogEntry CreateMirroredLogEntry(AppSessionLogSnapshotEntry entry)
        => new(
            entry.Timestamp,
            entry.Level,
            entry.Scope,
            Normalize(entry.Source, "application"),
            Normalize(FormatApplicationMessage(entry.Message, entry.Exception), string.Empty));

    private static string FormatApplicationMessage(string message, Exception? exception)
        => exception is null
            ? message
            : $"{message}{Environment.NewLine}{exception}";

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AppSessionLog.EntryWritten -= AppSessionLog_OnEntryWritten;
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
