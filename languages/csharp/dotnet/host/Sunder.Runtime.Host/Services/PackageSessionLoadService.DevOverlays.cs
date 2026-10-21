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
        IReadOnlyDictionary<string, string>? preparationSourcePaths = null,
        CancellationToken cancellationToken = default,
        PackageSessionStartupTraceAttempt? startupTrace = null)
    {
        var started = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var errors = new List<string>();
        RuntimePackageSessionDirectories.ScheduleStaleSessionCleanup();
        var sessionFolder = RuntimePackageSessionDirectories.CreateInstalledSessionFolder();
        Directory.CreateDirectory(sessionFolder);
        var preparedCandidates = new List<PreparedRuntimePackage>();
        var sessionPackages = new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase);
        var devPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = devFolders.Count;
        var retentionOwnershipTransferred = false;

        try
        {
        var preparationResults = new DevPreparationResult?[devFolders.Count];
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, devFolders.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _concurrency.MaximumPreparationConcurrency,
                    CancellationToken = cancellationToken,
                },
                async (devIndex, token) =>
                {
                    var devFolder = devFolders[devIndex];
                    preparationResults[devIndex] = await PrepareDevCandidateAsync(
                            devIndex,
                            devFolder,
                            sessionFolder,
                            token,
                            startupTrace?.GetDevPackage(devFolder))
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch
        {
            // Parallel.ForEachAsync has drained every in-flight preparation before cleanup.
            await DisposeExecutionRuntimeRetentionsAsync(
                    preparationResults
                        .Where(static result => result?.PreparedPackage is not null)
                        .Select(static result => result!.PreparedPackage!))
                .ConfigureAwait(false);
            throw;
        }

        foreach (var result in preparationResults.Select(static result => result!))
        {
            errors.AddRange(result.Errors);
            if (result.PreparedPackage is { } preparedPackage)
            {
                preparedCandidates.Add(preparedPackage);
                devPackageIds.Add(preparedPackage.PackageId);
            }
        }

        foreach (var package in packages)
        {
            ThrowIfCancellationRequested(cancellationToken, sessionFolder);
            if (devPackageIds.Contains(package.PackageId))
            {
                continue;
            }

            var physicalSourceRoot = preparationSourcePaths?.GetValueOrDefault(package.PackageId)
                ?? package.InstallPath;
            var packageTrace = startupTrace?.GetInstalledPackage(package);
            packageTrace?.BeginProjection();
            try
            {
                var errorCount = errors.Count;
                var activation = await _preparer.ReadInstalledActivationStateAsync(
                    package,
                    physicalSourceRoot,
                    errors,
                    cancellationToken,
                    packageTrace);
                if (activation is null)
                {
                    packageTrace?.CompleteProjection("failed");
                    sessionPackages[package.PackageId] = BuildSessionDescriptor(
                        new RuntimePackageActivationState(
                            package.PackageId,
                            package.Name,
                            package.Version,
                            PackageHostRoles.None,
                            package.Icon),
                        isEnabled: false,
                        readiness: PackageReadinessState.Failed,
                        failureOrigin: PackageFailureOrigin.RuntimeActivation,
                        lastError: errors.Skip(errorCount).LastOrDefault() ?? "Installed package failed strict validation.",
                        failureCount: 1);
                    continue;
                }
                if (!package.IsEnabled)
                {
                    packageTrace?.CompleteProjection("not-required");
                    sessionPackages[package.PackageId] = BuildSessionDescriptor(
                        activation,
                        isEnabled: false,
                        readiness: PackageReadinessState.Disabled);
                    continue;
                }

                errorCount = errors.Count;
                var preparedPackage = await _preparer.PrepareInstalledPackageAsync(
                    index++,
                    package,
                    physicalSourceRoot,
                    sessionFolder,
                    errors,
                    cancellationToken,
                    packageTrace);
                if (preparedPackage is not null)
                {
                    packageTrace?.SetPreparedIdentity(preparedPackage);
                    packageTrace?.CompleteProjection("succeeded");
                    preparedCandidates.Add(preparedPackage);
                    continue;
                }

                packageTrace?.CompleteProjection("failed");
                sessionPackages[package.PackageId] = BuildSessionDescriptor(
                    activation,
                    isEnabled: false,
                    readiness: PackageReadinessState.Failed,
                    failureOrigin: PackageFailureOrigin.RuntimeActivation,
                    lastError: errors.Skip(errorCount).LastOrDefault() ?? "Installed package could not be prepared for loading.",
                    failureCount: 1);
            }
            catch (OperationCanceledException)
            {
                packageTrace?.CompleteProjection("canceled");
                throw;
            }
            catch
            {
                packageTrace?.CompleteProjection("failed");
                throw;
            }
        }

        _logger.LogInformation(
            "Prepared {DevPackageCount} dev package(s) and {InstalledPackageCount} installed package(s) in {ElapsedMilliseconds} ms",
            devPackageIds.Count,
            packages.Count(package => !devPackageIds.Contains(package.PackageId)),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        if (preparedCandidates.Count == 0 && sessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(ActivePackageSession.CreateEmpty(startupTrace), warnings, errors);
        }

        ThrowIfCancellationRequested(cancellationToken, sessionFolder);
        retentionOwnershipTransferred = true;
        return await LoadPreparedPackagesAsync(
            sessionFolder,
            preparedCandidates,
            warnings,
            errors,
            sessionPackages,
            cancellationToken,
            startupTrace);
        }
        catch
        {
            if (retentionOwnershipTransferred)
            {
                // LoadPreparedPackagesAsync has already released candidate retentions.
                TryDeleteDirectory(sessionFolder);
            }
            throw;
        }
        finally
        {
            if (!retentionOwnershipTransferred)
            {
                await DisposeExecutionRuntimeRetentionsAsync(preparedCandidates).ConfigureAwait(false);
                TryDeleteDirectory(sessionFolder);
            }
        }
    }

    private async ValueTask<DevPreparationResult> PrepareDevCandidateAsync(
        int index,
        string devFolder,
        string sessionFolder,
        CancellationToken cancellationToken,
        PackageSessionStartupTracePackage? packageTrace)
    {
        var errors = new List<string>();
        packageTrace?.BeginProjection();
        try
        {
            var preparedPackage = await _preparer.PrepareDevPackageAsync(
                    index,
                    devFolder,
                    sessionFolder,
                    errors,
                    cancellationToken,
                    packageTrace)
                .ConfigureAwait(false);
            if (preparedPackage is not null)
            {
                packageTrace?.SetPreparedIdentity(preparedPackage);
            }
            packageTrace?.CompleteProjection(preparedPackage is null ? "failed" : "succeeded");
            return new DevPreparationResult(preparedPackage, errors);
        }
        catch (OperationCanceledException)
        {
            packageTrace?.CompleteProjection("canceled");
            throw;
        }
        catch
        {
            packageTrace?.CompleteProjection("failed");
            throw;
        }
    }

    private sealed record DevPreparationResult(
        PreparedRuntimePackage? PreparedPackage,
        IReadOnlyList<string> Errors);
}
