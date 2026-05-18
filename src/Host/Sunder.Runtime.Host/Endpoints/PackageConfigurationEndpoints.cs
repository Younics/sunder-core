using Sunder.Protocol;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapPackageConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/packages");
        group.MapGet(
            "configuration/schemas",
            (RuntimePackageSessionService packageSessionService) => Results.Ok(packageSessionService.GetConfigurationSchemas()));

        group.MapGet(
            "{packageId}/config/values",
            async (string packageId, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var values = await packageSessionService.GetConfigurationValuesAsync(packageId, cancellationToken);
                return values is null ? Results.NotFound() : Results.Ok(values);
            });

        group.MapPut(
            "{packageId}/config/values",
            async (
                string packageId,
                UpdatePackageConfigurationValuesRequest request,
                RuntimePackageSessionService packageSessionService,
                CancellationToken cancellationToken) =>
            {
                var saved = await packageSessionService.SaveConfigurationValuesAsync(packageId, request, cancellationToken);
                return saved ? Results.NoContent() : Results.NotFound();
            });

        return endpoints;
    }
}
