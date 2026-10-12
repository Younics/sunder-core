using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class ExecutionRuntimeEndpoints
{
    private const string IdentityRoute =
        "sources/{sourceId}/{runtimeId}/versions/{vendorVersion}/{rid}/abis/{moduleAbi}/checksums/{algorithm}/{digest}";

    public static IEndpointRouteBuilder MapExecutionRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var runtimes = endpoints.MapGroup("/execution-runtimes");
        runtimes.MapGet("settings", async (ExecutionRuntimeManagementState state, CancellationToken token) => Results.Ok((await state.ReadAsync(token)).Settings));
        runtimes.MapPut("settings", async (RuntimeExecutionRuntimeSettingsUpdateRequest request, ExecutionRuntimeManagementState state, RuntimeOperationGate operations, CancellationToken token) =>
        {
            await using var operation = await operations.EnterAsync(token);
            return Results.Ok(await state.UpdateSettingsAsync(request, operation.CancellationToken));
        });
        runtimes.MapGet("updates/status", async (ExecutionRuntimeUpdateService service, CancellationToken token) => Results.Ok(await service.StatusAsync(token)));
        runtimes.MapGet("catalog", async (HttpRequest request, ExecutionRuntimeInstallationService service, CancellationToken token) =>
        {
            foreach (var key in request.Query.Keys)
            {
                if (!string.Equals(key, "sourceId", StringComparison.Ordinal)
                    && !string.Equals(key, "skip", StringComparison.Ordinal)
                    && !string.Equals(key, "take", StringComparison.Ordinal))
                {
                    return Results.BadRequest($"Unknown execution runtime catalog query parameter '{key}'.");
                }
            }

            var sourceId = request.Query.TryGetValue("sourceId", out var sourceValues)
                ? ParseSourceId(sourceValues.FirstOrDefault())
                : null;
            var skip = request.Query.TryGetValue("skip", out var skipValues)
                && int.TryParse(skipValues.FirstOrDefault(), out var parsedSkip)
                ? parsedSkip
                : 0;
            var take = request.Query.TryGetValue("take", out var takeValues)
                && int.TryParse(takeValues.FirstOrDefault(), out var parsedTake)
                ? parsedTake
                : 50;
            return Results.Ok(await service.CatalogAsync(new(sourceId, skip, take), token));
        });
        runtimes.MapPost("install/preview", async (RuntimeExecutionRuntimeInstallPreviewRequest request, ExecutionRuntimeInstallationService service, CancellationToken token) => Results.Ok(await service.PreviewAsync(request, token)));
        runtimes.MapPost("install/apply", async (RuntimeExecutionRuntimePlanConfirmation request, ExecutionRuntimeInstallationService service, CancellationToken token) => Results.Ok(await service.ApplyAsync(request, token)));
        runtimes.MapPost("updates/preview", async (RuntimeExecutionRuntimeUpdatePreviewRequest request, ExecutionRuntimeUpdateService service, CancellationToken token) => Results.Ok(await service.PreviewAsync(request, token)));
        runtimes.MapPost("updates/prepare", async (RuntimeExecutionRuntimePlanConfirmation request, ExecutionRuntimeUpdateService service, CancellationToken token) => Results.Ok(await service.PrepareAsync(request, token)));
        runtimes.MapPost("updates/apply", async (RuntimeExecutionRuntimeUpdateApplyRequest request, ExecutionRuntimeUpdateService service, CancellationToken token) => Results.Ok(await service.ApplyAsync(request, token)));
        runtimes.MapPost("uninstall/preview", async (RuntimeExecutionRuntimeUninstallPreviewRequest request, ExecutionRuntimeUpdateService service, CancellationToken token) => Results.Ok(await service.PreviewUninstallAsync(request, token)));
        runtimes.MapPost("uninstall/apply", async (RuntimeExecutionRuntimePlanConfirmation request, ExecutionRuntimeUpdateService service, CancellationToken token) => Results.Ok(await service.UninstallAsync(request, token)));
        endpoints.MapPost("/packages/runtime-prerequisites", async (RuntimePackageRuntimePrerequisiteRequest request, PackageRuntimePrerequisiteService service, CancellationToken token) => Results.Ok(await service.PreviewAsync(request, token)));
        runtimes.MapGet(
            string.Empty,
            async (ExecutionRuntimeManagementService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.ListAsync(cancellationToken)));
        runtimes.MapPost(
            "ensure",
            async (RuntimeExecutionRuntimeEnsureRequest request, ExecutionRuntimeManagementService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.EnsureAsync(request, cancellationToken)));
        runtimes.MapPost(
            "gc",
            async (ExecutionRuntimeManagementService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.CollectGarbageAsync(cancellationToken)));
        runtimes.MapGet(
            IdentityRoute,
            async (
                string runtimeId,
                string sourceId,
                string vendorVersion,
                string rid,
                string moduleAbi,
                string algorithm,
                string digest,
                ExecutionRuntimeManagementService service,
                CancellationToken cancellationToken) => Results.Ok(await service.GetAsync(
                    CreateIdentity(sourceId, runtimeId, vendorVersion, rid, moduleAbi, algorithm, digest),
                    cancellationToken)));
        runtimes.MapGet(
            IdentityRoute + "/references",
            async (
                string runtimeId,
                string sourceId,
                string vendorVersion,
                string rid,
                string moduleAbi,
                string algorithm,
                string digest,
                ExecutionRuntimeManagementService service,
                CancellationToken cancellationToken) => Results.Ok(await service.GetReferencesAsync(
                    CreateIdentity(sourceId, runtimeId, vendorVersion, rid, moduleAbi, algorithm, digest),
                    cancellationToken)));
        runtimes.MapGet(
            IdentityRoute + "/impact",
            async (
                string runtimeId,
                string sourceId,
                string vendorVersion,
                string rid,
                string moduleAbi,
                string algorithm,
                string digest,
                ExecutionRuntimeManagementService service,
                CancellationToken cancellationToken) => Results.Ok(await service.GetImpactAsync(
                    CreateIdentity(sourceId, runtimeId, vendorVersion, rid, moduleAbi, algorithm, digest),
                    cancellationToken)));
        runtimes.MapPost(
            IdentityRoute + "/verify",
            async (
                string runtimeId,
                string sourceId,
                string vendorVersion,
                string rid,
                string moduleAbi,
                string algorithm,
                string digest,
                ExecutionRuntimeManagementService service,
                CancellationToken cancellationToken) => Results.Ok(await service.VerifyAsync(
                    CreateIdentity(sourceId, runtimeId, vendorVersion, rid, moduleAbi, algorithm, digest),
                    cancellationToken)));
        runtimes.MapPost(
            IdentityRoute + "/repair",
            async (
                string runtimeId,
                string sourceId,
                string vendorVersion,
                string rid,
                string moduleAbi,
                string algorithm,
                string digest,
                RuntimeExecutionRuntimeActionRequest request,
                ExecutionRuntimeManagementService service,
                CancellationToken cancellationToken) => Results.Ok(await service.RepairAsync(
                    CreateIdentity(sourceId, runtimeId, vendorVersion, rid, moduleAbi, algorithm, digest),
                    request,
                    cancellationToken)));

        endpoints.MapGet(
            "/packages/{packageId}/execution-runtime-bindings",
            async (string packageId, ExecutionRuntimeManagementService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.GetPackageBindingsAsync(packageId, cancellationToken)));
        return endpoints;
    }

    private static RuntimeExecutionRuntimeIdentity CreateIdentity(
        string sourceId,
        string runtimeId,
        string vendorVersion,
        string rid,
        string moduleAbi,
        string algorithm,
        string digest)
        => new(
            ParseSourceId(sourceId) ?? throw new RuntimeValidationException("Execution runtime source id is required."),
            runtimeId,
            vendorVersion,
            rid,
            moduleAbi,
            ParseChecksumAlgorithm(algorithm),
            digest);

    private static ConfiguredOfficialSourceId? ParseSourceId(string? sourceId)
        => sourceId switch
        {
            null or "" => null,
            "dotnet" => ConfiguredOfficialSourceId.DotNet,
            "node" => ConfiguredOfficialSourceId.Node,
            _ => throw new RuntimeValidationException("Execution runtime source id must be 'dotnet' or 'node'."),
        };

    private static RuntimeExecutionRuntimeChecksumAlgorithm ParseChecksumAlgorithm(string algorithm)
        => algorithm switch
        {
            "sha512" => RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
            "sha256" => RuntimeExecutionRuntimeChecksumAlgorithm.Sha256,
            _ => throw new RuntimeValidationException("Execution runtime checksum algorithm must be 'sha512' or 'sha256'."),
        };
}
