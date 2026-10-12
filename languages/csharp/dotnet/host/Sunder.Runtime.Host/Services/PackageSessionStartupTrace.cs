using System.Text.Json;
using System.Threading.Channels;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class PackageSessionStartupTraceEvents
{
    public const string LoadStarted = "package.session.load.started";
    public const string SourceDiscoveryCompleted = "package.source.discovery.completed";
    public const string GraphPlanCompleted = "package.graph.plan.completed";
    public const string ProjectionCacheCompleted = "package.projection.cache.completed";
    public const string ProjectionPrepared = "package.projection.prepared";
    public const string RuntimeBindingCompleted = "package.runtime.binding.completed";
    public const string WorkerWaveStarted = "package.worker.wave.started";
    public const string WorkerProcessStarted = "package.worker.process.started";
    public const string WorkerReady = "package.worker.ready";
    public const string CandidateReady = "package.candidate.ready";
    public const string WorkerWaveCompleted = "package.worker.wave.completed";
    public const string GenerationCommitted = "package.generation.committed";
    public const string LoadFailed = "package.session.load.failed";
}

internal sealed class PackageSessionStartupTraceWriter : IDisposable
{
    public const string OutputPathEnvironmentVariable = "SUNDER_RUNTIME_TRACE_PATH";
    internal const int MaxBufferedEvents = 4096;
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(1);

    private readonly object _syncRoot = new();
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsStream;
    private readonly Stream? _stream;
    private readonly Channel<TraceWriteRequest>? _requests;
    private readonly Task _writerTask;
    private readonly HashSet<PackageSessionStartupTraceAttempt> _attempts = [];
    private long _sequence;
    private int _bufferedRequestCount;
    private int _activeAttemptReservations;
    private int _streamDisposeQueued;
    private bool _acceptingAttempts = true;
    private bool _disabled;
    private bool _disposing;
    private bool _disposed;

    private PackageSessionStartupTraceWriter(
        Stream? stream,
        bool ownsStream,
        TimeProvider? timeProvider)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (stream is null)
        {
            _disabled = true;
            _writerTask = Task.CompletedTask;
            return;
        }

        _requests = Channel.CreateBounded<TraceWriteRequest>(new BoundedChannelOptions(MaxBufferedEvents)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public bool IsEnabled
    {
        get
        {
            lock (_syncRoot)
            {
                return _acceptingAttempts && !_disabled && !_disposing && !_disposed && _requests is not null;
            }
        }
    }

    public static PackageSessionStartupTraceWriter Open(
        string? outputPath,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return new PackageSessionStartupTraceWriter(null, ownsStream: false, timeProvider);
        }

        FileStream? stream = null;
        try
        {
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 16 * 1024,
                Options = FileOptions.SequentialScan,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            stream = new FileStream(fullPath, options);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return new PackageSessionStartupTraceWriter(stream, ownsStream: true, timeProvider);
        }
        catch
        {
            stream?.Dispose();
            return new PackageSessionStartupTraceWriter(null, ownsStream: false, timeProvider);
        }
    }

    internal static PackageSessionStartupTraceWriter Create(
        Stream stream,
        TimeProvider? timeProvider = null,
        bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("The startup trace stream must be writable.", nameof(stream));
        return new PackageSessionStartupTraceWriter(stream, ownsStream, timeProvider);
    }

    public PackageSessionStartupTraceAttempt? BeginAttempt(long baseGeneration)
    {
        lock (_syncRoot)
        {
            if (!_acceptingAttempts || _disabled || _disposing || _disposed || _requests is null) return null;
            if (_bufferedRequestCount + _activeAttemptReservations > MaxBufferedEvents - 2)
            {
                _acceptingAttempts = false;
                return null;
            }

            _activeAttemptReservations++;
            var attempt = new PackageSessionStartupTraceAttempt(this, _timeProvider, baseGeneration);
            if (!attempt.Started)
            {
                _activeAttemptReservations--;
                return null;
            }
            _attempts.Add(attempt);
            return attempt;
        }
    }

