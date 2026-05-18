using Sunder.Protocol;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageSessionEndpoints
{
    public static IEndpointRouteBuilder MapPackageSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/packages");
        group.MapGet(
            "active",
            (RuntimePackageSessionService packageSessionService) => Results.Ok(packageSessionService.GetActivePackages()));

        group.MapGet(
            "session",
            (RuntimePackageSessionService packageSessionService) => Results.Ok(packageSessionService.GetSessionPackages()));

        group.MapGet(
            "sources/active",
            (RuntimePackageSessionService packageSessionService) => Results.Ok(packageSessionService.GetActivePackageSources()));

        group.MapGet(
            "session/{packageId}/status",
            async (string packageId, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
                await packageSessionService.GetPackageSessionStatusAsync(packageId, cancellationToken) is { } status
                    ? Results.Ok(status)
                    : Results.NotFound());

        group.MapPost(
            "session/load",
            async (PackageSessionLoadRequest request, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
                Results.Ok(await packageSessionService.LoadPackageSessionAsync(request, cancellationToken)));

        group.MapPost(
            "session/{packageId}/unload",
            async (string packageId, PackageSessionUnloadRequest request, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
                Results.Ok(await packageSessionService.UnloadPackageSessionAsync(packageId, request.SourceKind, cancellationToken)));

        return endpoints;
    }
}
