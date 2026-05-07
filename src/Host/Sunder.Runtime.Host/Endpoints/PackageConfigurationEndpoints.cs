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
            (DevPackageSessionService devPackageSessionService) => Results.Ok(devPackageSessionService.GetConfigurationSchemas()));

        group.MapGet(
            "{packageId}/config/values",
            async (string packageId, DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
            {
                var values = await devPackageSessionService.GetConfigurationValuesAsync(packageId, cancellationToken);
                return values is null ? Results.NotFound() : Results.Ok(values);
            });

        group.MapPut(
            "{packageId}/config/values",
            async (
                string packageId,
                UpdatePackageConfigurationValuesRequest request,
                DevPackageSessionService devPackageSessionService,
                CancellationToken cancellationToken) =>
            {
                var saved = await devPackageSessionService.SaveConfigurationValuesAsync(packageId, request, cancellationToken);
                return saved ? Results.NoContent() : Results.NotFound();
            });

        return endpoints;
    }
}
