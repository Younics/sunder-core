using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class ExecutionRuntimeEndpoints
{
    private const string IdentityRoute =
        "{runtimeId}/versions/{version}/revisions/{packagingRevision:int}/{rid}/abis/{moduleAbi}";

    public static IEndpointRouteBuilder MapExecutionRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var runtimes = endpoints.MapGroup("/execution-runtimes");
        runtimes.MapGet(
            string.Empty,
            async (ExecutionRuntimeManagementService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.ListAsync(cancellationToken)));
        runtimes.MapPost(
            "ensure",
            async (RuntimeExecutionRuntimeEnsureRequest request, ExecutionRuntimeManagementService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.EnsureAsync(request, cancellationToken)));
        runtimes.MapPost(
            "import",
            async (RuntimeExecutionRuntimeImportRequest request, ExecutionRuntimeManagementService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.ImportAsync(request, cancellationToken)));
        runtimes.MapPost(
            "gc",
            async (ExecutionRuntimeManagementService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.CollectGarbageAsync(cancellationToken)));
        runtimes.MapGet(
            IdentityRoute,
            async (
                string runtimeId,
                string version,
                int packagingRevision,
                string rid,
                string moduleAbi,
                ExecutionRuntimeManagementService service,
                CancellationToken cancellationToken) => Results.Ok(await service.GetAsync(
                    runtimeId,
                    version,
                    packagingRevision,
                    rid,
                    moduleAbi,
                    cancellationToken)));
        runtimes.MapGet(
            IdentityRoute + "/references",
            async (
                string runtimeId,
                string version,
                int packagingRevision,
                string rid,
                string moduleAbi,
                ExecutionRuntimeManagementService service,
                CancellationToken cancellationToken) => Results.Ok(await service.GetReferencesAsync(
                    runtimeId,
                    version,
                    packagingRevision,
                    rid,
                    moduleAbi,
                    cancellationToken)));
        runtimes.MapGet(
            IdentityRoute + "/impact",
            async (
                string runtimeId,
                string version,
                int packagingRevision,
                string rid,
                string moduleAbi,
                ExecutionRuntimeManagementService service,
                CancellationToken cancellationToken) => Results.Ok(await service.GetImpactAsync(
                    runtimeId,
                    version,
                    packagingRevision,
                    rid,
                    moduleAbi,
                    cancellationToken)));
        runtimes.MapPost(
            IdentityRoute + "/verify",
            async (
                string runtimeId,
                string version,
                int packagingRevision,
                string rid,
                string moduleAbi,
                ExecutionRuntimeManagementService service,
                CancellationToken cancellationToken) => Results.Ok(await service.VerifyAsync(
                    runtimeId,
                    version,
                    packagingRevision,
                    rid,
                    moduleAbi,
                    cancellationToken)));
        runtimes.MapPost(
            IdentityRoute + "/repair",
            async (
                string runtimeId,
                string version,
                int packagingRevision,
                string rid,
                string moduleAbi,
                RuntimeExecutionRuntimeActionRequest request,
                ExecutionRuntimeManagementService service,
                CancellationToken cancellationToken) => Results.Ok(await service.RepairAsync(
                    runtimeId,
                    version,
                    packagingRevision,
                    rid,
                    moduleAbi,
                    request,
                    cancellationToken)));

        endpoints.MapGet(
            "/packages/{packageId}/execution-runtime-bindings",
            async (string packageId, ExecutionRuntimeManagementService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.GetPackageBindingsAsync(packageId, cancellationToken)));
        return endpoints;
    }
}
