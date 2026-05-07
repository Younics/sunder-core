using Sunder.Protocol;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageFaultEndpoints
{
    public static IEndpointRouteBuilder MapPackageFaultEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/packages");
        group.MapPost(
            "{packageId}/fault",
            (string packageId, ReportPackageFaultRequest request, DevPackageSessionService devPackageSessionService) =>
                devPackageSessionService.ReportPackageFault(packageId, request)
                    ? Results.NoContent()
                    : Results.NotFound());

        return endpoints;
    }
}
