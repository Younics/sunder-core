using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageSessionEndpoints
{
    public static IEndpointRouteBuilder MapPackageSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/packages");
        group.MapGet(
            "snapshot",
            (RuntimeSnapshotService snapshots) => Results.Ok(snapshots.GetSnapshot()));

        group.MapGet(
            "stages/{stageId}",
            (string stageId, RuntimeSnapshotService snapshots) => Results.Ok(
                RuntimeEndpointErrors.Required(snapshots.GetStageStatus(stageId), $"Package stage '{stageId}'")));

        group.MapGet(
            "active",
            (PackageSessionLifecycleService sessions) => Results.Ok(sessions.GetActivePackages()));

        group.MapGet(
            "session",
            (PackageSessionLifecycleService sessions) => Results.Ok(sessions.GetSessionPackages()));

        group.MapGet(
            "ui-snapshots",
            (RuntimePackageUiService packageUi) => Results.Ok(packageUi.GetActiveSnapshots()));

        group.MapGet("ui-snapshots/{snapshotId}", (
            string snapshotId,
            HttpResponse response,
            RuntimePackageUiService packageUi) =>
        {
            var snapshot = packageUi.AcquireCurrent(snapshotId)
                ?? throw new RuntimeNotFoundException("The package UI snapshot was not found or belongs to a stale Runtime generation.");
            response.RegisterForDispose(snapshot);
            return Results.Stream(snapshot.Stream, "application/vnd.sunder.package-ui-snapshot+zip");
        });

        group.MapGet("session/stage/{stageId}/ui-snapshots/{snapshotId}", (
            string stageId,
            string snapshotId,
            HttpResponse response,
            RuntimePackageUiService packageUi) =>
        {
            var snapshot = packageUi.AcquireStage(stageId, snapshotId)
                ?? throw new RuntimeNotFoundException("The staged package UI snapshot was not found or belongs to a stale Runtime generation.");
            response.RegisterForDispose(snapshot);
            return Results.Stream(snapshot.Stream, "application/vnd.sunder.package-ui-snapshot+zip");
        });

        group.MapGet(
            "session/{packageId}/status",
            async (string packageId, PackageSessionLifecycleService sessions, CancellationToken cancellationToken) =>
                Results.Ok(RuntimeEndpointErrors.Required(
                    await sessions.GetStatusAsync(packageId, cancellationToken),
                    $"Package session '{packageId}'")));

        group.MapPost(
            "session/load",
            async (PackageSessionLoadRequest request, PackageSessionCommandService commands, CancellationToken cancellationToken) =>
            {
                var result = await commands.LoadAsync(request, cancellationToken);
                return Results.Ok(result);
            });

        group.MapPost(
            "session/stage",
            async (PackageLifecycleStageRequest request, PackageSessionLifecycleService sessions, CancellationToken cancellationToken) =>
            {
                var result = await sessions.StageAsync(request, cancellationToken);
                return Results.Ok(result);
            });

        group.MapPost(
            "session/stage/{stageId}/commit",
            async (string stageId, PackageSessionLifecycleService sessions, CancellationToken cancellationToken) =>
            {
                var result = await sessions.CommitStageAsync(stageId, cancellationToken);
                return Results.Ok(result);
            });

        group.MapDelete(
            "session/stage/{stageId}",
            async (string stageId, PackageSessionLifecycleService sessions, CancellationToken cancellationToken) =>
            {
                if (!await sessions.DiscardStageAsync(stageId, cancellationToken))
                {
                    throw new RuntimeNotFoundException($"Package lifecycle stage '{stageId}' was not found.");
                }
                return Results.NoContent();
            });

        group.MapPost(
            "session/{packageId}/unload",
            async (string packageId, PackageSessionUnloadRequest request, PackageSessionCommandService commands, CancellationToken cancellationToken) =>
            {
                var result = await commands.UnloadAsync(packageId, request.SourceKind, cancellationToken);
                return Results.Ok(result);
            });

        return endpoints;
    }
}
