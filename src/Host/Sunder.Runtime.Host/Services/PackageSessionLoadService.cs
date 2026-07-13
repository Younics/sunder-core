using Microsoft.Extensions.Logging;
using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.PackageProtocolMapper;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionLoadService
{
    private readonly PackageSessionPreparer _preparer = new();
    private readonly RuntimePackageActivator _activator;

    public PackageSessionLoadService(ILogger logger, RuntimePackagePaths? paths = null)
    {
        _activator = new RuntimePackageActivator(logger, paths ?? new RuntimePackagePaths());
    }

    public async Task<PackageSessionLoadResult> LoadInstalledAsync(
        IReadOnlyList<InstalledPackageRecord> packages,
        bool startBackgroundServices = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var errors = new List<string>();
        RuntimePackageSessionDirectories.CleanupStaleSessions();
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
        return await LoadPreparedPackagesAsync(sessionFolder, preparedCandidates, warnings, errors, sessionPackages, startBackgroundServices, cancellationToken);
    }

    public async Task<PackageSessionLoadResult> LoadInstalledWithDevOverlaysAsync(
        IReadOnlyList<InstalledPackageRecord> packages,
        IReadOnlyList<string> devFolders,
        bool startBackgroundServices = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var errors = new List<string>();
        RuntimePackageSessionDirectories.CleanupStaleSessions();
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

        if (preparedCandidates.Count == 0 && sessionPackages.Count == 0)
        {
            TryDeleteDirectory(sessionFolder);
            return new PackageSessionLoadResult(ActivePackageSession.Empty, warnings, errors);
        }

        ThrowIfCancellationRequested(cancellationToken, sessionFolder);
        return await LoadPreparedPackagesAsync(sessionFolder, preparedCandidates, warnings, errors, sessionPackages, startBackgroundServices, cancellationToken);
    }

    private async Task<PackageSessionLoadResult> LoadPreparedPackagesAsync(
        string sessionFolder,
        IReadOnlyList<PreparedRuntimePackage> preparedCandidates,
        ICollection<string> warnings,
        ICollection<string> errors,
        IDictionary<string, SessionPackageDescriptor> initialSessionPackages,
        bool startBackgroundServices,
        CancellationToken cancellationToken)
    {
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
        var sessionPackages = new Dictionary<string, SessionPackageDescriptor>(initialSessionPackages, StringComparer.OrdinalIgnoreCase);
        var loadedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensionCatalog = new RuntimePackageExtensionCatalog();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var preparedPackage in orderedPackages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!preparedPackage.Dependencies.All(loadedPackageIds.Contains))
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

                var activation = await _activator.ActivateAsync(preparedPackage, sharedAssemblyRegistry, extensionCatalog, warnings, errors, startBackgroundServices, cancellationToken);
                if (!activation.Success)
                {
                    sessionPackages[preparedPackage.PackageId] = activation.SessionPackage;
                    continue;
                }

                loadedPackageIds.Add(preparedPackage.PackageId);
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
                backgroundServicesStarted: startBackgroundServices);
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
            backgroundServicesStarted: startBackgroundServices);
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
