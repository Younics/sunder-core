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
        Trace.WriteLine(exception is null ? message : $"{message}{Environment.NewLine}{exception}");
        EnsureProcessorStarted();
        Entries.TryWrite(AppSessionLogEntry.Write(level, message, exception));
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
            exception);
        lock (RecentEntriesGate)
        {
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
                if (entry.Exception is not null)
                {
                    line += $"{Environment.NewLine}{entry.Exception}";
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
    Exception? Exception,
    TaskCompletionSource? FlushCompletion)
{
    public static AppSessionLogEntry Write(PackageLogLevel level, string message, Exception? exception)
        => new(level, message, exception, FlushCompletion: null);

    public static AppSessionLogEntry Dropped(long count)
        => Write(PackageLogLevel.Warning, $"Dropped {count} file log entr{(count == 1 ? "y" : "ies")} because the log queue was full.", null);

    public static AppSessionLogEntry Flush(TaskCompletionSource completion)
        => new(PackageLogLevel.Information, string.Empty, Exception: null, completion);
}

internal sealed record AppSessionLogSnapshotEntry(
    DateTimeOffset Timestamp,
    PackageLogLevel Level,
    DeveloperLogEntryScope Scope,
    string Source,
    string Message,
    Exception? Exception);
