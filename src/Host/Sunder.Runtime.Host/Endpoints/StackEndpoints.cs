using Sunder.Protocol;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class StackEndpoints
{
    public static IEndpointRouteBuilder MapStackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/stacks");

        group.MapGet(
            "export/items",
            async (RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
                Results.Ok(await packageSessionService.ListStackExportItemsAsync(cancellationToken)));

        group.MapPost(
            "export",
            async (RuntimeStackExportRequest request, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.ExportStackAsync(request, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "import/preview",
            async (RuntimeStackImportPreviewRequest request, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.PreviewStackImportAsync(request, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "import/apply",
            async (RuntimeStackImportRequest request, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.ImportStackAsync(request, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        return endpoints;
    }
}
