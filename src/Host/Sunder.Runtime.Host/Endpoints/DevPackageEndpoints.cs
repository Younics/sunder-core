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

        return endpoints;
    }
}
