using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageAuthEndpoints
{
    public static IEndpointRouteBuilder MapPackageAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/packages");
        group.MapGet(
            "{packageId}/auth/status",
            async (string packageId, PackageAuthAccessService packageAuth, CancellationToken cancellationToken) =>
            {
                var status = await packageAuth.GetStatusAsync(packageId, cancellationToken);
                return Results.Ok(RuntimeEndpointErrors.Required(status, $"Package '{packageId}' authentication status"));
            });

        group.MapPost(
            "{packageId}/auth/start",
            async (string packageId, PackageAuthAccessService packageAuth, PackageCallbackServer packageCallbackServer, CancellationToken cancellationToken) =>
            {
                var session = await packageAuth.StartAsync(packageId, packageCallbackServer, cancellationToken);
                return Results.Ok(RuntimeEndpointErrors.Required(session, $"Package '{packageId}' authentication handler"));
            });

        group.MapGet(
            "{packageId}/auth/sessions/{authSessionId}",
            (string packageId, string authSessionId, PackageAuthAccessService packageAuth) =>
            {
                var status = packageAuth.GetSessionStatus(packageId, authSessionId);
                return Results.Ok(RuntimeEndpointErrors.Required(status, "Package authentication session"));
            });

        group.MapPost(
            "{packageId}/auth/disconnect",
            async (string packageId, PackageAuthAccessService packageAuth, CancellationToken cancellationToken) =>
            {
                var status = await packageAuth.DisconnectAsync(packageId, cancellationToken);
                return Results.Ok(RuntimeEndpointErrors.Required(status, $"Package '{packageId}' authentication handler"));
            });

        return endpoints;
    }
}
