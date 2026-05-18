using Sunder.Protocol;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class DevPackageEndpoints
{
    public static IEndpointRouteBuilder MapDevPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/dev/packages");
        group.MapPost(
            "load",
            async (DevPackageLoadRequest request, DevPackageSessionService devPackageSessionService) =>
                Results.Ok(await devPackageSessionService.LoadAsync(request)));
        group.MapPost(
            "stage",
            async (DevPackageLoadRequest request, DevPackageSessionService devPackageSessionService) =>
                Results.Ok(await devPackageSessionService.StageDevPackagesAsync(request)));
        group.MapPost(
            "stage/{stageId}/commit",
            async (string stageId, DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
                Results.Ok(await devPackageSessionService.CommitDevPackageStageAsync(stageId, cancellationToken)));
        group.MapDelete(
            "stage/{stageId}",
            async (string stageId, DevPackageSessionService devPackageSessionService) =>
                await devPackageSessionService.DiscardDevPackageStageAsync(stageId) ? Results.NoContent() : Results.NotFound());

        return endpoints;
    }
}
