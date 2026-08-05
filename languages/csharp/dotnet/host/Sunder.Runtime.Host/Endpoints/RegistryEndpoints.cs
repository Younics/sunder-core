using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class RegistryEndpoints
{
    public static IEndpointRouteBuilder MapRegistryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/registry");

        group.MapPost("auth/start", (RuntimeRegistryAuthStartRequest request, RegistryAuthCoordinator auth) => Results.Ok(auth.Start(request)));
        group.MapGet("auth/sessions/{sessionId}", (string sessionId, RegistryAuthCoordinator auth) =>
            Results.Ok(RuntimeEndpointErrors.Required(auth.GetSession(sessionId), "Registry authentication session")));
        group.MapGet("auth/status", async (string origin, RegistryAuthCoordinator auth, CancellationToken token) =>
            Results.Ok(await auth.GetStatusAsync(origin, token)));
        group.MapPost("auth/logout", async (RuntimeRegistryOriginRequest request, RegistryAuthCoordinator auth, CancellationToken token) =>
            Results.Ok(await auth.LogoutAsync(request.RegistryOrigin, token)));

        group.MapPost("packages/plan", async (RuntimeRegistryPackageBatchRequest request, RegistryPackageChangeOrchestrator orchestrator, CancellationToken token) =>
            Results.Ok(await orchestrator.ResolveAsync(request, token)));
        group.MapPost("packages/install", async (RuntimeRegistryPackageRequest request, RegistryPackageChangeOrchestrator orchestrator, CancellationToken token) =>
            Results.Ok(await orchestrator.InstallAsync(request, token)));
        group.MapPost("packages/apply", async (RuntimeRegistryPackageBatchRequest request, RegistryPackageChangeOrchestrator orchestrator, CancellationToken token) =>
            Results.Ok(await orchestrator.ExecuteAsync(request, token)));
        group.MapPost("packages/update", async (RuntimeRegistryUpdateRequest request, RegistryPackageChangeOrchestrator orchestrator, CancellationToken token) =>
            Results.Ok(await orchestrator.UpdateAsync(request, token)));
        group.MapPost("packages/adopt-source", async (RuntimeRegistrySourceAdoptionRequest request, RegistryPackageChangeOrchestrator orchestrator, CancellationToken token) =>
            Results.Ok(await orchestrator.AdoptSourceAsync(request, token)));

        group.MapPost("packages/star", async (RuntimeRegistryStarRequest request, RegistryAuthenticatedOperations operations, CancellationToken token) =>
            Results.Ok(await operations.SetPackageStarAsync(request, token)));
        group.MapPost("stacks/star", async (RuntimeRegistryStarRequest request, RegistryAuthenticatedOperations operations, CancellationToken token) =>
            Results.Ok(await operations.SetStackStarAsync(request, token)));
        group.MapPost("packages/publish", async (RuntimeRegistryPublishRequest request, RegistryAuthenticatedOperations operations, CancellationToken token) =>
            Results.Ok(await operations.PublishPackageAsync(request, token)));
        group.MapPost("stacks/publish", async (RuntimeRegistryPublishRequest request, RegistryAuthenticatedOperations operations, CancellationToken token) =>
            Results.Ok(await operations.PublishStackAsync(request, token)));
        group.MapPost("stacks/delete", async (RuntimeRegistryDeleteStackRequest request, RegistryAuthenticatedOperations operations, CancellationToken token) =>
            Results.Ok(await operations.DeleteStackAsync(request, token)));
        group.MapPost("packages/yank", async (RuntimeRegistryYankRequest request, RegistryAuthenticatedOperations operations, CancellationToken token) =>
            Results.Ok(await operations.SetYankAsync(request, token)));
        group.MapPost("packages/deprecate", async (RuntimeRegistryDeprecateRequest request, RegistryAuthenticatedOperations operations, CancellationToken token) =>
            Results.Ok(await operations.SetDeprecationAsync(request, token)));
        group.MapPost("packages/dist-tag", async (RuntimeRegistryDistTagRequest request, RegistryAuthenticatedOperations operations, CancellationToken token) =>
            Results.Ok(await operations.SetDistTagAsync(request, token)));

        return endpoints;
    }
}
