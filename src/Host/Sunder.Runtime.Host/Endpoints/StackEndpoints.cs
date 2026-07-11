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
                var result = await stackExport.ExportAsync(request, cancellationToken);
                if (!result.Success) RuntimeEndpointErrors.ThrowFailure(result.Errors.FirstOrDefault());
                return Results.Ok(result);
            });

        group.MapPost(
            "import/preview",
            async (RuntimeStackImportPreviewRequest request, RuntimeStackImportService stackImport, CancellationToken cancellationToken) =>
            {
                var result = await stackImport.PreviewAsync(request, cancellationToken);
                if (!result.Success) RuntimeEndpointErrors.ThrowFailure(result.Errors.FirstOrDefault(), packageValidation: true);
                return Results.Ok(result);
            });

        group.MapPost(
            "import/apply",
            async (RuntimeStackImportRequest request, RuntimeStackImportService stackImport, CancellationToken cancellationToken) =>
            {
                var result = await stackImport.ImportAsync(request, cancellationToken);
                if (!result.Success) RuntimeEndpointErrors.ThrowFailure(result.Errors.FirstOrDefault(), packageValidation: true);
                return Results.Ok(result);
            });

        return endpoints;
    }
}
