using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionReconciler(
    InstalledPackageStore installedPackageStore,
    PackageSessionLoadService loader,
    ILogger<PackageSessionReconciler> logger,
    PackageSessionStartupTraceWriter? startupTraceWriter = null)
{
    public async Task<PackageSessionLoadResult> LoadMergedSessionAsync(
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        long baseGeneration,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var startupTrace = startupTraceWriter?.BeginAttempt(baseGeneration);
        IReadOnlyList<InstalledPackageRecord> installedPackages;
        try
        {
            installedPackages = await installedPackageStore.ListAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            startupTrace?.TryFail("canceled");
            throw;
        }
        catch
        {
            startupTrace?.TryFail();
            throw;
        }
        return await LoadMergedSessionCoreAsync(
            installedPackages,
            devOverlays,
            null,
            null,
            baseGeneration,
            started,
            startupTrace,
            cancellationToken);
    }

    public async Task<PackageSessionLoadResult> LoadMergedSessionAsync(
        IReadOnlyList<InstalledPackageRecord> installedPackages,
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        long baseGeneration,
        IReadOnlyDictionary<string, string>? preparationSourcePaths = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var startupTrace = startupTraceWriter?.BeginAttempt(baseGeneration);
        return await LoadMergedSessionCoreAsync(
            installedPackages,
            devOverlays,
            preparationSourcePaths,
            null,
            baseGeneration,
            started,
            startupTrace,
            cancellationToken);
    }

    internal async Task<PackageSessionLoadResult> LoadMergedSessionAsync(
        IReadOnlyList<InstalledPackageRecord> installedPackages,
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        long baseGeneration,
        IReadOnlyDictionary<string, string>? preparationSourcePaths,
        ExecutionRuntimeLifecyclePolicySnapshot? runtimePolicyAuthority,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var startupTrace = startupTraceWriter?.BeginAttempt(baseGeneration);
        return await LoadMergedSessionCoreAsync(
            installedPackages,
            devOverlays,
            preparationSourcePaths,
            runtimePolicyAuthority,
            baseGeneration,
            started,
            startupTrace,
            cancellationToken);
    }

    private async Task<PackageSessionLoadResult> LoadMergedSessionCoreAsync(
        IReadOnlyList<InstalledPackageRecord> installedPackages,
        IReadOnlyCollection<PackageSessionDevOverlay> devOverlays,
        IReadOnlyDictionary<string, string>? preparationSourcePaths,
        ExecutionRuntimeLifecyclePolicySnapshot? runtimePolicyAuthority,
        long baseGeneration,
        long started,
        PackageSessionStartupTraceAttempt? startupTrace,
        CancellationToken cancellationToken)
    {
        try
        {
            startupTrace?.RecordSourceDiscovery(installedPackages, devOverlays);
            using var preparationScope = logger.BeginScope(
                "Preparing merged package session with {InstalledPackageCount} installed package(s) and {DevPackageCount} dev overlay(s)",
                installedPackages.Count,
                devOverlays.Count);
            var devFolders = devOverlays.Select(overlay => overlay.Folder).ToArray();
            PackageSessionLoadResult result;
            if (installedPackages.Count == 0 && devFolders.Length == 0)
            {
                result = new PackageSessionLoadResult(ActivePackageSession.CreateEmpty(startupTrace), [], []);
            }
            else
            {
                result = devFolders.Length == 0
                    ? await loader.LoadInstalledAsync(
                        installedPackages,
                        preparationSourcePaths,
                        cancellationToken,
                        startupTrace,
                        runtimePolicyAuthority)
                    : await loader.LoadInstalledWithDevOverlaysAsync(
                        installedPackages,
                        devFolders,
                        preparationSourcePaths,
                        cancellationToken,
                        startupTrace,
                        runtimePolicyAuthority);
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
        catch (OperationCanceledException)
        {
            startupTrace?.TryFail("canceled");
            throw;
        }
        catch
        {
            startupTrace?.TryFail();
            throw;
        }
    }
}
