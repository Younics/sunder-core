using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.PackageManagement;
using Sunder.Protocol;
using Sunder.Sdk.Stacks;

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
            ReplaceLiveReloadOverlays(sources);
            var owner = ToSessionOverlayOwner(request.OverlayOwner);
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
            ReplaceLiveReloadOverlays(sources);
            var owner = ToSessionOverlayOwner(request.OverlayOwner);
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

    public async Task<PackageOperationResult> InstallPackageFromPathAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var result = await _packageArchiveInstaller.InstallFromPathAsync(packagePath, cancellationToken);
        return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
    }

    public async Task<PackageOperationResult> InstallPackagesFromPathsAsync(
        PackageInstallBatchFromPathRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Items.Count == 0)
        {
            return PackageOperationResults.Success(
                "No package changes required.",
                runtimeSessionApplied: true,
                impactedPackageIds: []);
        }

        var warnings = new List<string>();
        var impactedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = string.IsNullOrWhiteSpace(item.PackageId)
                ? await _packageArchiveInstaller.InstallFromPathAsync(item.PackagePath, cancellationToken)
                : await _packageArchiveInstaller.UpgradeFromPathAsync(
                    item.PackageId,
                    item.PackagePath,
                    item.AllowDowngrade,
                    item.Reinstall,
                    cancellationToken);

            warnings.AddRange(result.Warnings);
            foreach (var packageId in result.ImpactedPackageIds)
            {
                impactedPackageIds.Add(packageId);
            }

            if (!result.Success)
            {
                return result with
                {
                    Warnings = warnings,
                    ImpactedPackageIds = impactedPackageIds.ToArray(),
                };
            }
        }

        var operationResult = PackageOperationResults.Success(
            $"Installed {request.Items.Count} package change(s).",
            warnings: warnings,
            impactedPackageIds: impactedPackageIds.ToArray());
        return await ReloadInstalledPackagesAfterMutationAsync(operationResult, cancellationToken);
    }

    public async Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(
        RuntimeStackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var stagingPath = CreateStackStagingPath();
        try
        {
            var loadResult = await LoadStackFragmentsAsync(request.StackPath, request.SelectedFragmentIds, stagingPath, cancellationToken);
            if (!loadResult.Success)
            {
                return new RuntimeStackImportPreviewResponse(false, [], [], [], loadResult.Warnings, loadResult.Errors);
            }

            var contributors = GetStackContributors();
            var actions = new List<RuntimeStackImportActionDescriptor>();
            var requiredInputs = new List<RuntimeStackRequiredInputDescriptor>();
            var conflicts = new List<RuntimeStackImportConflictDescriptor>();
            var warnings = loadResult.Warnings.ToList();
            var errors = new List<string>();

            foreach (var group in loadResult.Fragments.GroupBy(fragment => fragment.ContributorId, StringComparer.OrdinalIgnoreCase))
            {
                if (!contributors.TryGetValue(group.Key, out var contributor))
                {
                    errors.Add($"No active Stack contributor '{group.Key}' is available. Install and enable its package before importing this Stack.");
                    continue;
                }

                try
                {
                    var preview = await contributor.PreviewImportAsync(
                        new StackImportPreviewRequest(group.ToArray(), request.InputValues, request.IdRemaps),
                        cancellationToken);
                    actions.AddRange(preview.Actions.Select(action => ToProtocolAction(contributor.ContributorId, action)));
                    requiredInputs.AddRange(preview.RequiredInputs.Select(input => ToProtocolRequiredInput(contributor.ContributorId, input)));
                    conflicts.AddRange(preview.Conflicts.Select(conflict => ToProtocolConflict(contributor.ContributorId, conflict)));
                    warnings.AddRange(preview.Warnings);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"Stack contributor '{contributor.ContributorId}' preview failed: {ex.Message}");
                }
            }

            var hasBlockingConflict = conflicts.Any(conflict => string.Equals(conflict.Severity, StackImportConflictSeverity.Error.ToString(), StringComparison.OrdinalIgnoreCase));
            return new RuntimeStackImportPreviewResponse(
                errors.Count == 0 && !hasBlockingConflict,
                actions,
                requiredInputs,
                conflicts,
                warnings,
                errors);
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    public async Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<RuntimeStackExportItemDescriptor>();
        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var (packageId, contributor) in _sessionState.GetExtensionContributions(SunderStackExtensionPoints.StackContributors))
        {
            try
            {
                var discovered = await contributor.ListExportItemsAsync(new StackExportDiscoveryContext(packageId), cancellationToken);
                items.AddRange(discovered.Select(item => new RuntimeStackExportItemDescriptor(
                    contributor.ContributorId,
                    packageId,
                    item.ItemId,
                    item.DisplayName,
                    item.Kind,
                    item.DefaultSelected,
                    item.Sensitivities?.Select(sensitivity => sensitivity.ToString()).ToArray() ?? [],
                    item.Description,
                    item.Details?.Select(detail => new RuntimeStackExportItemDetail(
                        detail.Label,
                        detail.Value,
                        detail.Sensitivity?.ToString(),
                        detail.Description,
                        detail.ValueWhenExcluded,
                        string.IsNullOrWhiteSpace(detail.DetailId) ? BuildStackDetailId(detail.Label) : detail.DetailId,
                        detail.DefaultSelected,
                        detail.IsEditable,
                        detail.SupportsAskOnImport)).ToArray() ?? [])));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"Stack contributor '{contributor.ContributorId}' export discovery failed: {ex.Message}");
            }
        }

        return new RuntimeStackExportDiscoveryResponse(items, warnings, errors);
    }

    public async Task<RuntimeStackExportResponse> ExportStackAsync(
        RuntimeStackExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.StackId))
        {
            return new RuntimeStackExportResponse(false, null, [], ["Stack id is required."]);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return new RuntimeStackExportResponse(false, null, [], ["Stack name is required."]);
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            return new RuntimeStackExportResponse(false, null, [], ["Stack output path is required."]);
        }

        if (request.SelectedItems.Count == 0)
        {
            return new RuntimeStackExportResponse(false, null, [], ["Select at least one setup item to export."]);
        }

        var contributors = _sessionState
            .GetExtensionContributions(SunderStackExtensionPoints.StackContributors)
            .Where(contribution => !string.IsNullOrWhiteSpace(contribution.Contribution.ContributorId))
            .GroupBy(contribution => contribution.Contribution.ContributorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var fragments = new List<StackFragmentExport>();
        var fragmentDisplayMetadata = new Dictionary<string, StackFragmentDisplayMetadata>(StringComparer.OrdinalIgnoreCase);
        var packageRequirements = new List<StackPackageRequirement>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var exportOptions = new StackExportOptions(
            request.Options.IncludePrivateText,
            request.Options.IncludeMachineSpecificValues,
            request.Options.IncludeExecutableCommands,
            request.Options.IncludeNetworkEndpoints);

        foreach (var group in request.SelectedItems.GroupBy(item => item.ContributorId, StringComparer.OrdinalIgnoreCase))
        {
            if (!contributors.TryGetValue(group.Key, out var registration))
            {
                errors.Add($"No active Stack contributor '{group.Key}' is available for export.");
                continue;
            }

            var (packageId, contributor) = registration;

            try
            {
                var itemSelections = group
                    .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                    .Select(itemGroup => new StackExportItemSelection(
                        itemGroup.Key,
                        itemGroup.SelectMany(item => item.Details ?? [])
                            .Select(detail => new StackExportDetailSelection(
                                detail.DetailId,
                                detail.IsSelected,
                                detail.ValueOverride,
                                Enum.TryParse<StackValueSensitivity>(detail.SensitivityOverride, ignoreCase: true, out var sensitivityOverride)
                                    ? (StackValueSensitivity?)sensitivityOverride
                                    : null))
                            .ToArray()))
                    .ToArray();
                var discoveredItems = await contributor.ListExportItemsAsync(new StackExportDiscoveryContext(packageId), cancellationToken);
                var displayMetadataByItemId = BuildDisplayMetadataByItemId(discoveredItems, itemSelections);
                var contribution = await contributor.ExportAsync(
                    new StackExportRequest(
                        itemSelections.Select(item => item.ItemId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        exportOptions,
                        itemSelections),
                    cancellationToken);
                fragments.AddRange(contribution.Fragments);
                foreach (var fragment in contribution.Fragments)
                {
                    var sourceItemId = string.IsNullOrWhiteSpace(fragment.SourceItemId)
                        ? InferSourceItemId(itemSelections, contribution.Fragments)
                        : fragment.SourceItemId;
                    displayMetadataByItemId.TryGetValue(sourceItemId ?? string.Empty, out var itemDisplayMetadata);
                    fragmentDisplayMetadata[fragment.FragmentId] = new StackFragmentDisplayMetadata(
                        sourceItemId,
                        itemDisplayMetadata?.Kind,
                        itemDisplayMetadata?.DisplayDetails ?? []);
                }

                packageRequirements.AddRange(contribution.PackageRequirements);
                warnings.AddRange(contribution.Warnings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"Stack contributor '{contributor.ContributorId}' export failed: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            return new RuntimeStackExportResponse(false, null, warnings, errors);
        }

        var secretFragments = fragments
            .Where(fragment => fragment.Safety.ContainsSecrets)
            .Select(fragment => fragment.DisplayName)
            .ToArray();
        if (secretFragments.Length > 0)
        {
            return new RuntimeStackExportResponse(
                false,
                null,
                warnings,
                ["Stack export cannot include raw secrets. Remove or redact secret values from: " + string.Join(", ", secretFragments)]);
        }

        if (fragments.Count == 0 && packageRequirements.Count == 0)
        {
            return new RuntimeStackExportResponse(false, null, warnings, ["Selected setup items did not produce Stack content."]);
        }

        var stagingPath = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "export", Guid.NewGuid().ToString("N"));
        try
        {
            var payloadFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fragment in fragments)
            {
                var payloadPath = Path.Combine(stagingPath, "fragments", fragment.FragmentId + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
                await File.WriteAllTextAsync(payloadPath, fragment.JsonPayload, cancellationToken);
                payloadFiles[$"payload/fragments/{fragment.FragmentId}.json"] = payloadPath;

                foreach (var file in fragment.Files ?? [])
                {
                    payloadFiles[$"payload/files/{fragment.FragmentId}/{file.RelativePath.Replace('\\', '/')}"] = file.SourcePath;
                }
            }

            var now = DateTimeOffset.UtcNow;
            var manifest = new SunderStackManifest
            {
                SchemaVersion = 1,
                StackId = request.StackId,
                Name = request.Name,
                Summary = request.Summary,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Packages = BuildPackageRequirements(packageRequirements.Concat(fragments.SelectMany(fragment => fragment.RequiresPackages ?? []))),
                Fragments = fragments.Select(fragment => ToManifestFragment(
                    fragment,
                    fragmentDisplayMetadata.TryGetValue(fragment.FragmentId, out var metadata) ? metadata : null)).ToArray(),
                RequiredInputs = fragments.SelectMany(fragment => fragment.RequiredInputs ?? []).Select(ToManifestRequiredInput).ToArray(),
                Safety = BuildAggregateSafety(fragments.Select(fragment => fragment.Safety)),
            };

            await SunderStackArchiveWriter.WriteAsync(manifest, request.OutputPath, payloadFiles, cancellationToken);
            var validation = await SunderStackArchiveInspector.ExtractAndValidateAsync(
                request.OutputPath,
                Path.Combine(stagingPath, "validate"),
                cancellationToken);
            warnings.AddRange(validation.Warnings);
            if (!validation.Success)
            {
                TryDeleteFile(request.OutputPath);
                return new RuntimeStackExportResponse(false, null, warnings, validation.Errors);
            }

            return new RuntimeStackExportResponse(true, request.OutputPath, warnings, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RuntimeStackExportResponse(false, null, warnings, [$"Failed to write Stack archive: {ex.Message}"]);
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    public async Task<RuntimeStackImportResponse> ImportStackAsync(
        RuntimeStackImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var stagingPath = CreateStackStagingPath();
        try
        {
            var loadResult = await LoadStackFragmentsAsync(request.StackPath, request.SelectedFragmentIds, stagingPath, cancellationToken);
            if (!loadResult.Success)
            {
                return new RuntimeStackImportResponse(false, [], request.IdRemaps, loadResult.Warnings, loadResult.Errors);
            }

            var contributors = GetStackContributors();
            var importedItems = new List<RuntimeStackImportedItemDescriptor>();
            var idRemaps = new Dictionary<string, string>(request.IdRemaps, StringComparer.OrdinalIgnoreCase);
            var warnings = loadResult.Warnings.ToList();
            var errors = new List<string>();

            foreach (var group in loadResult.Fragments.GroupBy(fragment => fragment.ContributorId, StringComparer.OrdinalIgnoreCase))
            {
                if (!contributors.TryGetValue(group.Key, out var contributor))
                {
                    errors.Add($"No active Stack contributor '{group.Key}' is available. Install and enable its package before importing this Stack.");
                    continue;
                }

                try
                {
                    var result = await contributor.ImportAsync(
                        new StackImportRequest(group.ToArray(), request.InputValues, idRemaps, request.SelectedActionIds),
                        cancellationToken);
                    importedItems.AddRange(result.ImportedItems.Select(item => ToProtocolImportedItem(contributor.ContributorId, item)));
                    foreach (var (key, value) in result.IdRemaps)
                    {
                        idRemaps[key] = value;
                    }

                    warnings.AddRange(result.Warnings);
                    errors.AddRange(result.Errors);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"Stack contributor '{contributor.ContributorId}' import failed: {ex.Message}");
                }
            }

            return new RuntimeStackImportResponse(errors.Count == 0, importedItems, idRemaps, warnings, errors);
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
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
        return await ReloadInstalledPackagesAfterMutationAsync(result, cancellationToken);
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
            _ => throw new ArgumentOutOfRangeException(nameof(overlayOwner), overlayOwner, null),
        };

    private static string BuildStackDetailId(string label)
    {
        var builder = new System.Text.StringBuilder(label.Length);
        var pendingSeparator = false;
        foreach (var character in label.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                pendingSeparator = false;
                continue;
            }

            if (builder.Length > 0 && !pendingSeparator)
            {
                builder.Append('-');
                pendingSeparator = true;
            }
        }

        var detailId = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(detailId) ? "detail" : detailId;
    }

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

    private Dictionary<string, IPackageStackContributor> GetStackContributors()
        => _sessionState
            .GetExtensionContributions(SunderStackExtensionPoints.StackContributors)
            .Select(contribution => contribution.Contribution)
            .Where(contributor => !string.IsNullOrWhiteSpace(contributor.ContributorId))
            .GroupBy(contributor => contributor.ContributorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    private static async Task<StackFragmentLoadResult> LoadStackFragmentsAsync(
        string stackPath,
        IReadOnlyList<string> selectedFragmentIds,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stackPath))
        {
            return StackFragmentLoadResult.Failed("Stack path is required.");
        }

        stackPath = Path.GetFullPath(stackPath);
        if (!File.Exists(stackPath))
        {
            return StackFragmentLoadResult.Failed($"Stack file '{stackPath}' does not exist.");
        }

        var validation = await SunderStackArchiveInspector.ExtractAndValidateAsync(stackPath, stagingPath, cancellationToken);
        if (!validation.Success || validation.Manifest is null)
        {
            return new StackFragmentLoadResult(false, [], validation.Warnings, validation.Errors);
        }

        var selected = selectedFragmentIds.Count == 0
            ? (validation.Manifest.Fragments ?? [])
                .Where(fragment => fragment.DefaultSelected != false)
                .Select(fragment => fragment.FragmentId)
                .Where(fragmentId => !string.IsNullOrWhiteSpace(fragmentId))
                .Select(fragmentId => fragmentId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : selectedFragmentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fragments = new List<StackFragmentImport>();
        foreach (var fragment in validation.Manifest.Fragments ?? [])
        {
            if (string.IsNullOrWhiteSpace(fragment.FragmentId) || !selected.Contains(fragment.FragmentId))
            {
                continue;
            }

            var payloadPath = Path.Combine(stagingPath, fragment.PayloadPath!.Replace('/', Path.DirectorySeparatorChar));
            var jsonPayload = await File.ReadAllTextAsync(payloadPath, cancellationToken);
            fragments.Add(new StackFragmentImport(
                fragment.FragmentId,
                fragment.OwnerPackageId!,
                fragment.ContributorId!,
                fragment.SchemaId!,
                fragment.SchemaVersion!.Value,
                fragment.DisplayName!,
                jsonPayload,
                fragment.Description,
                ResolveFragmentFiles(stagingPath, fragment.FragmentId)));
        }

        return new StackFragmentLoadResult(true, fragments, validation.Warnings, []);
    }

    private static IReadOnlyList<StackPayloadFile> ResolveFragmentFiles(string stagingPath, string fragmentId)
    {
        var fragmentFilesPath = Path.Combine(stagingPath, "payload", "files", fragmentId);
        if (!Directory.Exists(fragmentFilesPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(fragmentFilesPath, "*", SearchOption.AllDirectories)
            .Select(path => new StackPayloadFile(
                Path.GetRelativePath(fragmentFilesPath, path).Replace('\\', '/'),
                path))
            .ToArray();
    }

    private static RuntimeStackImportActionDescriptor ToProtocolAction(string contributorId, StackImportAction action)
        => new(action.ActionId, contributorId, action.DisplayName, action.Kind.ToString(), action.DefaultSelected, action.Description);

    private static RuntimeStackRequiredInputDescriptor ToProtocolRequiredInput(string contributorId, StackRequiredInputDescriptor input)
        => new(input.InputId, contributorId, input.Kind.ToString(), input.Label, input.Required, input.Description, input.DefaultValue);

    private static RuntimeStackImportConflictDescriptor ToProtocolConflict(string contributorId, StackImportConflict conflict)
        => new(conflict.ConflictId, contributorId, conflict.Message, conflict.Severity.ToString(), conflict.FragmentId);

    private static RuntimeStackImportedItemDescriptor ToProtocolImportedItem(string contributorId, StackImportedItem item)
        => new(item.ItemId, contributorId, item.DisplayName, item.Kind);

    private static IReadOnlyList<SunderStackPackageRequirement> BuildPackageRequirements(IEnumerable<StackPackageRequirement> requirements)
        => requirements
            .Where(requirement => !string.IsNullOrWhiteSpace(requirement.PackageId))
            .GroupBy(requirement => requirement.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new SunderStackPackageRequirement
                {
                    PackageId = first.PackageId,
                    InstallTag = string.IsNullOrWhiteSpace(first.InstallTag) ? "latest" : first.InstallTag,
                    CreatedWithVersion = first.CreatedWithVersion,
                    MinimumVersion = group.Select(requirement => requirement.MinimumVersion).FirstOrDefault(version => !string.IsNullOrWhiteSpace(version)),
                    Required = group.Any(requirement => requirement.Required),
                };
            })
            .OrderBy(requirement => requirement.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed record StackFragmentDisplayMetadata(
        string? SourceItemId,
        string? Kind,
        IReadOnlyList<SunderStackFragmentDisplayDetail> DisplayDetails);

    private sealed record StackItemDisplayMetadata(
        string? Kind,
        IReadOnlyList<SunderStackFragmentDisplayDetail> DisplayDetails);

    private static IReadOnlyDictionary<string, StackItemDisplayMetadata> BuildDisplayMetadataByItemId(
        IReadOnlyList<StackExportItemDescriptor> discoveredItems,
        IReadOnlyList<StackExportItemSelection> itemSelections)
    {
        var selectionsByItemId = itemSelections.ToDictionary(selection => selection.ItemId, StringComparer.OrdinalIgnoreCase);
        var displayMetadataByItemId = new Dictionary<string, StackItemDisplayMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in discoveredItems)
        {
            if (!selectionsByItemId.TryGetValue(item.ItemId, out var selection))
            {
                continue;
            }

            var selectedDetailsById = (selection.Details ?? [])
                .GroupBy(detail => detail.DetailId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var hasExplicitDetailSelection = selection.Details is not null;
            var displayDetails = new List<SunderStackFragmentDisplayDetail>();
            foreach (var detail in item.Details ?? [])
            {
                var detailId = string.IsNullOrWhiteSpace(detail.DetailId) ? BuildStackDetailId(detail.Label) : detail.DetailId!;
                StackExportDetailSelection? selectedDetail = null;
                if (hasExplicitDetailSelection)
                {
                    if (!selectedDetailsById.TryGetValue(detailId, out selectedDetail) || !selectedDetail.IsSelected)
                    {
                        continue;
                    }
                }

                var effectiveSensitivity = selectedDetail?.SensitivityOverride ?? detail.Sensitivity;
                var asksOnImport = effectiveSensitivity == StackValueSensitivity.Secret;
                var value = asksOnImport
                    ? "Importer will provide this value."
                    : string.IsNullOrWhiteSpace(selectedDetail?.ValueOverride) ? detail.Value : selectedDetail!.ValueOverride!;
                if (string.IsNullOrWhiteSpace(detail.Label) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                displayDetails.Add(new SunderStackFragmentDisplayDetail
                {
                    Label = detail.Label,
                    Value = value,
                    Behavior = asksOnImport ? "Ask on import" : "Include value",
                });
            }

            displayMetadataByItemId[item.ItemId] = new StackItemDisplayMetadata(item.Kind, displayDetails);
        }

        return displayMetadataByItemId;
    }

    private static string? InferSourceItemId(
        IReadOnlyList<StackExportItemSelection> itemSelections,
        IReadOnlyList<StackFragmentExport> fragments)
        => itemSelections.Count == 1 && fragments.Count == 1 ? itemSelections[0].ItemId : null;

    private static SunderStackFragmentManifest ToManifestFragment(StackFragmentExport fragment, StackFragmentDisplayMetadata? displayMetadata)
        => new()
        {
            FragmentId = fragment.FragmentId,
            OwnerPackageId = fragment.OwnerPackageId,
            ContributorId = fragment.ContributorId,
            SchemaId = fragment.SchemaId,
            SchemaVersion = fragment.SchemaVersion,
            Kind = displayMetadata?.Kind,
            DisplayName = fragment.DisplayName,
            SourceItemId = displayMetadata?.SourceItemId,
            Description = fragment.Description,
            DefaultSelected = fragment.DefaultSelected,
            PayloadPath = $"payload/fragments/{fragment.FragmentId}.json",
            RequiresPackages = (fragment.RequiresPackages ?? [])
                .Select(requirement => requirement.PackageId)
                .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            RequiredInputs = (fragment.RequiredInputs ?? []).Select(ToManifestRequiredInput).ToArray(),
            DisplayDetails = displayMetadata?.DisplayDetails,
            Safety = ToManifestSafety(fragment.Safety),
        };

    private static SunderStackRequiredInputManifest ToManifestRequiredInput(StackRequiredInputDescriptor input)
        => new()
        {
            InputId = input.InputId,
            Kind = input.Kind.ToString(),
            Label = input.Label,
            Description = input.Description,
            Required = input.Required,
        };

    private static SunderStackSafetyManifest ToManifestSafety(StackSafetyDescriptor safety)
        => new()
        {
            ContainsSecrets = safety.ContainsSecrets,
            ContainsSecretReferences = safety.ContainsSecretReferences,
            ContainsLocalPaths = safety.ContainsLocalPaths,
            ContainsPrivateText = safety.ContainsPrivateText,
            ContainsExecutableCommands = safety.ContainsExecutableCommands,
            ContainsNetworkEndpoints = safety.ContainsNetworkEndpoints,
            ContainsMachineSpecificValues = safety.ContainsMachineSpecificValues,
        };

    private static SunderStackSafetyManifest BuildAggregateSafety(IEnumerable<StackSafetyDescriptor> safetyItems)
    {
        var items = safetyItems.ToArray();
        return new SunderStackSafetyManifest
        {
            ContainsSecrets = items.Any(item => item.ContainsSecrets),
            ContainsSecretReferences = items.Any(item => item.ContainsSecretReferences),
            ContainsLocalPaths = items.Any(item => item.ContainsLocalPaths),
            ContainsPrivateText = items.Any(item => item.ContainsPrivateText),
            ContainsExecutableCommands = items.Any(item => item.ContainsExecutableCommands),
            ContainsNetworkEndpoints = items.Any(item => item.ContainsNetworkEndpoints),
            ContainsMachineSpecificValues = items.Any(item => item.ContainsMachineSpecificValues),
        };
    }

    private static string CreateStackStagingPath()
        => Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "runtime", Guid.NewGuid().ToString("N"));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Stack staging cleanup must not hide contributor results.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Stack export validation cleanup is best effort.
        }
    }

    private sealed record PendingPackageLifecycleStage(
        string StageId,
        ActivePackageSession Session,
        PackageSessionSourceSnapshot Sources,
        IReadOnlyList<string> ImpactedPackageIds,
        long BaseSessionGeneration);

    private sealed record StackFragmentLoadResult(
        bool Success,
        IReadOnlyList<StackFragmentImport> Fragments,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Errors)
    {
        public static StackFragmentLoadResult Failed(string message)
            => new(false, [], [], [message]);
    }

}
