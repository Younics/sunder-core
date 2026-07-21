using System.Diagnostics;
using Sunder.Runtime.Contracts;
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

    private readonly object _syncRoot = new();
    private readonly List<DeveloperLogEntry> _entries = [];
    private readonly IRuntimeApiClientFactory? _runtimeApiClientFactory;
    private CancellationTokenSource? _packageLogCancellation;
    private Task _packageLogTask = Task.CompletedTask;
    private bool _disposed;

    public DeveloperLogService(IRuntimeApiClientFactory? runtimeApiClientFactory = null)
    {
        _runtimeApiClientFactory = runtimeApiClientFactory;
    }

    public event EventHandler<DeveloperLogEntriesChangedEventArgs>? EntriesChanged;

    public bool IsEnabled { get; private set; }

    public IReadOnlyList<DeveloperLogEntry> Snapshot()
    {
        lock (_syncRoot)
        {
            return _entries.ToArray();
        }
    }

    public void Enable(bool startPackageLogStreaming = true)
    {
        if (IsEnabled)
        {
            if (startPackageLogStreaming)
            {
                StartPackageLogStreaming();
            }
            return;
        }

        IsEnabled = true;
        AppSessionLog.EntryWritten += AppSessionLog_OnEntryWritten;
        AddEntries(AppSessionLog.Snapshot().Select(CreateMirroredLogEntry).ToArray(), forceReset: true);
        if (startPackageLogStreaming)
        {
            StartPackageLogStreaming();
        }
    }

    public void StartPackageLogStreaming()
    {
        lock (_syncRoot)
        {
            if (!IsEnabled
                || _disposed
                || _runtimeApiClientFactory is null
                || _packageLogCancellation is not null)
            {
                return;
            }

            _packageLogCancellation = new CancellationTokenSource();
            _packageLogTask = ConsumePackageLogsAsync(_packageLogCancellation.Token);
        }
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

    private async Task ConsumePackageLogsAsync(CancellationToken cancellationToken)
    {
        long sequenceId = 0;
        var runtimeInstanceId = Guid.Empty;
        var reconnectFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var connectedAt = Stopwatch.GetTimestamp();
            try
            {
                using var client = _runtimeApiClientFactory!.CreateClient<IRuntimeLogClient>();
                var snapshot = await client.GetPackageLogSnapshotAsync(sequenceId, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (snapshot.RuntimeInstanceId != Guid.Empty
                    && runtimeInstanceId != Guid.Empty
                    && snapshot.RuntimeInstanceId != runtimeInstanceId)
                {
                    ResetForRuntimeChange();
                    runtimeInstanceId = snapshot.RuntimeInstanceId;
                    sequenceId = 0;
                    continue;
                }

                if (snapshot.RuntimeInstanceId != Guid.Empty)
                {
                    runtimeInstanceId = snapshot.RuntimeInstanceId;
                }
                if (snapshot.HistoryGap)
                {
                    sequenceId = 0;
                }

                foreach (var entry in snapshot.Entries)
                {
                    if (entry.SequenceId <= sequenceId)
                    {
                        continue;
                    }

                    sequenceId = entry.SequenceId;
                    AddPackageLog(entry);
                }

                sequenceId = Math.Max(sequenceId, snapshot.SequenceId);
                await foreach (var entry in client.StreamPackageLogsAsync(sequenceId, cancellationToken).ConfigureAwait(false))
                {
                    if (entry.RuntimeInstanceId != Guid.Empty
                        && entry.RuntimeInstanceId != runtimeInstanceId)
                    {
                        sequenceId = 0;
                        break;
                    }
                    if (entry.SequenceId <= sequenceId)
                    {
                        continue;
                    }

                    sequenceId = entry.SequenceId;
                    AddPackageLog(entry);
                }

                reconnectFailures = ResetBackoffAfterStableConnection(connectedAt, reconnectFailures);
                await Task.Delay(
                    RuntimeReconnectBackoff.GetDelay(reconnectFailures++),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppSessionLog.WriteError("Runtime package-log stream disconnected.", ex, visibleInDeveloperLog: false);
                reconnectFailures = ResetBackoffAfterStableConnection(connectedAt, reconnectFailures);
                await Task.Delay(
                    RuntimeReconnectBackoff.GetDelay(reconnectFailures++),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static int ResetBackoffAfterStableConnection(long connectedAt, int failures)
        => Stopwatch.GetElapsedTime(connectedAt) >= TimeSpan.FromSeconds(30) ? 0 : failures;

    private void ResetForRuntimeChange()
    {
        var applicationEntries = AppSessionLog.Snapshot()
            .Select(CreateMirroredLogEntry)
            .ToArray();
        lock (_syncRoot)
        {
            _entries.Clear();
            _entries.AddRange(applicationEntries);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }
        }
        EntriesChanged?.Invoke(this, DeveloperLogEntriesChangedEventArgs.ForReset());
    }

    private void AddPackageLog(PackageLogEntryDescriptor entry)
        => WritePackageLog(
            ToPackageLogLevel(entry.Level),
            entry.PackageId,
            entry.Message,
            entry.Timestamp);

    private static PackageLogLevel ToPackageLogLevel(RuntimePackageLogLevel level)
        => level switch
        {
            RuntimePackageLogLevel.Trace => PackageLogLevel.Trace,
            RuntimePackageLogLevel.Debug => PackageLogLevel.Debug,
            RuntimePackageLogLevel.Warning => PackageLogLevel.Warning,
            RuntimePackageLogLevel.Error => PackageLogLevel.Error,
            RuntimePackageLogLevel.Critical => PackageLogLevel.Critical,
            _ => PackageLogLevel.Information,
        };

    internal void WritePackageLog(
        PackageLogLevel level,
        string packageId,
        string message,
        DateTimeOffset? timestamp = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        var entry = new DeveloperLogEntry(
            timestamp ?? DateTimeOffset.Now,
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
            _packageLogCancellation?.Cancel();
            _packageLogCancellation?.Dispose();
            _packageLogCancellation = null;
        }
    }
}
