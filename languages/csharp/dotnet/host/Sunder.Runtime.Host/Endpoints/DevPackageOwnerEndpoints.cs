using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class DevPackageOwnerEndpoints
{
    public static IEndpointRouteBuilder MapDevPackageOwnerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var owners = endpoints.MapGroup("/dev-package-owners");
        owners.MapPut(
            "{ownerId}",
            async (
                string ownerId,
                DevPackageOwnerMutationRequest request,
                DevPackageOwnerLeaseService leases,
                CancellationToken cancellationToken) =>
                Results.Ok(await leases.ReplaceAsync(ownerId, request, cancellationToken)));
        owners.MapPost(
            "{ownerId}/heartbeat",
            async (
                string ownerId,
                DevPackageOwnerHeartbeatRequest request,
                DevPackageOwnerLeaseService leases,
                CancellationToken cancellationToken) =>
                Results.Ok(await leases.HeartbeatAsync(ownerId, request, cancellationToken)));
        owners.MapPost(
            "{ownerId}/release",
            async (
                string ownerId,
                DevPackageOwnerReleaseRequest request,
                DevPackageOwnerLeaseService leases,
                CancellationToken cancellationToken) =>
            {
                await leases.ReleaseAsync(ownerId, request, cancellationToken);
                return Results.NoContent();
            });
        return endpoints;
    }
}