    internal bool Write(PackageSessionStartupTraceRecord record)
    {
        lock (_syncRoot)
        {
            if (_disabled || _disposing || _disposed || _requests is null) return false;
            if (_bufferedRequestCount + _activeAttemptReservations >= MaxBufferedEvents)
            {
                _acceptingAttempts = false;
                return false;
            }
            if (!_requests.Writer.TryWrite(new TraceWriteRequest(record, Completion: null)))
            {
                _disabled = true;
                _acceptingAttempts = false;
                return false;
            }
            _bufferedRequestCount++;
            return true;
        }
    }

    internal bool WriteTerminal(
        PackageSessionStartupTraceAttempt attempt,
        PackageSessionStartupTraceRecord record)
    {
        lock (_syncRoot)
        {
            if (!_attempts.Remove(attempt)) return false;
            _activeAttemptReservations--;
            if (_disabled || _disposed || _requests is null) return false;
            if (_bufferedRequestCount >= MaxBufferedEvents
                || !_requests.Writer.TryWrite(new TraceWriteRequest(record, Completion: null)))
            {
                _disabled = true;
                _acceptingAttempts = false;
                return false;
            }
            _bufferedRequestCount++;
            return true;
        }
    }

    internal Task FlushAsync()
    {
        lock (_syncRoot)
        {
            if (_disabled || _disposed || _requests is null) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_bufferedRequestCount + _activeAttemptReservations >= MaxBufferedEvents
                || !_requests.Writer.TryWrite(new TraceWriteRequest(Record: null, completion)))
            {
                completion.TrySetResult();
            }
            else
            {
                _bufferedRequestCount++;
            }
            return completion.Task;
        }
    }

    public void Dispose()
    {
        PackageSessionStartupTraceAttempt[] attempts;
        lock (_syncRoot)
        {
            if (_disposed || _disposing) return;
            _disposing = true;
            _acceptingAttempts = false;
            attempts = _attempts.ToArray();
        }
        foreach (var attempt in attempts) attempt.TryFail("shutdown");

        Channel<TraceWriteRequest>? requests;
        lock (_syncRoot)
        {
            _disposed = true;
            _disposing = false;
            requests = _requests;
        }
        try
        {
            requests?.Writer.TryComplete();
            if (!_writerTask.Wait(ShutdownFlushTimeout))
            {
                lock (_syncRoot) _disabled = true;
                QueueOwnedStreamDisposal();
                _ = ObserveAsync(_writerTask);
                return;
            }
        }
        catch
        {
            // Diagnostics must never affect Runtime execution or shutdown.
        }
        finally
        {
            QueueOwnedStreamDisposal();
        }
    }

    private async Task WriteLoopAsync()
    {
        var requests = _requests;
        var stream = _stream;
        if (requests is null || stream is null) return;
        try
        {
            await foreach (var request in requests.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                lock (_syncRoot) _bufferedRequestCount--;
                if (request.Record is not null) WriteRecord(stream, request.Record);
                request.Completion?.TrySetResult();
            }
            stream.Flush();
        }
        catch
        {
            lock (_syncRoot)
            {
                _disabled = true;
                _acceptingAttempts = false;
            }
            while (requests.Reader.TryRead(out var request))
            {
                lock (_syncRoot) _bufferedRequestCount--;
                request.Completion?.TrySetResult();
            }
        }
    }

    private void WriteRecord(Stream stream, PackageSessionStartupTraceRecord record)
    {
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 2);
            json.WriteNumber("sequence", ++_sequence);
            json.WriteString("correlationId", record.CorrelationId.ToString("N"));
            json.WriteString("event", record.EventName);
            json.WriteString("timestampUtc", record.TimestampUtc);
            json.WriteNumber("elapsedMilliseconds", record.Elapsed.TotalMilliseconds);
            json.WriteNumber("baseGeneration", record.BaseGeneration);
            WriteNumber(json, "candidateGeneration", record.CandidateGeneration);
            WriteString(json, "packageId", record.PackageId);
            WriteString(json, "packageVersion", record.PackageVersion);
            WriteString(json, "sourceKind", record.SourceKind);
            WriteString(json, "targetRole", record.TargetRole);
            WriteString(json, "runtimeIdentifier", record.RuntimeIdentifier);
            WriteString(json, "activationId", record.ActivationId?.ToString("N"));
            WriteNumber(json, "processId", record.ProcessId);
            if (record.ProcessStartedAtUtc is not null)
            {
                json.WriteString("processStartedAtUtc", record.ProcessStartedAtUtc.Value);
            }
            WriteDuration(json, "processStartElapsedMilliseconds", record.ProcessStartElapsed);
            WriteDuration(json, "processReadyElapsedMilliseconds", record.ProcessReadyElapsed);
            WriteDuration(json, "queueDurationMilliseconds", record.QueueDuration);
            WriteDuration(json, "activeDurationMilliseconds", record.ActiveDuration);
            WriteNumber(json, "bytesRead", record.BytesRead);
            WriteNumber(json, "bytesHashed", record.BytesHashed);
            WriteNumber(json, "bytesCopied", record.BytesCopied);
            WriteString(json, "projectionCacheResult", record.ProjectionCacheResult);
            WriteString(json, "executionRuntimeId", record.ExecutionRuntimeId);
            WriteString(json, "executionRuntimeVendorVersion", record.ExecutionRuntimeVendorVersion);
            WriteString(json, "executionRuntimeVendorChecksumDigest", record.ExecutionRuntimeVendorChecksumDigest);
            WriteNumber(json, "waveIndex", record.WaveIndex);
            WriteNumber(json, "waveCount", record.WaveCount);
            WriteNumber(json, "wavePackageCount", record.WavePackageCount);
            WriteNumber(json, "dependencyCount", record.DependencyCount);
            WriteNumber(json, "preparationConcurrency", record.PreparationConcurrency);
            WriteNumber(json, "workerStartupConcurrency", record.WorkerStartupConcurrency);
            WriteNumber(json, "installedPackageCount", record.InstalledPackageCount);
            WriteNumber(json, "devPackageCount", record.DevPackageCount);
            WriteNumber(json, "packageCount", record.PackageCount);
            json.WriteString("outcome", record.Outcome);
            json.WriteEndObject();
            json.Flush();
        }
        stream.WriteByte((byte)'\n');
        stream.Flush();
    }

    private void QueueOwnedStreamDisposal()
    {
        if (!_ownsStream || Interlocked.Exchange(ref _streamDisposeQueued, 1) != 0) return;
        _ = ObserveAsync(Task.Run(() =>
        {
            try
            {
                _stream?.Dispose();
            }
            catch
            {
            }
        }));
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void WriteString(Utf8JsonWriter json, string propertyName, string? value)
    {
        if (value is not null) json.WriteString(propertyName, value);
    }

    private static void WriteNumber(Utf8JsonWriter json, string propertyName, long? value)
    {
        if (value is not null) json.WriteNumber(propertyName, value.Value);
    }

    private static void WriteNumber(Utf8JsonWriter json, string propertyName, int? value)
    {
        if (value is not null) json.WriteNumber(propertyName, value.Value);
    }

    private static void WriteDuration(Utf8JsonWriter json, string propertyName, TimeSpan? value)
    {
        if (value is not null) json.WriteNumber(propertyName, value.Value.TotalMilliseconds);
    }

    private sealed record TraceWriteRequest(
        PackageSessionStartupTraceRecord? Record,
        TaskCompletionSource? Completion);
}

