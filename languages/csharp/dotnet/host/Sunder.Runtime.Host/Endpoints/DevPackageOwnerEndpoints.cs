using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class DevPackageOwnerEndpoints
{
    public static IEndpointRouteBuilder MapDevPackageOwnerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/dev-packages/prepare-imports", async (RuntimePrepareDevPackageImportsRequest request,
            DevelopmentImportAuthorityPreparation preparation, CancellationToken token) =>
        {
            try
            {
                return Results.Ok(await preparation.PrepareAsync(request, token));
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                // This operation validates caller-selected evidence. Parser/archive diagnostics
                // may contain local paths or untrusted metadata; expose fixed guidance instead.
                // Cancellation, transport and persistence failures retain their normal handling.
                throw new RuntimeValidationException(
                    "contract-authority.release-conflict: Development import evidence is invalid or incomplete. "
                    + "Supply the exact local candidate archives and Core catalog, or explicitly select the published dependency Registry.");
            }
        });
        var owners = endpoints.MapGroup("/dev-package-owners");
        owners.MapPost("{ownerId}/reload/stage", (string ownerId, DevPackageOwnerReloadStageRequest request,
            DevPackageOwnerLeaseService leases, CancellationToken token) => leases.StageReloadAsync(ownerId, request, token));
        owners.MapPost("{ownerId}/reload/commit", (string ownerId, DevPackageOwnerReloadCommitRequest request,
            DevPackageOwnerLeaseService leases, CancellationToken token) => leases.CommitReloadAsync(ownerId, request, token));
        owners.MapPost("{ownerId}/reload/discard", async (string ownerId, DevPackageOwnerReloadCommitRequest request,
            DevPackageOwnerLeaseService leases, CancellationToken token) =>
        {
            await leases.DiscardReloadAsync(ownerId, request, token);
            return Results.NoContent();
        });
        owners.MapPost("{ownerId}/release/stage", (string ownerId, DevPackageOwnerReleaseStageRequest request,
            DevPackageOwnerLeaseService leases, CancellationToken token) => leases.StageReleaseAsync(ownerId, request, token));
        owners.MapPost("{ownerId}/release/commit", (string ownerId, DevPackageOwnerReleaseCommitRequest request,
            DevPackageOwnerLeaseService leases, CancellationToken token) => leases.CommitReleaseAsync(ownerId, request, token));
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
