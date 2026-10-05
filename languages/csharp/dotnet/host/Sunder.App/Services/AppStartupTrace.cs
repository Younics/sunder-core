using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal static class AppStartupTrace
{
    internal const string TracePathEnvironmentVariable = "SUNDER_APP_STARTUP_TRACE_PATH";
    internal const string ShellRevealedEvent = "app.shell.revealed";
    internal const string PackageModuleLoadStartedEvent = "app.package.module.load.started";
    internal const string PackageModuleLoadCompletedEvent = "app.package.module.load.completed";
    internal const string PackageIconMaterializationStartedEvent = "app.package.icon.materialization.started";
    internal const string PackageIconMaterializationCompletedEvent = "app.package.icon.materialization.completed";
    internal const string PackageViewConstructionStartedEvent = "app.package.view.construction.started";
    internal const string PackageViewConstructionCompletedEvent = "app.package.view.construction.completed";
    internal const string PackageViewLoadStartedEvent = "app.package.view.load.started";
    internal const string PackageViewLoadCompletedEvent = "app.package.view.load.completed";
    internal const string FirstUsefulRpcStartedEvent = "app.package.rpc.first.started";
    internal const string FirstUsefulRpcCompletedEvent = "app.package.rpc.first.completed";

    private static readonly object SessionGate = new();
    private static AppStartupTraceSession? _session;
    private static string? _sessionPath;

    internal static AppStartupTraceOperation? BeginPackageModuleLoad(
        ActivePackageDescriptor package,
        PackageUiSnapshotDescriptor source,
        Guid appGenerationId,
        long queuedTimestamp)
        => TryBegin(
            PackageModuleLoadStartedEvent,
            PackageModuleLoadCompletedEvent,
            new AppStartupTraceMetadata(
                PackageId: package.PackageId,
                PackageVersion: package.Version,
                TargetRole: source.Target.Role,
                TargetKind: source.Target.Kind,
                RuntimeIdentifier: source.Target.Rid,
                AppGenerationId: appGenerationId.ToString("N"),
                SessionGeneration: source.SessionGeneration),
            queuedTimestamp);

    internal static AppStartupTraceOperation? BeginPackageIconMaterialization(
        string packageId,
        string assetPath,
        Guid? appGenerationId,
        Guid runtimeInstanceId,
        long sessionGeneration,
        long queuedTimestamp)
        => TryBegin(
            PackageIconMaterializationStartedEvent,
            PackageIconMaterializationCompletedEvent,
            new AppStartupTraceMetadata(
                PackageId: packageId,
                AppGenerationId: appGenerationId?.ToString("N"),
                RuntimeInstanceId: runtimeInstanceId.ToString("N"),
                SessionGeneration: sessionGeneration,
                AssetPath: assetPath),
            queuedTimestamp);

    internal static AppStartupTraceOperation? BeginPackageViewConstruction(
        string packageId,
        string viewId,
        Guid? appGenerationId)
        => TryBegin(
            PackageViewConstructionStartedEvent,
            PackageViewConstructionCompletedEvent,
            new AppStartupTraceMetadata(
                PackageId: packageId,
                AppGenerationId: appGenerationId?.ToString("N"),
                ViewId: viewId));

    internal static AppStartupTraceOperation? BeginPackageViewLoad(
        string packageId,
        string viewId,
        Guid? appGenerationId)
        => TryBegin(
            PackageViewLoadStartedEvent,
            PackageViewLoadCompletedEvent,
            new AppStartupTraceMetadata(
                PackageId: packageId,
                AppGenerationId: appGenerationId?.ToString("N"),
                ViewId: viewId));

    internal static AppStartupTraceOperation? BeginFirstUsefulRpc(ActivePackageWebStamp package)
        => TryBegin(
            FirstUsefulRpcStartedEvent,
            FirstUsefulRpcCompletedEvent,
            new AppStartupTraceMetadata(
                PackageId: package.PackageId,
                PackageVersion: package.PackageVersion,
                TargetRole: package.Target.Role,
                TargetKind: package.Target.Kind,
                RuntimeIdentifier: package.Target.Rid,
                AppGenerationId: package.AppGenerationId.ToString("N"),
                SessionGeneration: package.SessionGeneration));

    internal static void RecordShellRevealed(bool openCoreShell)
    {
        try
        {
            GetSession()?.RecordShellRevealed(openCoreShell);
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to write the App startup trace.", exception);
        }
    }

    internal static void WriteShellRevealed(
        string tracePath,
        double elapsedMilliseconds,
        int processId,
        bool openCoreShell)
    {
        ValidateShellRecord(tracePath, elapsedMilliseconds, processId);
        var correlationId = Guid.NewGuid().ToString("N");
        var shellEvent = new AppStartupTraceEventRecord(
            Sequence: 1,
            CorrelationId: correlationId,
            Event: ShellRevealedEvent,
            TimestampUtc: DateTimeOffset.UtcNow,
            ElapsedMilliseconds: elapsedMilliseconds,
            ProcessId: processId,
            Outcome: openCoreShell ? "core-shell" : "succeeded");
        WriteDocument(
            tracePath,
            new AppStartupTraceDocument(
                SchemaVersion: 1,
                Event: ShellRevealedEvent,
                ElapsedMilliseconds: elapsedMilliseconds,
                ProcessId: processId,
                OpenCoreShell: openCoreShell,
                CorrelationId: correlationId,
                Events: [shellEvent]));
    }

    internal static AppStartupTraceSession CreateSession(
        string tracePath,
        long startupTimestamp,
        int processId)
        => new(tracePath, startupTimestamp, processId);

    private static AppStartupTraceOperation? TryBegin(
        string startedEvent,
        string completedEvent,
        AppStartupTraceMetadata metadata,
        long? queuedTimestamp = null)
    {
        try
        {
            return GetSession()?.BeginOperation(
                startedEvent,
                completedEvent,
                metadata,
                queuedTimestamp);
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to record an App startup trace event.", exception);
            return null;
        }
    }

    private static AppStartupTraceSession? GetSession()
    {
        var tracePath = Environment.GetEnvironmentVariable(TracePathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(tracePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(tracePath);
        lock (SessionGate)
        {
            if (_session is null || !string.Equals(_sessionPath, fullPath, StringComparison.Ordinal))
            {
                _session = new AppStartupTraceSession(fullPath, Program.StartupTimestamp, Environment.ProcessId);
                _sessionPath = fullPath;
            }
            return _session;
        }
    }

    private static void ValidateShellRecord(
        string tracePath,
        double elapsedMilliseconds,
        int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tracePath);
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(elapsedMilliseconds),
                "Elapsed startup time must be finite and non-negative.");
        }
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }
    }

    internal static void WriteDocument(string tracePath, AppStartupTraceDocument document)
    {
        var fullPath = Path.GetFullPath(tracePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("App startup trace path must have a parent directory.", nameof(tracePath));
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(
                temporaryPath,
                JsonSerializer.SerializeToUtf8Bytes(document, AppStartupTraceJson.Options));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed class AppStartupTraceSession
{
    internal const int MaxEvents = 4096;

    private readonly object _syncRoot = new();
    private readonly string _tracePath;
    private readonly long _startupTimestamp;
    private readonly int _processId;
    private readonly string _correlationId = Guid.NewGuid().ToString("N");
    private readonly List<AppStartupTraceEventRecord> _events = [];
    private AppStartupTraceShellRecord? _shell;
    private bool _disabled;
    private int _sequence;
    private int _activeOperationReservations;

    internal AppStartupTraceSession(
        string tracePath,
        long startupTimestamp,
        int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tracePath);
        if (startupTimestamp <= 0) throw new ArgumentOutOfRangeException(nameof(startupTimestamp));
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        _tracePath = Path.GetFullPath(tracePath);
        _startupTimestamp = startupTimestamp;
        _processId = processId;
    }

    internal AppStartupTraceOperation? BeginOperation(
        string startedEvent,
        string completedEvent,
        AppStartupTraceMetadata metadata,
        long? queuedTimestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startedEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedEvent);
        ArgumentNullException.ThrowIfNull(metadata);

        lock (_syncRoot)
        {
            var shellReservation = _shell is null ? 1 : 0;
            if (_disabled
                || _events.Count + _activeOperationReservations + shellReservation + 2 > MaxEvents)
            {
                return null;
            }

            var startedTimestamp = Stopwatch.GetTimestamp();
            AddEventUnderLock(
                startedEvent,
                startedTimestamp,
                metadata,
                outcome: "started",
                queueDurationMilliseconds: queuedTimestamp is null
                    ? null
                    : GetNonNegativeElapsedMilliseconds(queuedTimestamp.Value, startedTimestamp));
            _activeOperationReservations++;
            TryWriteUnderLock();
            return new AppStartupTraceOperation(this, completedEvent, metadata, startedTimestamp);
        }
    }

    internal void RecordShellRevealed(bool openCoreShell)
        => RecordShellRevealed(
            GetNonNegativeElapsedMilliseconds(_startupTimestamp, Stopwatch.GetTimestamp()),
            openCoreShell);

    internal void RecordShellRevealed(double elapsedMilliseconds, bool openCoreShell)
    {
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        }

        lock (_syncRoot)
        {
            if (_disabled
                || _shell is not null
                || _events.Count + _activeOperationReservations >= MaxEvents)
            {
                return;
            }

            _shell = new AppStartupTraceShellRecord(elapsedMilliseconds, openCoreShell);
            _events.Add(new AppStartupTraceEventRecord(
                Sequence: ++_sequence,
                CorrelationId: _correlationId,
                Event: AppStartupTrace.ShellRevealedEvent,
                TimestampUtc: DateTimeOffset.UtcNow,
                ElapsedMilliseconds: elapsedMilliseconds,
                ProcessId: _processId,
                Outcome: openCoreShell ? "core-shell" : "succeeded"));
            TryWriteUnderLock();
        }
    }

    internal void CompleteOperation(
        string completedEvent,
        AppStartupTraceMetadata metadata,
        long startedTimestamp,
        string outcome,
        string? operation,
        long? bytesRead)
    {
        lock (_syncRoot)
        {
            if (_activeOperationReservations > 0)
            {
                _activeOperationReservations--;
            }
            if (_disabled || _events.Count >= MaxEvents)
            {
                return;
            }

            var completedTimestamp = Stopwatch.GetTimestamp();
            AddEventUnderLock(
                completedEvent,
                completedTimestamp,
                metadata with { Operation = operation ?? metadata.Operation },
                outcome,
                activeDurationMilliseconds: GetNonNegativeElapsedMilliseconds(
                    startedTimestamp,
                    completedTimestamp),
                bytesRead: bytesRead);
            TryWriteUnderLock();
        }
    }

    private void AddEventUnderLock(
        string eventName,
        long timestamp,
        AppStartupTraceMetadata metadata,
        string outcome,
        double? queueDurationMilliseconds = null,
        double? activeDurationMilliseconds = null,
        long? bytesRead = null)
        => _events.Add(new AppStartupTraceEventRecord(
            Sequence: ++_sequence,
            CorrelationId: _correlationId,
            Event: eventName,
            TimestampUtc: DateTimeOffset.UtcNow,
            ElapsedMilliseconds: GetNonNegativeElapsedMilliseconds(_startupTimestamp, timestamp),
            ProcessId: _processId,
            Outcome: outcome,
            PackageId: metadata.PackageId,
            PackageVersion: metadata.PackageVersion,
            TargetRole: metadata.TargetRole,
            TargetKind: metadata.TargetKind,
            RuntimeIdentifier: metadata.RuntimeIdentifier,
            RuntimeInstanceId: metadata.RuntimeInstanceId,
            AppGenerationId: metadata.AppGenerationId,
            SessionGeneration: metadata.SessionGeneration,
            ViewId: metadata.ViewId,
            AssetPath: metadata.AssetPath,
            Operation: metadata.Operation,
            QueueDurationMilliseconds: queueDurationMilliseconds,
            ActiveDurationMilliseconds: activeDurationMilliseconds,
            BytesRead: bytesRead));

    private void TryWriteUnderLock()
    {
        if (_shell is not { } shell)
        {
            return;
        }

        try
        {
            AppStartupTrace.WriteDocument(
                _tracePath,
                new AppStartupTraceDocument(
                    SchemaVersion: 1,
                    Event: AppStartupTrace.ShellRevealedEvent,
                    ElapsedMilliseconds: shell.ElapsedMilliseconds,
                    ProcessId: _processId,
                    OpenCoreShell: shell.OpenCoreShell,
                    CorrelationId: _correlationId,
                    Events: _events.ToArray()));
        }
        catch (Exception exception)
        {
            _disabled = true;
            AppSessionLog.WriteError("Failed to write the App startup trace.", exception);
        }
    }

    private static double GetNonNegativeElapsedMilliseconds(long startedTimestamp, long completedTimestamp)
        => Math.Max(0, Stopwatch.GetElapsedTime(startedTimestamp, completedTimestamp).TotalMilliseconds);
}

