using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Endpoints;

internal static class InstalledPackageEndpoints
{
    public static IEndpointRouteBuilder MapInstalledPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/packages");

        group.MapGet(
            "installed",
            async (InstalledPackageLifecycleService installedPackages, CancellationToken cancellationToken) =>
                Results.Ok(await installedPackages.GetInstalledAsync(cancellationToken)));

        group.MapGet(
            "{packageId}/assets/{**assetPath}",
            async (string packageId, string assetPath, RuntimePackageUiService packageUi, CancellationToken cancellationToken) =>
            {
                var assetFilePath = await packageUi.TryResolveAssetPathAsync(packageId, assetPath, cancellationToken);
                var contentType = assetFilePath is null ? null : ResolveImageContentType(assetFilePath);
                return contentType is null
                    ? throw new RuntimeNotFoundException($"Package asset '{assetPath}' was not found.")
                    : Results.File(assetFilePath!, contentType);
            });

        group.MapPost(
            "{packageId}/enable",
            async (string packageId, InstalledPackageLifecycleService installedPackages, CancellationToken cancellationToken) =>
            {
                var result = await installedPackages.SetEnabledAsync(packageId, enabled: true, cancellationToken);
                return Results.Ok(result);
            });

        group.MapPost(
            "{packageId}/disable",
            async (string packageId, InstalledPackageLifecycleService installedPackages, CancellationToken cancellationToken) =>
            {
                var result = await installedPackages.SetEnabledAsync(packageId, enabled: false, cancellationToken);
                return Results.Ok(result);
            });

        group.MapGet(
            "{packageId}/uninstall-plan",
            async (string packageId, InstalledPackageLifecycleService installedPackages, CancellationToken cancellationToken) =>
            {
                var plan = await installedPackages.GetUninstallPlanAsync(packageId, cancellationToken);
                return Results.Ok(RuntimeEndpointErrors.Required(plan, "Installed package"));
            });

        group.MapPost(
            "{packageId}/uninstall",
            async (string packageId, PackageUninstallRequest request, InstalledPackageLifecycleService installedPackages, CancellationToken cancellationToken) =>
            {
                var result = await installedPackages.UninstallAsync(packageId, request, cancellationToken);
                return Results.Ok(result);
            });

        group.MapPost(
            "store/stage",
            async (PackageStoreStageRequest request, InstalledPackageLifecycleService installedPackages, CancellationToken cancellationToken) =>
            {
                var result = await installedPackages.StageAsync(request, cancellationToken);
                return Results.Ok(result);
            });

        group.MapPost(
            "store/stage/{stageId}/commit",
            async (string stageId, InstalledPackageLifecycleService installedPackages, CancellationToken cancellationToken) =>
            {
                var result = await installedPackages.CommitStageAsync(stageId, cancellationToken);
                return Results.Ok(result);
            });

        group.MapDelete(
            "store/stage/{stageId}",
            async (string stageId, InstalledPackageLifecycleService installedPackages, CancellationToken cancellationToken) =>
            {
                var discarded = await installedPackages.DiscardStageAsync(stageId, cancellationToken);
                if (!discarded)
                {
                    throw new RuntimeNotFoundException($"Package store stage '{stageId}' was not found.");
                }
                return Results.NoContent();
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
