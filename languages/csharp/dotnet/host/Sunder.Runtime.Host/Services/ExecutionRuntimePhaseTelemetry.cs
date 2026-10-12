using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sunder.Runtime.Host.Services;

internal static class ExecutionRuntimePhaseNames
{
    public const string Plan = "runtime.plan";
    public const string Download = "runtime.download";
    public const string Inspect = "runtime.inspect";
    public const string Extract = "runtime.extract";
    public const string PublishRoot = "runtime.publish-root";
    public const string Bind = "runtime.bind";
    public const string HealthCheck = "runtime.health-check";
    public const string LaunchResolve = "runtime.launch-resolve";
    public const string ProcessStart = "runtime.process-start";
    public const string Lease = "runtime.lease";
    public const string GarbageCollection = "runtime.gc";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Plan,
        Download,
        Inspect,
        Extract,
        PublishRoot,
        Bind,
        HealthCheck,
        LaunchResolve,
        ProcessStart,
        Lease,
        GarbageCollection,
    };
}

internal sealed class ExecutionRuntimePhaseTelemetry
{
    private static readonly AsyncLocal<OperationContext?> CurrentOperation = new();
    private readonly ILogger _logger;

    public ExecutionRuntimePhaseTelemetry(ILogger<ExecutionRuntimePhaseTelemetry> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private ExecutionRuntimePhaseTelemetry()
        : this(NullLogger<ExecutionRuntimePhaseTelemetry>.Instance)
    {
    }

    public static ExecutionRuntimePhaseTelemetry Disabled { get; } = new();

    public Guid CurrentCorrelationId => CurrentOperation.Value?.CorrelationId ?? Guid.Empty;

    public OperationScope BeginOperation(Guid? correlationId = null)
    {
        var previous = CurrentOperation.Value;
        var resolved = correlationId is { } supplied && supplied != Guid.Empty
            ? supplied
            : previous?.CorrelationId ?? Guid.NewGuid();
        CurrentOperation.Value = new OperationContext(resolved);
        return new OperationScope(previous);
    }

    public PhaseScope BeginPhase(
        string phase,
        ExecutionRuntimeIdentity? identity = null,
        long? bytes = null,
        bool? cacheHit = null,
        Guid? correlationId = null)
    {
        if (!ExecutionRuntimePhaseNames.All.Contains(phase))
        {
            throw new ArgumentException($"Unknown execution-runtime telemetry phase '{phase}'.", nameof(phase));
        }
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        var previous = CurrentOperation.Value;
        var resolved = correlationId is { } supplied && supplied != Guid.Empty
            ? supplied
            : previous?.CorrelationId ?? Guid.NewGuid();
        CurrentOperation.Value = new OperationContext(resolved);
        return new PhaseScope(this, previous, phase, resolved, identity, bytes, cacheHit);
    }

    private void Write(PhaseScope scope)
    {
        try
        {
            _logger.Log(
                scope.Outcome == "succeeded" ? LogLevel.Information : LogLevel.Warning,
                new EventId(1701, "ExecutionRuntimePhase"),
                "Execution runtime phase {RuntimePhase} finished with outcome {Outcome} in {DurationMs} ms; correlation {CorrelationId}; runtime {RuntimeId} {RuntimeVendorVersion} source {RuntimeSourceId} checksum {RuntimeVendorChecksumAlgorithm}:{RuntimeVendorChecksumDigest} RID {RuntimeRid} ABI {RuntimeModuleAbi}; bytes {Bytes}; cache hit {CacheHit}; failure {FailureType}",
                scope.Phase,
                scope.Outcome,
                Stopwatch.GetElapsedTime(scope.StartedTimestamp).TotalMilliseconds,
                scope.CorrelationId,
                scope.Identity?.RuntimeId,
                scope.Identity?.VendorVersion,
                scope.Identity?.SourceId.ToString().ToLowerInvariant(),
                scope.Identity?.VendorChecksumAlgorithm.ToString().ToLowerInvariant(),
                scope.Identity?.VendorChecksumDigest,
                scope.Identity?.Rid,
                scope.Identity?.ModuleAbi,
                scope.Bytes,
                scope.CacheHit,
                scope.FailureType);
        }
        catch
        {
            // Operational telemetry must never change runtime behavior.
        }
    }

    internal sealed record OperationContext(Guid CorrelationId);

    internal sealed class OperationScope(OperationContext? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CurrentOperation.Value = previous;
            }
        }
    }

    internal sealed class PhaseScope : IDisposable
    {
        private readonly ExecutionRuntimePhaseTelemetry _owner;
        private readonly OperationContext? _previous;
        private int _disposed;

        internal PhaseScope(
            ExecutionRuntimePhaseTelemetry owner,
            OperationContext? previous,
            string phase,
            Guid correlationId,
            ExecutionRuntimeIdentity? identity,
            long? bytes,
            bool? cacheHit)
        {
            _owner = owner;
            _previous = previous;
            Phase = phase;
            CorrelationId = correlationId;
            Identity = identity;
            Bytes = bytes;
            CacheHit = cacheHit;
            StartedTimestamp = Stopwatch.GetTimestamp();
        }

        public string Phase { get; }
        public Guid CorrelationId { get; }
        public long StartedTimestamp { get; }
        public ExecutionRuntimeIdentity? Identity { get; private set; }
        public long? Bytes { get; private set; }
        public bool? CacheHit { get; private set; }
        public string Outcome { get; private set; } = "failed";
        public string? FailureType { get; private set; } = "UnreportedFailure";

        public void SetIdentity(ExecutionRuntimeIdentity identity)
            => Identity = identity ?? throw new ArgumentNullException(nameof(identity));

        public void SetBytes(long bytes)
        {
            if (bytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }
            Bytes = bytes;
        }

        public void SetCacheHit(bool cacheHit) => CacheHit = cacheHit;

        public void Succeeded()
        {
            Outcome = "succeeded";
            FailureType = null;
        }

        public void Failed(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            Outcome = "failed";
            FailureType = exception.GetType().Name;
        }

        public void Failed(string failureType)
        {
            Outcome = "failed";
            FailureType = string.IsNullOrWhiteSpace(failureType) ? "UnknownFailure" : failureType;
        }

        public void Canceled()
        {
            Outcome = "canceled";
            FailureType = nameof(OperationCanceledException);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            try
            {
                _owner.Write(this);
            }
            finally
            {
                CurrentOperation.Value = _previous;
            }
        }
    }
}
