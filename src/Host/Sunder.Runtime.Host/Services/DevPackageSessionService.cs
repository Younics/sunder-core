using Microsoft.Extensions.Logging;
using Sunder.Protocol;

namespace Sunder.Runtime.Host.Services;

internal sealed class DevPackageSessionService
{
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly ILogger<DevPackageSessionService> _logger;
    private readonly InstalledPackageStore _installedPackageStore;
    private readonly SunderPackageArchiveInstaller _packageArchiveInstaller;
    private readonly DevPackageConfigurationService _configurationService = new();
    private readonly PackageAuthSessionCoordinator _authCoordinator;
    private readonly DevPackageSessionState _sessionState;
    private bool _isDevPackageOverrideActive;

    public DevPackageSessionService(
        ILogger<DevPackageSessionService> logger,
        InstalledPackageStore installedPackageStore,
        SunderPackageArchiveInstaller packageArchiveInstaller)
    {
        _logger = logger;
        _installedPackageStore = installedPackageStore;
        _packageArchiveInstaller = packageArchiveInstaller;
        _authCoordinator = new PackageAuthSessionCoordinator(GetLoadedPackage, HandlePackageFault);
        _sessionState = new DevPackageSessionState(logger, _authCoordinator.Clear, _authCoordinator.RemovePackageSessions);
    }

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages()
        => _sessionState.GetActivePackages();

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages()
        => _sessionState.GetSessionPackages();

    public IReadOnlyList<PackageSourceDescriptor> GetActivePackageSources()
        => _sessionState.GetActivePackageSources();