internal sealed class PackageSessionStartupTraceAttempt
{
    private readonly object _syncRoot = new();
    private readonly PackageSessionStartupTraceWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly long _startedTimestamp;
    private readonly Dictionary<string, PackageSessionStartupTracePackage> _packages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageSessionStartupTracePackage> _devPackagesByFolder =
        new(PathComparer);
    private long _lastWrittenTimestamp;
    private long? _candidateGeneration;
    private bool _terminal;

    internal PackageSessionStartupTraceAttempt(
        PackageSessionStartupTraceWriter writer,
        TimeProvider timeProvider,
        long baseGeneration)
    {
        _writer = writer;
        _timeProvider = timeProvider;
        BaseGeneration = baseGeneration;
        CorrelationId = Guid.NewGuid();
        _startedAtUtc = timeProvider.GetUtcNow();
        _startedTimestamp = timeProvider.GetTimestamp();
        _lastWrittenTimestamp = _startedTimestamp;
        Started = WriteUnderLock(
            PackageSessionStartupTraceEvents.LoadStarted,
            _startedTimestamp,
            outcome: "started");
    }

    internal bool Started { get; }

    public Guid CorrelationId { get; }

    public long BaseGeneration { get; }

    public void RecordSourceDiscovery(
        IReadOnlyList<InstalledPackageRecord> installedPackages,
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays)
    {
        lock (_syncRoot)
        {
            if (_terminal) return;
            var queuedTimestamp = _timeProvider.GetTimestamp();
            foreach (var package in installedPackages)
            {
                _packages[SourceKey(PackageSourceKind.Installed, package.PackageId)] = new PackageSessionStartupTracePackage(
                    this,
                    package.PackageId,
                    package.Version,
                    PackageSourceKind.Installed,
                    queuedTimestamp);
            }
            foreach (var overlay in devOverlays)
            {
                var key = SourceKey(PackageSourceKind.Dev, overlay.PackageId);
                if (!_packages.TryGetValue(key, out var package))
                {
                    package = new PackageSessionStartupTracePackage(
                        this,
                        overlay.PackageId,
                        packageVersion: null,
                        PackageSourceKind.Dev,
                        queuedTimestamp);
                    _packages.Add(key, package);
                }
                _devPackagesByFolder[overlay.Folder] = package;
            }

            var now = _timeProvider.GetTimestamp();
            WriteUnderLock(
                PackageSessionStartupTraceEvents.SourceDiscoveryCompleted,
                now,
                outcome: "succeeded",
                activeDuration: Elapsed(_startedTimestamp, now),
                installedPackageCount: installedPackages.Count,
                devPackageCount: devOverlays.Count,
                packageCount: GetPackageCountUnderLock());
        }
    }

