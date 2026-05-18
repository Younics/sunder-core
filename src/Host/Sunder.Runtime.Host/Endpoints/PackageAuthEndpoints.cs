using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageAuthEndpoints
{
    public static IEndpointRouteBuilder MapPackageAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/packages");
        group.MapGet(
            "{packageId}/auth/status",
            async (string packageId, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var status = await packageSessionService.GetPackageAuthStatusAsync(packageId, cancellationToken);
                return status is null ? Results.NotFound() : Results.Ok(status);
            });

        group.MapPost(
            "{packageId}/auth/start",
            async (string packageId, RuntimePackageSessionService packageSessionService, PackageAuthCallbackServer packageAuthCallbackServer, CancellationToken cancellationToken) =>
            {
                var session = await packageSessionService.StartPackageAuthAsync(packageId, packageAuthCallbackServer, cancellationToken);
                return session is null ? Results.NotFound() : Results.Ok(session);
            });

        group.MapGet(
            "{packageId}/auth/sessions/{authSessionId}",
            (string packageId, string authSessionId, RuntimePackageSessionService packageSessionService) =>
            {
                var status = packageSessionService.GetPackageAuthSessionStatus(packageId, authSessionId);
                return status is null ? Results.NotFound() : Results.Ok(status);
            });

        group.MapPost(
            "{packageId}/auth/disconnect",
            async (string packageId, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var status = await packageSessionService.DisconnectPackageAsync(packageId, cancellationToken);
                return status is null ? Results.NotFound() : Results.Ok(status);
            });

        return endpoints;
    }
}
