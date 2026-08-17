using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionReconciler(
    InstalledPackageStore installedPackageStore,
    PackageSessionLoadService loader,
    ILogger<PackageSessionReconciler> logger)
{
    public async Task<PackageSessionLoadResult> LoadMergedSessionAsync(
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        return await LoadMergedSessionCoreAsync(
            await installedPackageStore.ListAsync(cancellationToken),
            devOverlays,
            null,
            started,
            cancellationToken);
    }

    public async Task<PackageSessionLoadResult> LoadMergedSessionAsync(
        IReadOnlyList<InstalledPackageRecord> installedPackages,
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        IReadOnlyDictionary<string, string>? preparationSourcePaths = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        return await LoadMergedSessionCoreAsync(
            installedPackages,
            devOverlays,
            preparationSourcePaths,
            started,
            cancellationToken);
    }

    private async Task<PackageSessionLoadResult> LoadMergedSessionCoreAsync(
        IReadOnlyList<InstalledPackageRecord> installedPackages,
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        IReadOnlyDictionary<string, string>? preparationSourcePaths,
        long started,
        CancellationToken cancellationToken)
    {
        using var preparationScope = logger.BeginScope(
            "Preparing merged package session with {InstalledPackageCount} installed package(s) and {DevPackageCount} dev overlay(s)",
            installedPackages.Count,
            devOverlays.Count);
        var devFolders = devOverlays.Select(overlay => overlay.Folder).ToArray();
        PackageSessionLoadResult result;
        if (installedPackages.Count == 0 && devFolders.Length == 0)
        {
            result = new PackageSessionLoadResult(ActivePackageSession.CreateEmpty(), [], []);
        }
        else
        {
            result = devFolders.Length == 0
                ? await loader.LoadInstalledAsync(installedPackages, preparationSourcePaths, cancellationToken)
                : await loader.LoadInstalledWithDevOverlaysAsync(installedPackages, devFolders, preparationSourcePaths, cancellationToken);
        }

        logger.LogInformation(
            "Prepared merged package session from {InstalledPackageCount} installed package(s) and {DevPackageCount} dev overlay(s) with {SessionPackageCount} session package(s), {WarningCount} warning(s), and {ErrorCount} error(s) in {ElapsedMilliseconds} ms",
            installedPackages.Count,
            devOverlays.Count,
            result.Session?.GetSessionPackages().Count ?? 0,
            result.Warnings.Count,
            result.Errors.Count,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }
}
