using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record DrainingPackageFault(
    string PackageId,
    RuntimePackageGeneration RuntimeGeneration,
    PackageFailureOrigin Origin,
    Exception Exception);

internal sealed class PackageSessionState(
    ILogger logger,
    Action clearAuthSessions,
    Action<string> removePackageAuthSessions,
    TimeSpan? sessionDrainTimeout = null)
{
    private readonly object _syncRoot = new();
    private SessionEntry _activeEntry = new(ActivePackageSession.CreateEmpty(), generation: 0);
    private SessionPublication? _pendingPublication;
    private SessionPublication? _rejectedPublication;
    private SessionPublication? _recoveryPublication;
    private long _generation;
    private readonly TimeSpan _sessionDrainTimeout = sessionDrainTimeout ?? TimeSpan.FromSeconds(10);

    public PackageSessionLease AcquireLease()
    {
        lock (_syncRoot)
        {
            var entry = _activeEntry;
            if (entry.Draining)
            {
                throw new RuntimeUnavailableException("The active package session is draining and is not accepting new requests.");
            }
            if (!entry.AcceptingLeases)
            {
                throw new RuntimeUnavailableException("The active package session is still activating and is not accepting requests.");
            }
            return CreateLeaseUnderLock(entry);
        }
    }

    internal PackageSessionLease AcquirePackageActivationLease(
        string packageId,
        RuntimePackageGeneration generation)
    {
        lock (_syncRoot)
        {
            SessionEntry? entry = null;
            if (!_activeEntry.Draining
                && _activeEntry.AcceptingLeases
                && _activeEntry.ActivationGeneration == generation.SessionGeneration
                && _activeEntry.Session.OwnsRuntimeGeneration(packageId, generation))
            {
                entry = _activeEntry;
            }
            else if (_pendingPublication is { Completed: false, CandidateEntry: { } candidate }
                     && !candidate.Draining
                     && candidate.ActivationGeneration == generation.SessionGeneration
                     && candidate.Session.OwnsRuntimeGeneration(packageId, generation))
            {
                entry = candidate;
            }
            else if (_recoveryPublication is { Recovering: true } recovery
                     && ReferenceEquals(_activeEntry, recovery.PreviousEntry)
                     && _activeEntry.ActivationGeneration == generation.SessionGeneration
                     && _activeEntry.Session.OwnsRuntimeGeneration(packageId, generation))
            {
                entry = _activeEntry;
            }
            if (entry is null)
            {
                throw new RuntimeUnavailableException(
                    "The package Runtime generation is stale or no longer accepting package-owned work.");
            }
            return CreateLeaseUnderLock(entry);
        }
    }

    private PackageSessionLease CreateLeaseUnderLock(SessionEntry entry)
    {
        var leaseId = ++entry.LastLeaseId;
        entry.ActiveLeaseIds.Add(leaseId);
        return new PackageSessionLease(
            entry.Session,
            entry.Generation,
            entry,
            entry.Retirement.Token,
            () => ReleaseLease(entry, leaseId));
    }

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages()
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.GetActivePackages();
        }
    }

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages()
    {
        lock (_syncRoot)
        {
            return _activeEntry.Session.GetSessionPackages();
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
            return lease.Session.LoadedPackageMap.Values
                .Where(package => lease.Session.IsPackageEnabled(package.Descriptor.PackageId))
                .ToArray();
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
            return await CommitPublicationAsync(publication, committed);
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
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            previousEntry.Draining = true;
            retirement = previousEntry.Retirement;
            SignalRetirementIfDrained(previousEntry);
        }

        previousEntry.RetirementCallbacks = CombineRetirementCallbacks(
            previousEntry.RetirementCallbacks,
            RuntimeCancellation.Signal(retirement));
        try
        {
            await previousEntry.Drained.Task.WaitAsync(_sessionDrainTimeout, cancellationToken);
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
                var publication = new SessionPublication(
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
        SessionPublication publication,
        Action<long>? committed = null)
    {
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
            publication.PreviousEntry.Retired = true;
            publication.Completed = true;
            publication.Committed = true;
            _generation = generation;
            _activeEntry = publication.CandidateEntry;
            _activeEntry.Generation = generation;
            _pendingPublication = null;

            try
            {
                committed?.Invoke(generation);
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

    internal void DiscardPublication(SessionPublication publication)
    {
        if (RejectPublication(publication))
        {
            RestoreRejectedPublication(publication);
        }
    }

    internal bool RejectPublication(SessionPublication publication)
    {
        SessionEntry candidate;
        lock (_syncRoot)
        {
            if (publication.Completed)
            {
                return false;
            }
            publication.Completed = true;
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

    internal void RestoreRejectedPublication(SessionPublication publication)
    {
        if (!TryRestoreRejectedPublication(publication))
        {
            throw new InvalidOperationException(
                "A quiesced or faulted previous session must be recovered as a fresh Runtime generation.");
        }
    }

    internal bool TryRestoreRejectedPublication(SessionPublication publication)
    {
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

    internal async Task WaitForPreviousRetirementCallbacksAsync(SessionPublication publication)
        => await publication.PreviousEntry.RetirementCallbacks.ConfigureAwait(false);

    internal void MarkPreviousWorkersQuiesced(SessionPublication publication)
    {
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
                    exception));
            }
            return true;
        }
    }

    internal IReadOnlyList<DrainingPackageFault> GetDrainingFaults(SessionPublication publication)
    {
        lock (_syncRoot)
        {
            return publication.DrainingFaults.ToArray();
        }
    }

    internal async Task BeginRecoveryPublicationAsync(SessionPublication publication)
    {
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
        SessionPublication publication,
        Action<long>? committed = null)
    {
        Exception? publicationException = null;
        long generation;
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
            _generation = generation;
            publication.PreviousEntry.Generation = generation;
            publication.PreviousEntry.Draining = false;
            publication.PreviousEntry.AcceptingLeases = false;
            publication.Recovering = false;
            publication.RecoveryCommitted = true;
            _recoveryPublication = null;
            try
            {
                committed?.Invoke(generation);
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
        SessionPublication publication,
        Action<long>? committed = null)
    {
        SessionEntry abandoned;
        long generation;
        Exception? publicationException = null;
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
            _activeEntry = new SessionEntry(
                ActivePackageSession.CreateEmpty(),
                generation,
                acceptingLeases: false,
                activationGeneration: generation);
            publication.Abandoned = true;
            try
            {
                committed?.Invoke(generation);
                clearAuthSessions();
            }
            catch (Exception exception)
            {
                publicationException = exception;
            }
        }
        abandoned.RetirementCallbacks = CombineRetirementCallbacks(
            abandoned.RetirementCallbacks,
            RuntimeCancellation.Signal(abandoned.Retirement));
        if (publicationException is not null)
        {
            throw publicationException;
        }
        return generation;
    }

    internal Task<IReadOnlyList<string>> RetireAbandonedPublicationAsync(
        SessionPublication publication)
    {
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

    internal Task WaitForRecoveryPackageCallsAsync(SessionPublication publication)
    {
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

    internal async Task WaitForDiscardedCandidateDrainAsync(SessionPublication publication)
    {
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
    {
        ActiveLoadedPackage? packageToDeactivate;
        PackageDeactivationWork? deactivation;
        long committedGeneration;
        lock (_syncRoot)
        {
            if (expectedGeneration != _generation
                || !ReferenceEquals(activationIdentity.SessionIdentity, _activeEntry)
                || !_activeEntry.AcceptingLeases
                || _activeEntry.Draining
                || !_activeEntry.Session.OwnsPackageActivation(
                    packageId,
                    activationIdentity.RuntimeActivationId)
                || !_activeEntry.Session.MarkPackageFailedInternal(
                    packageId,
                    origin,
                    exception.Message,
                    out packageToDeactivate))
            {
                return false;
            }

            _generation = checked(_generation + 1);
            _activeEntry.Generation = _generation;
            committedGeneration = _generation;
            deactivation = CreatePackageDeactivationLocked(packageId, packageToDeactivate);
        }

        if (deactivation is not null)
        {
            deactivation.RpcRetirementCallbacks = committed?.Invoke(committedGeneration) ?? Task.CompletedTask;
        }
        else if (committed is not null)
        {
            _ = committed(committedGeneration);
        }
        removePackageAuthSessions(packageId);
        QueuePackageDeactivation(deactivation);
        logger.LogError(exception, "Failed to {Action} for package {PackageId}; package disabled for current session", action, packageId);
        return true;
    }

    public bool HandleRuntimeGenerationFault(
        string packageId,
        RuntimePackageGeneration runtimeGeneration,
        long expectedGeneration,
        PackageFailureOrigin origin,
        Exception exception,
        string action,
        Func<long, Task>? committed = null)
    {
        ActiveLoadedPackage? packageToDeactivate;
        PackageDeactivationWork? deactivation;
        long committedGeneration;
        lock (_syncRoot)
        {
            if (expectedGeneration != _generation
                || !_activeEntry.AcceptingLeases
                || _activeEntry.Draining
                || !_activeEntry.Session.OwnsRuntimeGeneration(packageId, runtimeGeneration)
                || !_activeEntry.Session.MarkPackageFailedInternal(
                    packageId,
                    origin,
                    exception.Message,
                    out packageToDeactivate))
            {
                return false;
            }

            _generation = checked(_generation + 1);
            _activeEntry.Generation = _generation;
            committedGeneration = _generation;
            deactivation = CreatePackageDeactivationLocked(packageId, packageToDeactivate);
        }

        if (deactivation is not null)
        {
            deactivation.RpcRetirementCallbacks = committed?.Invoke(committedGeneration) ?? Task.CompletedTask;
        }
        else if (committed is not null)
        {
            _ = committed(committedGeneration);
        }
        removePackageAuthSessions(packageId);
        QueuePackageDeactivation(deactivation);
        logger.LogError(
            exception,
            "Failed to {Action} for exact Runtime generation {ActivationId} of package {PackageId}; package disabled for current session",
            action,
            runtimeGeneration.ActivationId,
            packageId);
        return true;
    }

    public ActiveLoadedPackage? GetLoadedPackage(PackageSessionLease lease, string packageId)
    {
        lock (_syncRoot)
        {
            return lease.Session.TryGetLoadedPackage(packageId, out var loadedPackage) ? loadedPackage : null;
        }
    }

    public bool ExecuteIfCurrent(PackageSessionLease lease, Func<bool> action)
    {
        lock (_syncRoot)
        {
            if (!ReferenceEquals(lease.Identity, _activeEntry)
                || lease.Generation != _activeEntry.Generation
                || _activeEntry.Draining)
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
            return new RpcReadinessRefresh(
                _generation,
                _activeEntry.Session.GetActivePackages(),
                _activeEntry.Session.GetSessionPackages(),
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

        var barrier = CaptureLeaseBarrier(_activeEntry);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeEntry.PendingCleanups.Add(completion.Task);
        return new PackageDeactivationWork(
            packageId,
            loadedPackage,
            barrier.Completion.Task,
            completion);
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
                work.RpcRetirementCallbacks);
            try
            {
                await retirementBarriers.WaitAsync(_sessionDrainTimeout);
            }
            catch (TimeoutException exception)
            {
                logger.LogWarning(
                    exception,
                    "Package {PackageId} session retirement exceeded the drain deadline; its provider and load context remain quarantined",
                    work.PackageId);
                await retirementBarriers;
            }
            await DeactivateLoadedPackageAsync(work.PackageId, work.LoadedPackage);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deactivate package {PackageId} after a package fault", work.PackageId);
        }
        finally
        {
            work.Completion.TrySetResult();
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
        Task[] pendingCleanups;
        lock (_syncRoot)
        {
            pendingCleanups = entry.PendingCleanups.ToArray();
        }
        await Task.WhenAll(pendingCleanups);
        await entry.RetirementCallbacks.ConfigureAwait(false);

        if (entry.Session.IsEmpty)
        {
            RuntimeCancellation.DisposeAfterCallbacks(entry.Retirement, entry.RetirementCallbacks);
            return [];
        }

        var warnings = new List<string>();
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

            foreach (var barrier in entry.LeaseBarriers.ToArray())
            {
                barrier.PendingLeaseIds.Remove(leaseId);
                if (barrier.PendingLeaseIds.Count == 0)
                {
                    entry.LeaseBarriers.Remove(barrier);
                    (completedBarriers ??= []).Add(barrier.Completion);
                }
            }

            if ((entry.Draining || entry.Retired) && entry.ActiveLeaseIds.Count == 0)
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

    private static void SignalRetirementIfDrained(SessionEntry entry)
    {
        if (entry.ActiveLeaseIds.Count == 0)
        {
            entry.Drained.TrySetResult();
        }
    }

    private async Task DeactivateLoadedPackageAsync(string packageId, ActiveLoadedPackage loadedPackage)
    {
        var lifecycleOperations = new List<Task>();
        await RuntimeWorkerLifecycle.StopWorkersAsync(
            [loadedPackage.Worker],
            packageId,
            logger,
            operationStarted: lifecycleOperations.Add);
        try
        {
            await Task.WhenAll(lifecycleOperations);
        }
        catch
        {
            // Stop failures were logged above; disposal must still wait until the worker exits.
        }
        await loadedPackage.Worker.DisposeAsync();
    }

    internal sealed class SessionEntry(
        ActivePackageSession session,
        long generation,
        bool acceptingLeases = true,
        long? activationGeneration = null)
    {
        public ActivePackageSession Session { get; } = session;
        public long Generation { get; set; } = generation;
        public long ActivationGeneration { get; set; } = activationGeneration ?? generation;
        public HashSet<long> ActiveLeaseIds { get; } = [];
        public List<LeaseBarrier> LeaseBarriers { get; } = [];
        public List<Task> PendingCleanups { get; } = [];
        public TaskCompletionSource Drained { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Retirement { get; set; } = new();
        public Task RetirementCallbacks { get; set; } = Task.CompletedTask;
        public long LastLeaseId { get; set; }
        public bool Retired { get; set; }
        public bool Draining { get; set; }
        public bool AcceptingLeases { get; set; } = acceptingLeases;
    }

    internal sealed class SessionPublication
    {
        private readonly SessionEntry _previousEntry;
        private readonly CancellationTokenSource _retirement;

        internal SessionPublication(
            ActivePackageSession session,
            SessionEntry previousEntry,
            CancellationTokenSource retirement,
            long? expectedGeneration,
            long candidateGeneration)
        {
            Session = session;
            _previousEntry = previousEntry;
            _retirement = retirement;
            ExpectedGeneration = expectedGeneration;
            CandidateGeneration = candidateGeneration;
            CandidateEntry = new SessionEntry(
                session,
                candidateGeneration,
                acceptingLeases: false,
                activationGeneration: candidateGeneration);
        }

        public ActivePackageSession Session { get; }
        internal SessionEntry PreviousEntry => _previousEntry;
        internal CancellationTokenSource Retirement => _retirement;
        public long? ExpectedGeneration { get; }
        public long CandidateGeneration { get; }
        internal SessionEntry CandidateEntry { get; }
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
        public Task RpcRetirementCallbacks { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Completion { get; } = completion;
        public Task? StartedTask { get; set; }
    }

    internal sealed record RpcReadinessRefresh(
        long Generation,
        IReadOnlyList<ActivePackageDescriptor> ActivePackages,
        IReadOnlyList<SessionPackageDescriptor> SessionPackages,
        IReadOnlyList<string> ChangedPackageIds);
}