    public PackageSessionStartupTracePackage? GetInstalledPackage(InstalledPackageRecord package)
    {
        lock (_syncRoot)
        {
            if (_terminal) return null;
            var key = SourceKey(PackageSourceKind.Installed, package.PackageId);
            if (_packages.TryGetValue(key, out var existing)) return existing;
            var created = new PackageSessionStartupTracePackage(
                this,
                package.PackageId,
                package.Version,
                PackageSourceKind.Installed,
                _timeProvider.GetTimestamp());
            _packages.Add(key, created);
            return created;
        }
    }

    public PackageSessionStartupTracePackage? GetDevPackage(string folder)
    {
        lock (_syncRoot)
        {
            if (_terminal) return null;
            return _devPackagesByFolder.GetValueOrDefault(folder);
        }
    }

    public void BindCandidateGeneration(long candidateGeneration)
    {
        lock (_syncRoot)
        {
            if (_terminal) return;
            _candidateGeneration = candidateGeneration;
        }
    }

    public void RecordGraphPlan(
        IReadOnlyList<IReadOnlyList<PreparedRuntimePackage>> waves,
        int preparationConcurrency,
        int workerStartupConcurrency)
    {
        lock (_syncRoot)
        {
            if (_terminal) return;
            var packages = waves.SelectMany(static wave => wave).ToArray();
            WriteUnderLock(
                PackageSessionStartupTraceEvents.GraphPlanCompleted,
                _timeProvider.GetTimestamp(),
                outcome: "succeeded",
                packageCount: packages.Length,
                waveCount: waves.Count,
                dependencyCount: packages.Sum(static package => package.Dependencies.Count),
                preparationConcurrency: preparationConcurrency,
                workerStartupConcurrency: workerStartupConcurrency);
        }
    }

    public void RecordWorkerWaveStarted(
        int waveIndex,
        int waveCount,
        int packageCount,
        int workerStartupConcurrency)
        => RecordWorkerWave(
            PackageSessionStartupTraceEvents.WorkerWaveStarted,
            waveIndex,
            waveCount,
            packageCount,
            workerStartupConcurrency,
            "started");

    public void RecordWorkerWaveCompleted(
        int waveIndex,
        int waveCount,
        int packageCount,
        int workerStartupConcurrency,
        string outcome)
        => RecordWorkerWave(
            PackageSessionStartupTraceEvents.WorkerWaveCompleted,
            waveIndex,
            waveCount,
            packageCount,
            workerStartupConcurrency,
            outcome);

    public bool TryCommitGeneration(long generation, string outcome = "committed")
        => TryComplete(PackageSessionStartupTraceEvents.GenerationCommitted, generation, outcome);

    public bool TryFail(string outcome = "failed")
        => TryComplete(PackageSessionStartupTraceEvents.LoadFailed, candidateGeneration: null, outcome);

    internal long GetTimestamp() => _timeProvider.GetTimestamp();

