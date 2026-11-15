using System.Diagnostics;
using Sunder.Sdk.Logging;

namespace Sunder.App.Services;

internal static class AppSessionLog
{
    private const int MaxRecentEntries = 5000;
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);

    private static readonly string LogRootPath = AppLocalState.GetPath("logs");
    private static readonly object RecentEntriesGate = new();
    private static readonly List<AppSessionLogSnapshotEntry> RecentEntries = [];
    internal static Guid InstanceId { get; } = Guid.NewGuid();
    private static long _sequence;

    private static readonly string SessionLogPath = Path.Combine(
        LogRootPath,
        $"app-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
    private static readonly AppSessionLogQueue Entries = new(1024);
    private static readonly Task Processor = Task.Run(ProcessEntriesAsync);

    public static event Action<AppSessionLogSnapshotEntry>? EntryWritten;

    public static IReadOnlyList<AppSessionLogSnapshotEntry> Snapshot()
    {
        lock (RecentEntriesGate)
        {
            return RecentEntries.ToArray();
        }
    }

    public static void WriteInfo(
        string message,
        bool visibleInDeveloperLog = true,
        DeveloperLogEntryScope developerLogScope = DeveloperLogEntryScope.Application,
        string? developerLogSource = null)
        => Write(PackageLogLevel.Information, message, exception: null, visibleInDeveloperLog, developerLogScope, developerLogSource);

    public static void WriteError(
        string message,
        Exception? exception = null,
        bool visibleInDeveloperLog = true,
        DeveloperLogEntryScope developerLogScope = DeveloperLogEntryScope.Application,
        string? developerLogSource = null)
        => Write(PackageLogLevel.Error, message, exception, visibleInDeveloperLog, developerLogScope, developerLogSource);

    internal static void WritePackage(
        PackageLogLevel level,
        string packageId,
        string message,
        Exception? exception = null)
        => Write(level, message, exception, visibleInDeveloperLog: true, DeveloperLogEntryScope.Package, packageId);

    internal static void WritePolicySupervisionDiagnostic(string message)
    {
        // Supervision must never wait for UI log subscribers, Trace listeners or
        // file I/O. The bounded queue is best-effort; terminal evidence is also
        // included in the watchdog's fail-fast message.
        EnsureProcessorStarted();
        Entries.TryWrite(AppSessionLogEntry.Write(PackageLogLevel.Information, message, exceptionText: null));
    }

    public static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureProcessorStarted();
        await Entries.FlushAsync(FlushTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static void Write(
        PackageLogLevel level,
        string message,
        Exception? exception,
        bool visibleInDeveloperLog,
        DeveloperLogEntryScope developerLogScope,
        string? developerLogSource)
    {
        // Exception stacks and custom fields can root a collectible package.
        // Preserve their diagnostics, not their live object graph, in both logs.
        var exceptionText = exception?.ToString();
        Trace.WriteLine(exceptionText is null ? message : $"{message}{Environment.NewLine}{exceptionText}");
        EnsureProcessorStarted();
        Entries.TryWrite(AppSessionLogEntry.Write(level, message, exceptionText));
        if (!visibleInDeveloperLog)
        {
            return;
        }

        var snapshotEntry = new AppSessionLogSnapshotEntry(
            DateTimeOffset.Now,
            level,
            developerLogScope,
            string.IsNullOrWhiteSpace(developerLogSource) ? "application" : developerLogSource.Trim(),
            message,
            exceptionText);
        lock (RecentEntriesGate)
        {
            snapshotEntry = snapshotEntry with { SequenceId = ++_sequence };
            RecentEntries.Add(snapshotEntry);
            if (RecentEntries.Count > MaxRecentEntries)
            {
                RecentEntries.RemoveRange(0, RecentEntries.Count - MaxRecentEntries);
            }
        }

        EntryWritten?.Invoke(snapshotEntry);
    }

    private static void EnsureProcessorStarted()
        => _ = Processor;

    private static async Task ProcessEntriesAsync()
    {
        await foreach (var entry in Entries.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (entry.FlushCompletion is not null)
                {
                    entry.FlushCompletion.TrySetResult();
                    continue;
                }

                Directory.CreateDirectory(LogRootPath);
                var line = $"{DateTimeOffset.Now:O} level={entry.Level} {entry.Message}";
                if (entry.ExceptionText is not null)
                {
                    line += $"{Environment.NewLine}{entry.ExceptionText}";
                }

                await File.AppendAllTextAsync(SessionLogPath, line + Environment.NewLine).ConfigureAwait(false);
            }
            catch
            {
                // Logging must never interrupt app startup or shutdown.
            }
        }
    }

}

internal sealed record AppSessionLogEntry(
    PackageLogLevel Level,
    string Message,
    string? ExceptionText,
    TaskCompletionSource? FlushCompletion)
{
    public static AppSessionLogEntry Write(PackageLogLevel level, string message, string? exceptionText)
        => new(level, message, exceptionText, FlushCompletion: null);

    public static AppSessionLogEntry Dropped(long count)
        => Write(PackageLogLevel.Warning, $"Dropped {count} file log entr{(count == 1 ? "y" : "ies")} because the log queue was full.", null);

    public static AppSessionLogEntry Flush(TaskCompletionSource completion)
        => new(PackageLogLevel.Information, string.Empty, ExceptionText: null, completion);
}

internal sealed record AppSessionLogSnapshotEntry(
    DateTimeOffset Timestamp,
    PackageLogLevel Level,
    DeveloperLogEntryScope Scope,
    string Source,
    string Message,
    string? ExceptionText)
{
    internal long SequenceId { get; init; }
}
