using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeSessionOwner
{
    private readonly object _snapshotGate = new();
    private readonly object _snapshotPublicationGate = new();
    private readonly object _packageFaultGate = new();
    private readonly ILogger<RuntimeSessionOwner> _logger;
    private readonly RuntimeEventStreamService _events;
    private readonly PackageUiSnapshotStore? _uiSnapshots;
    private readonly ConcurrentDictionary<string, long> _stageGenerations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _stageBaseGenerations = new(StringComparer.Ordinal);
    private readonly object _stageStatusGate = new();
    private readonly Dictionary<string, RuntimePackageStageStatus> _stageStatuses = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeLifecyclePolicyOptions _lifecyclePolicy;
    private readonly RuntimeRpcCatalog? _rpcCatalog;
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
        RuntimeLifecyclePolicyOptions? lifecyclePolicy = null,
        RuntimeRpcCatalog? rpcCatalog = null)
    {
        _logger = logger;
        _events = events;
        _uiSnapshots = uiSnapshots;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifecyclePolicy = lifecyclePolicy ?? new RuntimeLifecyclePolicyOptions();
        _rpcCatalog = rpcCatalog;
        _snapshot = new RuntimePackageSnapshot(
            events.RuntimeInstanceId,
            0,
            0,
            RuntimeBootstrapState.Starting,
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
            (packageId, activationIdentity, origin, exception, action) =>
                HandlePackageFault(packageId, activationIdentity, origin, exception, action));
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
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        var prepared = await State.PreparePublicationAsync(session, expectedGeneration, cancellationToken);
        try
        {
            var publication = await CommitPublicationAsync(prepared, sources, warnings, errors);
            State.ActivatePublication(prepared);
            return publication.Warnings;
        }
        catch
        {
            if (prepared.Committed && !prepared.Activated)
            {
                State.ActivatePublication(prepared);
            }
            throw;
        }
    }

    internal Task<PackageSessionState.SessionPublication> PreparePublicationAsync(
        ActivePackageSession session,
        long expectedGeneration,
        CancellationToken cancellationToken)
        => State.PreparePublicationAsync(session, expectedGeneration, cancellationToken);

    internal async Task<(RuntimePackageStamp Stamp, IReadOnlyList<string> Warnings)> CommitPublicationAsync(
        PackageSessionState.SessionPublication publication,
        PackageSessionSourceSnapshot sources,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
    {
        var result = await State.CommitPublicationAsync(
            publication,
            generation => CommitGeneration(
                generation,
                publication.Session.GetActivePackages(),
                publication.Session.GetSessionPackages(),
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

    internal void ActivatePublication(PackageSessionState.SessionPublication publication)
        => State.ActivatePublication(publication);

    internal async Task<(RuntimePackageStamp Stamp, IReadOnlyList<string> Warnings)> FailPublicationAsync(
        PackageSessionState.SessionPublication publication,
        PackageSessionSourceSnapshot sources,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        Exception exception)
    {
        var failureErrors = errors
            .Append($"Package Runtime generation activation failed: {exception.Message}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = await State.FailCommittedPublicationAsync(
            publication,
            generation => CommitGeneration(
                generation,
                [],
                [],
                sources,
                warnings,
                failureErrors));
        if (result.Warnings.Count > 0)
        {
            AppendWarnings(result.Generation, result.Warnings);
        }
        return (new RuntimePackageStamp(_events.RuntimeInstanceId, result.Generation), result.Warnings);
    }

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
        _uiSnapshots?.RemoveStage(stageId);
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
        PackageActivationIdentity activationIdentity,
        PackageFailureOrigin origin,
        Exception exception,
        string action,
        string? rpcFaultCode = null)
    {
        PackageSessionLease sessionLease;
        try
        {
            sessionLease = State.AcquireLease();
        }
        catch (RuntimeUnavailableException)
        {
            return false;
        }

        using (sessionLease)
        {
            var (applied, generation) = CommitPackageFault(
                packageId,
                exception.Message,
                (expectedGeneration, committed) => State.HandlePackageFault(
                    packageId,
                    activationIdentity,
                    expectedGeneration,
                    origin,
                    exception,
                    action,
                    committedGeneration =>
                    {
                        committed(committedGeneration);
                        return _rpcCatalog?.DeactivatePackageWithRetirement(
                                   packageId,
                                   activationIdentity.RuntimeActivationId,
                                   faulted: true,
                                   rpcFaultCode)
                               ?? Task.CompletedTask;
                    }));
            if (applied)
            {
                AlignRpcCatalogAfterPackageFault(sessionLease.Session, generation);
            }
            return applied;
        }
    }

    internal bool HandleRpcProviderFault(
        string packageId,
        PackageActivationIdentity activationIdentity,
        Exception exception,
        string faultCode)
        => HandlePackageFault(
            packageId,
            activationIdentity,
            PackageFailureOrigin.RuntimeRpcProvider,
            new InvalidOperationException("The package schema-first RPC provider faulted."),
            "produce valid schema-first RPC output",
            faultCode);

    internal void HandleRuntimeGenerationFault(
        string packageId,
        PackageRuntimeGeneration runtimeGeneration,
        Exception exception)
    {
        PackageSessionLease sessionLease;
        try
        {
            sessionLease = State.AcquireLease();
        }
        catch (RuntimeUnavailableException)
        {
            return;
        }

        using (sessionLease)
        {
            var (applied, generation) = CommitPackageFault(
                packageId,
                exception.Message,
                (expectedGeneration, committed) => State.HandleRuntimeGenerationFault(
                    packageId,
                    runtimeGeneration,
                    expectedGeneration,
                    exception is ProcessRuntimeWorkerException
                        ? PackageFailureOrigin.RuntimeProcess
                        : PackageFailureOrigin.RuntimeBackgroundService,
                    exception,
                    "maintain Runtime generation ownership",
                    committedGeneration =>
                    {
                        committed(committedGeneration);
                        return _rpcCatalog?.DeactivatePackageWithRetirement(
                                   packageId,
                                   runtimeGeneration.ActivationId,
                                   faulted: true)
                               ?? Task.CompletedTask;
                    }));
            if (applied)
            {
                AlignRpcCatalogAfterPackageFault(sessionLease.Session, generation);
            }
        }
    }

    private void AlignRpcCatalogAfterPackageFault(ActivePackageSession session, long generation)
    {
        if (_rpcCatalog is null)
        {
            return;
        }

        while (true)
        {
            _rpcCatalog.ActivateSession(session, generation);
            var currentGeneration = State.Generation;
            if (currentGeneration == generation)
            {
                _rpcCatalog.VerifySessionGeneration(generation);
                return;
            }
            generation = currentGeneration;
        }
    }

    private (bool Applied, long Generation) CommitPackageFault(
        string packageId,
        string message,
        Func<long, Action<long>, bool> apply)
    {
        lock (_packageFaultGate)
        {
            var current = GetSnapshot();
            var warnings = current.Warnings;
            var sources = Sources.Snapshot();
            var errors = current.Errors
                .Append($"Package '{packageId}' failed: {message}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var committedGeneration = -1L;
            var applied = apply(current.SessionGeneration, generation =>
            {
                committedGeneration = generation;
                CommitGeneration(
                    generation,
                    State.GetActivePackages(),
                    State.GetSessionPackages(),
                    sources,
                    warnings,
                    errors);
            });
            if (!applied)
            {
                return (false, current.SessionGeneration);
            }
            if (committedGeneration < 0)
            {
                throw new InvalidOperationException("The applied package fault did not commit a Runtime generation.");
            }
            return (true, committedGeneration);
        }
    }

    internal bool CommitGeneration(
        long generation,
        IReadOnlyList<ActivePackageDescriptor> activePackages,
        IReadOnlyList<SessionPackageDescriptor> sessionPackages,
        PackageSessionSourceSnapshot sources,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
    {
        lock (_snapshotPublicationGate)
        {
            lock (_snapshotGate)
            {
                if (generation < _snapshot.SessionGeneration || generation < State.Generation)
                {
                    return false;
                }
            }

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
                            NormalizeDiagnostics(warnings),
                            NormalizeDiagnostics(errors));
                        _uiSnapshots?.RemoveOlderGenerations(generation);
                    }
                });
            InvalidateStaleStages(generation);
            return true;
        }
    }

    private void SetBootstrapState(
        RuntimeBootstrapState state,
        IReadOnlyList<string>? warnings,
        IReadOnlyList<string>? errors,
        string? message)
    {
        lock (_snapshotPublicationGate)
        {
            RuntimePackageSnapshot current;
            lock (_snapshotGate)
            {
                current = _snapshot;
                if (current.BootstrapState == state
                    || current.BootstrapState == RuntimeBootstrapState.ShuttingDown)
                {
                    return;
                }
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
                            NormalizeDiagnostics(_snapshot.Warnings.Concat(warnings ?? [])),
                            NormalizeDiagnostics(_snapshot.Errors.Concat(errors ?? [])));
                    }
                });
            if (state != RuntimeBootstrapState.Starting)
            {
                _bootstrapCompletion.TrySetResult();
            }
        }
    }

    private void AppendWarnings(long generation, IReadOnlyList<string> warnings)
    {
        lock (_snapshotPublicationGate)
        {
            RuntimePackageSnapshot current;
            lock (_snapshotGate)
            {
                current = _snapshot;
                if (current.SessionGeneration != generation)
                {
                    return;
                }
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
                                NormalizeDiagnostics(_snapshot.Warnings.Concat(warnings)),
                                _snapshot.Errors);
                        }
                    }
                });
        }
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
