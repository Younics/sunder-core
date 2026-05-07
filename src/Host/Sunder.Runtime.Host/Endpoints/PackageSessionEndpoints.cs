using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageSessionEndpoints
{
    public static IEndpointRouteBuilder MapPackageSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/packages");
        group.MapGet(
            "active",
            (DevPackageSessionService devPackageSessionService) => Results.Ok(devPackageSessionService.GetActivePackages()));

        group.MapGet(
            "session",
            (DevPackageSessionService devPackageSessionService) => Results.Ok(devPackageSessionService.GetSessionPackages()));

        group.MapGet(
            "sources/active",
            (DevPackageSessionService devPackageSessionService) => Results.Ok(devPackageSessionService.GetActivePackageSources()));

        return endpoints;
    }
}
