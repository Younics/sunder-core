using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageSessionLoadService
{
    public async Task<PackageSessionLoadResult> LoadInstalledWithDevOverlaysAsync(
        IReadOnlyList<InstalledPackageRecord> packages,
        IReadOnlyList<string> devFolders,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var errors = new List<string>();
        RuntimePackageSessionDirectories.ScheduleStaleSessionCleanup();
        var sessionFolder = RuntimePackageSessionDirectories.CreateInstalledSessionFolder();
        Directory.CreateDirectory(sessionFolder);
        var fileMaterializer = new PackageSessionFileMaterializer();
        var preparedCandidates = new List<PreparedRuntimePackage>();
        var sessionPackages = new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase);
        var devPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var devFolder in devFolders)
        {
            ThrowIfCancellationRequested(cancellationToken, sessionFolder);
            var preparedPackage = _preparer.PrepareDevPackage(index++, devFolder, sessionFolder, fileMaterializer, errors);
            if (preparedPackage is null)
            {
                continue;
            }

            preparedCandidates.Add(preparedPackage);
            devPackageIds.Add(preparedPackage.PackageId);
        }

        foreach (var package in packages)
        {
            ThrowIfCancellationRequested(cancellationToken, sessionFolder);
            if (devPackageIds.Contains(package.PackageId))
            {
                continue;
            }

            var activation = PackageSessionPreparer.ToActivationState(package);
            if (!package.IsEnabled)
            {
                sessionPackages[package.PackageId] = BuildSessionDescriptor(
                    activation,
                    isEnabled: false,
                    readiness: PackageReadinessState.Disabled);
                continue;
            }

            var errorCount = errors.Count;
            var preparedPackage = _preparer.PrepareInstalledPackage(index++, package, sessionFolder, fileMaterializer, errors);
            if (preparedPackage is not null)
            {
                preparedCandidates.Add(preparedPackage);
                continue;
            }

            sessionPackages[package.PackageId] = BuildSessionDescriptor(
                activation,
                isEnabled: false,
                readiness: PackageReadinessState.Failed,
                failureOrigin: PackageFailureOrigin.RuntimeActivation,
                lastError: errors.Skip(errorCount).LastOrDefault() ?? "Installed package could not be prepared for loading.",
                failureCount: 1);
        }

        _logger.LogInformation(
            "Prepared {DevPackageCount} dev package(s) and {InstalledPackageCount} installed package(s) in {ElapsedMilliseconds} ms",
            devPackageIds.Count,
            packages.Count - devPackageIds.Count,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        if (preparedCandidates.Count == 0 && sessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(ActivePackageSession.Empty, warnings, errors);
        }

        ThrowIfCancellationRequested(cancellationToken, sessionFolder);
        return await LoadPreparedPackagesAsync(sessionFolder, preparedCandidates, warnings, errors, sessionPackages, cancellationToken);
    }
}
