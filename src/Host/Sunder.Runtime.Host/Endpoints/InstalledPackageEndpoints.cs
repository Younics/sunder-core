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
            async (DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
                Results.Ok(await devPackageSessionService.GetInstalledPackagesAsync(cancellationToken)));

        group.MapGet(
            "{packageId}/assets/{**assetPath}",
            async (string packageId, string assetPath, DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
            {
                var assetFilePath = await devPackageSessionService.TryResolvePackageAssetPathAsync(packageId, assetPath, cancellationToken);
                var contentType = assetFilePath is null ? null : ResolveImageContentType(assetFilePath);
                return contentType is null
                    ? Results.NotFound()
                    : Results.File(assetFilePath!, contentType);
            });

        group.MapPost(
            "install/local",
            async (PackageInstallFromPathRequest request, DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await devPackageSessionService.InstallPackageFromPathAsync(request.PackagePath, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "{packageId}/upgrade/local",
            async (string packageId, PackageUpgradeFromPathRequest request, DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await devPackageSessionService.UpgradePackageFromPathAsync(packageId, request, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "{packageId}/enable",
            async (string packageId, DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await devPackageSessionService.SetInstalledPackageEnabledAsync(packageId, isEnabled: true, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapPost(
            "{packageId}/disable",
            async (string packageId, DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await devPackageSessionService.SetInstalledPackageEnabledAsync(packageId, isEnabled: false, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
            });

        group.MapDelete(
            "{packageId}",
            async (string packageId, DevPackageSessionService devPackageSessionService, CancellationToken cancellationToken) =>
            {
                var result = await devPackageSessionService.UninstallPackageAsync(packageId, cancellationToken);
                return result.Success ? Results.Ok(result) : Results.BadRequest(result);
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
