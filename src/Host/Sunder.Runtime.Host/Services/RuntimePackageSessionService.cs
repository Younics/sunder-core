using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Protocol;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageSessionService
{
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly ILogger<RuntimePackageSessionService> _logger;
    private readonly InstalledPackageStore _installedPackageStore;
    private readonly SunderPackageArchiveInstaller _packageArchiveInstaller;
    private readonly PackageConfigurationService _configurationService = new();
    private readonly PackageAuthSessionCoordinator _authCoordinator;
    private readonly PackageSessionState _sessionState;
    private readonly PackageSessionSourceState _sourceState = new();
    private readonly PackageSessionReconciler _reconciler;
    private readonly Dictionary<string, PendingDevPackageStage> _pendingDevStages = new(StringComparer.OrdinalIgnoreCase);

    public RuntimePackageSessionService(
        ILogger<RuntimePackageSessionService> logger,
        InstalledPackageStore installedPackageStore,
        SunderPackageArchiveInstaller packageArchiveInstaller)
    {
        _logger = logger;
        _installedPackageStore = installedPackageStore;
        _packageArchiveInstaller = packageArchiveInstaller;
        _authCoordinator = new PackageAuthSessionCoordinator(GetLoadedPackage, HandlePackageFault);
        _sessionState = new PackageSessionState(logger, _authCoordinator.Clear, _authCoordinator.RemovePackageSessions);
        _reconciler = new PackageSessionReconciler(logger, installedPackageStore);
    }

    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages()
        => _sessionState.GetActivePackages();

    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages()
        => _sessionState.GetSessionPackages();

    public IReadOnlyList<PackageSourceDescriptor> GetActivePackageSources()
        => _sessionState.GetActivePackageSources();

    public async Task<PackageSessionStatus?> GetPackageSessionStatusAsync(
        string packageId,
        CancellationToken cancellationToken = default)
        => await BuildPackageSessionStatusAsync(packageId, cancellationToken);

    public async Task<PackageSessionOperationResult> LoadPackageSessionAsync(
        PackageSessionLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Source))
        {
            return PackageSessionOperationResult.Failed("A package id or dev package folder is required.");
        }

        return request.SourceKind switch
        {
            PackageSourceKind.Installed => await LoadInstalledPackageIntoSessionAsync(request, cancellationToken),
            PackageSourceKind.Dev => await LoadDevPackageOverlayAsync(request, cancellationToken),
            _ => PackageSessionOperationResult.Failed($"Unsupported package session source kind '{request.SourceKind}'."),
        };
    }

    public async Task<PackageSessionOperationResult> UnloadPackageSessionAsync(
        string packageId,
        PackageSourceKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return PackageSessionOperationResult.Failed("A package id is required.");
        }

        return sourceKind switch
        {
            PackageSourceKind.Installed => await UnloadInstalledPackageFromSessionAsync(packageId, cancellationToken),
            PackageSourceKind.Dev => await UnloadDevPackageOverlayAsync(packageId, cancellationToken),
            _ => PackageSessionOperationResult.Failed($"Unsupported package session source kind '{sourceKind}'."),
        };
    }

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

            var folders = NormalizeDevPackageFolders(request);

            var sources = _sourceState.Snapshot();
            sources.RemoveDevOverlaysOwnedBy(PackageSessionOverlayOwner.Startup, PackageSessionOverlayOwner.HotReload);
            foreach (var folder in folders)
            {
                AddDevPackageOverlay(sources, folder, watch: false, PackageSessionOverlayOwner.Startup, errors);
            }

            if (errors.Count > 0)
            {
                return new DevPackageLoadResult(_sessionState.GetActivePackages(), warnings, errors);
            }

            warnings.AddRange(await _sessionState.ClearActiveSessionAsync());

            var loadResult = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, startBackgroundServices: true);
            warnings.AddRange(loadResult.Warnings);
            errors.AddRange(loadResult.Errors);
            if (loadResult.Session is null)
            {
                return new DevPackageLoadResult([], warnings, errors);
            }

            _sourceState.Replace(sources);
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
            _sourceState.ClearDevOverlays();
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

    public async Task<DevPackageStageResult> StageDevPackagesAsync(DevPackageLoadRequest request)
    {
        await _reloadGate.WaitAsync();
        try
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            var folders = NormalizeDevPackageFolders(request);
            if (folders.Count == 0)
            {
                errors.Add("At least one dev package folder is required for staging.");
                return new DevPackageStageResult(null, [], [], warnings, errors);
            }

            var sources = _sourceState.Snapshot();
            sources.RemoveDevOverlaysOwnedBy(PackageSessionOverlayOwner.Startup, PackageSessionOverlayOwner.HotReload);
            foreach (var folder in folders)
            {
                AddDevPackageOverlay(sources, folder, watch: false, PackageSessionOverlayOwner.HotReload, errors);
            }

            if (errors.Count > 0)
            {
                return new DevPackageStageResult(null, [], [], warnings, errors);
            }

            var loadResult = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, startBackgroundServices: false);
            warnings.AddRange(loadResult.Warnings);
            errors.AddRange(loadResult.Errors);
            if (loadResult.Session is null || errors.Count > 0)
            {
                if (loadResult.Session is not null)
                {
                    await loadResult.Session.DisposeAsync();
                }

                return new DevPackageStageResult(null, [], [], warnings, errors);
            }

            var stageId = Guid.NewGuid().ToString("N");
            var stage = new PendingDevPackageStage(stageId, loadResult.Session, sources);
            _pendingDevStages[stageId] = stage;
            return new DevPackageStageResult(
                stageId,
                loadResult.Session.GetActivePackages(),
                loadResult.Session.GetActivePackageSources(),
                warnings,
                errors);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task<DevPackageLoadResult> CommitDevPackageStageAsync(string stageId, CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            if (!_pendingDevStages.Remove(stageId, out var stage))
            {
                return new DevPackageLoadResult([], [], [$"Dev package stage '{stageId}' was not found."]);
            }

            var warnings = new List<string>();
            var errors = new List<string>();
            try
            {
                await stage.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
                warnings.AddRange(await _sessionState.ClearActiveSessionAsync());
                _sourceState.Replace(stage.Sources);
                _sessionState.PublishSession(stage.Session);
                return new DevPackageLoadResult(stage.Session.GetActivePackages(), warnings, errors);
            }
            catch (Exception ex)
            {
                await stage.Session.DisposeAsync();
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                errors.Add($"Failed to commit dev package stage '{stageId}': {ex.Message}");
                _logger.LogError(ex, "Failed to commit dev package stage {StageId}", stageId);
                return new DevPackageLoadResult(_sessionState.GetActivePackages(), warnings, errors);
            }
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task<bool> DiscardDevPackageStageAsync(string stageId)
    {
        await _reloadGate.WaitAsync();
        try
        {
            if (!_pendingDevStages.Remove(stageId, out var stage))
            {
                return false;
            }

            await stage.Session.DisposeAsync();
            return true;
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
        if (result.Success && !isEnabled)
        {
            if (_sourceState.HasDevOverlays())
            {
                return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
            }

            _sessionState.DisableInstalledPackage(packageId);
            return result with { RequiresAppRestart = false };
        }

        return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
    }

    public async Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var result = await _installedPackageStore.UninstallAsync(packageId, cancellationToken);
        if (result.Success)
        {
            if (_sourceState.HasDevOverlays())
            {
                return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
            }

            IReadOnlyList<string> removedPackageIds = result.ImpactedPackageIds.Count > 0 ? result.ImpactedPackageIds : [packageId];
            foreach (var removedPackageId in removedPackageIds)
            {
                _sessionState.RemovePackage(removedPackageId);
            }

            return result with { RequiresAppRestart = false };
        }

        return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
    }

    private async Task<DevPackageLoadResult> LoadInstalledPackagesCoreAsync(List<string> warnings, List<string> errors)
    {
        var loadResult = await _reconciler.LoadMergedSessionAsync(_sourceState.Snapshot().ActiveDevOverlays, startBackgroundServices: true);
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

        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            var warnings = result.Warnings.ToList();
            var errors = result.Errors.ToList();
            var loadResult = await _reconciler.LoadMergedSessionAsync(_sourceState.Snapshot().ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
            warnings.AddRange(loadResult.Warnings);
            errors.AddRange(loadResult.Errors);
            if (loadResult.Session is null || errors.Count > 0)
            {
                if (loadResult.Session is not null)
                {
                    await loadResult.Session.DisposeAsync();
                }

                warnings.AddRange(errors.Select(error => $"Installed package changes are saved, but the running package session kept the previous loaded packages: {error}"));
                return result with
                {
                    RequiresAppRestart = true,
                    Warnings = warnings,
                };
            }

            try
            {
                await loadResult.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await loadResult.Session.DisposeAsync();
                warnings.Add($"Installed package changes are saved, but the running package session kept the previous loaded packages: {ex.Message}");
                return result with
                {
                    RequiresAppRestart = true,
                    Warnings = warnings,
                };
            }

            warnings.AddRange(await _sessionState.ClearActiveSessionAsync());
            _sessionState.PublishSession(loadResult.Session);

            return result with { RequiresAppRestart = true, Warnings = warnings };
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private void HandlePackageFault(string packageId, PackageFailureOrigin origin, Exception exception, string action)
        => _sessionState.HandlePackageFault(packageId, origin, exception, action);

    private async Task<PackageSessionOperationResult> LoadInstalledPackageIntoSessionAsync(
        PackageSessionLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Watch)
        {
            return PackageSessionOperationResult.Failed("Watch is only supported for dev package session sources.");
        }

        var packageId = request.Source.Trim();
        var operationResult = await SetInstalledPackageEnabledAsync(packageId, isEnabled: true, cancellationToken);
        return await ToPackageSessionOperationResultAsync(operationResult, packageId, cancellationToken);
    }

    private async Task<PackageSessionOperationResult> UnloadInstalledPackageFromSessionAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        var operationResult = await SetInstalledPackageEnabledAsync(packageId, isEnabled: false, cancellationToken);
        return await ToPackageSessionOperationResultAsync(operationResult, packageId, cancellationToken);
    }

    private async Task<PackageSessionOperationResult> LoadDevPackageOverlayAsync(
        PackageSessionLoadRequest request,
        CancellationToken cancellationToken)
    {
        var folder = Path.GetFullPath(request.Source.Trim());
        if (!TryReadDevPackageId(folder, out var packageId, out var errorMessage))
        {
            return PackageSessionOperationResult.Failed(errorMessage ?? $"'{folder}' is not a loadable Sunder dev package folder.");
        }

        var sources = _sourceState.Snapshot();
        sources.SetDevOverlay(new PackageSessionDevOverlay(packageId, folder, request.Watch, PackageSessionOverlayOwner.Sdk));
        return await CommitMergedPackageSessionAsync(
            sources,
            $"Loaded dev package '{packageId}'.",
            [packageId],
            packageId,
            cancellationToken);
    }

    private async Task<PackageSessionOperationResult> UnloadDevPackageOverlayAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        var sources = _sourceState.Snapshot();
        if (!sources.RemoveDevOverlay(packageId, PackageSessionOverlayOwner.Sdk))
        {
            return PackageSessionOperationResult.Failed($"Dev package overlay '{packageId}' is not loaded.");
        }

        return await CommitMergedPackageSessionAsync(
            sources,
            $"Unloaded dev package '{packageId}'.",
            [packageId],
            packageId,
            cancellationToken);
    }

    private async Task<PackageSessionOperationResult> CommitMergedPackageSessionAsync(
        PackageSessionSourceSnapshot sources,
        string successMessage,
        IReadOnlyList<string> impactedPackageIds,
        string statusPackageId,
        CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            var loadResult = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
            warnings.AddRange(loadResult.Warnings);
            errors.AddRange(loadResult.Errors);
            if (loadResult.Session is null || errors.Count > 0)
            {
                if (loadResult.Session is not null)
                {
                    await loadResult.Session.DisposeAsync();
                }

                return new PackageSessionOperationResult(false, errors.FirstOrDefault() ?? "Package session load failed.", warnings, errors, impactedPackageIds, null);
            }

            try
            {
                await loadResult.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await loadResult.Session.DisposeAsync();
                var message = $"Package session load failed while starting background services: {ex.Message}";
                _logger.LogError(ex, "Failed to start background services for merged package session");
                return new PackageSessionOperationResult(false, message, warnings, [message], impactedPackageIds, null);
            }

            warnings.AddRange(await _sessionState.ClearActiveSessionAsync());
            _sourceState.Replace(sources);
            _sessionState.PublishSession(loadResult.Session);

            return new PackageSessionOperationResult(
                true,
                successMessage,
                warnings,
                [],
                impactedPackageIds,
                await BuildPackageSessionStatusAsync(statusPackageId, cancellationToken));
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<PackageSessionOperationResult> ToPackageSessionOperationResultAsync(
        PackageOperationResult result,
        string packageId,
        CancellationToken cancellationToken)
        => new(
            result.Success,
            result.Message,
            result.Warnings,
            result.Errors,
            result.ImpactedPackageIds.Count == 0 ? [packageId] : result.ImpactedPackageIds,
            await BuildPackageSessionStatusAsync(packageId, cancellationToken));

    private async Task<PackageSessionStatus?> BuildPackageSessionStatusAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        var overlay = _sourceState.TryGetActiveDevOverlay(packageId);
        var installedPackage = await _installedPackageStore.GetAsync(packageId, cancellationToken);
        var sessionPackage = _sessionState.GetSessionPackage(packageId);
        if (overlay is null && installedPackage is null && sessionPackage is null)
        {
            return null;
        }

        var activeSourceKind = overlay is not null ? PackageSourceKind.Dev : PackageSourceKind.Installed;
        return new PackageSessionStatus(
            packageId,
            sessionPackage?.DisplayName ?? installedPackage?.Name,
            sessionPackage?.Version ?? installedPackage?.Version,
            activeSourceKind,
            sessionPackage?.IsEnabled == true,
            overlay?.Watch ?? false,
            overlay is not null && installedPackage is not null,
            sessionPackage?.Readiness,
            sessionPackage?.LastError);
    }

    private static void AddDevPackageOverlay(
        PackageSessionSourceSnapshot sources,
        string folder,
        bool watch,
        PackageSessionOverlayOwner owner,
        ICollection<string> errors)
    {
        if (!TryReadDevPackageId(folder, out var packageId, out var errorMessage))
        {
            errors.Add(errorMessage ?? $"'{folder}' is not a loadable Sunder dev package folder.");
            return;
        }

        sources.SetDevOverlay(new PackageSessionDevOverlay(packageId, Path.GetFullPath(folder), watch, owner));
    }

    private static bool TryReadDevPackageId(string folder, out string packageId, out string? errorMessage)
    {
        packageId = string.Empty;
        errorMessage = null;
        if (!Directory.Exists(folder))
        {
            errorMessage = $"Dev package folder '{folder}' does not exist.";
            return false;
        }

        var manifestPath = Path.Combine(folder, "sunder-package.json");
        if (!File.Exists(manifestPath))
        {
            errorMessage = $"Dev package folder '{folder}' does not contain sunder-package.json.";
            return false;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<DevPackageManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(manifest?.Id))
            {
                errorMessage = $"Dev package manifest '{manifestPath}' is missing id.";
                return false;
            }

            packageId = manifest.Id.Trim();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to parse dev package manifest '{manifestPath}': {ex.Message}";
            return false;
        }
    }

    private ActiveLoadedPackage? GetLoadedPackage(string packageId)
        => _sessionState.GetLoadedPackage(packageId);

    private static List<string> NormalizeDevPackageFolders(DevPackageLoadRequest request)
        => (request.Folders ?? [])
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed record PendingDevPackageStage(
        string StageId,
        ActivePackageSession Session,
        PackageSessionSourceSnapshot Sources);

}
