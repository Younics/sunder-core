using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record DrainingPackageFault(
    string PackageId,
    RuntimePackageGeneration RuntimeGeneration,
    PackageFailureOrigin Origin,
    Exception Exception,
    DateTimeOffset FailedAtUtc);

internal sealed class PackageSessionState(
    ILogger logger,
    Action clearAuthSessions,
    Action<string> removePackageAuthSessions,
    TimeSpan? sessionDrainTimeout = null,
    TimeProvider? timeProvider = null)
{
    private readonly object _syncRoot = new();
    private SessionEntry _activeEntry = new(ActivePackageSession.CreateEmpty(), generation: 0);
    private MutableSessionPublication? _pendingPublication;
    private MutableSessionPublication? _rejectedPublication;
    private MutableSessionPublication? _recoveryPublication;
    private PackageRecoveryActivation? _packageRecoveryActivation;
    private PackageRecoveryCommit? _packageRecoveryCommit;
    private FaultPublicationReservation? _faultPublication;
    private readonly List<PackageDeactivationWork> _quarantinedPackageCleanups = [];
    private long _generation;
    private readonly TimeSpan _sessionDrainTimeout = sessionDrainTimeout ?? TimeSpan.FromSeconds(10);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public PackageSessionLease AcquireLease()
    {
        lock (_syncRoot) return AcquireLeaseUnderLock();
    }

    private PackageSessionLease AcquireLeaseUnderLock()
    {
        var entry = _activeEntry;
        if (entry.Draining)
        {
            throw new RuntimeUnavailableException("The active package session is draining and is not accepting new requests.");
        }
        if (_packageRecoveryCommit is { SessionEntry: var committingEntry }
            && ReferenceEquals(entry, committingEntry))
        {
            throw new RuntimeUnavailableException(
                "The active package session is publishing a recovered generation and is not accepting new requests.");
        }
        if (!entry.AcceptingLeases)
        {
            throw new RuntimeUnavailableException("The active package session is still activating and is not accepting requests.");
        }
        return CreateLeaseUnderLock(entry);
    }

    // Null means no work was admitted for an exact continuing Worker during a
    // publication drain. Classify under the same lock as ordinary admission so
    // publication cannot turn a temporary fence into a false stale-birth error.
    internal PackageSessionLease? AcquireRpcLease(RuntimeRpcPackageActivation caller)
    {
        lock (_syncRoot)
        {
            var entry = _activeEntry;
            var birth = new RuntimePackageGeneration(caller.Package.RuntimeActivationId, caller.ActivationGeneration);
            // A publication drain is temporary only for the exact Worker already
            // selected into its successor. The published generation and Worker
            // birth are separate authorities after earlier continuation(s).
            if (entry.Draining && !entry.Retired
                && entry.Generation == caller.SessionGeneration
                && entry.Session.OwnsRuntimeGeneration(caller.PackageId, birth)
                && entry.Session.TryGetLoadedPackage(caller.PackageId, out var previous)
                && ReferenceEquals(previous?.Worker, caller.Package.Worker)
                && entry.DrainSuccessor is { } successor
                && successor.OwnsRuntimeGeneration(caller.PackageId, birth)
                && successor.TryGetLoadedPackage(caller.PackageId, out var next)
                && ReferenceEquals(next?.Worker, caller.Package.Worker)
                && !caller.RetirementToken.IsCancellationRequested
                && !caller.Package.Worker.GenerationCompletion.IsCompleted)
            {
                return null;
            }
            return AcquireLeaseUnderLock();
        }
    }

    internal PackageSessionLease AcquirePackageActivationLease(
        string packageId,
        RuntimePackageGeneration generation)
    {
        lock (_syncRoot)
        {
            if (_packageRecoveryCommit is { SessionEntry: var committingEntry }
                && ReferenceEquals(_activeEntry, committingEntry))
            {
                throw new RuntimeUnavailableException(
                    "The active package session is publishing a recovered generation and is not accepting package-owned work.");
            }
            SessionEntry? entry = null;
            if (!_activeEntry.Draining
                && _activeEntry.AcceptingLeases
                && _activeEntry.Session.OwnsRuntimeGeneration(packageId, generation))
            {
                entry = _activeEntry;
            }
            else if (_pendingPublication is { Completed: false, CandidateEntry: { } candidate }
                     && !candidate.Draining
                     && candidate.Session.OwnsRuntimeGeneration(packageId, generation))
            {
                entry = candidate;
            }
            else if (_recoveryPublication is { Recovering: true } recovery
                     && ReferenceEquals(_activeEntry, recovery.PreviousEntry)
                      && _activeEntry.Session.OwnsRuntimeGeneration(packageId, generation))
            {
                entry = _activeEntry;
            }
            else if (_packageRecoveryActivation is { } packageRecovery
                     && ReferenceEquals(_activeEntry, packageRecovery.SessionEntry)
                     && !_activeEntry.Draining
                     && _activeEntry.AcceptingLeases
                     && string.Equals(packageRecovery.Request.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                     && packageRecovery.Replacement.RuntimeActivationId == generation.ActivationId
                     && packageRecovery.Request.ActivationGeneration == generation.SessionGeneration)
            {
                entry = _activeEntry;
            }
            if (entry is null)
            {
                throw new RuntimeUnavailableException(
                    "The package Runtime generation is stale or no longer accepting package-owned work.");
            }
            var lease = CreateLeaseUnderLock(entry);
            MarkPackageLeaseUnderLock(lease, packageId);
            return lease;
        }
    }

    private PackageSessionLease CreateLeaseUnderLock(SessionEntry entry)
    {
        var leaseId = ++entry.LastLeaseId;
        entry.ActiveLeaseIds.Add(leaseId);
        return new PackageSessionLease(
            entry.Session,
            entry.Generation,
            leaseId,
            entry,
            entry.GenerationEpoch,
            entry.Retirement.Token,
            entry.GenerationEpoch.RetirementToken,
            () => ReleaseLease(entry, leaseId));
    }

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages()
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.GetActivePackages();
        }
    }

    internal bool AppPredicatesSatisfied(RuntimeRpcReadinessEvaluator readiness, string version, bool development, string? manifestHash,
        IReadOnlyList<RuntimeRequiredProviderPredicate> predicates, IReadOnlyList<string>? ownerOnlyContracts = null)
    {
        lock (_syncRoot) return readiness.AppPredicatesSatisfied(_activeEntry.Session, version, development, manifestHash, predicates, ownerOnlyContracts);
    }

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages()
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.GetSessionPackages();
        }
    }

    internal IReadOnlyList<string> GetExecutionRuntimeDependentPackageIds(
        IReadOnlySet<string> blockedIdentityKeys)
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.GetExecutionRuntimeDependentPackageIds(blockedIdentityKeys);
        }
    }

    internal SessionPublicationSnapshot CapturePublicationSnapshot(
        long expectedGeneration,
        RuntimeRpcReadinessEvaluator? readinessEvaluator = null)
    {
        lock (_syncRoot)
        {
            if (_generation != expectedGeneration
                || _activeEntry.Generation != expectedGeneration
                || _activeEntry.Retired
                || (_activeEntry.Draining && !HasFaultPublicationUnderLock(_activeEntry)))
            {
                throw new RuntimeStaleGenerationException(
                    "The package session changed before its publication snapshot was captured.");
            }

            if (readinessEvaluator is not null
                && !_activeEntry.Draining
                && _activeEntry.AcceptingLeases)
            {
                readinessEvaluator.Evaluate(_activeEntry.Session);
            }

            return CapturePublicationSnapshotUnderLock();
        }
    }

    public SessionPackageDescriptor? GetSessionPackage(string packageId)
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.TryGetSessionPackage(packageId, out var package) ? package : null;
        }
    }

    public IReadOnlyList<RuntimePackageSource> GetActivePackageSources()
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.GetActivePackageSources();
        }
    }

    public IReadOnlyList<RuntimePackageSource> GetAppPackageSources()
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.GetAppPackageSources();
        }
    }

    public IReadOnlyList<ActiveLoadedPackage> ListEnabledLoadedPackages(PackageSessionLease lease)
    {
        lock (_syncRoot)
        {
            var packages = lease.Session.LoadedPackageMap.Values
                .Where(package => lease.Session.IsPackageEnabled(package.Descriptor.PackageId))
                .ToArray();
            foreach (var package in packages)
            {
                MarkPackageLeaseUnderLock(lease, package.Descriptor.PackageId);
            }
            return packages;
        }
    }

    public async Task<IReadOnlyList<string>> ClearActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionEntry? previousEntry = null;
        CancellationTokenSource? retirement = null;
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_packageRecoveryCommit is { SessionEntry: var committingEntry }
                && ReferenceEquals(_activeEntry, committingEntry))
            {
                throw new RuntimeUnavailableException(
                    "The active package session is publishing a recovered generation and cannot be cleared.");
            }
            if (_activeEntry.Draining)
            {
                throw new RuntimeUnavailableException("The active package session is already draining.");
            }
            if (_activeEntry.Session.IsEmpty)
            {
                // Authentication state is external to the session lock.
            }
            else
            {
                previousEntry = _activeEntry;
                previousEntry.Draining = true;
                retirement = previousEntry.Retirement;
                SignalRetirementIfDrained(previousEntry);
            }
        }

        if (previousEntry is null)
        {
            clearAuthSessions();
            return [];
        }

        var activeRetirement = retirement!;
        previousEntry.RetirementCallbacks = CombineRetirementCallbacks(
            previousEntry.RetirementCallbacks,
            RuntimeCancellation.Signal(activeRetirement));
        try
        {
            await previousEntry.Drained.Task.WaitAsync(_sessionDrainTimeout, cancellationToken);
        }
        catch
        {
            AbortDrain(previousEntry, activeRetirement);
            throw;
        }

        lock (_syncRoot)
        {
            if (!ReferenceEquals(_activeEntry, previousEntry) || !previousEntry.Draining)
            {
                throw new InvalidOperationException("The active package session changed while it was draining.");
            }
            previousEntry.Retired = true;
            _activeEntry = new SessionEntry(ActivePackageSession.CreateEmpty(), _generation);
        }

        clearAuthSessions();
        return await RetireSessionAsync(previousEntry);
    }

    public async Task<(long Generation, IReadOnlyList<string> Warnings)> PublishSessionAsync(
        ActivePackageSession session,
        CancellationToken cancellationToken = default,
        long? expectedGeneration = null,
        Action<long>? committed = null)
    {
        var publication = await PreparePublicationAsync(session, expectedGeneration, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await CommitPublicationAsync(
                publication,
                committed is null
                    ? null
                    : snapshot => committed(snapshot.Generation));
        }
        catch
        {
            DiscardPublication(publication);
            throw;
        }
    }

    internal async Task<SessionPublication> PreparePublicationAsync(
        ActivePackageSession session,
        long? expectedGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        SessionEntry previousEntry;
        CancellationTokenSource retirement;
        SessionGenerationEpoch generationEpoch;
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_packageRecoveryCommit is { SessionEntry: var committingEntry }
                && ReferenceEquals(_activeEntry, committingEntry))
            {
                throw new RuntimeUnavailableException(
                    "The active package session is publishing a recovered generation and cannot be replaced.");
            }
            if (ReferenceEquals(session, _activeEntry.Session))
            {
                throw new InvalidOperationException("The active package session cannot be published again.");
            }
            if (expectedGeneration is not null && expectedGeneration != _generation)
            {
                throw new RuntimeStaleGenerationException("The active package session changed while the replacement was being prepared.");
            }

            previousEntry = _activeEntry;
            if (previousEntry.Draining)
            {
                throw new RuntimeUnavailableException("The active package session is already draining.");
            }
            previousEntry.DrainSuccessor = session;
            previousEntry.Draining = true;
            retirement = previousEntry.Retirement;
            generationEpoch = AdvanceGenerationUnderLock(
                previousEntry,
                previousEntry.Generation);
            SignalRetirementIfDrained(previousEntry);
        }

        var generationRetirement = RetireGenerationEpochAsync(
            previousEntry,
            generationEpoch);
        previousEntry.RetirementCallbacks = CombineRetirementCallbacks(
            previousEntry.RetirementCallbacks,
            RuntimeCancellation.Signal(retirement));
        try
        {
            await Task.WhenAll(previousEntry.Drained.Task, generationRetirement)
                .WaitAsync(_sessionDrainTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                if (!ReferenceEquals(_activeEntry, previousEntry)
                    || !previousEntry.Draining
                    || _pendingPublication is not null)
                {
                    throw new InvalidOperationException(
                        "The active package session changed while the replacement was being prepared.");
                }
                if (expectedGeneration is not null && expectedGeneration != _generation)
                {
                    throw new RuntimeStaleGenerationException(
                        "The active package session changed while the replacement was being prepared.");
                }

                var candidateGeneration = checked(_generation + 1);
                var publication = new MutableSessionPublication(
                    this,
                    session,
                    previousEntry,
                    retirement,
                    expectedGeneration,
                    candidateGeneration);
                _pendingPublication = publication;
                return publication;
            }
        }
        catch (TimeoutException exception)
        {
            AbortDrain(previousEntry, retirement);
            throw new RuntimeUnavailableException(
                $"The active package session did not drain within {_sessionDrainTimeout.TotalSeconds:0.###} seconds; reload was not applied.",
                exception);
        }
        catch
        {
            AbortDrain(previousEntry, retirement);
            throw;
        }
    }

    internal async Task<(long Generation, IReadOnlyList<string> Warnings)> CommitPublicationAsync(
        SessionPublication publicationHandle,
        Action<SessionPublicationSnapshot>? committed = null)
    {
        var publication = RequireMutablePublication(publicationHandle);
        await publication.PreviousEntry.RetirementCallbacks.ConfigureAwait(false);
        long generation;
        Exception? publicationException = null;
        lock (_syncRoot)
        {
            if (publication.Completed)
            {
                throw new InvalidOperationException("The package session publication is already complete.");
            }
            if (!ReferenceEquals(_activeEntry, publication.PreviousEntry) || !publication.PreviousEntry.Draining)
            {
                throw new InvalidOperationException("The active package session changed while it was draining.");
            }
            if (!ReferenceEquals(_pendingPublication, publication))
            {
                throw new InvalidOperationException("The package session publication is no longer the active candidate.");
            }
            if (publication.ExpectedGeneration is not null && publication.ExpectedGeneration != _generation)
            {
                throw new RuntimeStaleGenerationException("The active package session changed while the replacement was being prepared.");
            }

            generation = checked(_generation + 1);
            if (generation != publication.CandidateGeneration)
            {
                throw new RuntimeStaleGenerationException(
                    "The candidate Runtime generation changed before publication.");
            }
            publication.CandidateEntry.Session.CommitWorkerRetention();
            publication.PreviousEntry.DrainSuccessor = null;
            publication.PreviousEntry.Retired = true;
            publication.Completed = true;
            publication.Committed = true;
            _generation = generation;
            _activeEntry = publication.CandidateEntry;
            _activeEntry.Generation = generation;
            _pendingPublication = null;

            try
            {
                committed?.Invoke(CapturePublicationSnapshotUnderLock());
                clearAuthSessions();
            }
            catch (Exception exception)
            {
                publicationException = exception;
            }
            finally
            {
                _activeEntry.AcceptingLeases = true;
            }
        }

        var warnings = await RetireSessionAsync(publication.PreviousEntry);
        if (publicationException is not null)
        {
            throw publicationException;
        }
        return (generation, warnings);
    }

    internal void DiscardPublication(SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        if (RejectPublication(publication))
        {
            RestoreRejectedPublication(publication);
        }
    }

    internal bool RejectPublication(SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        SessionEntry candidate;
        lock (_syncRoot)
        {
            if (publication.Completed)
            {
                return false;
            }
            publication.Completed = true;
            publication.PreviousEntry.DrainSuccessor = null;
            candidate = publication.CandidateEntry;
            candidate.Draining = true;
            SignalRetirementIfDrained(candidate);
            if (ReferenceEquals(_pendingPublication, publication))
            {
                _pendingPublication = null;
            }
            if (ReferenceEquals(_activeEntry, publication.PreviousEntry))
            {
                _rejectedPublication = publication;
            }
        }
        candidate.RetirementCallbacks = CombineRetirementCallbacks(
            candidate.RetirementCallbacks,
            RuntimeCancellation.Signal(candidate.Retirement));
        return true;
    }

    internal void RestoreRejectedPublication(SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        if (!TryRestoreRejectedPublication(publication))
        {
            throw new InvalidOperationException(
                "A quiesced or faulted previous session must be recovered as a fresh Runtime generation.");
        }
    }

    internal bool TryRestoreRejectedPublication(SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        Task retirementCallbacks;
        CancellationTokenSource cancelledRetirement;
        lock (_syncRoot)
        {
            if (!publication.Completed || publication.Committed)
            {
                throw new InvalidOperationException(
                    "Only a rejected candidate publication can restore its previous session.");
            }
            if (publication.PreviousWorkersQuiesced || publication.DrainingFaults.Count > 0)
            {
                return false;
            }
            var previousEntry = publication.PreviousEntry;
            if (!ReferenceEquals(_rejectedPublication, publication)
                || !ReferenceEquals(_activeEntry, previousEntry)
                || previousEntry.Retired
                || !previousEntry.Draining)
            {
                throw new InvalidOperationException(
                    "The rejected package session is no longer the active publication source.");
            }
            _rejectedPublication = null;
            previousEntry.Draining = false;
            retirementCallbacks = previousEntry.RetirementCallbacks;
            cancelledRetirement = previousEntry.Retirement;
            previousEntry.Retirement = new CancellationTokenSource();
            previousEntry.Drained = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
        RuntimeCancellation.DisposeAfterCallbacks(cancelledRetirement, retirementCallbacks);
        return true;
    }

    internal async Task WaitForPreviousRetirementCallbacksAsync(SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        await publication.PreviousEntry.RetirementCallbacks.ConfigureAwait(false);
    }

    internal void MarkPreviousWorkersQuiesced(SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        lock (_syncRoot)
        {
            var isPending = ReferenceEquals(_pendingPublication, publication)
                            && !publication.Completed;
            var isRejected = ReferenceEquals(_rejectedPublication, publication)
                             && publication.Completed
                             && !publication.Committed;
            if ((!isPending && !isRejected)
                || publication.Recovering
                || !ReferenceEquals(_activeEntry, publication.PreviousEntry))
            {
                throw new InvalidOperationException(
                    "The previous package session is no longer the active publication source.");
            }
            publication.PreviousWorkersQuiesced = true;
        }
    }

    internal bool RecordDrainingRuntimeGenerationFault(
        string packageId,
        RuntimePackageGeneration runtimeGeneration,
        PackageFailureOrigin origin,
        Exception exception)
    {
        lock (_syncRoot)
        {
            var publication = _pendingPublication ?? _rejectedPublication ?? _recoveryPublication;
            if (publication is null
                || publication.Committed
                || publication.RecoveryCommitted
                || !ReferenceEquals(_activeEntry, publication.PreviousEntry)
                || !_activeEntry.Draining
                || !_activeEntry.Session.OwnsRuntimeGeneration(packageId, runtimeGeneration))
            {
                return false;
            }
            if (!publication.DrainingFaults.Any(fault =>
                    string.Equals(fault.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
                    && fault.RuntimeGeneration == runtimeGeneration))
            {
                publication.DrainingFaults.Add(new DrainingPackageFault(
                    packageId,
                    runtimeGeneration,
                    origin,
                    exception,
                    _timeProvider.GetUtcNow()));
            }
            return true;
        }
    }

    internal IReadOnlyList<DrainingPackageFault> GetDrainingFaults(SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        lock (_syncRoot)
        {
            return publication.DrainingFaults.ToArray();
        }
    }

    internal async Task BeginRecoveryPublicationAsync(SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        await publication.PreviousEntry.RetirementCallbacks.ConfigureAwait(false);
        CancellationTokenSource retiredCancellation;
        lock (_syncRoot)
        {
            if (!publication.Completed
                || publication.Committed
                || publication.Recovering
                || !ReferenceEquals(_activeEntry, publication.PreviousEntry)
                || !publication.PreviousEntry.Draining
                || _pendingPublication is not null
                || !ReferenceEquals(_rejectedPublication, publication)
                || _recoveryPublication is not null)
            {
                throw new InvalidOperationException(
                    "The rejected package session publication cannot begin recovery.");
            }
            retiredCancellation = publication.PreviousEntry.Retirement;
            publication.PreviousEntry.Retirement = new CancellationTokenSource();
            publication.PreviousEntry.RetirementCallbacks = Task.CompletedTask;
            publication.PreviousEntry.Drained = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (publication.PreviousWorkersQuiesced)
            {
                publication.PreviousEntry.Generation = publication.CandidateGeneration;
                publication.PreviousEntry.ActivationGeneration = publication.CandidateGeneration;
            }
            publication.Recovering = true;
            _rejectedPublication = null;
            _recoveryPublication = publication;
        }
        retiredCancellation.Dispose();
    }

    internal long CommitRecoveryPublication(
        SessionPublication publicationHandle,
        Action<SessionPublicationSnapshot>? committed = null)
    {
        var publication = RequireMutablePublication(publicationHandle);
        long generation;
        SessionGenerationEpoch retiredEpoch;
        int drainingFaultCount;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_recoveryPublication, publication)
                || !publication.Recovering
                || !ReferenceEquals(_activeEntry, publication.PreviousEntry)
                || !publication.PreviousEntry.Draining)
            {
                throw new InvalidOperationException(
                    "The previous package session is not awaiting recovery publication.");
            }
            generation = publication.CandidateGeneration;
            if (generation != checked(_generation + 1))
            {
                throw new RuntimeStaleGenerationException(
                    "The recovery Runtime generation changed before publication.");
            }
            retiredEpoch = AdvanceGenerationUnderLock(publication.PreviousEntry, generation);
            publication.PreviousEntry.AcceptingLeases = false;
            drainingFaultCount = publication.DrainingFaults.Count;
        }

        _ = RetireGenerationEpochAsync(publication.PreviousEntry, retiredEpoch);
        Exception? publicationException = null;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_recoveryPublication, publication)
                || !publication.Recovering
                || !ReferenceEquals(_activeEntry, publication.PreviousEntry)
                || !publication.PreviousEntry.Draining
                || generation != checked(_generation + 1))
            {
                throw new RuntimeStaleGenerationException(
                    "The recovery Runtime generation changed while old calls were retiring.");
            }
            if (publication.DrainingFaults.Count != drainingFaultCount)
            {
                throw new InvalidOperationException(
                    "A recovered package Runtime generation faulted while old calls were retiring.");
            }
            _generation = generation;
            publication.PreviousEntry.Draining = false;
            publication.Recovering = false;
            publication.RecoveryCommitted = true;
            _recoveryPublication = null;
            try
            {
                committed?.Invoke(CapturePublicationSnapshotUnderLock());
                clearAuthSessions();
            }
            catch (Exception exception)
            {
                publicationException = exception;
            }
            finally
            {
                publication.PreviousEntry.AcceptingLeases = true;
            }
        }
        if (publicationException is not null)
        {
            throw publicationException;
        }
        return generation;
    }

    internal long AbandonRecoveryPublication(
        SessionPublication publicationHandle,
        Action<SessionPublicationSnapshot>? committed = null)
    {
        var publication = RequireMutablePublication(publicationHandle);
        SessionEntry abandoned;
        SessionGenerationEpoch abandonedEpoch;
        SessionEntry unavailableEntry;
        long generation;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_activeEntry, publication.PreviousEntry))
            {
                return _generation;
            }
            generation = publication.CandidateGeneration;
            if (generation != checked(_generation + 1))
            {
                throw new RuntimeStaleGenerationException(
                    "The unavailable Runtime recovery generation changed before publication.");
            }
            abandoned = publication.PreviousEntry;
            abandonedEpoch = abandoned.GenerationEpoch;
            abandoned.Retired = true;
            abandoned.Draining = true;
            SignalRetirementIfDrained(abandoned);
            publication.Recovering = false;
            _recoveryPublication = null;
            if (ReferenceEquals(_rejectedPublication, publication))
            {
                _rejectedPublication = null;
            }
            _generation = generation;
            unavailableEntry = new SessionEntry(
                ActivePackageSession.CreateEmpty(),
                generation,
                acceptingLeases: false,
                activationGeneration: generation);
            _activeEntry = unavailableEntry;
            publication.Abandoned = true;
        }
        abandoned.RetirementCallbacks = CombineRetirementCallbacks(
            abandoned.RetirementCallbacks,
            RuntimeCancellation.Signal(abandoned.Retirement));
        _ = RetireGenerationEpochAsync(abandoned, abandonedEpoch);
        Exception? publicationException = null;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_activeEntry, unavailableEntry)
                || _generation != generation
                || unavailableEntry.Generation != generation)
            {
                throw new RuntimeStaleGenerationException(
                    "The unavailable Runtime recovery generation changed before publication completed.");
            }
            try
            {
                committed?.Invoke(CapturePublicationSnapshotUnderLock());
                clearAuthSessions();
            }
            catch (Exception exception)
            {
                publicationException = exception;
            }
        }
        if (publicationException is not null)
        {
            throw publicationException;
        }
        return generation;
    }

    internal Task<IReadOnlyList<string>> RetireAbandonedPublicationAsync(
        SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        lock (_syncRoot)
        {
            if (!publication.Abandoned
                || !publication.PreviousEntry.Retired
                || ReferenceEquals(_activeEntry, publication.PreviousEntry))
            {
                throw new InvalidOperationException(
                    "The previous package session is not an abandoned publication.");
            }
        }
        return RetireSessionAsync(publication.PreviousEntry);
    }

    internal Task WaitForRecoveryPackageCallsAsync(SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_recoveryPublication, publication)
                || !publication.Recovering
                || !ReferenceEquals(_activeEntry, publication.PreviousEntry)
                || !publication.PreviousEntry.Draining)
            {
                throw new InvalidOperationException(
                    "The previous package session is not an active recovery publication.");
            }
            return CaptureLeaseBarrier(publication.PreviousEntry).Completion.Task;
        }
    }

    internal async Task WaitForDiscardedCandidateDrainAsync(SessionPublication publicationHandle)
    {
        var publication = RequireMutablePublication(publicationHandle);
        await publication.CandidateEntry.Drained.Task.ConfigureAwait(false);
        await publication.CandidateEntry.RetirementCallbacks.ConfigureAwait(false);
        RuntimeCancellation.DisposeAfterCallbacks(
            publication.CandidateEntry.Retirement,
            publication.CandidateEntry.RetirementCallbacks);
    }

    public bool HandlePackageFault(
        string packageId,
        PackageActivationIdentity activationIdentity,
        long expectedGeneration,
        PackageFailureOrigin origin,
        Exception exception,
        string action,
        Func<long, Task>? committed = null)
        => HandlePackageFault(
            packageId,
            activationIdentity,
            expectedGeneration,
            origin,
            exception,
            action,
            committed,
            out _);

    internal bool HandlePackageFault(
        string packageId,
        PackageActivationIdentity activationIdentity,
        long expectedGeneration,
        PackageFailureOrigin origin,
        Exception exception,
        string action,
        Func<long, Task>? committed,
        out RuntimePackageRecoveryRequest? recoveryRequest)
        => HandlePackageFaultCore(
            packageId,
            PackageFaultTarget.ForPackageActivation(activationIdentity),
            expectedGeneration,
            origin,
            exception,
            action,
            committed,
            out recoveryRequest,
            out _);

    public bool HandleRuntimeGenerationFault(
        string packageId,
        RuntimePackageGeneration runtimeGeneration,
        long expectedGeneration,
        PackageFailureOrigin origin,
        Exception exception,
        string action,
        Func<long, Task>? committed = null)
        => HandleRuntimeGenerationFault(
            packageId,
            runtimeGeneration,
            expectedGeneration,
            origin,
            exception,
            action,
            committed,
            out _);

    internal bool HandleRuntimeGenerationFault(
        string packageId,
        RuntimePackageGeneration runtimeGeneration,
        long expectedGeneration,
        PackageFailureOrigin origin,
        Exception exception,
        string action,
        Func<long, Task>? committed,
        out RuntimePackageRecoveryRequest? recoveryRequest)
        => HandlePackageFaultCore(
            packageId,
            PackageFaultTarget.ForRuntimeGeneration(runtimeGeneration),
            expectedGeneration,
            origin,
            exception,
            action,
            committed,
            out recoveryRequest,
            out _);

    internal bool HandlePackageExecutionFault(
        string packageId,
        RuntimePackageGeneration runtimeGeneration,
        long expectedGeneration,
        Exception exception,
        Func<long, Task>? committed,
        out Task cleanupCompletion)
        => HandlePackageFaultCore(
            packageId,
            PackageFaultTarget.ForRuntimeGeneration(runtimeGeneration),
            expectedGeneration,
            PackageFailureOrigin.RuntimeActivation,
            exception,
            "comply with exact execution-runtime lifecycle policy",
            committed,
            out _,
            out cleanupCompletion);

    private bool HandlePackageFaultCore(
        string packageId,
        PackageFaultTarget target,
        long expectedGeneration,
        PackageFailureOrigin origin,
        Exception exception,
        string action,
        Func<long, Task>? committed,
        out RuntimePackageRecoveryRequest? recoveryRequest,
        out Task cleanupCompletion)
    {
        ActiveLoadedPackage? packageToDeactivate;
        PackageDeactivationWork? deactivation;
        SessionEntry sessionEntry;
        SessionGenerationEpoch retiredEpoch;
        FaultPublicationReservation publication;
        long committedGeneration;
        recoveryRequest = null;
        cleanupCompletion = Task.CompletedTask;
        lock (_syncRoot)
        {
            var failedAtUtc = _timeProvider.GetUtcNow();
            if (_faultPublication is not null
                || _packageRecoveryCommit is not null
                || expectedGeneration != _generation
                || !target.MatchesSession(_activeEntry)
                || !_activeEntry.AcceptingLeases
                || _activeEntry.Draining
                || !target.IsOwnedBy(_activeEntry, packageId)
                || !_activeEntry.Session.MarkPackageFailedInternal(
                    packageId,
                    origin,
                    exception.Message,
                    out packageToDeactivate,
                    failedAtUtc))
            {
                return false;
            }

            sessionEntry = _activeEntry;
            committedGeneration = checked(_generation + 1);
            _generation = committedGeneration;
            retiredEpoch = AdvanceGenerationUnderLock(sessionEntry, committedGeneration);
            deactivation = CreatePackageDeactivationLocked(packageId, packageToDeactivate);
            cleanupCompletion = deactivation?.Completion.Task ?? Task.CompletedTask;
            recoveryRequest = CreateRecoveryRequestLocked(
                packageId,
                packageToDeactivate,
                deactivation,
                origin,
                exception,
                failedAtUtc);
            publication = new FaultPublicationReservation(
                sessionEntry,
                committedGeneration);
            _faultPublication = publication;
        }

        try
        {
            var generationRetirement = RetireGenerationEpochAsync(sessionEntry, retiredEpoch);
            if (deactivation is not null)
            {
                deactivation.GenerationRetirementCallbacks = generationRetirement;
            }

            Exception? publicationException = null;
            if (committed is not null)
            {
                try
                {
                    var retirementCallbacks = committed(committedGeneration) ?? Task.CompletedTask;
                    if (deactivation is not null)
                    {
                        deactivation.RpcRetirementCallbacks = retirementCallbacks;
                    }
                    else
                    {
                        _ = retirementCallbacks;
                    }
                }
                catch (Exception commitException)
                {
                    recoveryRequest = null;
                    publicationException = commitException;
                }
            }
            removePackageAuthSessions(packageId);
            QueuePackageDeactivation(deactivation);
            target.LogFault(logger, exception, action, packageId);
            if (publicationException is not null)
            {
                throw publicationException;
            }
            return true;
        }
        finally
        {
            CompleteFaultPublication(publication);
        }
    }

    internal RuntimePackageRecoveryValidation ValidatePackageRecovery(
        RuntimePackageRecoveryRequest request)
    {
        lock (_syncRoot)
        {
            return ValidatePackageRecoveryLocked(request);
        }
    }

    internal bool TryEnqueueCurrentPackageRecovery(
        RuntimePackageRecoveryRequest request,
        Action<RuntimePackageRecoveryRequest> enqueue)
    {
        ArgumentNullException.ThrowIfNull(enqueue);
        lock (_syncRoot)
        {
            if (ValidatePackageRecoveryLocked(request) == RuntimePackageRecoveryValidation.Stale)
            {
                return false;
            }
            enqueue(request);
            return true;
        }
    }

    internal RuntimePackageRecoveryValidation BeginPackageRecoveryActivation(
        RuntimePackageRecoveryRequest request,
        ActiveLoadedPackage replacement)
    {
        lock (_syncRoot)
        {
            var validation = ValidatePackageRecoveryLocked(request);
            if (validation != RuntimePackageRecoveryValidation.Ready)
            {
                return validation;
            }
            if (_packageRecoveryActivation is not null)
            {
                throw new InvalidOperationException(
                    "Another exact package recovery activation is already being prepared.");
            }
            if (replacement.RuntimeActivationId == Guid.Empty
                || replacement.RuntimeActivationId == request.FailedActivationId
                || !string.Equals(
                    replacement.Descriptor.PackageId,
                    request.PackageId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The package recovery candidate does not identify a fresh exact activation.");
            }
            _packageRecoveryActivation = new PackageRecoveryActivation(
                request,
                replacement,
                _activeEntry);
            return RuntimePackageRecoveryValidation.Ready;
        }
    }

    internal Task AbandonPackageRecoveryActivation(
        RuntimePackageRecoveryRequest request,
        Guid runtimeActivationId)
    {
        lock (_syncRoot)
        {
            if (_packageRecoveryActivation is { } candidate
                && ReferenceEquals(candidate.Request, request)
                && candidate.Replacement.RuntimeActivationId == runtimeActivationId)
            {
                _packageRecoveryActivation = null;
                return CapturePackageLeaseBarrier(
                    candidate.SessionEntry,
                    request.PackageId).Completion.Task;
            }
            return Task.CompletedTask;
        }
    }

    internal bool TryPublishCurrentRecoverySuccess(
        RuntimePackageRecoveryRequest request,
        ActiveLoadedPackage replacement,
        long committedGeneration,
        Func<bool> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_syncRoot)
        {
            if (!ReferenceEquals(request.SessionIdentity, _activeEntry)
                || !ReferenceEquals(request.Session, _activeEntry.Session)
                || _faultPublication is not null
                || _packageRecoveryCommit is not null
                || _activeEntry.Retired
                || _activeEntry.Draining
                || !_activeEntry.AcceptingLeases
                || _generation != committedGeneration
                || _activeEntry.Generation != committedGeneration
                || _activeEntry.ActivationGeneration != request.ActivationGeneration
                || !request.Session.OwnsRuntimeGeneration(
                    request.PackageId,
                    new RuntimePackageGeneration(
                        replacement.RuntimeActivationId,
                        request.ActivationGeneration)))
            {
                return false;
            }
            return publish();
        }
    }

    internal SessionPublicationSnapshot? RecordPackageRecoveryFailure(
        RuntimePackageRecoveryRequest request,
        PackageFailureOrigin origin,
        Exception exception,
        DateTimeOffset failedAtUtc)
    {
        lock (_syncRoot)
        {
            if (ValidatePackageRecoveryLocked(request) == RuntimePackageRecoveryValidation.Stale
                || !request.Session.RecordRecoveryFailure(
                    request.PackageId,
                    request.Source,
                    origin,
                    exception.Message,
                    failedAtUtc))
            {
                return null;
            }

            return CapturePublicationSnapshotUnderLock();
        }
    }

    internal RuntimePackageRecoveryValidation CommitPackageRecovery(
        RuntimePackageRecoveryRequest request,
        ActiveLoadedPackage replacement,
        Action<long>? committed,
        CancellationToken cancellationToken,
        out long committedGeneration)
    {
        SessionEntry sessionEntry;
        SessionGenerationEpoch retiredEpoch;
        PackageRecoveryCommit recoveryCommit;
        SessionPackageDescriptor failedDescriptor;
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            committedGeneration = _generation;
            var validation = ValidatePackageRecoveryLocked(request);
            if (validation != RuntimePackageRecoveryValidation.Ready)
            {
                return validation;
            }
            if (_packageRecoveryActivation is not { } candidate
                || !ReferenceEquals(candidate.Request, request)
                || candidate.Replacement.RuntimeActivationId != replacement.RuntimeActivationId
                || !ReferenceEquals(candidate.Replacement.Worker, replacement.Worker)
                || !ReferenceEquals(candidate.SessionEntry, _activeEntry))
            {
                return RuntimePackageRecoveryValidation.Stale;
            }
            failedDescriptor = request.Session.SessionPackageMap[request.PackageId];
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.Session.PublishRecoveredPackage(
                    request.PackageId,
                    request.Source,
                    replacement,
                    request.ActivationGeneration))
            {
                return RuntimePackageRecoveryValidation.Stale;
            }

            var previousGeneration = _generation;
            committedGeneration = checked(previousGeneration + 1);
            _generation = committedGeneration;
            sessionEntry = _activeEntry;
            retiredEpoch = AdvanceGenerationUnderLock(sessionEntry, committedGeneration);
            recoveryCommit = new PackageRecoveryCommit(
                sessionEntry,
                previousGeneration,
                sessionEntry.GenerationEpoch);
            _packageRecoveryCommit = recoveryCommit;
        }

        _ = RetireGenerationEpochAsync(sessionEntry, retiredEpoch);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                if (!ReferenceEquals(_packageRecoveryCommit, recoveryCommit)
                    || !ReferenceEquals(_activeEntry, sessionEntry)
                    || _generation != committedGeneration
                    || !ReferenceEquals(sessionEntry.GenerationEpoch, recoveryCommit.PublishedEpoch))
                {
                    throw new RuntimeStaleGenerationException(
                        "The exact package recovery generation changed before publication completed.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            committed?.Invoke(committedGeneration);
            lock (_syncRoot)
            {
                if (!ReferenceEquals(_packageRecoveryCommit, recoveryCommit)
                    || !ReferenceEquals(_activeEntry, sessionEntry)
                    || _generation != committedGeneration
                    || !ReferenceEquals(sessionEntry.GenerationEpoch, recoveryCommit.PublishedEpoch))
                {
                    throw new RuntimeStaleGenerationException(
                        "The exact package recovery generation changed during publication.");
                }
                _packageRecoveryActivation = null;
                _packageRecoveryCommit = null;
            }
            return RuntimePackageRecoveryValidation.Ready;
        }
        catch
        {
            SessionGenerationEpoch? rejectedEpoch = null;
            lock (_syncRoot)
            {
                if (ReferenceEquals(_packageRecoveryCommit, recoveryCommit))
                {
                    if (ReferenceEquals(_activeEntry, sessionEntry)
                        && _generation == committedGeneration
                        && ReferenceEquals(sessionEntry.GenerationEpoch, recoveryCommit.PublishedEpoch))
                    {
                        request.Session.RollbackRecoveredPackage(
                            request.PackageId,
                            request.Source,
                            replacement.RuntimeActivationId,
                            failedDescriptor.FailureOrigin ?? request.FailureOrigin,
                            failedDescriptor.LastError ?? request.Failure.Message,
                            failedDescriptor.LastFailureAtUtc ?? request.FailedAtUtc);
                        _generation = recoveryCommit.PreviousGeneration;
                        rejectedEpoch = AdvanceGenerationUnderLock(
                            sessionEntry,
                            recoveryCommit.PreviousGeneration);
                    }
                    _packageRecoveryCommit = null;
                }
            }
            if (rejectedEpoch is not null)
            {
                _ = RetireGenerationEpochAsync(sessionEntry, rejectedEpoch);
            }
            throw;
        }
    }

    internal RuntimePackageRecoveryRequest? CreateRestoredRecoveryRequest(
        SessionPublication publicationHandle,
        DrainingPackageRecoveryCandidate candidate)
    {
        var publication = RequireMutablePublication(publicationHandle);
        lock (_syncRoot)
        {
            if (!publication.RecoveryCommitted
                || !ReferenceEquals(_activeEntry, publication.PreviousEntry)
                || _activeEntry.Draining
                || !_activeEntry.AcceptingLeases
                || !ReferenceEquals(_activeEntry.Session, publication.PreviousEntry.Session)
                || candidate.FailedPackage.RecoveryFactory is null
                || !_activeEntry.Session.IsExactFailedPackage(
                    candidate.Fault.PackageId,
                    candidate.FailedPackage.Source))
            {
                return null;
            }

            return new RuntimePackageRecoveryRequest(
                _activeEntry,
                _activeEntry.Session,
                candidate.Fault.PackageId,
                candidate.FailedPackage.RuntimeActivationId,
                _activeEntry.ActivationGeneration,
                candidate.FailedPackage.Source,
                candidate.FailedPackage,
                Task.CompletedTask,
                candidate.Fault.Origin,
                candidate.Fault.Exception,
                candidate.FailedAtUtc);
        }
    }

    public ActiveLoadedPackage? GetLoadedPackage(PackageSessionLease lease, string packageId)
    {
        lock (_syncRoot)
        {
            if (!lease.Session.TryGetLoadedPackage(packageId, out var loadedPackage))
            {
                return null;
            }
            MarkPackageLeaseUnderLock(lease, packageId);
            return loadedPackage;
        }
    }

    public bool ExecuteIfCurrent(PackageSessionLease lease, Func<bool> action)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(lease.Identity, _activeEntry)
                || !ReferenceEquals(lease.GenerationIdentity, _activeEntry.GenerationEpoch)
                || lease.Generation != _activeEntry.Generation
                || lease.RetirementToken.IsCancellationRequested
                || lease.GenerationRetirementToken.IsCancellationRequested
                || _activeEntry.Draining
                || _packageRecoveryCommit is not null)
            {
                return false;
            }

            return action();
        }
    }

    internal RpcReadinessRefresh? RefreshRpcReadiness(
        RuntimeRpcReadinessEvaluator evaluator,
        long? expectedGeneration = null)
    {
        lock (_syncRoot)
        {
            if (expectedGeneration is not null && expectedGeneration != _generation
                || _activeEntry.Draining
                || !_activeEntry.AcceptingLeases)
            {
                return null;
            }
            var changedPackageIds = evaluator.Evaluate(_activeEntry.Session);
            if (changedPackageIds.Count == 0)
            {
                return null;
            }
            var snapshot = CapturePublicationSnapshotUnderLock();
            return new RpcReadinessRefresh(
                snapshot.Generation,
                snapshot.ActivePackages,
                snapshot.SessionPackages,
                changedPackageIds);
        }
    }

    public long Generation
    {
        get
        {
            lock (_syncRoot)
            {
                return _generation;
            }
        }
    }

    internal ActivePackageSession ActiveSession
    {
        get
        {
            lock (_syncRoot)
            {
                return _activeEntry.Session;
            }
        }
    }

    public string? TryResolvePackageAssetPath(string packageId, string assetPath)
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.TryResolvePackageAssetPath(packageId, assetPath);
        }
    }

    private PackageDeactivationWork? CreatePackageDeactivationLocked(
        string packageId,
        ActiveLoadedPackage? loadedPackage)
    {
        if (loadedPackage is null)
        {
            return null;
        }

        var barrier = CapturePackageLeaseBarrier(_activeEntry, packageId);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeEntry.PendingCleanups.Add(completion.Task);
        return new PackageDeactivationWork(
            packageId,
            loadedPackage,
            barrier.Completion.Task,
            completion);
    }

    private RuntimePackageRecoveryRequest? CreateRecoveryRequestLocked(
        string packageId,
        ActiveLoadedPackage? loadedPackage,
        PackageDeactivationWork? deactivation,
        PackageFailureOrigin origin,
        Exception exception,
        DateTimeOffset failedAtUtc)
    {
        if (loadedPackage?.RecoveryFactory is null || deactivation is null)
        {
            return null;
        }
        return new RuntimePackageRecoveryRequest(
            _activeEntry,
            _activeEntry.Session,
            packageId,
            loadedPackage.RuntimeActivationId,
            _activeEntry.ActivationGeneration,
            loadedPackage.Source,
            loadedPackage,
            deactivation.Completion.Task,
            origin,
            exception,
            failedAtUtc);
    }

    private RuntimePackageRecoveryValidation ValidatePackageRecoveryLocked(
        RuntimePackageRecoveryRequest request)
    {
        if (_packageRecoveryCommit is not null
            || _faultPublication is not null
            || !ReferenceEquals(request.SessionIdentity, _activeEntry)
            || !ReferenceEquals(request.Session, _activeEntry.Session)
            || _activeEntry.Retired
            || _activeEntry.Draining
            || !_activeEntry.AcceptingLeases
            || _activeEntry.ActivationGeneration != request.ActivationGeneration
            || !request.Session.IsExactFailedPackage(request.PackageId, request.Source))
        {
            return RuntimePackageRecoveryValidation.Stale;
        }
        return request.Session.AreRecoveryDependenciesAvailable(request.PackageId)
            ? RuntimePackageRecoveryValidation.Ready
            : RuntimePackageRecoveryValidation.DependencyUnavailable;
    }

    private void CompleteFaultPublication(FaultPublicationReservation publication)
    {
        TaskCompletionSource? drained = null;
        var invalidated = false;
        lock (_syncRoot)
        {
            invalidated = !ReferenceEquals(_faultPublication, publication)
                          || !ReferenceEquals(_activeEntry, publication.SessionEntry)
                          || publication.SessionEntry.Retired
                          || publication.SessionEntry.Generation != publication.Generation;
            _faultPublication = null;
            if ((publication.SessionEntry.Draining || publication.SessionEntry.Retired)
                && publication.SessionEntry.ActiveLeaseIds.Count == 0)
            {
                drained = publication.SessionEntry.Drained;
            }
        }
        drained?.TrySetResult();
        if (invalidated)
        {
            throw new InvalidOperationException(
                "The admitted package fault publication changed before its callback completed.");
        }
    }

    private void QueuePackageDeactivation(PackageDeactivationWork? work)
    {
        if (work is null)
        {
            return;
        }

        work.StartedTask = RunPackageDeactivationAsync(work);
    }

    private async Task RunPackageDeactivationAsync(PackageDeactivationWork work)
    {
        try
        {
            var retirementBarriers = Task.WhenAll(
                work.SessionLeasesDrained,
                work.GenerationRetirementCallbacks,
                work.RpcRetirementCallbacks);
            try
            {
                await retirementBarriers.WaitAsync(_sessionDrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                logger.LogWarning(
                    exception,
                    "Package {PackageId} authority callback drain exceeded the deadline; its provider and load context remain quarantined",
                    work.PackageId);
                var deferredCleanup = CompleteDeferredPackageDeactivationAsync(
                    work,
                    retirementBarriers);
                throw new RuntimeWorkerCleanupUnprovenException(
                    $"Package authority cleanup for '{work.PackageId}' could not be proven within {_sessionDrainTimeout.TotalSeconds:0.###} seconds.",
                    ProveDeferredPackageCleanupAsync(deferredCleanup),
                    exception);
            }
            await DeactivateLoadedPackageAsync(work.PackageId, work.LoadedPackage);
            work.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deactivate package {PackageId} after a package fault", work.PackageId);
            lock (_syncRoot)
            {
                _quarantinedPackageCleanups.Add(work);
            }
            if (ex is RuntimeWorkerCleanupUnprovenException cleanupFailure)
            {
                _ = ReleaseQuarantinedPackageCleanupAsync(work, cleanupFailure.CleanupCompletion);
            }
            work.Completion.TrySetException(ex);
        }
    }

    private async Task CompleteDeferredPackageDeactivationAsync(
        PackageDeactivationWork work,
        Task retirementBarriers)
    {
        await retirementBarriers.ConfigureAwait(false);
        await DeactivateLoadedPackageAsync(work.PackageId, work.LoadedPackage)
            .ConfigureAwait(false);
    }

    private static async Task ProveDeferredPackageCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch (RuntimeWorkerCleanupUnprovenException exception)
        {
            await exception.CleanupCompletion.ConfigureAwait(false);
        }
    }

    private async Task ReleaseQuarantinedPackageCleanupAsync(
        PackageDeactivationWork work,
        Task cleanupCompletion)
    {
        try
        {
            await cleanupCompletion.ConfigureAwait(false);
            lock (_syncRoot)
            {
                _quarantinedPackageCleanups.Remove(work);
            }
        }
        catch
        {
            // Keep the package resources rooted when cleanup cannot ever be proven.
        }
    }

    private async Task<IReadOnlyList<string>> RetireSessionAsync(SessionEntry entry)
    {
        await entry.Drained.Task;
        var cleanup = CleanupRetiredSessionAsync(entry);
        try
        {
            return await cleanup.WaitAsync(_sessionDrainTimeout);
        }
        catch (TimeoutException)
        {
            const string warning = "Previous package session cleanup exceeded the drain deadline; its code remains quarantined until cleanup exits.";
            logger.LogWarning(warning);
            _ = ObserveRetiredSessionCleanupAsync(cleanup);
            return [warning];
        }
        catch (Exception exception)
        {
            var warning = $"Previous package session cleanup failed: {exception.Message}";
            logger.LogWarning(exception, "Previous package session cleanup failed");
            return [warning];
        }
    }

    private async Task<IReadOnlyList<string>> CleanupRetiredSessionAsync(SessionEntry entry)
    {
        var started = Stopwatch.GetTimestamp();
        var warnings = new List<string>();
        Task[] pendingCleanups;
        lock (_syncRoot)
        {
            pendingCleanups = entry.PendingCleanups.ToArray();
        }
        try
        {
            await Task.WhenAll(pendingCleanups).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "One or more faulted package cleanups remain quarantined during session retirement");
            warnings.Add($"Faulted package cleanup remains quarantined: {exception.Message}");
        }
        try
        {
            await entry.RetirementCallbacks.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "One or more package retirement callbacks failed during session retirement");
            warnings.Add($"Package retirement callbacks failed: {exception.Message}");
        }

        SessionGenerationEpoch[] generationEpochs;
        lock (_syncRoot)
        {
            generationEpochs = entry.RetiredGenerationEpochs
                .Append(entry.GenerationEpoch)
                .Distinct()
                .ToArray();
        }
        await Task.WhenAll(generationEpochs.Select(static epoch => epoch.RetireAsync()))
            .ConfigureAwait(false);

        if (entry.Session.IsEmpty)
        {
            RuntimeCancellation.DisposeAfterCallbacks(entry.Retirement, entry.RetirementCallbacks);
            return warnings;
        }

        try
        {
            var stopStarted = Stopwatch.GetTimestamp();
            await entry.Session.StopWorkersAsync(logger, _sessionDrainTimeout);
            logger.LogInformation(
                "Stopped retired package workers in {ElapsedMilliseconds} ms",
                Stopwatch.GetElapsedTime(stopStarted).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop workers while retiring the previous package session");
            warnings.Add($"Previous package workers did not stop cleanly: {ex.Message}");
        }

        try
        {
            var disposeStarted = Stopwatch.GetTimestamp();
            await entry.Session.DisposeAsync();
            logger.LogInformation(
                "Disposed the retired package session in {ElapsedMilliseconds} ms",
                Stopwatch.GetElapsedTime(disposeStarted).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to dispose the previous package session cleanly");
            warnings.Add($"Previous package session cleanup failed: {ex.Message}");
        }

        RuntimeCancellation.DisposeAfterCallbacks(entry.Retirement, entry.RetirementCallbacks);
        logger.LogInformation(
            "Cleaned the retired package session in {ElapsedMilliseconds} ms",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return warnings;
    }

    private async Task ObserveRetiredSessionCleanupAsync(Task<IReadOnlyList<string>> cleanup)
    {
        try
        {
            await cleanup;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Deferred package session cleanup failed");
        }
    }

    private void AbortDrain(SessionEntry entry, CancellationTokenSource cancelledRetirement)
    {
        Task retirementCallbacks;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_activeEntry, entry) || entry.Retired || !entry.Draining)
            {
                return;
            }

            entry.Draining = false;
            entry.DrainSuccessor = null;
            retirementCallbacks = entry.RetirementCallbacks;
            entry.Retirement = new CancellationTokenSource();
            entry.Drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        RuntimeCancellation.DisposeAfterCallbacks(cancelledRetirement, retirementCallbacks);
    }

    private static Task CombineRetirementCallbacks(Task existing, Task current)
        => existing.IsCompletedSuccessfully
            ? current
            : Task.WhenAll(existing, current);

    private static SessionGenerationEpoch AdvanceGenerationUnderLock(
        SessionEntry entry,
        long generation)
    {
        var retiredEpoch = entry.GenerationEpoch;
        entry.RetiredGenerationEpochs.Add(retiredEpoch);
        entry.GenerationEpoch = new SessionGenerationEpoch(generation);
        entry.Generation = generation;
        return retiredEpoch;
    }

    private Task RetireGenerationEpochAsync(
        SessionEntry entry,
        SessionGenerationEpoch epoch)
    {
        var retirement = epoch.RetireAsync();
        _ = RemoveRetiredGenerationEpochAsync(entry, epoch, retirement);
        return retirement;
    }

    private async Task RemoveRetiredGenerationEpochAsync(
        SessionEntry entry,
        SessionGenerationEpoch epoch,
        Task retirement)
    {
        try
        {
            await retirement.WaitAsync(_sessionDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(
                exception,
                "Runtime generation {SessionGeneration} callback drain exceeded the deadline and remains quarantined",
                epoch.Generation);
            await retirement.ConfigureAwait(false);
        }
        lock (_syncRoot)
        {
            entry.RetiredGenerationEpochs.Remove(epoch);
        }
    }

    private void ReleaseLease(SessionEntry entry, long leaseId)
    {
        List<TaskCompletionSource>? completedBarriers = null;
        TaskCompletionSource? retirement = null;
        lock (_syncRoot)
        {
            if (!entry.ActiveLeaseIds.Remove(leaseId))
            {
                return;
            }
            foreach (var packageLeases in entry.PackageLeaseIds.Values)
            {
                packageLeases.Remove(leaseId);
            }

            foreach (var barrier in entry.LeaseBarriers.ToArray())
            {
                barrier.PendingLeaseIds.Remove(leaseId);
                if (barrier.PendingLeaseIds.Count == 0)
                {
                    entry.LeaseBarriers.Remove(barrier);
                    (completedBarriers ??= []).Add(barrier.Completion);
                }
            }

            if ((entry.Draining || entry.Retired)
                && entry.ActiveLeaseIds.Count == 0
                && !HasFaultPublicationUnderLock(entry))
            {
                retirement = entry.Drained;
            }
        }

        if (completedBarriers is not null)
        {
            foreach (var barrier in completedBarriers)
            {
                barrier.TrySetResult();
            }
        }
        retirement?.TrySetResult();
    }

    private static LeaseBarrier CaptureLeaseBarrier(SessionEntry entry)
    {
        var barrier = new LeaseBarrier(entry.ActiveLeaseIds);
        if (barrier.PendingLeaseIds.Count == 0)
        {
            barrier.Completion.TrySetResult();
        }
        else
        {
            entry.LeaseBarriers.Add(barrier);
        }
        return barrier;
    }

    private static LeaseBarrier CapturePackageLeaseBarrier(SessionEntry entry, string packageId)
    {
        var barrier = new LeaseBarrier(
            entry.PackageLeaseIds.GetValueOrDefault(packageId) ?? []);
        if (barrier.PendingLeaseIds.Count == 0)
        {
            barrier.Completion.TrySetResult();
        }
        else
        {
            entry.LeaseBarriers.Add(barrier);
        }
        return barrier;
    }

    private static void MarkPackageLeaseUnderLock(PackageSessionLease lease, string packageId)
    {
        if (lease.Identity is not SessionEntry entry
            || !entry.ActiveLeaseIds.Contains(lease.LeaseId))
        {
            return;
        }
        if (!entry.PackageLeaseIds.TryGetValue(packageId, out var leaseIds))
        {
            leaseIds = [];
            entry.PackageLeaseIds.Add(packageId, leaseIds);
        }
        leaseIds.Add(lease.LeaseId);
    }

    private void SignalRetirementIfDrained(SessionEntry entry)
    {
        if (entry.ActiveLeaseIds.Count == 0 && !HasFaultPublicationUnderLock(entry))
        {
            entry.Drained.TrySetResult();
        }
    }

    private bool HasFaultPublicationUnderLock(SessionEntry entry)
        => _faultPublication is { } publication
           && ReferenceEquals(publication.SessionEntry, entry);

    private SessionPublicationSnapshot CapturePublicationSnapshotUnderLock()
    {
        Debug.Assert(Monitor.IsEntered(_syncRoot));
        return SessionPublicationSnapshot.Create(
            _generation,
            _activeEntry.Session.GetActivePackages(),
            _activeEntry.Session.GetSessionPackages());
    }

    private async Task DeactivateLoadedPackageAsync(string packageId, ActiveLoadedPackage loadedPackage)
    {
        await RuntimeWorkerLifecycle.ProveStoppedAndDisposedAsync(
            loadedPackage.Worker,
            packageId,
            logger,
            _sessionDrainTimeout).ConfigureAwait(false);
    }

    private MutableSessionPublication RequireMutablePublication(SessionPublication publication)
    {
        if (publication is not MutableSessionPublication mutablePublication
            || !ReferenceEquals(mutablePublication.Owner, this))
        {
            throw new InvalidOperationException(
                "The package session publication does not belong to this session state.");
        }
        return mutablePublication;
    }

    private sealed class SessionEntry(
        ActivePackageSession session,
        long generation,
        bool acceptingLeases = true,
        long? activationGeneration = null)
    {
        public ActivePackageSession Session { get; } = session;
        public long Generation { get; set; } = generation;
        public SessionGenerationEpoch GenerationEpoch { get; set; } = new(generation);
        public HashSet<SessionGenerationEpoch> RetiredGenerationEpochs { get; } = [];
        public long ActivationGeneration { get; set; } = activationGeneration ?? generation;
        public HashSet<long> ActiveLeaseIds { get; } = [];
        public Dictionary<string, HashSet<long>> PackageLeaseIds { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<LeaseBarrier> LeaseBarriers { get; } = [];
        public List<Task> PendingCleanups { get; } = [];
        public TaskCompletionSource Drained { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Retirement { get; set; } = new();
        public Task RetirementCallbacks { get; set; } = Task.CompletedTask;
        public long LastLeaseId { get; set; }
        public bool Retired { get; set; }
        public bool Draining { get; set; }
        public ActivePackageSession? DrainSuccessor { get; set; }
        public bool AcceptingLeases { get; set; } = acceptingLeases;
    }

    internal interface SessionPublication
    {
        ActivePackageSession Session { get; }
        ActivePackageSession PreviousSession { get; }
        long? ExpectedGeneration { get; }
        long CandidateGeneration { get; }
        bool Completed { get; }
        bool Committed { get; }
        bool PreviousWorkersQuiesced { get; }
        bool RecoveryCommitted { get; }
        bool Abandoned { get; }
    }

    internal sealed class SessionPublicationSnapshot
    {
        private SessionPublicationSnapshot(
            long generation,
            IEnumerable<ActivePackageDescriptor> activePackages,
            IEnumerable<SessionPackageDescriptor> sessionPackages)
        {
            Generation = generation;
            ActivePackages = Freeze(activePackages.Select(Copy));
            SessionPackages = Freeze(sessionPackages.Select(Copy));
        }

        public long Generation { get; }
        public IReadOnlyList<ActivePackageDescriptor> ActivePackages { get; }
        public IReadOnlyList<SessionPackageDescriptor> SessionPackages { get; }

        internal static SessionPublicationSnapshot Create(
            long generation,
            IEnumerable<ActivePackageDescriptor> activePackages,
            IEnumerable<SessionPackageDescriptor> sessionPackages)
            => new(generation, activePackages, sessionPackages);

        private static ActivePackageDescriptor Copy(ActivePackageDescriptor package)
            => package with
            {
                Icon = Copy(package.Icon),
                Views = Freeze(package.Views.Select(Copy)),
            };

        private static SessionPackageDescriptor Copy(SessionPackageDescriptor package)
            => package with
            {
                Icon = Copy(package.Icon),
                Views = Freeze(package.Views.Select(Copy)),
                RpcContractUses = Freeze(package.RpcContractUses.Select(use => use with
                {
                    Actions = Freeze(use.Actions),
                })),
            };

        private static PackageViewDescriptor Copy(PackageViewDescriptor view)
            => view with { Icon = Copy(view.Icon) };

        private static PackageIconDescriptor? Copy(PackageIconDescriptor? icon)
            => icon is null ? null : icon with { };

        private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values)
            => Array.AsReadOnly(values.ToArray());
    }

    private sealed class MutableSessionPublication : SessionPublication
    {
        public MutableSessionPublication(
            PackageSessionState owner,
            ActivePackageSession session,
            SessionEntry previousEntry,
            CancellationTokenSource retirement,
            long? expectedGeneration,
            long candidateGeneration)
        {
            Owner = owner;
            Session = session;
            PreviousEntry = previousEntry;
            Retirement = retirement;
            ExpectedGeneration = expectedGeneration;
            CandidateGeneration = candidateGeneration;
            CandidateEntry = new SessionEntry(
                session,
                candidateGeneration,
                acceptingLeases: false,
                activationGeneration: candidateGeneration);
        }

        public PackageSessionState Owner { get; }
        public ActivePackageSession Session { get; }
        public ActivePackageSession PreviousSession => PreviousEntry.Session;
        public long? ExpectedGeneration { get; }
        public long CandidateGeneration { get; }
        public SessionEntry PreviousEntry { get; }
        public CancellationTokenSource Retirement { get; }
        public SessionEntry CandidateEntry { get; }
        public bool Completed { get; set; }
        public bool Committed { get; set; }
        public bool PreviousWorkersQuiesced { get; set; }
        public bool Recovering { get; set; }
        public bool RecoveryCommitted { get; set; }
        public bool Abandoned { get; set; }
        public List<DrainingPackageFault> DrainingFaults { get; } = [];
    }

    internal sealed class LeaseBarrier(IEnumerable<long> leaseIds)
    {
        public HashSet<long> PendingLeaseIds { get; } = [.. leaseIds];
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PackageDeactivationWork(
        string packageId,
        ActiveLoadedPackage loadedPackage,
        Task sessionLeasesDrained,
        TaskCompletionSource completion)
    {
        public string PackageId { get; } = packageId;
        public ActiveLoadedPackage LoadedPackage { get; } = loadedPackage;
        public Task SessionLeasesDrained { get; } = sessionLeasesDrained;
        public Task GenerationRetirementCallbacks { get; set; } = Task.CompletedTask;
        public Task RpcRetirementCallbacks { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Completion { get; } = completion;
        public Task? StartedTask { get; set; }
    }

    internal sealed record RpcReadinessRefresh(
        long Generation,
        IReadOnlyList<ActivePackageDescriptor> ActivePackages,
        IReadOnlyList<SessionPackageDescriptor> SessionPackages,
        IReadOnlyList<string> ChangedPackageIds);

    private sealed record PackageRecoveryActivation(
        RuntimePackageRecoveryRequest Request,
        ActiveLoadedPackage Replacement,
        SessionEntry SessionEntry);

    private sealed record PackageRecoveryCommit(
        SessionEntry SessionEntry,
        long PreviousGeneration,
        SessionGenerationEpoch PublishedEpoch);

    private enum PackageFaultTargetKind
    {
        PackageActivation,
        RuntimeGeneration,
    }

    private readonly record struct PackageFaultTarget(
        PackageFaultTargetKind Kind,
        PackageActivationIdentity PackageActivation,
        RuntimePackageGeneration? RuntimeGeneration)
    {
        public static PackageFaultTarget ForPackageActivation(
            PackageActivationIdentity activationIdentity)
            => new(
                PackageFaultTargetKind.PackageActivation,
                activationIdentity,
                RuntimeGeneration: null);

        public static PackageFaultTarget ForRuntimeGeneration(
            RuntimePackageGeneration runtimeGeneration)
            => new(
                PackageFaultTargetKind.RuntimeGeneration,
                PackageActivation: default,
                runtimeGeneration);

        public bool MatchesSession(SessionEntry entry)
            => Kind != PackageFaultTargetKind.PackageActivation
               || ReferenceEquals(PackageActivation.SessionIdentity, entry);

        public bool IsOwnedBy(SessionEntry entry, string packageId)
            => Kind == PackageFaultTargetKind.PackageActivation
                ? entry.Session.OwnsPackageActivation(
                    packageId,
                    PackageActivation.RuntimeActivationId)
                : entry.Session.OwnsRuntimeGeneration(packageId, RuntimeGeneration!);

        public void LogFault(
            ILogger logger,
            Exception exception,
            string action,
            string packageId)
        {
            if (Kind == PackageFaultTargetKind.RuntimeGeneration)
            {
                logger.LogError(
                    exception,
                    "Failed to {Action} for exact Runtime generation {ActivationId} of package {PackageId}; package disabled for current session",
                    action,
                    RuntimeGeneration!.ActivationId,
                    packageId);
                return;
            }

            logger.LogError(
                exception,
                "Failed to {Action} for package {PackageId}; package disabled for current session",
                action,
                packageId);
        }
    }

    private sealed record FaultPublicationReservation(
        SessionEntry SessionEntry,
        long Generation);

    internal sealed class SessionGenerationEpoch(long generation)
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _retirement = new();
        private Task? _retirementTask;

        public long Generation { get; } = generation;
        public CancellationToken RetirementToken => _retirement.Token;

        public Task RetireAsync()
        {
            TaskCompletionSource completion;
            Task callbacks;
            lock (_gate)
            {
                if (_retirementTask is not null)
                {
                    return _retirementTask;
                }
                completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _retirementTask = completion.Task;
                callbacks = RuntimeCancellation.Signal(_retirement);
            }
            _ = RetireCoreAsync(completion, callbacks);
            return completion.Task;
        }

        private async Task RetireCoreAsync(
            TaskCompletionSource completion,
            Task callbacks)
        {
            try
            {
                await callbacks.ConfigureAwait(false);
                _retirement.Dispose();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }
}
