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
    private readonly Dictionary<string, PendingPackageLifecycleStage> _pendingLifecycleStages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingPackageStoreStage> _pendingPackageStoreStages = new(StringComparer.OrdinalIgnoreCase);
    private long _sessionGeneration;

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

    public async Task<PackageLifecycleOperationResult> LoadPackageLifecycleAsync(
        PackageLifecycleLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            var currentActivePackages = _sessionState.GetActivePackages();
            var currentPackageSources = _sessionState.GetActivePackageSources();
            var sources = _sourceState.Snapshot();
            var owner = ToSessionOverlayOwner(request.OverlayOwner);
            if (owner is PackageSessionOverlayOwner.Startup or PackageSessionOverlayOwner.HotReload)
            {
                ReplaceLiveReloadOverlays(sources);
            }

            var forceReloadDevFolders = AddLifecyclePackageSources(sources, request.Packages, owner, errors);
            if (errors.Count > 0)
            {
                return PackageLifecycleOperationResult.Failed(
                    errors[0],
                    currentActivePackages,
                    currentPackageSources,
                    warnings,
                    errors);
            }

            var loadResult = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
            warnings.AddRange(loadResult.Warnings);
            errors.AddRange(loadResult.Errors);
            if (loadResult.Session is null || errors.Count > 0)
            {
                if (loadResult.Session is not null)
                {
                    await loadResult.Session.DisposeAsync();
                }

                return PackageLifecycleOperationResult.Failed(
                    errors.FirstOrDefault() ?? "Package lifecycle load failed.",
                    currentActivePackages,
                    currentPackageSources,
                    warnings,
                    errors);
            }

            var stagedActivePackages = loadResult.Session.GetActivePackages();
            var stagedPackageSources = loadResult.Session.GetActivePackageSources();
            var impactedPackageIds = BuildImpactedPackageIds(
                currentActivePackages,
                currentPackageSources,
                stagedActivePackages,
                stagedPackageSources,
                forceReloadDevFolders);
            try
            {
                await loadResult.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await loadResult.Session.DisposeAsync();
                var message = $"Package lifecycle load failed while starting background services: {ex.Message}";
                _logger.LogError(ex, "Failed to start background services for package lifecycle load");
                return PackageLifecycleOperationResult.Failed(
                    message,
                    currentActivePackages,
                    currentPackageSources,
                    warnings,
                    [message],
                    impactedPackageIds);
            }

            warnings.AddRange(await _sessionState.ClearActiveSessionAsync());
            _sourceState.Replace(sources);
            _sessionState.PublishSession(loadResult.Session);
            _sessionGeneration++;
            return new PackageLifecycleOperationResult(
                true,
                "Package lifecycle loaded.",
                stagedActivePackages,
                stagedPackageSources,
                warnings,
                [],
                impactedPackageIds);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task<PackageLifecycleStageResult> StagePackageLifecycleAsync(
        PackageLifecycleStageRequest request,
        CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            var currentActivePackages = _sessionState.GetActivePackages();
            var currentPackageSources = _sessionState.GetActivePackageSources();
            var sources = _sourceState.Snapshot();
            var owner = ToSessionOverlayOwner(request.OverlayOwner);
            if (owner is PackageSessionOverlayOwner.Startup or PackageSessionOverlayOwner.HotReload)
            {
                ReplaceLiveReloadOverlays(sources);
            }

            var forceReloadDevFolders = AddLifecyclePackageSources(sources, request.Packages, owner, errors);
            if (errors.Count > 0)
            {
                return PackageLifecycleStageResult.Failed(errors[0], currentActivePackages, currentPackageSources, warnings, errors);
            }

            var loadResult = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
            warnings.AddRange(loadResult.Warnings);
            errors.AddRange(loadResult.Errors);
            if (loadResult.Session is null || errors.Count > 0)
            {
                if (loadResult.Session is not null)
                {
                    await loadResult.Session.DisposeAsync();
                }

                return PackageLifecycleStageResult.Failed(
                    errors.FirstOrDefault() ?? "Package lifecycle stage failed.",
                    currentActivePackages,
                    currentPackageSources,
                    warnings,
                    errors);
            }

            var stagedActivePackages = loadResult.Session.GetActivePackages();
            var stagedPackageSources = loadResult.Session.GetActivePackageSources();
            var impactedPackageIds = BuildImpactedPackageIds(
                currentActivePackages,
                currentPackageSources,
                stagedActivePackages,
                stagedPackageSources,
                forceReloadDevFolders);
            var stageId = Guid.NewGuid().ToString("N");
            var stage = new PendingPackageLifecycleStage(stageId, loadResult.Session, sources, impactedPackageIds, _sessionGeneration);
            _pendingLifecycleStages[stageId] = stage;
            return new PackageLifecycleStageResult(
                stageId,
                stagedActivePackages,
                stagedPackageSources,
                warnings,
                errors,
                impactedPackageIds);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task<PackageLifecycleOperationResult> CommitPackageLifecycleStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            if (!_pendingLifecycleStages.Remove(stageId, out var stage))
            {
                return PackageLifecycleOperationResult.Failed(
                    $"Package lifecycle stage '{stageId}' was not found.",
                    _sessionState.GetActivePackages(),
                    _sessionState.GetActivePackageSources());
            }

            var warnings = new List<string>();
            var errors = new List<string>();
            if (stage.BaseSessionGeneration != _sessionGeneration)
            {
                await stage.Session.DisposeAsync();
                var message = $"Package lifecycle stage '{stageId}' is stale because the active package session changed before commit.";
                return PackageLifecycleOperationResult.Failed(
                    message,
                    _sessionState.GetActivePackages(),
                    _sessionState.GetActivePackageSources(),
                    warnings,
                    [message],
                    stage.ImpactedPackageIds);
            }

            try
            {
                await stage.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
                var activePackages = stage.Session.GetActivePackages();
                var packageSources = stage.Session.GetActivePackageSources();
                warnings.AddRange(await _sessionState.ClearActiveSessionAsync());
                _sourceState.Replace(stage.Sources);
                _sessionState.PublishSession(stage.Session);
                _sessionGeneration++;
                return new PackageLifecycleOperationResult(
                    true,
                    "Package lifecycle stage committed.",
                    activePackages,
                    packageSources,
                    warnings,
                    errors,
                    stage.ImpactedPackageIds);
            }
            catch (Exception ex)
            {
                await stage.Session.DisposeAsync();
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                errors.Add($"Failed to commit package lifecycle stage '{stageId}': {ex.Message}");
                _logger.LogError(ex, "Failed to commit package lifecycle stage {StageId}", stageId);
                return PackageLifecycleOperationResult.Failed(
                    errors[0],
                    _sessionState.GetActivePackages(),
                    _sessionState.GetActivePackageSources(),
                    warnings,
                    errors,
                    stage.ImpactedPackageIds);
            }
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task<bool> DiscardPackageLifecycleStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            if (!_pendingLifecycleStages.Remove(stageId, out var stage))
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

    public async Task<PackageStoreStageResult> StagePackageStoreChangesAsync(
        PackageStoreStageRequest request,
        CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            if (request.Mutations.Count == 0)
            {
                return PackageStoreStageResult.Failed(
                    "At least one package store mutation is required.",
                    _sessionState.GetActivePackages(),
                    _sessionState.GetActivePackageSources());
            }

            var warnings = new List<string>();
            var currentActivePackages = _sessionState.GetActivePackages();
            var currentPackageSources = _sessionState.GetActivePackageSources();
            var stagedPackages = (await _installedPackageStore.SnapshotAsync(cancellationToken)).ToList();
            var committedPackages = stagedPackages.ToList();
            var fileActions = new List<PendingPackageStoreFileAction>();
            var impactedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var operationMessages = new List<string>();

            foreach (var mutation in request.Mutations)
            {
                var mutationPlan = await ApplyPackageStoreMutationStageAsync(
                    mutation,
                    stagedPackages,
                    committedPackages,
                    fileActions,
                    cancellationToken);
                if (!mutationPlan.Result.Success)
                {
                    CleanupPackageStoreFileActions(fileActions);
                    return PackageStoreStageResult.Failed(
                        mutationPlan.Result.Errors.FirstOrDefault() ?? mutationPlan.Result.Message ?? "Package store stage failed.",
                        currentActivePackages,
                        currentPackageSources,
                        warnings.Concat(mutationPlan.Result.Warnings).ToArray(),
                        mutationPlan.Result.Errors.Count == 0 ? null : mutationPlan.Result.Errors,
                        impactedPackageIds.ToArray());
                }

                warnings.AddRange(mutationPlan.Result.Warnings);
                if (!string.IsNullOrWhiteSpace(mutationPlan.Result.Message))
                {
                    operationMessages.Add(mutationPlan.Result.Message!);
                }

                foreach (var impactedPackageId in mutationPlan.Result.ImpactedPackageIds)
                {
                    impactedPackageIds.Add(impactedPackageId);
                }
            }

            var sources = _sourceState.Snapshot();
            var loadResult = await _reconciler.LoadMergedSessionAsync(stagedPackages, sources.ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
            warnings.AddRange(loadResult.Warnings);
            if (loadResult.Session is null)
            {
                CleanupPackageStoreFileActions(fileActions);
                return PackageStoreStageResult.Failed(
                    loadResult.Errors.FirstOrDefault() ?? "Package store stage failed while loading the prospective package session.",
                    currentActivePackages,
                    currentPackageSources,
                    warnings,
                    loadResult.Errors,
                    impactedPackageIds.ToArray());
            }

            warnings.AddRange(loadResult.Errors.Select(error => $"Staged installed package session loaded with package errors: {error}"));
            var stagedActivePackages = loadResult.Session.GetActivePackages();
            var stagedPackageSources = loadResult.Session.GetActivePackageSources();
            foreach (var impactedPackageId in BuildImpactedPackageIds(
                         currentActivePackages,
                         currentPackageSources,
                         stagedActivePackages,
                         stagedPackageSources,
                         []))
            {
                impactedPackageIds.Add(impactedPackageId);
            }

            var result = PackageOperationResults.Success(
                BuildPackageStoreStageMessage(operationMessages, impactedPackageIds.Count),
                warnings: warnings,
                impactedPackageIds: impactedPackageIds.ToArray());
            var stageId = Guid.NewGuid().ToString("N");
            _pendingPackageStoreStages[stageId] = new PendingPackageStoreStage(
                stageId,
                loadResult.Session,
                committedPackages.ToArray(),
                fileActions.ToArray(),
                result,
                _sessionGeneration);
            return new PackageStoreStageResult(stageId, result, stagedActivePackages, stagedPackageSources);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task<PackageOperationResult> CommitPackageStoreStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            if (!_pendingPackageStoreStages.Remove(stageId, out var stage))
            {
                return PackageOperationResults.Failure($"Package store stage '{stageId}' was not found.");
            }

            try
            {
                await stage.Session.DisposeAsync();
                if (stage.BaseSessionGeneration != _sessionGeneration)
                {
                    CleanupPackageStoreFileActions(stage.FileActions);
                    return PackageOperationResults.Failure($"Package store stage '{stageId}' is stale because the active package session changed before commit.");
                }

                var (fileCommitError, fileCommits) = CommitPackageStoreFileAdds(stage.FileActions);
                if (fileCommitError is not null)
                {
                    CleanupPackageStoreFileActions(stage.FileActions);
                    return PackageOperationResults.Failure(fileCommitError);
                }

                try
                {
                    await _installedPackageStore.CommitSnapshotAsync(stage.CommittedPackages, cancellationToken);
                }
                catch
                {
                    RollBackPackageStoreFileAdds(fileCommits);
                    throw;
                }

                CompletePackageStoreFileAdds(fileCommits);
                DeletePackageStoreRemovedFiles(stage.FileActions);
                if (stage.Result.ImpactedPackageIds.Count == 0)
                {
                    return stage.Result with
                    {
                        RuntimeSessionApplied = true,
                        RequiresAppRestart = false,
                    };
                }

                return await ReloadInstalledPackagesAfterMutationCoreAsync(stage.Result, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CleanupPackageStoreFileActions(stage.FileActions);
                throw;
            }
            catch (Exception ex)
            {
                CleanupPackageStoreFileActions(stage.FileActions);
                return PackageOperationResults.Failure($"Failed to commit package store stage '{stageId}': {ex.Message}");
            }
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task<bool> DiscardPackageStoreStageAsync(
        string stageId,
        CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            if (!_pendingPackageStoreStages.Remove(stageId, out var stage))
            {
                return false;
            }

            await stage.Session.DisposeAsync();
            CleanupPackageStoreFileActions(stage.FileActions);
            return true;
        }
        finally
        {
            _reloadGate.Release();
        }
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

    public async Task<PackageLifecycleOperationResult> LoadInstalledPackagesAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            var warnings = new List<string>();
            var errors = new List<string>();
            var currentActivePackages = _sessionState.GetActivePackages();
            var currentPackageSources = _sessionState.GetActivePackageSources();
            var sources = _sourceState.Snapshot();
            sources.RemoveDevOverlaysOwnedBy(
                PackageSessionOverlayOwner.Startup,
                PackageSessionOverlayOwner.HotReload,
                PackageSessionOverlayOwner.Sdk);

            var loadResult = await _reconciler.LoadMergedSessionAsync(sources.ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
            warnings.AddRange(loadResult.Warnings);
            errors.AddRange(loadResult.Errors);
            if (loadResult.Session is null)
            {
                return PackageLifecycleOperationResult.Failed(
                    errors.FirstOrDefault() ?? "Installed package lifecycle load failed.",
                    currentActivePackages,
                    currentPackageSources,
                    warnings,
                    errors);
            }

            warnings.AddRange(errors.Select(error => $"Installed package session loaded with package errors: {error}"));

            var activePackages = loadResult.Session.GetActivePackages();
            var packageSources = loadResult.Session.GetActivePackageSources();
            var impactedPackageIds = BuildImpactedPackageIds(
                currentActivePackages,
                currentPackageSources,
                activePackages,
                packageSources,
                []);
            try
            {
                await loadResult.Session.StartBackgroundServicesAsync(_logger, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await loadResult.Session.DisposeAsync();
                var message = $"Installed package lifecycle load failed while starting background services: {ex.Message}";
                _logger.LogError(ex, "Failed to start background services for installed package lifecycle load");
                return PackageLifecycleOperationResult.Failed(
                    message,
                    currentActivePackages,
                    currentPackageSources,
                    warnings,
                    [message],
                    impactedPackageIds);
            }

            warnings.AddRange(await _sessionState.ClearActiveSessionAsync());
            _sourceState.Replace(sources);
            _sessionState.PublishSession(loadResult.Session);
            _sessionGeneration++;
            return new PackageLifecycleOperationResult(
                true,
                "Installed packages loaded.",
                activePackages,
                packageSources,
                warnings,
                [],
                impactedPackageIds);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async Task<PackageOperationResult> InstallPackageFromPathAsync(
        string packagePath,
        bool applyRuntimeSession = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _packageArchiveInstaller.InstallFromPathAsync(packagePath, cancellationToken);
        return applyRuntimeSession
            ? await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken)
            : DeferInstalledPackageSessionReload(result);
    }

    public async Task<PackageOperationResult> ReloadInstalledPackageSessionAsync(
        InstalledPackageSessionReloadRequest request,
        CancellationToken cancellationToken = default)
    {
        var impactedPackageIds = request.ImpactedPackageIds
            .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = PackageOperationResults.Success(
            "Installed package session reloaded.",
            impactedPackageIds: impactedPackageIds);
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
        return request.ApplyRuntimeSession
            ? await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken)
            : DeferInstalledPackageSessionReload(result);
    }

    public async Task<PackageOperationResult> SetInstalledPackageEnabledAsync(string packageId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        var result = await _installedPackageStore.SetEnabledAsync(packageId, isEnabled, cancellationToken);
        return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
    }

    public async Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var result = await _installedPackageStore.UninstallAsync(packageId, cancellationToken);
        return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
    }

    private async Task<PackageOperationResult> ReloadInstalledPackagesAfterMutationAsync(PackageOperationResult result, CancellationToken cancellationToken)
    {
        if (!result.Success)
        {
            return result;
        }

        if (result.ImpactedPackageIds.Count == 0)
        {
            return result with
            {
                RuntimeSessionApplied = true,
                RequiresAppRestart = false,
            };
        }

        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            return await ReloadInstalledPackagesAfterMutationCoreAsync(result, cancellationToken);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<PackageOperationResult> ReloadInstalledPackagesAfterMutationCoreAsync(PackageOperationResult result, CancellationToken cancellationToken)
    {
        var warnings = result.Warnings.ToList();
        var errors = result.Errors.ToList();
        var loadResult = await _reconciler.LoadMergedSessionAsync(_sourceState.Snapshot().ActiveDevOverlays, startBackgroundServices: false, cancellationToken);
        warnings.AddRange(loadResult.Warnings);
        errors.AddRange(loadResult.Errors);
        if (loadResult.Session is null)
        {
            warnings.AddRange(errors.Select(error => $"Installed package changes are saved, but the running package session kept the previous loaded packages: {error}"));
            return result with
            {
                RuntimeSessionApplied = false,
                RequiresAppRestart = false,
                Warnings = warnings,
            };
        }

        warnings.AddRange(errors.Select(error => $"Installed package session loaded with package errors: {error}"));

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
                RuntimeSessionApplied = false,
                RequiresAppRestart = false,
                Warnings = warnings,
            };
        }

        warnings.AddRange(await _sessionState.ClearActiveSessionAsync());
        _sessionState.PublishSession(loadResult.Session);
        _sessionGeneration++;

        return result with
        {
            RuntimeSessionApplied = true,
            RequiresAppRestart = false,
            Warnings = warnings,
        };
    }

    private static PackageOperationResult DeferInstalledPackageSessionReload(PackageOperationResult result)
    {
        if (!result.Success)
        {
            return result;
        }

        return result with
        {
            RuntimeSessionApplied = false,
            RequiresAppRestart = false,
        };
    }

    private async Task<InstalledPackageMutationPlan> ApplyPackageStoreMutationStageAsync(
        PackageStoreMutationRequest mutation,
        List<InstalledPackageRecord> stagedPackages,
        List<InstalledPackageRecord> committedPackages,
        ICollection<PendingPackageStoreFileAction> fileActions,
        CancellationToken cancellationToken)
    {
        var nextStagedPackages = stagedPackages.ToList();
        var nextCommittedPackages = committedPackages.ToList();
        InstalledPackageMutationPlan stagedPlan;
        InstalledPackageMutationPlan committedPlan;

        switch (mutation.Kind)
        {
            case PackageStoreMutationKind.Install:
            {
                var preparation = await _packageArchiveInstaller.PrepareInstallStageAsync(mutation.PackagePath ?? string.Empty, cancellationToken);
                if (!preparation.Success || preparation.Mutation is null)
                {
                    return new InstalledPackageMutationPlan(preparation.Failure ?? PackageOperationResults.Failure("Package install stage failed."), []);
                }

                stagedPlan = _installedPackageStore.ApplyInstall(nextStagedPackages, preparation.Mutation.StagedRecord);
                committedPlan = _installedPackageStore.ApplyInstall(nextCommittedPackages, preparation.Mutation.InstalledRecord);
                if (!stagedPlan.Result.Success || !committedPlan.Result.Success)
                {
                    TryDeleteDirectory(preparation.Mutation.StagingPath);
                    return stagedPlan.Result.Success ? committedPlan : stagedPlan;
                }

                stagedPackages.Clear();
                stagedPackages.AddRange(nextStagedPackages);
                committedPackages.Clear();
                committedPackages.AddRange(nextCommittedPackages);
                fileActions.Add(PendingPackageStoreFileAction.AddOrReplace(preparation.Mutation.StagingPath, preparation.Mutation.InstalledPath));
                return committedPlan;
            }
            case PackageStoreMutationKind.Upgrade:
            {
                if (string.IsNullOrWhiteSpace(mutation.PackageId))
                {
                    return new InstalledPackageMutationPlan(PackageOperationResults.Failure("Package id is required."), []);
                }

                var installedPackage = committedPackages.FirstOrDefault(package => string.Equals(package.PackageId, mutation.PackageId, StringComparison.OrdinalIgnoreCase));
                if (installedPackage is null)
                {
                    return new InstalledPackageMutationPlan(PackageOperationResults.Failure($"Package '{mutation.PackageId}' is not installed."), []);
                }

                var preparation = await _packageArchiveInstaller.PrepareUpgradeStageAsync(
                    mutation.PackageId,
                    mutation.PackagePath ?? string.Empty,
                    installedPackage,
                    cancellationToken);
                if (!preparation.Success || preparation.Mutation is null)
                {
                    return new InstalledPackageMutationPlan(preparation.Failure ?? PackageOperationResults.Failure("Package upgrade stage failed."), []);
                }

                stagedPlan = _installedPackageStore.ApplyUpgrade(nextStagedPackages, mutation.PackageId, preparation.Mutation.StagedRecord, mutation.AllowDowngrade, mutation.Reinstall);
                committedPlan = _installedPackageStore.ApplyUpgrade(nextCommittedPackages, mutation.PackageId, preparation.Mutation.InstalledRecord, mutation.AllowDowngrade, mutation.Reinstall);
                if (!stagedPlan.Result.Success || !committedPlan.Result.Success)
                {
                    TryDeleteDirectory(preparation.Mutation.StagingPath);
                    return stagedPlan.Result.Success ? committedPlan : stagedPlan;
                }

                stagedPackages.Clear();
                stagedPackages.AddRange(nextStagedPackages);
                committedPackages.Clear();
                committedPackages.AddRange(nextCommittedPackages);
                fileActions.Add(PendingPackageStoreFileAction.AddOrReplace(preparation.Mutation.StagingPath, preparation.Mutation.InstalledPath));
                foreach (var removedPackage in committedPlan.RemovedPackages)
                {
                    if (!string.Equals(removedPackage.InstallPath, preparation.Mutation.InstalledPath, StringComparison.OrdinalIgnoreCase))
                    {
                        fileActions.Add(PendingPackageStoreFileAction.Delete(removedPackage.InstallPath));
                    }
                }

                return committedPlan;
            }
            case PackageStoreMutationKind.Enable:
            case PackageStoreMutationKind.Disable:
            {
                if (string.IsNullOrWhiteSpace(mutation.PackageId))
                {
                    return new InstalledPackageMutationPlan(PackageOperationResults.Failure("Package id is required."), []);
                }

                var isEnabled = mutation.Kind == PackageStoreMutationKind.Enable;
                stagedPlan = _installedPackageStore.ApplySetEnabled(nextStagedPackages, mutation.PackageId, isEnabled);
                committedPlan = _installedPackageStore.ApplySetEnabled(nextCommittedPackages, mutation.PackageId, isEnabled);
                if (!stagedPlan.Result.Success || !committedPlan.Result.Success)
                {
                    return stagedPlan.Result.Success ? committedPlan : stagedPlan;
                }

                stagedPackages.Clear();
                stagedPackages.AddRange(nextStagedPackages);
                committedPackages.Clear();
                committedPackages.AddRange(nextCommittedPackages);
                return committedPlan;
            }
            case PackageStoreMutationKind.Uninstall:
            {
                if (string.IsNullOrWhiteSpace(mutation.PackageId))
                {
                    return new InstalledPackageMutationPlan(PackageOperationResults.Failure("Package id is required."), []);
                }

                stagedPlan = _installedPackageStore.ApplyUninstall(nextStagedPackages, mutation.PackageId);
                committedPlan = _installedPackageStore.ApplyUninstall(nextCommittedPackages, mutation.PackageId);
                if (!stagedPlan.Result.Success || !committedPlan.Result.Success)
                {
                    return stagedPlan.Result.Success ? committedPlan : stagedPlan;
                }

                stagedPackages.Clear();
                stagedPackages.AddRange(nextStagedPackages);
                committedPackages.Clear();
                committedPackages.AddRange(nextCommittedPackages);
                foreach (var removedPackage in committedPlan.RemovedPackages)
                {
                    fileActions.Add(PendingPackageStoreFileAction.Delete(removedPackage.InstallPath));
                }

                return committedPlan;
            }
            default:
                return new InstalledPackageMutationPlan(PackageOperationResults.Failure($"Unsupported package store mutation kind '{mutation.Kind}'."), []);
        }
    }

    private static string BuildPackageStoreStageMessage(IReadOnlyList<string> operationMessages, int impactedPackageCount)
    {
        if (operationMessages.Count == 1)
        {
            return operationMessages[0];
        }

        return impactedPackageCount == 0
            ? "No package changes required."
            : $"Applied {impactedPackageCount} package store change(s).";
    }

    private static (string? Error, IReadOnlyList<PendingPackageStoreFileCommit> Commits) CommitPackageStoreFileAdds(IReadOnlyList<PendingPackageStoreFileAction> fileActions)
    {
        var commits = new List<PendingPackageStoreFileCommit>();
        foreach (var action in fileActions.Where(action => action.Kind == PendingPackageStoreFileActionKind.AddOrReplace))
        {
            var stagingPath = action.StagingPath!;
            var installedPath = action.InstalledPath!;
            var backupPath = string.Empty;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
                if (Directory.Exists(installedPath))
                {
                    backupPath = installedPath + ".backup-" + Guid.NewGuid().ToString("N");
                    Directory.Move(installedPath, backupPath);
                }

                Directory.Move(stagingPath, installedPath);
                commits.Add(new PendingPackageStoreFileCommit(stagingPath, installedPath, backupPath));
            }
            catch (Exception ex)
            {
                if (Directory.Exists(installedPath) && !Directory.Exists(stagingPath))
                {
                    TryMoveDirectory(installedPath, stagingPath);
                }

                if (!string.IsNullOrWhiteSpace(backupPath))
                {
                    RestoreBackupDirectory(backupPath, installedPath);
                }

                RollBackPackageStoreFileAdds(commits);
                return ($"Failed to commit package files to '{installedPath}': {ex.Message}", commits);
            }
        }

        return (null, commits);
    }

    private static void CompletePackageStoreFileAdds(IReadOnlyList<PendingPackageStoreFileCommit> commits)
    {
        foreach (var commit in commits)
        {
            TryDeleteDirectory(commit.BackupPath);
        }
    }

    private static void RollBackPackageStoreFileAdds(IReadOnlyList<PendingPackageStoreFileCommit> commits)
    {
        foreach (var commit in commits.Reverse())
        {
            if (Directory.Exists(commit.InstalledPath) && !Directory.Exists(commit.StagingPath))
            {
                TryMoveDirectory(commit.InstalledPath, commit.StagingPath);
            }

            if (!string.IsNullOrWhiteSpace(commit.BackupPath))
            {
                RestoreBackupDirectory(commit.BackupPath, commit.InstalledPath);
            }
        }
    }

    private static void DeletePackageStoreRemovedFiles(IReadOnlyList<PendingPackageStoreFileAction> fileActions)
    {
        foreach (var action in fileActions.Where(action => action.Kind == PendingPackageStoreFileActionKind.Delete))
        {
            TryDeleteDirectory(action.DeletePath);
        }
    }

    private static void CleanupPackageStoreFileActions(IReadOnlyList<PendingPackageStoreFileAction> fileActions)
    {
        foreach (var action in fileActions.Where(action => action.Kind == PendingPackageStoreFileActionKind.AddOrReplace))
        {
            TryDeleteDirectory(action.StagingPath);
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for discarded package store stages.
        }
    }

    private static void RestoreBackupDirectory(string backupPath, string restorePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(backupPath)
                && !string.IsNullOrWhiteSpace(restorePath)
                && Directory.Exists(backupPath)
                && !Directory.Exists(restorePath))
            {
                Directory.Move(backupPath, restorePath);
            }
        }
        catch
        {
            // Best effort rollback for package file replacement.
        }
    }

    private static void TryMoveDirectory(string sourcePath, string destinationPath)
    {
        try
        {
            if (Directory.Exists(sourcePath) && !Directory.Exists(destinationPath))
            {
                Directory.Move(sourcePath, destinationPath);
            }
        }
        catch
        {
            // Best effort rollback for package file replacement.
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

        return await CommitMergedPackageSessionAsync(
            sources =>
            {
                sources.SetDevOverlay(new PackageSessionDevOverlay(packageId, folder, request.Watch, PackageSessionOverlayOwner.Sdk));
                return true;
            },
            failureMessage: null,
            $"Loaded dev package '{packageId}'.",
            [packageId],
            packageId,
            cancellationToken);
    }

    private async Task<PackageSessionOperationResult> UnloadDevPackageOverlayAsync(
        string packageId,
        CancellationToken cancellationToken)
    {
        return await CommitMergedPackageSessionAsync(
            sources => sources.RemoveDevOverlay(packageId, PackageSessionOverlayOwner.Sdk),
            $"Dev package overlay '{packageId}' is not loaded.",
            $"Unloaded dev package '{packageId}'.",
            [packageId],
            packageId,
            cancellationToken);
    }

    private async Task<PackageSessionOperationResult> CommitMergedPackageSessionAsync(
        Func<PackageSessionSourceSnapshot, bool> updateSources,
        string? failureMessage,
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
            var sources = _sourceState.Snapshot();
            if (!updateSources(sources))
            {
                return PackageSessionOperationResult.Failed(failureMessage ?? "Package session source update failed.");
            }

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
            _sessionGeneration++;

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

    private static IReadOnlyList<string> AddLifecyclePackageSources(
        PackageSessionSourceSnapshot sources,
        IReadOnlyList<PackageSessionLoadRequest>? packages,
        PackageSessionOverlayOwner owner,
        ICollection<string> errors)
    {
        var forceReloadDevFolders = new List<string>();
        foreach (var package in packages ?? [])
        {
            if (string.IsNullOrWhiteSpace(package.Source))
            {
                errors.Add("A package lifecycle source cannot be empty.");
                continue;
            }

            if (package.SourceKind != PackageSourceKind.Dev)
            {
                errors.Add($"Package lifecycle batch loading currently supports dev package sources only, not '{package.SourceKind}'.");
                continue;
            }

            var folder = Path.GetFullPath(package.Source.Trim());
            AddDevPackageOverlay(sources, folder, package.Watch, owner, errors);
            forceReloadDevFolders.Add(folder);
        }

        return forceReloadDevFolders;
    }

    private static void ReplaceLiveReloadOverlays(PackageSessionSourceSnapshot sources)
        => sources.RemoveDevOverlaysOwnedBy(PackageSessionOverlayOwner.Startup, PackageSessionOverlayOwner.HotReload);

    private static PackageSessionOverlayOwner ToSessionOverlayOwner(PackageLifecycleOverlayOwner overlayOwner)
        => overlayOwner switch
        {
            PackageLifecycleOverlayOwner.Startup => PackageSessionOverlayOwner.Startup,
            PackageLifecycleOverlayOwner.HotReload => PackageSessionOverlayOwner.HotReload,
            PackageLifecycleOverlayOwner.Sdk => PackageSessionOverlayOwner.Sdk,
            _ => throw new ArgumentOutOfRangeException(nameof(overlayOwner), overlayOwner, null),
        };

    private static IReadOnlyList<string> BuildImpactedPackageIds(
        IReadOnlyList<ActivePackageDescriptor> currentActivePackages,
        IReadOnlyList<PackageSourceDescriptor> currentPackageSources,
        IReadOnlyList<ActivePackageDescriptor> stagedActivePackages,
        IReadOnlyList<PackageSourceDescriptor> stagedPackageSources,
        IReadOnlyCollection<string> forceReloadDevFolders)
    {
        var impactedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentActivePackagesById = currentActivePackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var stagedActivePackagesById = stagedActivePackages.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in currentActivePackagesById.Keys.Concat(stagedActivePackagesById.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            currentActivePackagesById.TryGetValue(packageId, out var currentPackage);
            stagedActivePackagesById.TryGetValue(packageId, out var stagedPackage);
            if (!ActivePackageDescriptorsEqual(currentPackage, stagedPackage))
            {
                impactedPackageIds.Add(packageId);
            }
        }

        var currentSourcesById = currentPackageSources
            .GroupBy(source => source.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var stagedSourcesById = stagedPackageSources
            .GroupBy(source => source.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in currentSourcesById.Keys.Concat(stagedSourcesById.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            currentSourcesById.TryGetValue(packageId, out var currentSource);
            stagedSourcesById.TryGetValue(packageId, out var stagedSource);
            if (!PackageSourceDescriptorsEqual(currentSource, stagedSource))
            {
                impactedPackageIds.Add(packageId);
            }
        }

        var forcedDevFolders = forceReloadDevFolders
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in stagedPackageSources)
        {
            if (source.Kind == PackageSourceKind.Dev && forcedDevFolders.Contains(Path.GetFullPath(source.Folder)))
            {
                impactedPackageIds.Add(source.PackageId);
            }
        }

        return impactedPackageIds.OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool ActivePackageDescriptorsEqual(ActivePackageDescriptor? current, ActivePackageDescriptor? staged)
    {
        if (current is null || staged is null)
        {
            return current is null && staged is null;
        }

        return string.Equals(current.PackageId, staged.PackageId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(current.DisplayName, staged.DisplayName, StringComparison.Ordinal)
               && string.Equals(current.Version, staged.Version, StringComparison.OrdinalIgnoreCase)
               && current.Icon == staged.Icon
               && current.IsEnabled == staged.IsEnabled
               && current.Readiness == staged.Readiness
               && current.Views.SequenceEqual(staged.Views);
    }

    private static bool PackageSourceDescriptorsEqual(PackageSourceDescriptor? current, PackageSourceDescriptor? staged)
    {
        if (current is null || staged is null)
        {
            return current is null && staged is null;
        }

        return string.Equals(current.PackageId, staged.PackageId, StringComparison.OrdinalIgnoreCase)
               && current.Kind == staged.Kind
               && string.Equals(Path.GetFullPath(current.Folder), Path.GetFullPath(staged.Folder), StringComparison.OrdinalIgnoreCase);
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
            var manifest = JsonSerializer.Deserialize<RuntimePackageManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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

    private sealed record PendingPackageLifecycleStage(
        string StageId,
        ActivePackageSession Session,
        PackageSessionSourceSnapshot Sources,
        IReadOnlyList<string> ImpactedPackageIds,
        long BaseSessionGeneration);

    private enum PendingPackageStoreFileActionKind
    {
        AddOrReplace,
        Delete,
    }

    private sealed record PendingPackageStoreFileAction(
        PendingPackageStoreFileActionKind Kind,
        string? StagingPath,
        string? InstalledPath,
        string? DeletePath)
    {
        public static PendingPackageStoreFileAction AddOrReplace(string stagingPath, string installedPath)
            => new(PendingPackageStoreFileActionKind.AddOrReplace, stagingPath, installedPath, null);

        public static PendingPackageStoreFileAction Delete(string deletePath)
            => new(PendingPackageStoreFileActionKind.Delete, null, null, deletePath);
    }

    private sealed record PendingPackageStoreFileCommit(
        string StagingPath,
        string InstalledPath,
        string BackupPath);

    private sealed record PendingPackageStoreStage(
        string StageId,
        ActivePackageSession Session,
        IReadOnlyList<InstalledPackageRecord> CommittedPackages,
        IReadOnlyList<PendingPackageStoreFileAction> FileActions,
        PackageOperationResult Result,
        long BaseSessionGeneration);

}
