using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record PreparedPackageSession(
    ActivePackageSession Session,
    PackageSessionSourceSnapshot Sources,
    IReadOnlyList<PackageUiSnapshotDescriptor> UiSnapshots,
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
    IReadOnlyList<PackageUiSnapshotDescriptor> UiSnapshots,
    bool ReconciliationPending = false);

internal sealed class PackageSessionPublisher(
    RuntimeSessionOwner sessions,
    RuntimePackageUiService ui,
    ILogger<PackageSessionPublisher> logger,
    RuntimeLifecyclePolicyOptions? lifecyclePolicy = null)
{
    private readonly RuntimeLifecyclePolicyOptions _lifecyclePolicy = lifecyclePolicy ?? new RuntimeLifecyclePolicyOptions();

    public PreparedPackageSession Prepare(
        ActivePackageSession session,
        PackageSessionSourceSnapshot sources,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        long baseGeneration,
        string? stageId = null)
    {
        var started = Stopwatch.GetTimestamp();
        var snapshots = ui.CreateSnapshots(session.GetAppPackageSources(), checked(baseGeneration + 1), stageId);
        logger.LogInformation(
            "Prepared {SnapshotCount} package UI snapshot(s) in {ElapsedMilliseconds} ms",
            snapshots.Count,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new PreparedPackageSession(
            session,
            sources,
            snapshots,
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
        var started = Stopwatch.GetTimestamp();
        try
        {
            var publication = await sessions.PreparePublicationAsync(
                candidate.Session,
                candidate.BaseGeneration,
                cancellationToken);
            logger.LogInformation(
                "Drained the previous package session in {ElapsedMilliseconds} ms",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
        var started = Stopwatch.GetTimestamp();
        var candidate = pending.Candidate;
        var snapshots = candidate.UiSnapshots;
        try
        {
            await candidate.Session.StartBackgroundServicesAsync(
                logger,
                _lifecyclePolicy.PackageBackgroundServiceStartupTimeout,
                _lifecyclePolicy.PackageBackgroundServiceCleanupTimeout,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.StageId is not null)
            {
                snapshots = ui.PromoteStage(candidate.StageId, candidate.UiSnapshots);
            }
            var publication = await sessions.CommitPublicationAsync(
                pending.Publication,
                candidate.Sources,
                snapshots,
                candidate.Warnings,
                candidate.Errors);
            ui.ScheduleCacheGarbageCollection();
            logger.LogInformation(
                "Started background services and committed the package session in {ElapsedMilliseconds} ms",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new PackageSessionPublicationResult(publication.Stamp, publication.Warnings, snapshots);
        }
        catch (Exception exception)
        {
            sessions.DiscardPublication(pending.Publication);
            if (pending.Publication.Committed)
            {
                const string warning = "The package session was applied, but publishing its Runtime snapshot did not complete; reconciliation is pending.";
                logger.LogError(exception, "Package session publication failed after the active session was swapped");
                return new PackageSessionPublicationResult(
                    sessions.Stamp,
                    [warning],
                    snapshots,
                    ReconciliationPending: true);
            }
            if (candidate.StageId is not null)
            {
                ui.DiscardStage(candidate.StageId);
            }
            ui.DiscardSnapshots(snapshots);
            await DisposeSessionAsync(candidate.Session);
            throw;
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
        if (candidate.StageId is not null)
        {
            ui.DiscardStage(candidate.StageId);
        }
        else
        {
            ui.DiscardSnapshots(candidate.UiSnapshots);
        }
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
}
