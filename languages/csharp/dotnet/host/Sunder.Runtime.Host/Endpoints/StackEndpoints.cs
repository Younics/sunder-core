using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class StackEndpoints
{
    public static IEndpointRouteBuilder MapStackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/stacks");

        group.MapGet(
            "export/items",
            async (RuntimeStackExportService stackExport, CancellationToken cancellationToken) =>
                Results.Ok(await stackExport.ListItemsAsync(cancellationToken)));

        group.MapPost(
            "export",
            async (RuntimeStackExportRequest request, RuntimeStackExportService stackExport, CancellationToken cancellationToken) =>
            {
                var operation = await stackExport.ExportAsync(request, cancellationToken);
                if (operation.Failure is not null) RuntimeEndpointErrors.ThrowFailure(operation.Failure);
                return Results.Ok(operation.Response);
            });

        group.MapPost(
            "import/preview",
            async (RuntimeStackImportPreviewRequest request, RuntimeStackImportService stackImport, CancellationToken cancellationToken) =>
            {
                var operation = await stackImport.PreviewAsync(request, cancellationToken);
                if (operation.Failure is not null) RuntimeEndpointErrors.ThrowFailure(operation.Failure);
                return Results.Ok(operation.Response);
            });

        group.MapPost(
            "import/apply",
            async (RuntimeStackImportRequest request, RuntimeStackImportService stackImport, CancellationToken cancellationToken) =>
            {
                var result = await stackImport.ImportAsync(request, cancellationToken);
                return Results.Ok(result);
            });

        group.MapDelete(
            "import/plans/{planId}",
            (string planId, RuntimeStackImportService stackImport) =>
                stackImport.DiscardPlan(planId) ? Results.NoContent() : Results.NotFound());

        return endpoints;
    }
}