internal sealed class AppStartupTraceOperation(
    AppStartupTraceSession session,
    string completedEvent,
    AppStartupTraceMetadata metadata,
    long startedTimestamp)
{
    private int _completed;

    internal bool Complete(
        string outcome = "succeeded",
        string? operation = null,
        long? bytesRead = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return false;
        }

        session.CompleteOperation(
            completedEvent,
            metadata,
            startedTimestamp,
            outcome,
            operation,
            bytesRead);
        return true;
    }

    internal bool Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Complete(exception is OperationCanceledException ? "cancelled" : "failed");
    }
}

internal sealed record AppStartupTraceMetadata(
    string? PackageId = null,
    string? PackageVersion = null,
    string? TargetRole = null,
    string? TargetKind = null,
    string? RuntimeIdentifier = null,
    string? RuntimeInstanceId = null,
    string? AppGenerationId = null,
    long? SessionGeneration = null,
    string? ViewId = null,
    string? AssetPath = null,
    string? Operation = null);

internal sealed record AppStartupTraceEventRecord(
    int Sequence,
    string CorrelationId,
    string Event,
    DateTimeOffset TimestampUtc,
    double ElapsedMilliseconds,
    int ProcessId,
    string Outcome,
    string? PackageId = null,
    string? PackageVersion = null,
    string? TargetRole = null,
    string? TargetKind = null,
    string? RuntimeIdentifier = null,
    string? RuntimeInstanceId = null,
    string? AppGenerationId = null,
    long? SessionGeneration = null,
    string? ViewId = null,
    string? AssetPath = null,
    string? Operation = null,
    double? QueueDurationMilliseconds = null,
    double? ActiveDurationMilliseconds = null,
    long? BytesRead = null);

internal sealed record AppStartupTraceDocument(
    int SchemaVersion,
    string Event,
    double ElapsedMilliseconds,
    int ProcessId,
    bool OpenCoreShell,
    string CorrelationId,
    IReadOnlyList<AppStartupTraceEventRecord> Events);

internal sealed record AppStartupTraceShellRecord(
    double ElapsedMilliseconds,
    bool OpenCoreShell);

internal static class AppStartupTraceJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
