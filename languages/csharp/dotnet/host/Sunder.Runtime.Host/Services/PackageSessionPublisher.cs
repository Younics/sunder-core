using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record PreparedPackageSession(
    ActivePackageSession Session,
    PackageSessionSourceSnapshot Sources,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    long BaseGeneration,
    string? StageId,
    bool RequiresPackageStoreCommit)
{
    internal IReadOnlyCollection<string>? RetainWorkersExcept { get; init; }
    internal bool DevelopmentWorkerContinuation { get; init; }
    internal bool UnplannedWithdrawal { get; init; }
    internal bool AppSourceWithdrawal { get; init; }
}

internal sealed class PendingPackageSessionPublication(
    PreparedPackageSession candidate,
    PackageSessionState.SessionPublication publication)
{
    private readonly object _gate = new();
    private Task? _candidateStartTask;
    internal IDisposable? AppAdmissionFence { get; init; }

    public PreparedPackageSession Candidate { get; } = candidate;

    public PackageSessionState.SessionPublication Publication { get; } = publication;

    public bool DurablePackageStoreCommitted { get; private set; }

    public bool PackageStoreOutcomeIndeterminate { get; private set; }

    internal bool PreviousAuthorityCannotBeRestored =>
        DurablePackageStoreCommitted || PackageStoreOutcomeIndeterminate;

    internal bool CandidateStartRequested
    {
        get
        {
            lock (_gate) return _candidateStartTask is not null;
        }
    }

    internal Task GetOrStartCandidate(Func<Task> start)
    {
        lock (_gate)
        {
            return _candidateStartTask ??= start();
        }
    }

    internal void MarkPackageStoreCommitted()
    {
        if (!Candidate.RequiresPackageStoreCommit)
        {
            throw new InvalidOperationException(
                "Only a package-store publication can cross the durable package-store boundary.");
        }
        DurablePackageStoreCommitted = true;
    }

    internal void MarkPackageStoreIndeterminate()
    {
        if (!Candidate.RequiresPackageStoreCommit)
        {
            throw new InvalidOperationException(
                "Only a package-store publication can have an indeterminate package-store outcome.");
        }
        PackageStoreOutcomeIndeterminate = true;
    }
}

internal sealed record PackageSessionPublicationResult(
    RuntimePackageStamp Stamp,
    IReadOnlyList<string> CleanupWarnings,
    bool ReconciliationPending = false);

