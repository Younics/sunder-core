using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class PackageCallbackEndpoints
{
    public static IEndpointRouteBuilder MapPackageCallbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/packages");
        group.MapPost(
            "{packageId}/callbacks/{callbackHandlerId}/start",
            async (
                string packageId,
                string callbackHandlerId,
                PackageCallbackSessionStartRequest request,
                PackageCallbackAccessService callbacks,
                PackageCallbackServer callbackServer,
                CancellationToken cancellationToken) =>
            {
                var session = await callbacks.StartAsync(
                    packageId,
                    callbackHandlerId,
                    request,
                    callbackServer,
                    cancellationToken);
                return Results.Ok(RuntimeEndpointErrors.Required(
                    session,
                    $"Package '{packageId}' callback handler '{callbackHandlerId}'"));
            });

        group.MapGet(
            "{packageId}/callbacks/sessions/{callbackSessionId}",
            (string packageId, string callbackSessionId, PackageCallbackAccessService callbacks) =>
            {
                var status = callbacks.GetStatus(packageId, callbackSessionId);
                return Results.Ok(RuntimeEndpointErrors.Required(status, "Package callback session"));
            });
        group.MapDelete(
            "{packageId}/callbacks/sessions/{callbackSessionId}",
            (string packageId, string callbackSessionId, PackageCallbackAccessService callbacks) =>
                Results.Ok(callbacks.Cancel(packageId, callbackSessionId)));
        return endpoints;
    }
}
