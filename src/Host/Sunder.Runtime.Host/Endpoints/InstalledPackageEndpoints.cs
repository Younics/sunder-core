using Sunder.Protocol;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class InstalledPackageEndpoints
{
    public static IEndpointRouteBuilder MapInstalledPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/packages");

        group.MapGet(
            "installed",
            async (RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
                Results.Ok(await packageSessionService.GetInstalledPackagesAsync(cancellationToken)));

        group.MapGet(
            "{packageId}/assets/{**assetPath}",
            async (string packageId, string assetPath, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var assetFilePath = await packageSessionService.TryResolvePackageAssetPathAsync(packageId, assetPath, cancellationToken);
                var contentType = assetFilePath is null ? null : ResolveImageContentType(assetFilePath);
                return contentType is null
                    ? Results.NotFound()
                    : Results.File(assetFilePath!, contentType);
            });

        group.MapPost(
            "install/local",
            async (PackageInstallFromPathRequest request, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.InstallPackageFromPathAsync(request.PackagePath, request.ApplyRuntimeSession, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "install/local-batch",
            async (PackageInstallBatchFromPathRequest request, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.InstallPackagesFromPathsAsync(request, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "{packageId}/upgrade/local",
            async (string packageId, PackageUpgradeFromPathRequest request, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.UpgradePackageFromPathAsync(packageId, request, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "{packageId}/enable",
            async (string packageId, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.SetInstalledPackageEnabledAsync(packageId, isEnabled: true, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "{packageId}/disable",
            async (string packageId, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.SetInstalledPackageEnabledAsync(packageId, isEnabled: false, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapDelete(
            "{packageId}",
            async (string packageId, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.UninstallPackageAsync(packageId, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "store/stage",
            async (PackageStoreStageRequest request, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.StagePackageStoreChangesAsync(request, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "store/stage/{stageId}/commit",
            async (string stageId, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await packageSessionService.CommitPackageStoreStageAsync(stageId, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapDelete(
            "store/stage/{stageId}",
            async (string stageId, RuntimePackageSessionService packageSessionService, CancellationToken cancellationToken) =>
            {
                var discarded = await packageSessionService.DiscardPackageStoreStageAsync(stageId, cancellationToken);
                return discarded ? Results.NoContent() : Results.NotFound();
            });

        return endpoints;
    }

    private static string? ResolveImageContentType(string filePath)
        => Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".ico" => "image/x-icon",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".svg" or ".svgz" => "image/svg+xml",
            ".webp" => "image/webp",
            _ => null,
        };
}