    internal TimeSpan Elapsed(long start, long end) => _timeProvider.GetElapsedTime(start, end);

    internal void WritePackageEvent(
        PackageSessionStartupTracePackage package,
        string eventName,
        long timestamp,
        string outcome,
        TimeSpan? queueDuration,
        TimeSpan? activeDuration,
        int? processId = null,
        DateTimeOffset? processStartedAtUtc = null,
        long? processStartTimestamp = null,
        long? processReadyTimestamp = null,
        string? executionRuntimeId = null,
        string? executionRuntimeVendorVersion = null,
        string? executionRuntimeVendorChecksumDigest = null)
    {
        lock (_syncRoot)
        {
            if (_terminal) return;
            var identity = package.GetIdentity();
            var counters = package.GetCounters();
            var cacheResult = package.GetProjectionCacheResult();
            WriteUnderLock(
                eventName,
                timestamp,
                outcome,
                identity.PackageId,
                identity.PackageVersion,
                SourceKindText(identity.SourceKind),
                identity.TargetRole,
                identity.RuntimeIdentifier,
                identity.ActivationId,
                processId,
                processStartedAtUtc,
                processStartTimestamp is null ? null : Elapsed(_startedTimestamp, processStartTimestamp.Value),
                processReadyTimestamp is null ? null : Elapsed(_startedTimestamp, processReadyTimestamp.Value),
                queueDuration,
                activeDuration,
                counters.BytesRead,
                counters.BytesHashed,
                counters.BytesCopied,
                projectionCacheResult: cacheResult,
                executionRuntimeId: executionRuntimeId,
                executionRuntimeVendorVersion: executionRuntimeVendorVersion,
                executionRuntimeVendorChecksumDigest: executionRuntimeVendorChecksumDigest);
        }
    }

    private void RecordWorkerWave(
        string eventName,
        int waveIndex,
        int waveCount,
        int packageCount,
        int workerStartupConcurrency,
        string outcome)
    {
        lock (_syncRoot)
        {
            if (_terminal) return;
            WriteUnderLock(
                eventName,
                _timeProvider.GetTimestamp(),
                outcome,
                waveIndex: waveIndex + 1,
                waveCount: waveCount,
                wavePackageCount: packageCount,
                workerStartupConcurrency: workerStartupConcurrency);
        }
    }

    private bool TryComplete(string eventName, long? candidateGeneration, string outcome)
    {
        lock (_syncRoot)
        {
            if (_terminal) return false;
            if (candidateGeneration is not null) _candidateGeneration = candidateGeneration;
            var now = _timeProvider.GetTimestamp();
            var counters = _packages.Values
                .Select(static package => package.GetCounters())
                .Aggregate(
                    new PackageSessionIoCounters(),
                    static (total, item) => new PackageSessionIoCounters(
                        total.BytesRead + item.BytesRead,
                        total.BytesHashed + item.BytesHashed,
                        total.BytesCopied + item.BytesCopied));
            var accepted = WriteUnderLock(
                eventName,
                now,
                outcome,
                activeDuration: Elapsed(_startedTimestamp, now),
                bytesRead: counters.BytesRead,
                bytesHashed: counters.BytesHashed,
                bytesCopied: counters.BytesCopied,
                packageCount: GetPackageCountUnderLock(),
                terminal: true);
            _terminal = true;
            return accepted;
        }
    }

