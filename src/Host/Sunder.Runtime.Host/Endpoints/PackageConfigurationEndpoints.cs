using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapPackageConfigurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/packages");
        group.MapGet(
            "configuration/schemas",
            (PackageConfigurationAccessService configuration) => Results.Ok(configuration.GetSchemas()));

        group.MapGet(
            "{packageId}/config/values",
            async (string packageId, PackageConfigurationAccessService configuration, CancellationToken cancellationToken) =>
            {
                var values = await configuration.GetValuesAsync(packageId, cancellationToken);
                return Results.Ok(RuntimeEndpointErrors.Required(values, $"Package '{packageId}' configuration"));
            });

        group.MapPut(
            "{packageId}/config/values",
            async (
                string packageId,
                UpdatePackageConfigurationValuesRequest request,
                PackageConfigurationAccessService configuration,
                CancellationToken cancellationToken) =>
            {
                var saved = await configuration.SaveValuesAsync(packageId, request, cancellationToken);
                if (!saved)
                {
                    throw new RuntimeNotFoundException($"Package '{packageId}' configuration was not found.");
                }
                return Results.NoContent();
            });

        return endpoints;
    }
}
