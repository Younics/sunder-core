using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeSessionOwner
{
    private readonly object _snapshotGate = new();
    private readonly ILogger<RuntimeSessionOwner> _logger;
    private readonly RuntimeEventStreamService _events;
    private readonly PackageUiSnapshotStore? _uiSnapshots;
    private readonly ConcurrentDictionary<string, long> _stageGenerations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _stageBaseGenerations = new(StringComparer.Ordinal);
    private readonly object _stageStatusGate = new();
    private readonly Dictionary<string, RuntimePackageStageStatus> _stageStatuses = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeLifecyclePolicyOptions _lifecyclePolicy;
    private readonly TaskCompletionSource _bootstrapCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RuntimePackageSnapshot _snapshot;

    public RuntimeSessionOwner(
        ILogger<RuntimeSessionOwner> logger,
        RuntimeEventStreamService events,
        RuntimeAuthPolicyOptions? authPolicy = null,
        RuntimePackageOperationPolicyOptions? packageOperationPolicy = null,
        TimeProvider? timeProvider = null,
        IHostApplicationLifetime? hostLifetime = null,
        PackageUiSnapshotStore? uiSnapshots = null,
        RuntimeLifecyclePolicyOptions? lifecyclePolicy = null)
    {
        _logger = logger;
        _events = events;
        _uiSnapshots = uiSnapshots;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifecyclePolicy = lifecyclePolicy ?? new RuntimeLifecyclePolicyOptions();
        _snapshot = new RuntimePackageSnapshot(
            events.RuntimeInstanceId,
            0,
            0,
            RuntimeBootstrapState.Starting,
            [],
            [],
            [],
            [],
            []);
        PackageCallbackSessionCoordinator? callbacks = null;
        State = new PackageSessionState(
            logger,
            () => callbacks?.Clear(),
            packageId => callbacks?.RemovePackageSessions(packageId),
            packageOperationPolicy?.SessionDrainTimeout);
        Callbacks = callbacks = new PackageCallbackSessionCoordinator(
            State,
            authPolicy,
            timeProvider,
            hostLifetime?.ApplicationStopping ?? CancellationToken.None);
        Auth = new PackageAuthSessionCoordinator(
            State,
            Callbacks,
            (packageId, generation, origin, exception, action) =>
                HandlePackageFault(packageId, generation, origin, exception, action));
        hostLifetime?.ApplicationStopping.Register(MarkShuttingDown);
    }

    public PackageSessionState State { get; }

    public PackageSessionSourceState Sources { get; } = new();

    public PackageAuthSessionCoordinator Auth { get; }

    public PackageCallbackSessionCoordinator Callbacks { get; }

    public long Generation => State.Generation;

    public RuntimePackageStamp Stamp => new(_events.RuntimeInstanceId, Generation);

    public RuntimePackageSnapshot GetSnapshot()
        => _events.Capture(sequenceId =>
        {
            lock (_snapshotGate)
            {
                return _snapshot with { EventSequence = sequenceId };
            }
        });

    public Task WaitForBootstrapAsync(CancellationToken cancellationToken = default)
        => _bootstrapCompletion.Task.WaitAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> PublishAsync(
        ActivePackageSession session,
        PackageSessionSourceSnapshot sources,
        IReadOnlyList<PackageUiSnapshotDescriptor> uiSnapshots,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        var prepared = await State.PreparePublicationAsync(session, expectedGeneration, cancellationToken);
        var publication = await CommitPublicationAsync(prepared, sources, uiSnapshots, warnings, errors);
        return publication.Warnings;
    }

    internal Task<PackageSessionState.SessionPublication> PreparePublicationAsync(
        ActivePackageSession session,
        long expectedGeneration,
        CancellationToken cancellationToken)
        => State.PreparePublicationAsync(session, expectedGeneration, cancellationToken);

    internal async Task<(RuntimePackageStamp Stamp, IReadOnlyList<string> Warnings)> CommitPublicationAsync(
        PackageSessionState.SessionPublication publication,
        PackageSessionSourceSnapshot sources,
        IReadOnlyList<PackageUiSnapshotDescriptor> uiSnapshots,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
    {
        var result = await State.CommitPublicationAsync(
            publication,
            generation => CommitGeneration(
                generation,
                publication.Session.GetActivePackages(),
                publication.Session.GetSessionPackages(),
                uiSnapshots,
                sources,
                warnings,
                errors));
        if (result.Warnings.Count > 0)
        {
            AppendWarnings(result.Generation, result.Warnings);
        }
        return (new RuntimePackageStamp(_events.RuntimeInstanceId, result.Generation), result.Warnings);
    }

    internal void DiscardPublication(PackageSessionState.SessionPublication publication)
        => State.DiscardPublication(publication);

    public bool ReportPackageFault(string packageId, ReportPackageFaultRequest request)
        => CommitPackageFault(
            packageId,
            request.GenerationId,
            request.Message,
            committed => State.ReportPackageFault(packageId, request, committed));

    public void MarkReady(IReadOnlyList<string>? warnings = null, IReadOnlyList<string>? errors = null)
        => SetBootstrapState(RuntimeBootstrapState.Ready, warnings, errors, message: null);

    public void MarkFailed(string message, IReadOnlyList<string>? warnings = null, IReadOnlyList<string>? errors = null)
        => SetBootstrapState(
            RuntimeBootstrapState.Failed,
            warnings,
            errors is { Count: > 0 } ? errors : [message],
            message);

    public void MarkShuttingDown()
        => SetBootstrapState(RuntimeBootstrapState.ShuttingDown, warnings: null, errors: null, message: null);

    public void RegisterStage(
        string stageId,
        long generation,
        long baseGeneration,
        RuntimePackageStageKind kind,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        _stageGenerations[stageId] = generation;
        _stageBaseGenerations[stageId] = baseGeneration;
        UpdateStageStatus(new RuntimePackageStageStatus(
            stageId,
            kind,
            RuntimePackageStageState.Pending,
            createdAtUtc,
            null,
            RuntimeSessionApplied: false,
            ReconciliationPending: false,
            Message: null)
        {
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
        });
    }

    public bool TryGetStageGeneration(string stageId, out long generation)
        => _stageGenerations.TryGetValue(stageId, out generation);

    public void RemoveStage(string stageId)
    {
        _stageGenerations.TryRemove(stageId, out _);
        _stageBaseGenerations.TryRemove(stageId, out _);
    }

    public void ClearStages()
    {
        _stageGenerations.Clear();
        _stageBaseGenerations.Clear();
    }

    public RuntimePackageStageStatus? GetStageStatus(string stageId)
    {
        lock (_stageStatusGate)
        {
            PruneStageStatusesLocked();
            return _stageStatuses.GetValueOrDefault(stageId);
        }
    }

    public void MarkStageCommitting(string stageId)
        => TransitionStage(stageId, RuntimePackageStageState.Committing);

    public void MarkStageCommitted(
        string stageId,
        RuntimePackageStamp stamp,
        bool runtimeSessionApplied,
        bool reconciliationPending,
        string? message)
        => TransitionStage(
            stageId,
            RuntimePackageStageState.Committed,
            stamp,
            runtimeSessionApplied,
            reconciliationPending,
            message);

    public void MarkStageFailed(string stageId, string? message)
        => TransitionStage(stageId, RuntimePackageStageState.Failed, message: message);

    public void MarkStageDiscarded(string stageId)
        => TransitionStage(stageId, RuntimePackageStageState.Discarded);

    private bool HandlePackageFault(
        string packageId,
        long generation,
        PackageFailureOrigin origin,
        Exception exception,
        string action)
        => CommitPackageFault(
            packageId,
            generation,
            exception.Message,
            committed => State.HandlePackageFault(packageId, generation, origin, exception, action, committed));

    private bool CommitPackageFault(
        string packageId,
        long generation,
        string message,
        Func<Action<long>, bool> apply)
    {
        var current = GetSnapshot();
        if (generation != current.SessionGeneration)
        {
            return false;
        }

        var nextGeneration = checked(generation + 1);
        var warnings = current.Warnings;
        IReadOnlyList<PackageUiSnapshotDescriptor> snapshots;
        try
        {
            snapshots = _uiSnapshots?.CreateSnapshots(
                State.GetActivePackageSources()
                    .Where(source => !string.Equals(source.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                nextGeneration) ?? [];
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to prepare package UI snapshots while faulting package {PackageId}", packageId);
            warnings = current.Warnings
                .Append($"Package UI snapshots could not be prepared after '{packageId}' failed: {exception.Message}")
                .ToArray();
            snapshots = [];
        }

        var sources = Sources.Snapshot();
        var errors = current.Errors
            .Append($"Package '{packageId}' failed: {message}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var applied = apply(committedGeneration => CommitGeneration(
            committedGeneration,
            State.GetActivePackages(),
            State.GetSessionPackages(),
            snapshots,
            sources,
            warnings,
            errors));
        if (!applied)
        {
            _uiSnapshots?.RemoveSnapshots(snapshots);
            return false;
        }

        return true;
    }

    private void CommitGeneration(
        long generation,
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<SessionPackageDescriptor> sessionPackages,
        IReadOnlyList<PackageUiSnapshotDescriptor> uiSnapshots,
        PackageSessionSourceSnapshot sources,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
    {
        _events.PublishSessionGeneration(
            generation,
            activePackages.Select(package => package.PackageId).ToArray(),
            runtimeEvent =>
            {
                Sources.Replace(sources);
                lock (_snapshotGate)
                {
                    _snapshot = new RuntimePackageSnapshot(
                        runtimeEvent.RuntimeInstanceId,
                        generation,
                        runtimeEvent.SequenceId,
                        _snapshot.BootstrapState,
                        activePackages.ToArray(),
                        sessionPackages.ToArray(),
                        uiSnapshots.ToArray(),
                        NormalizeDiagnostics(warnings),
                        NormalizeDiagnostics(errors));
                    _uiSnapshots?.RemoveOlderGenerations(generation);
                }
            });
        InvalidateStaleStages(generation);
    }

    private void SetBootstrapState(
        RuntimeBootstrapState state,
        IReadOnlyList<string>? warnings,
        IReadOnlyList<string>? errors,
        string? message)
    {
        var current = GetSnapshot();
        if (current.BootstrapState == state
            || current.BootstrapState == RuntimeBootstrapState.ShuttingDown)
        {
            return;
        }

        _events.PublishBootstrapState(
            state,
            current.SessionGeneration,
            current.ActivePackages.Select(package => package.PackageId).ToArray(),
            message,
            runtimeEvent =>
            {
                lock (_snapshotGate)
                {
                    _snapshot = new RuntimePackageSnapshot(
                        _snapshot.RuntimeInstanceId,
                        _snapshot.SessionGeneration,
                        runtimeEvent.SequenceId,
                        state,
                        _snapshot.ActivePackages,
                        _snapshot.SessionPackages,
                        _snapshot.PackageUiSnapshots,
                        NormalizeDiagnostics(_snapshot.Warnings.Concat(warnings ?? [])),
                        NormalizeDiagnostics(_snapshot.Errors.Concat(errors ?? [])));
                }
            });
        if (state != RuntimeBootstrapState.Starting)
        {
            _bootstrapCompletion.TrySetResult();
        }
    }

    private void AppendWarnings(long generation, IReadOnlyList<string> warnings)
    {
        var current = GetSnapshot();
        if (current.SessionGeneration != generation)
        {
            return;
        }

        _events.PublishSnapshotDiagnostics(
            generation,
            current.ActivePackages.Select(package => package.PackageId).ToArray(),
            runtimeEvent =>
            {
                lock (_snapshotGate)
                {
                    if (_snapshot.SessionGeneration == generation)
                    {
                        _snapshot = new RuntimePackageSnapshot(
                            _snapshot.RuntimeInstanceId,
                            _snapshot.SessionGeneration,
                            runtimeEvent.SequenceId,
                            _snapshot.BootstrapState,
                            _snapshot.ActivePackages,
                            _snapshot.SessionPackages,
                            _snapshot.PackageUiSnapshots,
                            NormalizeDiagnostics(_snapshot.Warnings.Concat(warnings)),
                            _snapshot.Errors);
                    }
                }
            });
    }

    private void TransitionStage(
        string stageId,
        RuntimePackageStageState state,
        RuntimePackageStamp? committedStamp = null,
        bool runtimeSessionApplied = false,
        bool reconciliationPending = false,
        string? message = null)
    {
        lock (_stageStatusGate)
        {
            PruneStageStatusesLocked();
            if (!_stageStatuses.TryGetValue(stageId, out var current))
            {
                return;
            }

            _stageStatuses[stageId] = current with
            {
                State = state,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                CommittedStamp = committedStamp,
                RuntimeSessionApplied = runtimeSessionApplied,
                ReconciliationPending = reconciliationPending,
                Message = message,
            };
            if (IsTerminal(state)) RemoveStage(stageId);
            PruneStageStatusesLocked();
        }
    }

    private void UpdateStageStatus(RuntimePackageStageStatus status)
    {
        lock (_stageStatusGate)
        {
            PruneStageStatusesLocked();
            _stageStatuses[status.StageId] = status;
        }
    }

    private void PruneStageStatusesLocked()
    {
        var cutoff = _timeProvider.GetUtcNow() - _lifecyclePolicy.TerminalStageRetention;
        foreach (var stageId in _stageStatuses
                     .Where(pair => IsTerminal(pair.Value.State) && pair.Value.UpdatedAtUtc < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _stageStatuses.Remove(stageId);
            RemoveStage(stageId);
        }

        foreach (var stageId in _stageStatuses.Values
                     .Where(status => IsTerminal(status.State))
                     .OrderByDescending(status => status.UpdatedAtUtc)
                     .Skip(_lifecyclePolicy.MaximumTerminalStageStatuses)
                     .Select(status => status.StageId)
                     .ToArray())
        {
            _stageStatuses.Remove(stageId);
            RemoveStage(stageId);
        }
    }

    private static bool IsTerminal(RuntimePackageStageState state)
        => state is RuntimePackageStageState.Committed
            or RuntimePackageStageState.Discarded
            or RuntimePackageStageState.Failed;

    private void InvalidateStaleStages(long generation)
    {
        string[] staleStageIds;
        lock (_stageStatusGate)
        {
            var now = _timeProvider.GetUtcNow();
            staleStageIds = _stageBaseGenerations
                .Where(pair => pair.Value != generation
                               && _stageStatuses.TryGetValue(pair.Key, out var status)
                               && status.State == RuntimePackageStageState.Pending)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var stageId in staleStageIds)
            {
                if (_stageStatuses.TryGetValue(stageId, out var status))
                {
                    _stageStatuses[stageId] = status with
                    {
                        State = RuntimePackageStageState.Failed,
                        UpdatedAtUtc = now,
                        Message = "The package stage became stale because the active package session changed.",
                    };
                }
                RemoveStage(stageId);
            }
            PruneStageStatusesLocked();
        }

        foreach (var stageId in staleStageIds)
        {
            _uiSnapshots?.RemoveStage(stageId);
        }
    }

    private static IReadOnlyList<string> NormalizeDiagnostics(IEnumerable<string> messages)
        => messages
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

internal sealed class RuntimeSnapshotService(RuntimeSessionOwner sessions)
{
    public RuntimePackageSnapshot GetSnapshot() => sessions.GetSnapshot();

    public RuntimePackageStageStatus? GetStageStatus(string stageId) => sessions.GetStageStatus(stageId);
}
