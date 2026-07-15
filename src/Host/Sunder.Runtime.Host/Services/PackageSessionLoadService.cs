using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class PackageSessionLoadService
{
    private readonly PackageSessionPreparer _preparer = new();
    private readonly RuntimePackageActivator _activator;
    private readonly ILogger _logger;

    public PackageSessionLoadService(ILogger logger, RuntimePackagePaths? paths = null)
    {
        _logger = logger;
        _activator = new RuntimePackageActivator(logger, paths ?? new RuntimePackagePaths());
    }

    public async Task<PackageSessionLoadResult> LoadInstalledAsync(
        IReadOnlyList<InstalledPackageRecord> packages,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var errors = new List<string>();
        RuntimePackageSessionDirectories.ScheduleStaleSessionCleanup();
        var sessionFolder = RuntimePackageSessionDirectories.CreateInstalledSessionFolder();
        Directory.CreateDirectory(sessionFolder);
        var fileMaterializer = new PackageSessionFileMaterializer();
        var preparedCandidates = new List<PreparedRuntimePackage>();
        var sessionPackages = new Dictionary<string, SessionPackageDescriptor>(StringComparer.OrdinalIgnoreCase);
        var enabledPackages = packages.Where(static package => package.IsEnabled).ToArray();

        for (var index = 0; index < packages.Count; index++)
        {
            ThrowIfCancellationRequested(cancellationToken, sessionFolder);
            var package = packages[index];
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
            var preparedPackage = _preparer.PrepareInstalledPackage(index, package, sessionFolder, fileMaterializer, errors);
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

        if (enabledPackages.Length == 0 && sessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(ActivePackageSession.Empty, warnings, errors);
        }

        ThrowIfCancellationRequested(cancellationToken, sessionFolder);
        return await LoadPreparedPackagesAsync(sessionFolder, preparedCandidates, warnings, errors, sessionPackages, cancellationToken);
    }

    private async Task<PackageSessionLoadResult> LoadPreparedPackagesAsync(
        string sessionFolder,
        IReadOnlyList<PreparedRuntimePackage> preparedCandidates,
        ICollection<string> warnings,
        ICollection<string> errors,
        IDictionary<string, SessionPackageDescriptor> initialSessionPackages,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (preparedCandidates.Count == 0 && initialSessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(ActivePackageSession.Empty, warnings.ToArray(), errors.ToArray());
        }

        ThrowIfCancellationRequested(cancellationToken, sessionFolder);
        var orderedPackages = new PackageLoadPlanner().ResolveLoadOrder(preparedCandidates, errors);
        RuntimeSharedAssemblyRegistry sharedAssemblyRegistry;
        try
        {
            sharedAssemblyRegistry = new RuntimeSharedAssemblyRegistry(orderedPackages.Select(x => x.LibraryFolder));
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(null, warnings.ToArray(), errors.ToArray());
        }

        var loadedPackages = new Dictionary<string, ActiveLoadedPackage>(StringComparer.OrdinalIgnoreCase);
        var readySources = new List<RuntimePackageSource>(orderedPackages.Count);
        var sessionPackages = new Dictionary<string, SessionPackageDescriptor>(initialSessionPackages, StringComparer.OrdinalIgnoreCase);
        var readyPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensionCatalog = new RuntimePackageExtensionCatalog();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var preparedPackage in orderedPackages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!preparedPackage.Dependencies.All(readyPackageIds.Contains))
                {
                    var message = $"Skipped '{preparedPackage.PackageId}' because one of its dependencies did not load successfully.";
                    errors.Add(message);
                    sessionPackages[preparedPackage.PackageId] = BuildSessionDescriptor(
                        preparedPackage.Activation,
                        isEnabled: false,
                        readiness: PackageReadinessState.Failed,
                        failureOrigin: PackageFailureOrigin.RuntimeActivation,
                        lastError: message,
                        failureCount: 1);
                    continue;
                }

                if (RuntimePackageRoleActivation.TryCompleteWithoutRuntime(preparedPackage, readyPackageIds, readySources, sessionPackages)) continue;

                var activationStarted = Stopwatch.GetTimestamp();
                var activation = await _activator.ActivateAsync(
                    preparedPackage, sharedAssemblyRegistry, extensionCatalog, warnings, errors, cancellationToken);
                _logger.LogInformation(
                    "Activated Runtime package {PackageId} in {ElapsedMilliseconds} ms",
                    preparedPackage.PackageId,
                    Stopwatch.GetElapsedTime(activationStarted).TotalMilliseconds);
                if (!activation.Success)
                {
                    sessionPackages[preparedPackage.PackageId] = activation.SessionPackage;
                    continue;
                }

                RuntimePackageRoleActivation.MarkReady(preparedPackage, readyPackageIds, readySources);
                loadedPackages[preparedPackage.PackageId] = activation.LoadedPackage!;
                sessionPackages[preparedPackage.PackageId] = activation.SessionPackage;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var partialSession = new ActivePackageSession(
                sessionFolder,
                loadedPackages,
                sessionPackages,
                extensionCatalog,
                sharedAssemblyRegistry,
                false,
                readySources);
            await partialSession.StopBackgroundServicesAsync();
            await partialSession.DisposeAsync();
            throw;
        }

        var session = new ActivePackageSession(
            sessionFolder,
            loadedPackages,
            sessionPackages,
            extensionCatalog,
            sharedAssemblyRegistry,
            false,
            readySources);
        _logger.LogInformation(
            "Loaded Runtime package session with {PreparedPackageCount} prepared package(s) in {ElapsedMilliseconds} ms",
            preparedCandidates.Count,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new PackageSessionLoadResult(session, warnings.ToArray(), errors.ToArray());
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup for failed reload attempts.
        }
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken, string sessionFolder)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        TryDeleteDirectory(sessionFolder);
        cancellationToken.ThrowIfCancellationRequested();
    }

}
