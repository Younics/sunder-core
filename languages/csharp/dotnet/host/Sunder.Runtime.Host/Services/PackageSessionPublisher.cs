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
    string? StageId);

internal sealed record PendingPackageSessionPublication(
    PreparedPackageSession Candidate,
    PackageSessionState.SessionPublication Publication);

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
    TimeProvider? timeProvider = null)
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
        string? stageId = null)
    {
        return new PreparedPackageSession(
            session,
            sources,
            warnings.ToArray(),
            errors.ToArray(),
            baseGeneration,
            stageId);
    }

    public async Task<PackageSessionPublicationResult> PublishAsync(
        ActivePackageSession session,
        PackageSessionSourceSnapshot? sources,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        long expectedGeneration,
        CancellationToken cancellationToken,
        Action<PendingPackageSessionPublication>? publicationPrepared = null)
    {
        PreparedPackageSession candidate;
        try
        {
            candidate = Prepare(
                session,
                sources ?? sessions.Sources.Snapshot(),
                warnings,
                errors,
                expectedGeneration);
        }
        catch
        {
            await DisposeSessionAsync(session);
            throw;
        }

        var publication = await BeginPublishAsync(candidate, cancellationToken);
        publicationPrepared?.Invoke(publication);
        return await CommitAsync(publication, cancellationToken);
    }

    public async Task<PendingPackageSessionPublication> BeginPublishAsync(
        PreparedPackageSession candidate,
        CancellationToken cancellationToken)
    {
        var preparationStarted = Stopwatch.GetTimestamp();
        try
        {
            var publication = await sessions.PreparePublicationAsync(
                candidate.Session,
                candidate.BaseGeneration,
                cancellationToken);
            logger.LogInformation(
                "Prepared package session publication for generation {SessionGeneration} in {ElapsedMilliseconds} ms",
                publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            return new PendingPackageSessionPublication(candidate, publication);
        }
        catch
        {
            logger.LogWarning(
                "Package session publication preparation from generation {BaseGeneration} failed after {ElapsedMilliseconds} ms",
                candidate.BaseGeneration,
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds);
            await DiscardAsync(candidate);
            throw;
        }
    }

    public async Task<PackageSessionPublicationResult> CommitAsync(
        PendingPackageSessionPublication pending,
        CancellationToken cancellationToken = default)
    {
        var candidate = pending.Candidate;
        var publicationStarted = Stopwatch.GetTimestamp();
        using var publicationScope = logger.BeginScope(
            "Publishing package session generation {SessionGeneration}",
            pending.Publication.CandidateGeneration);
        try
        {
            var previousSession = pending.Publication.PreviousEntry.Session;
            if (previousSession.SharesPersistentWorkspaceWith(candidate.Session))
            {
                sessions.MarkPreviousWorkersQuiesced(pending.Publication);
                await sessions.WaitForPreviousRetirementCallbacksAsync(pending.Publication)
                    .ConfigureAwait(false);
                await previousSession.QuiesceWorkersAsync(
                    logger,
                    _lifecyclePolicy.PackageWorkerCleanupTimeout).ConfigureAwait(false);
            }
            await candidate.Session.BeginStorageActivationTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (rpcPermissions is not null)
            {
                await rpcPermissions.GrantDeclaredInstalledActionsAsync(
                    RuntimeRpcPermissionSubjects.FromSession(candidate.Session),
                    cancellationToken);
            }
            rpcReadiness?.Evaluate(candidate.Session);
            await candidate.Session.StartWorkersAsync(
                logger,
                _lifecyclePolicy.PackageWorkerStartupTimeout,
                _lifecyclePolicy.PackageWorkerCleanupTimeout,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ActivateRuntimeGenerationAsync(
                candidate.Session,
                pending.Publication.CandidateGeneration,
                () =>
                {
                    rpcCatalog?.PrepareCandidateSession(
                        candidate.Session,
                        pending.Publication.CandidateGeneration);
                    candidate.Session.StartRuntimeGenerationMonitoring(sessions.HandleRuntimeGenerationFault);
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
        if (pending.Publication.Committed)
        {
            sessions.DiscardPublication(pending.Publication);
            return;
        }
        var reason = new OperationCanceledException("The package session candidate was discarded before publication.");
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

    public async Task DiscardAsync(PreparedPackageSession candidate)
    {
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
            logger.LogInformation(
                "Restored the previous package session after generation {SessionGeneration} was rejected in {ElapsedMilliseconds} ms without worker recovery",
                publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds);
            return null;
        }
        faults = sessions.GetDrainingFaults(publication);

        var previousSession = publication.PreviousEntry.Session;
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
            if (publication.PreviousWorkersQuiesced)
            {
                await previousSession.RebuildQuiescedWorkersAsync(
                    logger,
                    _lifecyclePolicy.PackageWorkerCleanupTimeout).ConfigureAwait(false);
                await previousSession.BeginStorageActivationTransactionAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                await previousSession.StartWorkersAsync(
                    logger,
                    _lifecyclePolicy.PackageWorkerStartupTimeout,
                    _lifecyclePolicy.PackageWorkerCleanupTimeout,
                    CancellationToken.None).ConfigureAwait(false);
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
                    CancellationToken.None).ConfigureAwait(false);
                previousSession.PublishCommittedRuntimeGeneration(PublishRecovery);
                previousSession.CompleteStorageMigrations();
            }
            else
            {
                rpcReadiness?.Evaluate(previousSession);
                PublishRecovery();
            }
            sessions.EnqueueRestoredPackageRecoveries(publication, drainingRecoveries);
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
                    snapshot.Errors.Concat(diagnostics).ToArray(),
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

    private async Task<Exception?> AbandonPreviousSessionAfterFailedRecoveryAsync(
        PackageSessionState.SessionPublication publication,
        Exception candidateFailure,
        Exception recoveryFailure)
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
                    .Append($"Package session recovery failed after candidate rejection: {recoveryFailure.Message}")
                    .Append($"Rejected candidate failure: {candidateFailure.Message}")
                    .Concat(DrainingFaultDiagnostics(publication))
                    .ToArray());
        }
        catch (Exception abandonFailure)
        {
            logger.LogCritical(
                abandonFailure,
                "Failed to publish explicit Runtime unavailability after package session recovery failed");
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
                "Completed failed recovery handling for package session generation {SessionGeneration} in {ElapsedMilliseconds} ms",
                publication.CandidateGeneration,
                Stopwatch.GetElapsedTime(abandonmentStarted).TotalMilliseconds);
        }
        else
        {
            logger.LogWarning(
                "Failed recovery handling for package session generation {SessionGeneration} completed with cleanup failures after {ElapsedMilliseconds} ms",
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
