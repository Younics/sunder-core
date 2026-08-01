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
    RuntimeRpcPermissionStore? rpcPermissions = null)
{
    private readonly RuntimeLifecyclePolicyOptions _lifecyclePolicy = ValidateLifecyclePolicy(
        lifecyclePolicy ?? new RuntimeLifecyclePolicyOptions());

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
        CancellationToken cancellationToken)
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
        return await CommitAsync(publication, cancellationToken);
    }

    public async Task<PendingPackageSessionPublication> BeginPublishAsync(
        PreparedPackageSession candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            var publication = await sessions.PreparePublicationAsync(
                candidate.Session,
                candidate.BaseGeneration,
                cancellationToken);
            return new PendingPackageSessionPublication(candidate, publication);
        }
        catch
        {
            await DiscardAsync(candidate);
            throw;
        }
    }

    public async Task<PackageSessionPublicationResult> CommitAsync(
        PendingPackageSessionPublication pending,
        CancellationToken cancellationToken = default)
    {
        var candidate = pending.Candidate;
        try
        {
            if (rpcPermissions is not null)
            {
                await rpcPermissions.GrantDeclaredInstalledActionsAsync(
                    RuntimeRpcPermissionSubjects.FromSession(candidate.Session),
                    cancellationToken);
            }
            await candidate.Session.StartBackgroundServicesAsync(
                logger,
                _lifecyclePolicy.PackageBackgroundServiceStartupTimeout,
                _lifecyclePolicy.PackageBackgroundServiceCleanupTimeout,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var publication = await sessions.CommitPublicationAsync(
                pending.Publication,
                candidate.Sources,
                candidate.Warnings,
                candidate.Errors);
            await CommitRuntimeGenerationAsync(
                candidate.Session,
                publication.Stamp.SessionGeneration,
                cancellationToken);
            rpcCatalog?.ActivateSession(candidate.Session, publication.Stamp.SessionGeneration);
            rpcCatalog?.VerifySessionGeneration(publication.Stamp.SessionGeneration);
            sessions.ActivatePublication(pending.Publication);
            candidate.Session.StartRuntimeGenerationMonitoring(sessions.HandleRuntimeGenerationFault);
            candidate.Session.ActivateCommittedRuntimeGeneration();
            return new PackageSessionPublicationResult(publication.Stamp, publication.Warnings);
        }
        catch (Exception exception)
        {
            sessions.DiscardPublication(pending.Publication);
            if (pending.Publication.Committed && !pending.Publication.Activated)
            {
                try
                {
                    var failed = await sessions.FailPublicationAsync(
                        pending.Publication,
                        candidate.Sources,
                        candidate.Warnings,
                        candidate.Errors,
                        exception);
                    if (failed.Warnings.Count > 0)
                    {
                        logger.LogWarning(
                            "Failed package Runtime generation cleanup completed with warnings: {Warnings}",
                            string.Join(" | ", failed.Warnings));
                    }
                }
                catch (Exception retirementException)
                {
                    logger.LogCritical(
                        retirementException,
                        "Failed to remove a package session whose Runtime generation did not activate");
                }
                finally
                {
                    DeactivateRpcCatalog();
                }
                logger.LogError(exception, "Package session publication failed before Runtime generation activation completed");
                throw;
            }
            if (pending.Publication.Committed)
            {
                AlignRpcCatalogToActiveSession();
                const string warning = "The package session was applied, but publishing its Runtime snapshot did not complete; reconciliation is pending.";
                logger.LogError(exception, "Package session publication failed after Runtime generation activation completed");
                return new PackageSessionPublicationResult(
                    sessions.Stamp,
                    [warning],
                    ReconciliationPending: true);
            }
            await DisposeSessionAsync(candidate.Session);
            AlignRpcCatalogToActiveSession();
            throw;
        }
    }

    private void DeactivateRpcCatalog()
    {
        if (rpcCatalog is null)
        {
            return;
        }

        var generation = sessions.Generation;
        rpcCatalog.DeactivateAll(generation);
        rpcCatalog.VerifySessionGeneration(generation);
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
            using var timeoutCancellation = new CancellationTokenSource(
                _lifecyclePolicy.PackageRuntimeGenerationActivationTimeout);
            using var activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCancellation.Token);
            try
            {
                await session.CommitRuntimeGenerationAsync(
                    sessionGeneration,
                    activationCancellation.Token);
                return;
            }
            catch (OperationCanceledException exception) when (
                timeoutCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Package Runtime generation activation did not complete within {_lifecyclePolicy.PackageRuntimeGenerationActivationTimeout.TotalSeconds:0.###} seconds.",
                    exception);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && attempt < _lifecyclePolicy.PackageRuntimeGenerationActivationAttempts)
            {
                logger.LogWarning(
                    exception,
                    "Package Runtime generation activation attempt {Attempt} of {MaximumAttempts} failed; retrying the exact generation",
                    attempt,
                    _lifecyclePolicy.PackageRuntimeGenerationActivationAttempts);
            }
        }
    }

    public async Task DiscardAsync(PendingPackageSessionPublication pending)
    {
        sessions.DiscardPublication(pending.Publication);
        if (pending.Publication.Committed)
        {
            return;
        }
        await DiscardAsync(pending.Candidate);
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
            await session.StopBackgroundServicesAsync(
                logger,
                _lifecyclePolicy.PackageBackgroundServiceCleanupTimeout);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to stop package background services while discarding a session");
        }
        try
        {
            await session.DisposeAsync(_lifecyclePolicy.PackageBackgroundServiceCleanupTimeout);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to dispose a discarded package session within the cleanup deadline");
        }
    }

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
