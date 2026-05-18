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
            async (DevPackageLoadRequest request, RuntimePackageSessionService packageSessionService) =>
                Results.Ok(await packageSessionService.LoadAsync(request)));
        group.MapPost(
            "stage",
            async (DevPackageLoadRequest request, RuntimePackageSessionService packageSessionService) =>
                Results.Ok(await packageSessionService.StageDevPackagesAsync(request)));
        group.MapPost(
            "stage/{stageId}/commit",
            async (string stageId, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
                Results.Ok(await packageSessionService.CommitDevPackageStageAsync(stageId, cancellationToken)));
        group.MapDelete(
            "stage/{stageId}",
            async (string stageId, RuntimePackageSessionService packageSessionService) =>
                await packageSessionService.DiscardDevPackageStageAsync(stageId) ? Results.NoContent() : Results.NotFound());

        return endpoints;
    }
}