    public async Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default)
        => (await _installedPackageStore.ListAsync(cancellationToken))
            .Select(_installedPackageStore.ToDescriptor)
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<string?> TryResolvePackageAssetPathAsync(
        string packageId,
        string assetPath,
        CancellationToken cancellationToken = default)
        => _sessionState.TryResolvePackageAssetPath(packageId, assetPath)
            ?? await _installedPackageStore.TryResolvePackageAssetPathAsync(packageId, assetPath, cancellationToken);

    public IReadOnlyList<PackageConfigurationSchemaDescriptor> GetConfigurationSchemas()
        => _configurationService.GetConfigurationSchemas(_sessionState.ListEnabledLoadedPackages());

    public bool ReportPackageFault(string packageId, ReportPackageFaultRequest request)
        => _sessionState.ReportPackageFault(packageId, request);

    public async Task<PackageConfigurationValuesResponse?> GetConfigurationValuesAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var loadedPackage = GetLoadedPackage(packageId);
        if (loadedPackage is null)
        {
            return null;
        }

        try
        {
            return await _configurationService.GetConfigurationValuesAsync(loadedPackage, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            HandlePackageFault(packageId, PackageFailureOrigin.RuntimeConfiguration, ex, "load package configuration");
            return null;
        }
    }

    public async Task<bool> SaveConfigurationValuesAsync(
        string packageId,
        UpdatePackageConfigurationValuesRequest request,
        CancellationToken cancellationToken = default)
    {
        var loadedPackage = GetLoadedPackage(packageId);
        if (loadedPackage?.ConfigurationSchema is null)
        {
            return false;
        }

        try
        {
            return await _configurationService.SaveConfigurationValuesAsync(loadedPackage, request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            HandlePackageFault(packageId, PackageFailureOrigin.RuntimeConfiguration, ex, "save package configuration");
            return false;
        }
    }

    public async Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => await _authCoordinator.GetPackageAuthStatusAsync(packageId, cancellationToken);

    public async Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(
        string packageId,
        PackageAuthCallbackServer packageAuthCallbackServer,
        CancellationToken cancellationToken = default)
        => await _authCoordinator.StartPackageAuthAsync(packageId, packageAuthCallbackServer, cancellationToken);

    public PackageAuthSessionStatusResponse? GetPackageAuthSessionStatus(string packageId, string authSessionId)
        => _authCoordinator.GetPackageAuthSessionStatus(packageId, authSessionId);

    public async Task<bool> CompletePackageAuthSessionAsync(
        string authSessionId,
        IReadOnlyDictionary<string, string?> queryValues,
        CancellationToken cancellationToken = default)
        => await _authCoordinator.CompletePackageAuthSessionAsync(authSessionId, queryValues, cancellationToken);

    public async Task<PackageAuthStatusResponse?> DisconnectPackageAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => await _authCoordinator.DisconnectPackageAsync(packageId, cancellationToken);

    public async Task<DevPackageLoadResult> LoadAsync(DevPackageLoadRequest request)
    {
        await _reloadGate.WaitAsync();
        try
        {
            var warnings = new List<string>();
            var errors = new List<string>();

            var folders = (request.Folders ?? [])
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (folders.Count == 0)
            {
                _isDevPackageOverrideActive = false;
                warnings.AddRange(await _sessionState.ClearActiveSessionAsync());
                return await LoadInstalledPackagesCoreAsync(warnings, errors);
            }

            _isDevPackageOverrideActive = true;
            warnings.AddRange(await _sessionState.ClearActiveSessionAsync());

            var loadResult = await new DevPackageLoadService(_logger).LoadAsync(folders);
            warnings.AddRange(loadResult.Warnings);
            errors.AddRange(loadResult.Errors);
            if (loadResult.Session is null)
            {
                return new DevPackageLoadResult([], warnings, errors);
            }

            _sessionState.PublishSession(loadResult.Session);

            return new DevPackageLoadResult(loadResult.Session.GetActivePackages(), warnings, errors);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task<DevPackageLoadResult> LoadInstalledPackagesAsync()
    {
        await _reloadGate.WaitAsync();
        try
        {
            _isDevPackageOverrideActive = false;
            var warnings = new List<string>();
            var errors = new List<string>();
            warnings.AddRange(await _sessionState.ClearActiveSessionAsync());
            return await LoadInstalledPackagesCoreAsync(warnings, errors);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var result = await _packageArchiveInstaller.InstallFromPathAsync(packagePath, cancellationToken);
        return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
    }

    public async Task<PackageOperationResult> UpgradePackageFromPathAsync(
        string packageId,
        PackageUpgradeFromPathRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _packageArchiveInstaller.UpgradeFromPathAsync(
            packageId,
            request.PackagePath,
            request.AllowDowngrade,
            request.Reinstall,
            cancellationToken);
        return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
    }

    public async Task<PackageOperationResult> SetInstalledPackageEnabledAsync(string packageId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var result = await _installedPackageStore.SetEnabledAsync(packageId, isEnabled, cancellationToken);
        if (result.Success && !isEnabled && !_isDevPackageOverrideActive)
        {
            _sessionState.DisableInstalledPackage(packageId);
            return result with { RequiresAppRestart = false };
        }

        return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
    }

    public async Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var result = await _installedPackageStore.UninstallAsync(packageId, cancellationToken);
        if (result.Success && !_isDevPackageOverrideActive)
        {
            _sessionState.RemovePackage(packageId);
            return result with { RequiresAppRestart = false };
        }

        return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
    }

    private async Task<DevPackageLoadResult> LoadInstalledPackagesCoreAsync(List<string> warnings, List<string> errors)
    {
        var installedPackages = await _installedPackageStore.ListAsync();
        if (installedPackages.Count == 0)
        {
            return new DevPackageLoadResult([], warnings, errors);
        }

        var loadResult = await new DevPackageLoadService(_logger).LoadInstalledAsync(installedPackages);
        warnings.AddRange(loadResult.Warnings);
        errors.AddRange(loadResult.Errors);
        if (loadResult.Session is null)
        {
            return new DevPackageLoadResult([], warnings, errors);
        }

        _sessionState.PublishSession(loadResult.Session);
        return new DevPackageLoadResult(loadResult.Session.GetActivePackages(), warnings, errors);
    }

    private async Task<PackageOperationResult> ReloadInstalledPackagesAfterMutationAsync(PackageOperationResult result, CancellationToken cancellationToken)
    {
        if (!result.Success)
        {
            return result;
        }

        if (_isDevPackageOverrideActive)
        {
            return result with
            {
                RequiresAppRestart = true,
                Warnings = result.Warnings.Concat(["Installed package changes are saved, but dev-package override mode is active for this runtime session."]).ToArray(),
            };
        }

        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            var warnings = result.Warnings.ToList();
            var errors = result.Errors.ToList();
            warnings.AddRange(await _sessionState.ClearActiveSessionAsync());
            var loadResult = await LoadInstalledPackagesCoreAsync(warnings, errors);
            if (loadResult.Errors.Count > 0)
            {
                return result with
                {
                    RequiresAppRestart = true,
                    Warnings = loadResult.Warnings,
                    Errors = loadResult.Errors,
                };
            }

            return result with { RequiresAppRestart = true, Warnings = loadResult.Warnings };
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private void HandlePackageFault(string packageId, PackageFailureOrigin origin, Exception exception, string action)
        => _sessionState.HandlePackageFault(packageId, origin, exception, action);

    private ActiveLoadedDevPackage? GetLoadedPackage(string packageId)
        => _sessionState.GetLoadedPackage(packageId);

}
