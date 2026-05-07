using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageAuthEndpoints
{
    public static IEndpointRouteBuilder MapPackageAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/packages");
        group.MapGet(
            "{packageId}/auth/status",
            async (string packageId, DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
            {
                var status = await devPackageSessionService.GetPackageAuthStatusAsync(packageId, cancellationToken);
                return status is null ? Results.NotFound() : Results.Ok(status);
            });

        group.MapPost(
            "{packageId}/auth/start",
            async (string packageId, DevPackageSessionService devPackageSessionService, PackageAuthCallbackServer packageAuthCallbackServer, CancellationToken cancellationToken) =>
            {
                var session = await devPackageSessionService.StartPackageAuthAsync(packageId, packageAuthCallbackServer, cancellationToken);
                return session is null ? Results.NotFound() : Results.Ok(session);
            });

        group.MapGet(
            "{packageId}/auth/sessions/{authSessionId}",
            (string packageId, string authSessionId, DevPackageSessionService devPackageSessionService) =>
            {
                var status = devPackageSessionService.GetPackageAuthSessionStatus(packageId, authSessionId);
                return status is null ? Results.NotFound() : Results.Ok(status);
            });

        group.MapPost(
            "{packageId}/auth/disconnect",
            async (string packageId, DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
            {
                var status = await devPackageSessionService.DisconnectPackageAsync(packageId, cancellationToken);
                return status is null ? Results.NotFound() : Results.Ok(status);
            });

        return endpoints;
    }
}
