using System.Diagnostics;
using System.Threading.Channels;
using Sunder.Sdk.Logging;

namespace Sunder.App.Services;

internal static class AppSessionLog
{
    private const int MaxRecentEntries = 5000;

    private static readonly string LogRootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sunder",
        "logs");
    private static readonly object RecentEntriesGate = new();
    private static readonly List<AppSessionLogSnapshotEntry> RecentEntries = [];

    private static readonly Lazy<IPackageLogging> Logging = new(() => new FilePackageLogging(
        LogRootPath,
        "sunder.app",
        typeof(AppSessionLog).Assembly.GetName().Version ?? new Version(0, 0)));
    private static readonly Channel<AppSessionLogEntry> Entries = Channel.CreateBounded<AppSessionLogEntry>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
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

    public static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureProcessorStarted();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Entries.Writer.TryWrite(AppSessionLogEntry.Flush(completion)))
        {
            return;
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        Entries.Writer.TryWrite(AppSessionLogEntry.Write(level, message, exception));
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
        await foreach (var entry in Entries.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (entry.FlushCompletion is not null)
                {
                    entry.FlushCompletion.SetResult();
                    continue;
                }

                await Logging.Value.Events.WriteAsync(
                    entry.Level,
                    "app.session.log",
                    entry.Message,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["log.scope"] = "app",
                    },
                    entry.Exception).ConfigureAwait(false);
            }
            catch
            {
                // Logging must never interrupt app startup or shutdown.
            }
        }
    }

    private sealed record AppSessionLogEntry(
        PackageLogLevel Level,
        string Message,
        Exception? Exception,
        TaskCompletionSource? FlushCompletion)
    {
        public static AppSessionLogEntry Write(PackageLogLevel level, string message, Exception? exception)
            => new(level, message, exception, FlushCompletion: null);

        public static AppSessionLogEntry Flush(TaskCompletionSource completion)
            => new(PackageLogLevel.Information, string.Empty, Exception: null, completion);
    }
}

internal sealed record AppSessionLogSnapshotEntry(
    DateTimeOffset Timestamp,
    PackageLogLevel Level,
    DeveloperLogEntryScope Scope,
    string Source,
    string Message,
    Exception? Exception);