    private int GetPackageCountUnderLock()
        => _packages.Values
            .Select(static package => package.GetIdentity().PackageId)
            .Where(static packageId => packageId is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static string SourceKey(PackageSourceKind sourceKind, string packageId)
        => SourceKindText(sourceKind) + "\0" + packageId;

    private bool WriteUnderLock(
        string eventName,
        long timestamp,
        string outcome,
        string? packageId = null,
        string? packageVersion = null,
        string? sourceKind = null,
        string? targetRole = null,
        string? runtimeIdentifier = null,
        Guid? activationId = null,
        int? processId = null,
        DateTimeOffset? processStartedAtUtc = null,
        TimeSpan? processStartElapsed = null,
        TimeSpan? processReadyElapsed = null,
        TimeSpan? queueDuration = null,
        TimeSpan? activeDuration = null,
        long? bytesRead = null,
        long? bytesHashed = null,
        long? bytesCopied = null,
        string? projectionCacheResult = null,
        string? executionRuntimeId = null,
        string? executionRuntimeVendorVersion = null,
        string? executionRuntimeVendorChecksumDigest = null,
        int? waveIndex = null,
        int? waveCount = null,
        int? wavePackageCount = null,
        int? dependencyCount = null,
        int? preparationConcurrency = null,
        int? workerStartupConcurrency = null,
        int? installedPackageCount = null,
        int? devPackageCount = null,
        int? packageCount = null,
        bool terminal = false)
    {
        timestamp = Math.Max(timestamp, _lastWrittenTimestamp);
        _lastWrittenTimestamp = timestamp;
        var elapsed = Elapsed(_startedTimestamp, timestamp);
        var record = new PackageSessionStartupTraceRecord(
            CorrelationId,
            eventName,
            _startedAtUtc + elapsed,
            elapsed,
            BaseGeneration,
            _candidateGeneration,
            packageId,
            packageVersion,
            sourceKind,
            targetRole,
            runtimeIdentifier,
            activationId,
            processId,
            processStartedAtUtc,
            processStartElapsed,
            processReadyElapsed,
            queueDuration,
            activeDuration,
            bytesRead,
            bytesHashed,
            bytesCopied,
            projectionCacheResult,
            executionRuntimeId,
            executionRuntimeVendorVersion,
            executionRuntimeVendorChecksumDigest,
            waveIndex,
            waveCount,
            wavePackageCount,
            dependencyCount,
            preparationConcurrency,
            workerStartupConcurrency,
            installedPackageCount,
            devPackageCount,
            packageCount,
            outcome);
        return terminal
            ? _writer.WriteTerminal(this, record)
            : _writer.Write(record);
    }

    private static string SourceKindText(PackageSourceKind sourceKind)
        => sourceKind == PackageSourceKind.Dev ? "dev" : "installed";

    private static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed class PackageSessionStartupTracePackage : ISunderPackageValidationIoObserver
{
    private readonly object _syncRoot = new();
    private readonly PackageSessionStartupTraceAttempt _attempt;
    private readonly long _queuedTimestamp;
    private string? _packageId;
    private string? _packageVersion;
    private readonly PackageSourceKind _sourceKind;
    private string? _targetRole;
    private string? _runtimeIdentifier;
    private Guid? _activationId;
    private long _bytesRead;
    private long _bytesHashed;
    private long _bytesCopied;
    private string? _projectionCacheResult;
    private long? _projectionStartedTimestamp;
    private long? _projectionCacheStartedTimestamp;
    private bool _projectionCacheCompleted;
    private bool _projectionCompleted;
    private long? _projectionCompletedTimestamp;
    private long? _runtimeBindingStartedTimestamp;
    private bool _runtimeBindingCompleted;
    private long? _processStartRequestedTimestamp;
    private long? _processStartedTimestamp;
    private int? _processId;
    private DateTimeOffset? _processStartedAtUtc;
    private bool _workerReadyCompleted;
    private long? _workerReadyTimestamp;
    private long? _candidateStartedTimestamp;
    private bool _candidateCompleted;

    internal PackageSessionStartupTracePackage(
        PackageSessionStartupTraceAttempt attempt,
        string? packageId,
        string? packageVersion,
        PackageSourceKind sourceKind,
        long queuedTimestamp)
    {
        _attempt = attempt;
        _packageId = packageId;
        _packageVersion = packageVersion;
        _sourceKind = sourceKind;
        _queuedTimestamp = queuedTimestamp;
    }

    public void SetPreparedIdentity(PreparedRuntimePackage package)
    {
        lock (_syncRoot)
        {
            _packageId = package.PackageId;
            _packageVersion = package.Version;
            _targetRole = package.SelectedTargetKey?.Role;
            _runtimeIdentifier = package.SelectedTargetKey?.Rid;
        }
    }

    public void BindActivation(Guid activationId)
    {
        lock (_syncRoot) _activationId = activationId;
    }

    public void BeginProjection()
    {
        lock (_syncRoot) _projectionStartedTimestamp ??= _attempt.GetTimestamp();
    }

    public void CompleteProjection(string outcome)
    {
        long completed;
        long started;
        lock (_syncRoot)
        {
            if (_projectionCompleted) return;
            _projectionCompleted = true;
            completed = _attempt.GetTimestamp();
            _projectionCompletedTimestamp = completed;
            started = _projectionStartedTimestamp ?? completed;
        }
        _attempt.WritePackageEvent(
            this,
            PackageSessionStartupTraceEvents.ProjectionPrepared,
            completed,
            outcome,
            _attempt.Elapsed(_queuedTimestamp, started),
            _attempt.Elapsed(started, completed));
    }

    public void BeginProjectionCache()
    {
        lock (_syncRoot) _projectionCacheStartedTimestamp ??= _attempt.GetTimestamp();
    }

    public void CompleteProjectionCache(string result, string outcome = "succeeded")
    {
        SetProjectionCacheResult(result);
        long completed;
        long started;
        long queued;
        lock (_syncRoot)
        {
            if (_projectionCacheCompleted) return;
            _projectionCacheCompleted = true;
            completed = _attempt.GetTimestamp();
            started = _projectionCacheStartedTimestamp ?? completed;
            queued = _projectionStartedTimestamp ?? started;
        }
        _attempt.WritePackageEvent(
            this,
            PackageSessionStartupTraceEvents.ProjectionCacheCompleted,
            completed,
            outcome,
            _attempt.Elapsed(queued, started),
            _attempt.Elapsed(started, completed));
    }

    public void BeginRuntimeBinding()
    {
        lock (_syncRoot) _runtimeBindingStartedTimestamp ??= _attempt.GetTimestamp();
    }

    public void CompleteRuntimeBinding(
        RuntimePackageExecutionRuntimeBinding? binding,
        string outcome)
    {
        long completed;
        long started;
        long queued;
        lock (_syncRoot)
        {
            if (_runtimeBindingCompleted) return;
            _runtimeBindingCompleted = true;
            completed = _attempt.GetTimestamp();
            started = _runtimeBindingStartedTimestamp ?? completed;
            queued = _projectionCompletedTimestamp ?? started;
        }
        _attempt.WritePackageEvent(
            this,
            PackageSessionStartupTraceEvents.RuntimeBindingCompleted,
            completed,
            outcome,
            _attempt.Elapsed(queued, started),
            _attempt.Elapsed(started, completed),
            executionRuntimeId: binding?.Runtime.RuntimeId,
            executionRuntimeVendorVersion: binding?.Runtime.VendorVersion,
            executionRuntimeVendorChecksumDigest: binding?.Runtime.VendorChecksumDigest);
    }

    public void RecordProcessStartRequested()
    {
        lock (_syncRoot) _processStartRequestedTimestamp ??= _attempt.GetTimestamp();
    }

    public void RecordProcessStarted(int processId, DateTimeOffset? processStartedAtUtc = null)
    {
        long started;
        long requested;
        long queued;
        lock (_syncRoot)
        {
            started = _attempt.GetTimestamp();
            _processStartedTimestamp = started;
            _processId = processId;
            _processStartedAtUtc = processStartedAtUtc;
            requested = _processStartRequestedTimestamp ?? started;
            queued = _runtimeBindingStartedTimestamp ?? _projectionCompletedTimestamp ?? requested;
        }
        _attempt.WritePackageEvent(
            this,
            PackageSessionStartupTraceEvents.WorkerProcessStarted,
            started,
            "succeeded",
            _attempt.Elapsed(queued, requested),
            _attempt.Elapsed(requested, started),
            processId,
            processStartedAtUtc,
            started);
    }

    public void CompleteWorkerReady(string outcome)
    {
        long completed;
        long started;
        long queued;
        long? processStarted;
        int? processId;
        DateTimeOffset? processStartedAtUtc;
        lock (_syncRoot)
        {
            if (_workerReadyCompleted) return;
            _workerReadyCompleted = true;
            completed = _attempt.GetTimestamp();
            _workerReadyTimestamp = completed;
            started = _processStartRequestedTimestamp ?? completed;
            queued = _projectionCompletedTimestamp ?? started;
            processStarted = _processStartedTimestamp;
            processId = _processId;
            processStartedAtUtc = _processStartedAtUtc;
        }
        _attempt.WritePackageEvent(
            this,
            PackageSessionStartupTraceEvents.WorkerReady,
            completed,
            outcome,
            _attempt.Elapsed(queued, started),
            _attempt.Elapsed(started, completed),
            processId,
            processStartedAtUtc,
            processStarted,
            completed);
    }

    public void BeginCandidate()
    {
        lock (_syncRoot) _candidateStartedTimestamp ??= _attempt.GetTimestamp();
    }

    public void CompleteCandidate(string outcome)
    {
        long completed;
        long started;
        long queued;
        lock (_syncRoot)
        {
            if (_candidateCompleted || _candidateStartedTimestamp is null) return;
            _candidateCompleted = true;
            completed = _attempt.GetTimestamp();
            started = _candidateStartedTimestamp.Value;
            queued = _workerReadyTimestamp ?? started;
        }
        _attempt.WritePackageEvent(
            this,
            PackageSessionStartupTraceEvents.CandidateReady,
            completed,
            outcome,
            _attempt.Elapsed(queued, started),
            _attempt.Elapsed(started, completed));
    }

    public void AddIo(long bytesRead = 0, long bytesHashed = 0, long bytesCopied = 0)
    {
        if (bytesRead > 0) Interlocked.Add(ref _bytesRead, bytesRead);
        if (bytesHashed > 0) Interlocked.Add(ref _bytesHashed, bytesHashed);
        if (bytesCopied > 0) Interlocked.Add(ref _bytesCopied, bytesCopied);
    }

    public void SetProjectionCacheResult(string result)
    {
        if (result is not ("hit" or "miss" or "corrupt-rebuild"))
        {
            throw new ArgumentException("Unknown package projection cache result.", nameof(result));
        }
        lock (_syncRoot) _projectionCacheResult = result;
    }

    void ISunderPackageValidationIoObserver.Record(long bytesRead, long bytesHashed)
        => AddIo(bytesRead, bytesHashed);

    public void AddFileIo(
        string path,
        bool read = false,
        bool hashed = false,
        bool copied = false)
    {
        try
        {
            var length = new FileInfo(path).Length;
            AddIo(
                read ? length : 0,
                hashed ? length : 0,
                copied ? length : 0);
        }
        catch
        {
            // Instrumentation never changes package validation or preparation outcomes.
        }
    }

    internal PackageSessionTracePackageIdentity GetIdentity()
    {
        lock (_syncRoot)
        {
            return new PackageSessionTracePackageIdentity(
                _packageId,
                _packageVersion,
                _sourceKind,
                _targetRole,
                _runtimeIdentifier,
                _activationId);
        }
    }

    internal PackageSessionIoCounters GetCounters()
        => new(
            Interlocked.Read(ref _bytesRead),
            Interlocked.Read(ref _bytesHashed),
            Interlocked.Read(ref _bytesCopied));

    internal string? GetProjectionCacheResult()
    {
        lock (_syncRoot) return _projectionCacheResult;
    }
}

internal readonly record struct PackageSessionIoCounters(
    long BytesRead = 0,
    long BytesHashed = 0,
    long BytesCopied = 0);

internal readonly record struct PackageSessionTracePackageIdentity(
    string? PackageId,
    string? PackageVersion,
    PackageSourceKind SourceKind,
    string? TargetRole,
    string? RuntimeIdentifier,
    Guid? ActivationId);

internal sealed record PackageSessionStartupTraceRecord(
    Guid CorrelationId,
    string EventName,
    DateTimeOffset TimestampUtc,
    TimeSpan Elapsed,
    long BaseGeneration,
    long? CandidateGeneration,
    string? PackageId,
    string? PackageVersion,
    string? SourceKind,
    string? TargetRole,
    string? RuntimeIdentifier,
    Guid? ActivationId,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAtUtc,
    TimeSpan? ProcessStartElapsed,
    TimeSpan? ProcessReadyElapsed,
    TimeSpan? QueueDuration,
    TimeSpan? ActiveDuration,
    long? BytesRead,
    long? BytesHashed,
    long? BytesCopied,
    string? ProjectionCacheResult,
    string? ExecutionRuntimeId,
    string? ExecutionRuntimeVendorVersion,
    string? ExecutionRuntimeVendorChecksumDigest,
    int? WaveIndex,
    int? WaveCount,
    int? WavePackageCount,
    int? DependencyCount,
    int? PreparationConcurrency,
    int? WorkerStartupConcurrency,
    int? InstalledPackageCount,
    int? DevPackageCount,
    int? PackageCount,
    string Outcome);
