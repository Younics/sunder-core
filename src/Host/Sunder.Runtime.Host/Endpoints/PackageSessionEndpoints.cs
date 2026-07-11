using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageSessionEndpoints
{
    public static IEndpointRouteBuilder MapPackageSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/packages");
        group.MapGet(
            "active",
            (PackageSessionLifecycleService sessions) => Results.Ok(sessions.GetActivePackages()));

        group.MapGet(
            "session",
            (PackageSessionLifecycleService sessions) => Results.Ok(sessions.GetSessionPackages()));

        group.MapGet(
            "ui-snapshots",
            (RuntimePackageUiService packageUi) => Results.Ok(packageUi.GetActiveSnapshots()));

        group.MapGet("ui-snapshots/{snapshotId}", (string snapshotId, RuntimePackageUiService packageUi) =>
            packageUi.AcquireCurrent(snapshotId) is { } snapshot
                ? Results.Stream(
                    new FileStream(snapshot.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete),
                    "application/vnd.sunder.package-ui-snapshot+zip")
                : throw new RuntimeNotFoundException("The package UI snapshot was not found or belongs to a stale Runtime generation."));

        group.MapGet("session/stage/{stageId}/ui-snapshots/{snapshotId}", (string stageId, string snapshotId, RuntimePackageUiService packageUi) =>
            packageUi.AcquireStage(stageId, snapshotId) is { } snapshot
                ? Results.Stream(
                    new FileStream(snapshot.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete),
                    "application/vnd.sunder.package-ui-snapshot+zip")
                : throw new RuntimeNotFoundException("The staged package UI snapshot was not found or belongs to a stale Runtime generation."));

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
                if (!result.Success) RuntimeEndpointErrors.ThrowFailure(result.Message, packageValidation: true);
                return Results.Ok(result);
            });

        group.MapPost(
            "session/reload-installed",
            async (InstalledPackageSessionReloadRequest request, InstalledPackageLifecycleService installedPackages, CancellationToken cancellationToken) =>
            {
                var result = await installedPackages.ReloadAsync(request, cancellationToken);
                if (!result.Success) RuntimeEndpointErrors.ThrowFailure(result.Message, packageValidation: true);
                return Results.Ok(result);
            });

        group.MapPost(
            "session/stage",
            async (PackageLifecycleStageRequest request, PackageSessionLifecycleService sessions, CancellationToken cancellationToken) =>
            {
                var result = await sessions.StageAsync(request, cancellationToken);
                if (!result.Success) RuntimeEndpointErrors.ThrowFailure(result.Errors.FirstOrDefault(), packageValidation: true);
                return Results.Ok(result);
            });

        group.MapPost(
            "session/stage/{stageId}/commit",
            async (string stageId, PackageSessionLifecycleService sessions, CancellationToken cancellationToken) =>
            {
                var result = await sessions.CommitStageAsync(stageId, cancellationToken);
                if (!result.Success) RuntimeEndpointErrors.ThrowFailure(result.Message, packageValidation: true);
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
                if (!result.Success) RuntimeEndpointErrors.ThrowFailure(result.Message, packageValidation: true);
                return Results.Ok(result);
            });

        return endpoints;
    }
}