internal sealed class PackageSessionPublisher(
    RuntimeSessionOwner sessions,
    ILogger<PackageSessionPublisher> logger,
    RuntimeLifecyclePolicyOptions? lifecyclePolicy = null,
    RuntimeRpcCatalog? rpcCatalog = null,
    RuntimeRpcPermissionStore? rpcPermissions = null,
    RuntimeRpcReadinessEvaluator? rpcReadiness = null,
    TimeProvider? timeProvider = null,
    AppProcessPolicySessions? appPolicies = null)
{
    private readonly RuntimeLifecyclePolicyOptions _lifecyclePolicy = ValidateLifecyclePolicy(
        lifecyclePolicy ?? new RuntimeLifecyclePolicyOptions());
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public PreparedPackageSession Prepare(
        ActivePackageSession session,
        PackageSessionSourceSnapshot sources,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        long baseGeneration,
        string? stageId = null,
        bool requiresPackageStoreCommit = false,
        IReadOnlyCollection<string>? retainWorkersExcept = null,
        bool developmentWorkerContinuation = false,
        bool unplannedWithdrawal = false)
    {
        if (rpcReadiness is not null) session.RequiredProviderPlan = rpcReadiness.Plan(session);
        return new PreparedPackageSession(
            session,
            sources,
            warnings.ToArray(),
            errors.ToArray(),
            baseGeneration,
            stageId,
            requiresPackageStoreCommit)
        {
            RetainWorkersExcept = requiresPackageStoreCommit ? null : retainWorkersExcept,
            DevelopmentWorkerContinuation = !requiresPackageStoreCommit && developmentWorkerContinuation,
            UnplannedWithdrawal = unplannedWithdrawal,
        };
    }

    public async Task<PackageSessionPublicationResult> PublishAsync(
        ActivePackageSession session,
        PackageSessionSourceSnapshot? sources,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        long expectedGeneration,
        CancellationToken cancellationToken,
        Action<PendingPackageSessionPublication>? publicationPrepared = null,
        string? committedStoreTransactionId = null,
        Func<Task>? candidateGenerationCommitted = null,
        Func<Task>? previousWorkersQuiesced = null,
        Func<CancellationToken, Task>? storageActivationPreparing = null,
        IReadOnlyCollection<string>? retainWorkersExcept = null,
        bool unplannedWithdrawal = false)
    {
        PreparedPackageSession candidate;
        try
        {
            candidate = Prepare(
                session,
                sources ?? sessions.Sources.Snapshot(),
                warnings,
                errors,
                expectedGeneration,
                stageId: committedStoreTransactionId,
                requiresPackageStoreCommit: committedStoreTransactionId is not null,
                retainWorkersExcept: retainWorkersExcept,
                unplannedWithdrawal: unplannedWithdrawal);
        }
        catch
        {
            await DisposeSessionAsync(session);
            throw;
        }

        var publication = await BeginPublishAsync(candidate, cancellationToken);
        publicationPrepared?.Invoke(publication);
        if (committedStoreTransactionId is not null)
        {
            MarkPackageStoreCommitted(publication);
        }
        return await CommitAsync(
            publication,
            cancellationToken,
            candidateGenerationCommitted,
            previousWorkersQuiesced,
            storageActivationPreparing);
    }

    public async Task<PendingPackageSessionPublication> BeginPublishAsync(
        PreparedPackageSession candidate,
        CancellationToken cancellationToken)
    {
        var preparationStarted = Stopwatch.GetTimestamp();
        IDisposable? appFence = null;
        try
        {
            if (!sessions.Sources.IsCurrent(candidate.Sources)) throw new RuntimeStaleGenerationException("The development owner sources changed after stage preparation.");
            if (!candidate.UnplannedWithdrawal && appPolicies is not null)
                appFence = await appPolicies.FencePublicationAsync(candidate.Session, cancellationToken, candidate.AppSourceWithdrawal);
            if (candidate.RetainWorkersExcept is { } forced)
            {
                using var current = sessions.State.AcquireLease();
                if (current.Generation != candidate.BaseGeneration) throw new RuntimeStaleGenerationException("The Runtime changed before worker retention preparation.");
                await candidate.Session.RetainUnchangedWorkersAsync(current.Session, forced, cancellationToken,
                    candidate.DevelopmentWorkerContinuation).ConfigureAwait(false);
            }
            var publication = await sessions.PreparePublicationAsync(
                candidate.Session,
                candidate.BaseGeneration,
                cancellationToken);
            candidate.Session.StartupTrace?.BindCandidateGeneration(publication.CandidateGeneration);
            logger.LogInformation(
                "Prepared package session publication for generation {SessionGeneration} in {ElapsedMilliseconds} ms",
                publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            return new PendingPackageSessionPublication(candidate, publication) { AppAdmissionFence = appFence };
        }
        catch (Exception exception)
        {
            appFence?.Dispose();
            candidate.Session.CancelRetentionReservation();
            candidate.Session.StartupTrace?.TryFail(
                exception is OperationCanceledException ? "canceled" : "failed");
            logger.LogWarning(
                "Package session publication preparation from generation {BaseGeneration} failed after {ElapsedMilliseconds} ms",
                candidate.BaseGeneration,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            await DiscardAsync(candidate);
            throw;
        }
    }

    public Task StartCandidateAsync(
        PendingPackageSessionPublication pending,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return pending.GetOrStartCandidate(
            () => StartCandidateCoreAsync(pending, cancellationToken));
    }

    public void MarkPackageStoreCommitted(PendingPackageSessionPublication pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        pending.MarkPackageStoreCommitted();
    }

    public void MarkPackageStoreIndeterminate(PendingPackageSessionPublication pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        pending.MarkPackageStoreIndeterminate();
    }

    private async Task StartCandidateCoreAsync(
        PendingPackageSessionPublication pending,
        CancellationToken cancellationToken)
    {
        var session = pending.Candidate.Session;
        if (session.RequiredProviderPlan is not null)
        {
            session.BindStartupGeneration(pending.Publication.CandidateGeneration);
            rpcCatalog?.PrepareStartingCandidate(session, pending.Publication.CandidateGeneration);
            session.CandidateProviderReady = (id, ready) => rpcCatalog?.MarkCandidateProviderReady(session, id, ready);
        }
        await session.StartWorkersAsync(
            logger,
            _lifecyclePolicy.PackageWorkerStartupTimeout,
            _lifecyclePolicy.PackageWorkerCleanupTimeout,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (session.RequiredProviderPlan is not null) rpcCatalog?.CompleteStartingCandidate(session);
        rpcReadiness?.Evaluate(session);
        rpcCatalog?.PrepareCandidateSession(
            session,
            pending.Publication.CandidateGeneration);
    }

    public async Task<PackageSessionPublicationResult> CommitAsync(
        PendingPackageSessionPublication pending,
        CancellationToken cancellationToken = default,
        Func<Task>? candidateGenerationCommitted = null,
        Func<Task>? previousWorkersQuiesced = null,
        Func<CancellationToken, Task>? storageActivationPreparing = null)
    {
        using var appFence = pending.AppAdmissionFence;
        var candidate = pending.Candidate;
        var publicationStarted = Stopwatch.GetTimestamp();
        using var publicationScope = logger.BeginScope(
            "Publishing package session generation {SessionGeneration}",
            pending.Publication.CandidateGeneration);
        try
        {
            var previousSession = pending.Publication.PreviousSession;
            var storageActivationStarted = false;
            if (!candidate.RequiresPackageStoreCommit
                && !pending.CandidateStartRequested
                && previousSession.SharesPersistentWorkspaceWith(candidate.Session))
            {
                await QuiescePreviousWorkersAsync(pending.Publication)
                    .ConfigureAwait(false);
                if (storageActivationPreparing is not null)
                {
                    await storageActivationPreparing(cancellationToken).ConfigureAwait(false);
                }
                await candidate.Session.BeginStorageActivationTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
                storageActivationStarted = true;
            }

            await StartCandidateAsync(pending, cancellationToken).ConfigureAwait(false);
            if (candidate.RequiresPackageStoreCommit && !pending.DurablePackageStoreCommitted)
            {
                throw new InvalidOperationException(
                    "The package session cannot commit its Runtime generation before the package store is authoritative.");
            }

            if (rpcPermissions is not null)
            {
                await rpcPermissions.GrantDeclaredInstalledActionsAsync(
                    RuntimeRpcPermissionSubjects.FromSession(candidate.Session),
                    cancellationToken);
            }
            rpcReadiness?.Evaluate(candidate.Session);
            cancellationToken.ThrowIfCancellationRequested();
            await ActivateRuntimeGenerationAsync(
                candidate.Session,
                pending.Publication.CandidateGeneration,
                () =>
                {
                    candidate.Session.StartRuntimeGenerationMonitoring(sessions.HandleRuntimeGenerationFault);
                },
                async () =>
                {
                    if (candidateGenerationCommitted is not null)
                    {
                        await candidateGenerationCommitted().ConfigureAwait(false);
                    }
                    await QuiescePreviousWorkersAsync(pending.Publication)
                        .ConfigureAwait(false);
                    if (previousWorkersQuiesced is not null)
                    {
                        await previousWorkersQuiesced().ConfigureAwait(false);
                    }
                    if (!storageActivationStarted)
                    {
                        if (storageActivationPreparing is not null)
                        {
                            await storageActivationPreparing(cancellationToken).ConfigureAwait(false);
                        }
                        await candidate.Session.BeginStorageActivationTransactionAsync(cancellationToken)
                            .ConfigureAwait(false);
                        storageActivationStarted = true;
                    }
                },
                cancellationToken);
            var snapshotPublicationStarted = Stopwatch.GetTimestamp();
            Task<(RuntimePackageStamp Stamp, IReadOnlyList<string> Warnings)>? publicationTask = null;
            candidate.Session.PublishCommittedRuntimeGeneration(() =>
            {
                publicationTask = sessions.CommitPublicationAsync(
                    pending.Publication,
                    candidate.Sources,
                    candidate.Warnings,
                    candidate.Errors.Concat(DrainingFaultDiagnostics(pending.Publication)).ToArray(),
                    generation =>
                    {
                        candidate.Session.StartupTrace?.TryCommitGeneration(generation);
                        rpcCatalog?.ActivateSession(candidate.Session, generation);
                        rpcCatalog?.VerifySessionGeneration(generation);
                    });
            });
            var publication = await (publicationTask
                                     ?? throw new InvalidOperationException(
                                         "The candidate Runtime generation did not begin publication."));
            logger.LogInformation(
                "Published package session snapshot for generation {SessionGeneration} in {ElapsedMilliseconds} ms",
                publication.Stamp.SessionGeneration,
                Stopwatch.GetElapsedTime(snapshotPublicationStarted).TotalMilliseconds);
            candidate.Session.CompleteStorageMigrations();
            logger.LogInformation(
                "Published package session generation {SessionGeneration} with {ActivePackageCount} active package(s) in {ElapsedMilliseconds} ms",
                publication.Stamp.SessionGeneration,
                candidate.Session.GetActivePackages().Count,
                Stopwatch.GetElapsedTime(publicationStarted).TotalMilliseconds);
            return new PackageSessionPublicationResult(publication.Stamp, publication.Warnings);
        }
        catch (Exception exception)
        {
            if (!pending.Publication.Committed)
            {
                candidate.Session.StartupTrace?.TryFail(
                    exception is OperationCanceledException ? "canceled" : "failed");
            }
            logger.LogWarning(
                "Package session publication for generation {SessionGeneration} failed after {ElapsedMilliseconds} ms; starting failure recovery",
                pending.Publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(publicationStarted).TotalMilliseconds);
            if (pending.Publication.Committed)
            {
                candidate.Session.CompleteStorageMigrations();
            }
            rpcCatalog?.DiscardCandidateSession(candidate.Session);
            if (pending.Publication.Committed)
            {
                sessions.DiscardPublication(pending.Publication);
                AlignRpcCatalogToActiveSession();
                const string warning = "The package session was applied, but publishing its Runtime snapshot did not complete; reconciliation is pending.";
                logger.LogError(exception, "Package session publication failed after Runtime generation activation completed");
                logger.LogWarning(
                    "Package session generation {SessionGeneration} requires reconciliation after {ElapsedMilliseconds} ms",
                    pending.Publication.CandidateGeneration,
                    Stopwatch.GetElapsedTime(publicationStarted).TotalMilliseconds);
                return new PackageSessionPublicationResult(
                    sessions.Stamp,
                    [warning],
                    ReconciliationPending: true);
            }
            if (pending.PreviousAuthorityCannotBeRestored)
            {
                var committedFailure = await RejectUnrestorableStorePublicationAsync(
                    pending,
                    exception).ConfigureAwait(false);
                if (committedFailure is not null)
                {
                    throw new AggregateException(
                        "Package session publication failed after the package store commit and Runtime unavailability cleanup did not complete cleanly.",
                        exception,
                        committedFailure);
                }
                throw;
            }
            if (!sessions.RejectPublication(pending.Publication))
            {
                throw new InvalidOperationException(
                    "The failed candidate publication could not be rejected.",
                    exception);
            }
            var cleanupFailure = await DisposeFailedCandidateAsync(
                candidate.Session,
                pending.Publication);
            if (cleanupFailure is not null)
            {
                var abandonmentFailure = await AbandonPreviousSessionAfterFailedRecoveryAsync(
                    pending.Publication,
                    exception,
                    cleanupFailure).ConfigureAwait(false);
                var failures = abandonmentFailure is null
                    ? new[] { exception, cleanupFailure }
                    : new[] { exception, cleanupFailure, abandonmentFailure };
                throw new AggregateException(
                    "Package session publication failed and candidate rollback did not complete cleanly.",
                    failures);
            }
            if (exception is PackageStorageRollbackException)
            {
                var abandonmentFailure = await AbandonPreviousSessionAsync(
                    pending.Publication,
                    [$"Package storage rollback failed during candidate publication: {exception.Message}"],
                    "package storage rollback failure").ConfigureAwait(false);
                if (abandonmentFailure is not null)
                {
                    throw new AggregateException(
                        "Package session publication failed, storage rollback was incomplete, and Runtime unavailability cleanup failed.",
                        exception,
                        abandonmentFailure);
                }
                throw;
            }
            var recoveryFailure = await RecoverOrRestorePreviousSessionAsync(
                pending.Publication,
                exception).ConfigureAwait(false);
            if (recoveryFailure is not null)
            {
                throw new AggregateException(
                    "Package session publication failed and the previous Runtime generation could not be recovered.",
                    exception,
                    recoveryFailure);
            }
            AlignRpcCatalogToActiveSession();
            throw;
        }
    }

    private async Task QuiescePreviousWorkersAsync(
        PackageSessionState.SessionPublication publication)
    {
        if (publication.PreviousWorkersQuiesced
            || publication.PreviousSession.LoadedPackageMap.Count == 0)
        {
            return;
        }

        sessions.MarkPreviousWorkersQuiesced(publication);
        await sessions.RetireReplacedRpcActivationsAsync(publication).ConfigureAwait(false);
        await sessions.WaitForPreviousRetirementCallbacksAsync(publication)
            .ConfigureAwait(false);
        await publication.PreviousSession.QuiesceWorkersAsync(
            logger,
            _lifecyclePolicy.PackageWorkerCleanupTimeout).ConfigureAwait(false);
    }

    private void AlignRpcCatalogToActiveSession()
    {
        if (rpcCatalog is null)
        {
            return;
        }

        PackageSessionLease sessionLease;
        try
        {
            sessionLease = sessions.State.AcquireLease();
        }
        catch (RuntimeUnavailableException)
        {
            return;
        }

        using (sessionLease)
        {
            var generation = sessionLease.Generation;
            while (true)
            {
                if (rpcCatalog.SessionGeneration != generation)
                {
                    rpcCatalog.ActivateSession(sessionLease.Session, generation);
                }
                var currentGeneration = sessions.Generation;
                if (currentGeneration == generation)
                {
                    rpcCatalog.VerifySessionGeneration(generation);
                    return;
                }
                generation = currentGeneration;
            }
        }
    }

    private async Task CommitRuntimeGenerationAsync(
        ActivePackageSession session,
        long sessionGeneration,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var attemptStarted = Stopwatch.GetTimestamp();
            try
            {
                await session.CommitRuntimeGenerationAsync(
                    sessionGeneration,
                    cancellationToken);
                logger.LogInformation(
                    "Committed package Runtime generation {SessionGeneration} on attempt {Attempt} in {ElapsedMilliseconds} ms",
                    sessionGeneration,
                    attempt,
                    Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds);
                return;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && attempt < _lifecyclePolicy.PackageRuntimeGenerationActivationAttempts)
            {
                logger.LogWarning(
                    exception,
                    "Package Runtime generation activation attempt {Attempt} of {MaximumAttempts} failed after {ElapsedMilliseconds} ms; retrying the exact generation",
                    attempt,
                    _lifecyclePolicy.PackageRuntimeGenerationActivationAttempts,
                    Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds);
            }
        }
    }

    private async Task ActivateRuntimeGenerationAsync(
        ActivePackageSession session,
        long sessionGeneration,
        Action prepareActivation,
        Func<Task>? generationCommitted,
        CancellationToken cancellationToken)
    {
        var activationStarted = Stopwatch.GetTimestamp();
        try
        {
            using var timeoutCancellation = new CancellationTokenSource(
                _lifecyclePolicy.PackageRuntimeGenerationActivationTimeout);
            using var activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            try
            {
                await CommitRuntimeGenerationAsync(
                    session,
                    sessionGeneration,
                    activationCancellation.Token).ConfigureAwait(false);
                prepareActivation();
                if (generationCommitted is not null)
                {
                    await generationCommitted().ConfigureAwait(false);
                }
                await session.ActivateCommittedRuntimeGenerationAsync(activationCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                timeoutCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Package Runtime generation {sessionGeneration} did not complete commit, migration preparation, and activation acknowledgement within {_lifecyclePolicy.PackageRuntimeGenerationActivationTimeout.TotalSeconds:0.###} seconds.",
                    exception);
            }
            logger.LogInformation(
                "Activated package Runtime generation {SessionGeneration} for {PackageWorkerCount} package worker(s) in {ElapsedMilliseconds} ms",
                sessionGeneration,
                session.LoadedPackageMap.Count,
                Stopwatch.GetElapsedTime(activationStarted).TotalMilliseconds);
        }
        catch
        {
            logger.LogWarning(
                "Package Runtime generation {SessionGeneration} activation failed after {ElapsedMilliseconds} ms",
                sessionGeneration,
                Stopwatch.GetElapsedTime(activationStarted).TotalMilliseconds);
            throw;
        }
    }

    public async Task DiscardAsync(PendingPackageSessionPublication pending)
    {
        using var appFence = pending.AppAdmissionFence;
        pending.Candidate.Session.StartupTrace?.TryFail("discarded");
        rpcCatalog?.DiscardCandidateSession(pending.Candidate.Session);
        if (pending.Publication.Committed)
        {
            sessions.DiscardPublication(pending.Publication);
            return;
        }
        var reason = new OperationCanceledException("The package session candidate was discarded before publication.");
        if (pending.PreviousAuthorityCannotBeRestored)
        {
            if (pending.Candidate.StageId is not null)
            {
                sessions.RemoveStage(pending.Candidate.StageId);
            }
            var committedFailure = await RejectUnrestorableStorePublicationAsync(
                pending,
                reason).ConfigureAwait(false);
            if (committedFailure is not null)
            {
                throw new AggregateException(
                    "Discarding a package session after its package-store commit did not complete cleanly.",
                    committedFailure);
            }
            return;
        }
        if (!sessions.RejectPublication(pending.Publication))
        {
            return;
        }
        if (pending.Candidate.StageId is not null)
        {
            sessions.RemoveStage(pending.Candidate.StageId);
        }
        var cleanupFailure = await DisposeFailedCandidateAsync(
            pending.Candidate.Session,
            pending.Publication).ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            var abandonmentFailure = await AbandonPreviousSessionAfterFailedRecoveryAsync(
                pending.Publication,
                reason,
                cleanupFailure).ConfigureAwait(false);
            var failures = abandonmentFailure is null
                ? new[] { cleanupFailure }
                : new[] { cleanupFailure, abandonmentFailure };
            throw new AggregateException(
                "Discarding the package session candidate did not complete cleanly.",
                failures);
        }
        var recoveryFailure = await RecoverOrRestorePreviousSessionAsync(
            pending.Publication,
            reason).ConfigureAwait(false);
        if (recoveryFailure is not null)
        {
            throw new AggregateException(
                "The previous Runtime generation could not be recovered after discarding a package session candidate.",
                recoveryFailure);
        }
        AlignRpcCatalogToActiveSession();
    }

    private async Task<Exception?> RejectUnrestorableStorePublicationAsync(
        PendingPackageSessionPublication pending,
        Exception publicationFailure)
    {
        List<Exception>? failures = null;
        if (!pending.Publication.Completed
            && !sessions.RejectPublication(pending.Publication))
        {
            (failures ??= []).Add(new InvalidOperationException(
                "The failed committed-store candidate publication could not be rejected."));
        }

        if (pending.Publication.Completed && !pending.Publication.Committed)
        {
            var cleanupFailure = await DisposeFailedCandidateAsync(
                pending.Candidate.Session,
                pending.Publication).ConfigureAwait(false);
            if (cleanupFailure is not null)
            {
                (failures ??= []).Add(cleanupFailure);
            }

            var abandonmentFailure = await AbandonPreviousSessionAfterStoreBoundaryFailureAsync(
                pending.Publication,
                publicationFailure,
                cleanupFailure,
                pending.DurablePackageStoreCommitted).ConfigureAwait(false);
            if (abandonmentFailure is not null)
            {
                (failures ??= []).Add(abandonmentFailure);
            }
        }

        return failures switch
        {
            null => null,
            { Count: 1 } => failures[0],
            _ => new AggregateException("Committed-store publication cleanup failed.", failures),
        };
    }

    public async Task DiscardAsync(PreparedPackageSession candidate)
    {
        candidate.Session.StartupTrace?.TryFail("discarded");
        if (candidate.StageId is not null) sessions.RemoveStage(candidate.StageId);
        await DisposeSessionAsync(candidate.Session);
    }

    private async Task DisposeSessionAsync(ActivePackageSession session)
    {
        try
        {
            await session.QuiesceWorkersAsync(
                logger,
                _lifecyclePolicy.PackageWorkerCleanupTimeout);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to stop package workers while discarding a session");
        }
        try
        {
            await session.DisposeAsync(_lifecyclePolicy.PackageWorkerCleanupTimeout);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to dispose a discarded package session within the cleanup deadline");
        }
    }

    private async Task<Exception?> DisposeFailedCandidateAsync(
        ActivePackageSession session,
        PackageSessionState.SessionPublication publication)
    {
        var rollbackStarted = Stopwatch.GetTimestamp();
        logger.LogWarning(
            "Rolling back rejected package session generation {SessionGeneration}",
            publication.CandidateGeneration);
        List<Exception>? failures = null;
        var workersQuiesced = false;
        var callsDrained = false;
        var disposed = false;
        try
        {
            await session.QuiesceWorkersAsync(
                logger,
                _lifecyclePolicy.PackageWorkerCleanupTimeout);
            workersQuiesced = true;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Failed to quiesce package workers before rolling back a candidate session");
        }
        try
        {
            await sessions.WaitForDiscardedCandidateDrainAsync(publication);
            callsDrained = true;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Failed to drain package data calls for a rejected candidate session");
            (failures ??= []).Add(exception);
        }
        try
        {
            await session.DisposeAsync(_lifecyclePolicy.PackageWorkerCleanupTimeout);
            disposed = true;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Failed to dispose a rejected candidate session before storage rollback");
            (failures ??= []).Add(exception);
        }
        if (callsDrained && disposed)
        {
            try
            {
                await session.RollbackStorageMigrationsAsync();
            }
            catch (Exception exception)
            {
                logger.LogCritical(exception, "Failed to roll back package storage for a rejected candidate session");
                (failures ??= []).Add(exception);
            }
        }
        else if (callsDrained)
        {
            session.RegisterPostDisposalCleanup(
                RollbackStorageAfterDisposalAsync(session, "rejected candidate"));
        }
        var rollbackFailureCount = (failures?.Count ?? 0) + (workersQuiesced ? 0 : 1);
        if (rollbackFailureCount == 0)
        {
            logger.LogInformation(
                "Rolled back rejected package session generation {SessionGeneration} in {ElapsedMilliseconds} ms",
                publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(rollbackStarted).TotalMilliseconds);
        }
        else
        {
            logger.LogWarning(
                "Rollback for rejected package session generation {SessionGeneration} completed with {RollbackFailureCount} failure(s) after {ElapsedMilliseconds} ms",
                publication.CandidateGeneration,
                rollbackFailureCount,
                Stopwatch.GetElapsedTime(rollbackStarted).TotalMilliseconds);
        }
        return failures is null
            ? null
            : new AggregateException("Candidate session cleanup failed.", failures);
    }

    private async Task<Exception?> RecoverOrRestorePreviousSessionAsync(
        PackageSessionState.SessionPublication publication,
        Exception candidateFailure)
    {
        var recoveryStarted = Stopwatch.GetTimestamp();
        var faults = sessions.GetDrainingFaults(publication);
        if (!publication.PreviousWorkersQuiesced
            && faults.Count == 0
            && sessions.TryRestoreRejectedPublication(publication))
        {
            publication.PreviousSession.CompleteRetentionRollback();
            logger.LogInformation(
                "Restored the previous package session after generation {SessionGeneration} was rejected in {ElapsedMilliseconds} ms without worker recovery",
                publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds);
            return null;
        }
        faults = sessions.GetDrainingFaults(publication);

        var previousSession = publication.PreviousSession;
        var snapshot = sessions.GetSnapshot();
        var sources = sessions.Sources.Snapshot();
        var diagnostics = DrainingFaultDiagnostics(publication);
        logger.LogWarning(
            "Recovering the previous package session after generation {SessionGeneration} was rejected",
            publication.CandidateGeneration);
        IReadOnlyList<DrainingPackageRecoveryCandidate> drainingRecoveries = [];
        try
        {
            if (!publication.PreviousWorkersQuiesced)
            {
                sessions.MarkPreviousWorkersQuiesced(publication);
                await previousSession.QuiesceWorkersAsync(
                    logger,
                    _lifecyclePolicy.PackageWorkerCleanupTimeout).ConfigureAwait(false);
                faults = sessions.GetDrainingFaults(publication);
                diagnostics = DrainingFaultDiagnostics(publication);
            }
            await sessions.BeginRecoveryPublicationAsync(publication).ConfigureAwait(false);
            drainingRecoveries = await previousSession.ApplyDrainingFaultsAsync(
                faults,
                logger,
                _lifecyclePolicy.PackageWorkerCleanupTimeout).ConfigureAwait(false);
            // A failed provider/dependency is not authority to abandon unrelated
            // packages. Suspend its actual consumers before planning the restored
            // graph; keep their exact source/recovery evidence for the ordinary
            // dependency-aware recovery queue. Candidate admission stays strict.
            var deferred = await DeferUnavailableRecoveryPackagesAsync(previousSession).ConfigureAwait(false);
            drainingRecoveries = drainingRecoveries.Concat(deferred).ToArray();
            if (publication.PreviousWorkersQuiesced)
            {
                await previousSession.RebuildQuiescedWorkersAsync(
                    logger,
                    _lifecyclePolicy.PackageWorkerCleanupTimeout).ConfigureAwait(false);
                await previousSession.BeginStorageActivationTransactionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (rpcReadiness is not null)
                {
                    previousSession.RequiredProviderPlan = rpcReadiness.Plan(previousSession);
                    previousSession.BindStartupGeneration(publication.CandidateGeneration);
                    rpcCatalog?.PrepareStartingCandidate(previousSession, publication.CandidateGeneration);
                    previousSession.CandidateProviderReady = (id, ready) => rpcCatalog?.MarkCandidateProviderReady(previousSession, id, ready);
                }
                await previousSession.StartWorkersAsync(
                    logger,
                    _lifecyclePolicy.PackageWorkerStartupTimeout,
                    _lifecyclePolicy.PackageWorkerCleanupTimeout,
                    CancellationToken.None).ConfigureAwait(false);
                if (previousSession.RequiredProviderPlan is not null) rpcCatalog?.CompleteStartingCandidate(previousSession);
                await ActivateRuntimeGenerationAsync(
                    previousSession,
                    publication.CandidateGeneration,
                    () =>
                    {
                        rpcReadiness?.Evaluate(previousSession);
                        rpcCatalog?.PrepareCandidateSession(
                            previousSession,
                            publication.CandidateGeneration);
                        previousSession.StartRuntimeGenerationMonitoring(sessions.HandleRuntimeGenerationFault);
                    },
                    generationCommitted: null,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                previousSession.PublishCommittedRuntimeGeneration(PublishRecovery);
                previousSession.CompleteStorageMigrations();
            }
            else
            {
                rpcReadiness?.Evaluate(previousSession);
                PublishRecovery();
            }
            sessions.EnqueueRestoredPackageRecoveries(publication, drainingRecoveries);
            previousSession.CompleteRetentionRollback();
            logger.LogInformation(
                "Recovered the previous package session after generation {SessionGeneration} was rejected in {ElapsedMilliseconds} ms",
                publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds);
            return null;

            void PublishRecovery()
            {
                sessions.CommitRecoveryPublication(
                    publication,
                    sources,
                    snapshot.Warnings,
                    snapshot.Errors.Concat(diagnostics).Concat(deferred.Select(candidate =>
                        $"Package '{candidate.Fault.PackageId}' is awaiting dependency recovery: {candidate.Fault.Exception.Message}")).ToArray(),
                    generation =>
                    {
                        rpcCatalog?.ActivateSession(previousSession, generation);
                        rpcCatalog?.VerifySessionGeneration(generation);
                    });
            }
        }
        catch (Exception recoveryFailure)
        {
            logger.LogWarning(
                "Previous package session recovery after generation {SessionGeneration} was rejected failed after {ElapsedMilliseconds} ms",
                publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds);
            if (publication.RecoveryCommitted)
            {
                previousSession.CompleteStorageMigrations();
                logger.LogError(
                    recoveryFailure,
                    "Recovered package workers became active, but publishing the recovery snapshot did not complete after {ElapsedMilliseconds} ms",
                    Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds);
                return null;
            }
            rpcCatalog?.DiscardCandidateSession(previousSession);
            var callsDrained = false;
            var disposed = false;
            try
            {
                await previousSession.QuiesceWorkersAsync(
                    logger,
                    _lifecyclePolicy.PackageWorkerCleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                recoveryFailure = new AggregateException(recoveryFailure, cleanupFailure);
            }
            try
            {
                await sessions.WaitForRecoveryPackageCallsAsync(publication).ConfigureAwait(false);
                callsDrained = true;
            }
            catch (Exception drainFailure)
            {
                recoveryFailure = new AggregateException(recoveryFailure, drainFailure);
            }
            try
            {
                await previousSession.DisposeAsync(_lifecyclePolicy.PackageWorkerCleanupTimeout)
                    .ConfigureAwait(false);
                disposed = true;
            }
            catch (Exception cleanupFailure)
            {
                recoveryFailure = new AggregateException(recoveryFailure, cleanupFailure);
            }
            if (callsDrained && disposed)
            {
                try
                {
                    await previousSession.RollbackStorageMigrationsAsync().ConfigureAwait(false);
                }
                catch (Exception rollbackFailure)
                {
                    recoveryFailure = new AggregateException(recoveryFailure, rollbackFailure);
                }
            }
            else if (callsDrained)
            {
                previousSession.RegisterPostDisposalCleanup(
                    RollbackStorageAfterDisposalAsync(previousSession, "failed recovery"));
            }
            var unavailableFailure = await AbandonPreviousSessionAfterFailedRecoveryAsync(
                publication,
                candidateFailure,
                recoveryFailure).ConfigureAwait(false);
            return unavailableFailure is null
                ? recoveryFailure
                : new AggregateException(recoveryFailure, unavailableFailure);
        }
    }

    private async Task<IReadOnlyList<DrainingPackageRecoveryCandidate>> DeferUnavailableRecoveryPackagesAsync(ActivePackageSession session)
    {
        List<DrainingPackageRecoveryCandidate> deferred = [];
        while (true)
        {
            var unavailable = session.LoadedPackageMap.Keys.Where(id => !session.AreRecoveryDependenciesAvailable(id))
                .Concat(rpcReadiness?.UnsatisfiedConsumers(session, Sunder.Package.Format.SunderPackageFormat.RuntimeHostRole) ?? [])
                .Concat(rpcReadiness?.UnsatisfiedConsumers(session, Sunder.Package.Format.SunderPackageFormat.AppHostRole) ?? [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (unavailable.Length == 0) return deferred;
            var faults = unavailable.Select(id =>
            {
                var package = session.LoadedPackageMap[id];
                return new DrainingPackageFault(id,
                    new(package.RuntimeActivationId, package.ActivationSessionGeneration!.Value),
                    PackageFailureOrigin.RuntimeActivation,
                    new InvalidOperationException("A required package or RPC provider is unavailable during session recovery."),
                    _timeProvider.GetUtcNow());
            }).ToArray();
            // ApplyDrainingFaultsAsync proves stop/disposal before releasing any
            // source for recovery. Unproven cleanup still fails closed.
            deferred.AddRange(await session.ApplyDrainingFaultsAsync(faults, logger,
                _lifecyclePolicy.PackageWorkerCleanupTimeout).ConfigureAwait(false));
        }
    }

    private async Task<Exception?> AbandonPreviousSessionAfterFailedRecoveryAsync(
        PackageSessionState.SessionPublication publication,
        Exception candidateFailure,
        Exception recoveryFailure)
        => await AbandonPreviousSessionAsync(
            publication,
            [
                $"Package session recovery failed after candidate rejection: {recoveryFailure.Message}",
                $"Rejected candidate failure: {candidateFailure.Message}",
            ],
            "failed recovery").ConfigureAwait(false);

    private async Task<Exception?> AbandonPreviousSessionAfterStoreBoundaryFailureAsync(
        PackageSessionState.SessionPublication publication,
        Exception publicationFailure,
        Exception? cleanupFailure,
        bool packageStoreAuthoritative)
    {
        var storeBoundary = packageStoreAuthoritative
            ? "became authoritative"
            : "outcome became indeterminate";
        return await AbandonPreviousSessionAsync(
            publication,
            cleanupFailure is null
                ? [$"Runtime session publication failed after the package store {storeBoundary}: {publicationFailure.Message}"]
                : [
                    $"Runtime session publication failed after the package store {storeBoundary}: {publicationFailure.Message}",
                    $"Failed candidate cleanup: {cleanupFailure.Message}",
                ],
            "package-store publication failure").ConfigureAwait(false);
    }

    private async Task<Exception?> AbandonPreviousSessionAsync(
        PackageSessionState.SessionPublication publication,
        IReadOnlyList<string> diagnostics,
        string operation)
    {
        var abandonmentStarted = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            var snapshot = sessions.GetSnapshot();
            sessions.AbandonRecoveryPublication(
                publication,
                sessions.Sources.Snapshot(),
                snapshot.Warnings,
                snapshot.Errors
                    .Concat(diagnostics)
                    .Concat(DrainingFaultDiagnostics(publication))
                    .ToArray());
        }
        catch (Exception abandonFailure)
        {
            logger.LogCritical(
                abandonFailure,
                "Failed to publish explicit Runtime unavailability after {Operation}",
                operation);
            failure = abandonFailure;
        }
        if (publication.Abandoned)
        {
            try
            {
                var warnings = await sessions.RetireAbandonedPublicationAsync(publication)
                    .ConfigureAwait(false);
                if (warnings.Count > 0)
                {
                    logger.LogWarning(
                        "Abandoned package session cleanup completed with warnings: {Warnings}",
                        string.Join(" | ", warnings));
                }
            }
            catch (Exception cleanupFailure)
            {
                logger.LogCritical(
                    cleanupFailure,
                    "Failed to retire an abandoned package session after publishing Runtime unavailability");
                failure = failure is null
                    ? cleanupFailure
                    : new AggregateException(failure, cleanupFailure);
            }
        }
        if (failure is null)
        {
            logger.LogInformation(
                "Completed {Operation} handling for package session generation {SessionGeneration} in {ElapsedMilliseconds} ms",
                operation,
                publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(abandonmentStarted).TotalMilliseconds);
        }
        else
        {
            logger.LogWarning(
                "{Operation} handling for package session generation {SessionGeneration} completed with cleanup failures after {ElapsedMilliseconds} ms",
                operation,
                publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(abandonmentStarted).TotalMilliseconds);
        }
        return failure;
    }

    private async Task RollbackStorageAfterDisposalAsync(
        ActivePackageSession session,
        string operation)
    {
        var rollbackStarted = Stopwatch.GetTimestamp();
        try
        {
            await session.WaitForDisposalCompletionAsync().ConfigureAwait(false);
            await session.RollbackStorageMigrationsAsync().ConfigureAwait(false);
            logger.LogInformation(
                "Completed deferred package storage rollback after {Operation} disposal in {ElapsedMilliseconds} ms",
                operation,
                Stopwatch.GetElapsedTime(rollbackStarted).TotalMilliseconds);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                exception,
                "Deferred package storage rollback failed after {Operation} disposal in {ElapsedMilliseconds} ms",
                operation,
                Stopwatch.GetElapsedTime(rollbackStarted).TotalMilliseconds);
        }
    }

    private IReadOnlyList<string> DrainingFaultDiagnostics(
        PackageSessionState.SessionPublication publication)
        => sessions.GetDrainingFaults(publication)
            .Select(fault =>
                $"Previous Runtime generation for package '{fault.PackageId}' faulted while draining: {fault.Exception.Message}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static RuntimeLifecyclePolicyOptions ValidateLifecyclePolicy(RuntimeLifecyclePolicyOptions policy)
    {
        if (policy.PackageRuntimeGenerationActivationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "The package Runtime generation activation timeout must be positive.");
        }
        if (policy.PackageRuntimeGenerationActivationAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "At least one package Runtime generation activation attempt is required.");
        }
        return policy;
    }
}
