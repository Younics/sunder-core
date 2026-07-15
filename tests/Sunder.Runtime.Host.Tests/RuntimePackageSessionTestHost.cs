using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimePackageSessionTestHost
{
    private readonly RuntimeSessionOwner _owner;
    private readonly PackageSessionLifecycleService _sessions;
    private readonly InstalledPackageLifecycleService _installed;
    private readonly RuntimePackageUiService _ui;
    private readonly RuntimePackageDataService _data;
    private readonly PackageSettingsAccessService _settings;
    private readonly PackageAuthAccessService _auth;
    private readonly PackageFaultService _faults;
    private readonly RuntimeStackExportService _stackExport;
    private readonly RuntimeStackImportService _stackImport;
    private readonly DevPackageWatchService _devWatcher;
    private readonly DevPackageOwnerLeaseService _devOwners;
    private readonly RuntimeProtocolDescriptor _protocol;

    public RuntimePackageSessionTestHost(
        ILogger<RuntimePackageSessionTestHost> logger,
        InstalledPackageStore installedPackageStore,
        SunderPackageArchiveInstaller packageArchiveInstaller,
        RuntimeOperationGate? operationGate = null,
        PackageStoreCoordinator? packageStoreCoordinator = null,
        PackageUiSnapshotStore? snapshotStore = null,
        RuntimeContentTransferStore? transferStore = null,
        RuntimeEventStreamService? eventStream = null,
        IInstalledPackageLifecycleFaultInjector? lifecycleFaultInjector = null,
        RuntimeLifecyclePolicyOptions? lifecyclePolicy = null,
        TimeProvider? timeProvider = null)
    {
        var gate = operationGate ?? new RuntimeOperationGate();
        var events = eventStream ?? new RuntimeEventStreamService();
        var transfers = transferStore ?? new RuntimeContentTransferStore(packageArchiveInstaller.Paths);
        var snapshots = snapshotStore ?? new PackageUiSnapshotStore(packageArchiveInstaller.Paths);
        var storeCoordinator = packageStoreCoordinator
            ?? new PackageStoreCoordinator(packageArchiveInstaller.Paths, installedPackageStore, packageArchiveInstaller);
        lifecyclePolicy ??= new RuntimeLifecyclePolicyOptions();
        timeProvider ??= TimeProvider.System;
        _owner = new RuntimeSessionOwner(
            NullLogger<RuntimeSessionOwner>.Instance,
            events,
            timeProvider: timeProvider,
            uiSnapshots: snapshots,
            lifecyclePolicy: lifecyclePolicy);
        _ui = new RuntimePackageUiService(_owner, snapshots, installedPackageStore);
        var loader = new PackageSessionLoadService(logger, packageArchiveInstaller.Paths);
        var reconciler = new PackageSessionReconciler(installedPackageStore, loader);
        var publisher = new PackageSessionPublisher(_owner, _ui, NullLogger<PackageSessionPublisher>.Instance);
        _sessions = new PackageSessionLifecycleService(
            _owner,
            gate,
            reconciler,
            _ui,
            installedPackageStore,
            publisher,
            new PackageLifecycleStageStore(),
            NullLogger<PackageSessionLifecycleService>.Instance,
            lifecyclePolicy,
            timeProvider);
        _devWatcher = new DevPackageWatchService(
            _sessions,
            events,
            NullLogger<DevPackageWatchService>.Instance);
        _protocol = new RuntimeProtocolDescriptor();
        _devOwners = new DevPackageOwnerLeaseService(
            _sessions,
            _owner,
            _devWatcher,
            _protocol,
            lifecyclePolicy,
            timeProvider,
            NullLogger<DevPackageOwnerLeaseService>.Instance);
        _installed = new InstalledPackageLifecycleService(
            _owner,
            gate,
            installedPackageStore,
            storeCoordinator,
            reconciler,
            _ui,
            transfers,
            publisher,
            NullLogger<InstalledPackageLifecycleService>.Instance,
            lifecycleFaultInjector,
            lifecyclePolicy,
            timeProvider);
        _data = new RuntimePackageDataService(_owner.State);
        _settings = new PackageSettingsAccessService(_owner);
        _auth = new PackageAuthAccessService(_owner);
        _faults = new PackageFaultService(_owner);
        _stackExport = new RuntimeStackExportService(_owner, transfers, packageArchiveInstaller.Paths);
        _stackImport = new RuntimeStackImportService(_owner, transfers);
    }

    public long SessionGeneration => _owner.Generation;
    public Guid RuntimeInstanceId => _protocol.RuntimeInstanceId;
    public void MarkBootstrapReady() => _owner.MarkReady();
    public IReadOnlyList<ActivePackageDescriptor> GetActivePackages() => _sessions.GetActivePackages();
    public IReadOnlyList<SessionPackageDescriptor> GetSessionPackages() => _sessions.GetSessionPackages();
    public IReadOnlyList<PackageUiSnapshotDescriptor> GetActivePackageUiSnapshots() => _ui.GetActiveSnapshots();
    internal IReadOnlyList<RuntimePackageSource> GetActiveRuntimePackageSources() => _sessions.GetActiveSources();
    public PackageUiSnapshotLease? AcquireCurrentUiSnapshot(string snapshotId) => _ui.AcquireCurrent(snapshotId);
    public PackageUiSnapshotLease? AcquireStageUiSnapshot(string stageId, string snapshotId) => _ui.AcquireStage(stageId, snapshotId);
    public Task<PackageLifecycleOperationResult> LoadStartupDevPackagesAsync(IReadOnlyList<string> folders, CancellationToken token = default) => _sessions.LoadStartupDevPackagesAsync(folders, token);
    internal Task<PackageSessionOperationResult> LoadDevPackageFromRuntimeInputAsync(string folder, bool watch = true, CancellationToken token = default) => _sessions.LoadDevPackageAsync(folder, watch, token);
    internal Task<PackageOperationResult> InstallPackageFromRuntimePathAsync(string path, CancellationToken token = default) => _installed.InstallFromRuntimePathAsync(path, token);
    internal async Task<PackageOperationResult> ReinstallPackageFromRuntimePathAsync(string packageId, string path, CancellationToken token = default)
        => await _installed.UpgradeFromRuntimePathAsync(packageId, path, reinstall: true, cancellationToken: token);
    public Task<PackageSessionStatus?> GetPackageSessionStatusAsync(string packageId, CancellationToken token = default) => _sessions.GetStatusAsync(packageId, token);
    public Task<PackageLifecycleStageResult> StagePackageLifecycleAsync(PackageLifecycleStageRequest request, CancellationToken token = default) => _sessions.StageAsync(request, token);
    public Task<PackageLifecycleOperationResult> CommitPackageLifecycleStageAsync(string stageId, CancellationToken token = default) => _sessions.CommitStageAsync(stageId, token);
    public Task<bool> DiscardPackageLifecycleStageAsync(string stageId, CancellationToken token = default) => _sessions.DiscardStageAsync(stageId, token);
    public Task<PackageStoreStageResult> StagePackageStoreChangesAsync(PackageStoreStageRequest request, CancellationToken token = default) => _installed.StageAsync(request, token);
    public Task<PackageOperationResult> CommitPackageStoreStageAsync(string stageId, CancellationToken token = default) => _installed.CommitStageAsync(stageId, token);
    public Task<bool> DiscardPackageStoreStageAsync(string stageId, CancellationToken token = default) => _installed.DiscardStageAsync(stageId, token);
    public RuntimePackageStageStatus? GetPackageStageStatus(string stageId) => _owner.GetStageStatus(stageId);
    public Task SweepLifecycleStagesAsync(DateTimeOffset now, CancellationToken token = default) => _sessions.SweepStagesAsync(now, token);
    public Task SweepStoreStagesAsync(DateTimeOffset now, CancellationToken token = default) => _installed.SweepStagesAsync(now, token);
    public Task<DevPackageOwnerLeaseResponse> ReplaceDevPackageOwnerAsync(string ownerId, DevPackageOwnerMutationRequest request, CancellationToken token = default) => _devOwners.ReplaceAsync(ownerId, request, token);
    public Task<DevPackageOwnerLeaseResponse> HeartbeatDevPackageOwnerAsync(string ownerId, DevPackageOwnerHeartbeatRequest request, CancellationToken token = default) => _devOwners.HeartbeatAsync(ownerId, request, token);
    public Task ReleaseDevPackageOwnerAsync(string ownerId, DevPackageOwnerReleaseRequest request, CancellationToken token = default) => _devOwners.ReleaseAsync(ownerId, request, token);
    public Task ReapDevPackageOwnersAsync(CancellationToken token = default) => _devOwners.ReapExpiredAsync(token);
    public bool ContainsDevPackageOwner(string ownerId) => _devOwners.ContainsOwner(ownerId);
    public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken token = default) => _installed.GetInstalledAsync(token);
    public Task<string?> TryResolvePackageAssetPathAsync(string packageId, string assetPath, CancellationToken token = default) => _ui.TryResolveAssetPathAsync(packageId, assetPath, token);
    public IReadOnlyList<PackageSettingsSchemaDescriptor> GetSettingsSchemas() => _settings.GetSchemas();
    public Task<PackageSettingsValuesResponse?> GetSettingsValuesAsync(string packageId, CancellationToken token = default) => _settings.GetValuesAsync(packageId, token);
    public Task<bool> SaveSettingsValuesAsync(string packageId, UpdatePackageSettingsRequest request, CancellationToken token = default) => _settings.SaveValuesAsync(packageId, request, token);
    public Task<PackageAuthStatusResponse?> GetPackageAuthStatusAsync(string packageId, CancellationToken token = default) => _auth.GetStatusAsync(packageId, token);
    public Task<PackageAuthSessionStartResponse?> StartPackageAuthAsync(string packageId, PackageCallbackServer server, CancellationToken token = default) => _auth.StartAsync(packageId, server, token);
    public PackageAuthSessionStatusResponse? GetPackageAuthSessionStatus(string packageId, string sessionId) => _auth.GetSessionStatus(packageId, sessionId);
    public Task<PackageAuthStatusResponse?> DisconnectPackageAsync(string packageId, CancellationToken token = default) => _auth.DisconnectAsync(packageId, token);
    public bool ReportPackageFault(string packageId, ReportPackageFaultRequest request) => _faults.Report(packageId, request);
    public Task<PackageLifecycleOperationResult> LoadInstalledPackagesAsync(CancellationToken token = default) => _installed.LoadInstalledPackagesAsync(token);
    public Task<PackageOperationResult> SetInstalledPackageEnabledAsync(string packageId, bool isEnabled, CancellationToken token = default) => _installed.SetEnabledAsync(packageId, isEnabled, token);
    public Task<PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken token = default) => _installed.UninstallAsync(packageId, token);
    internal ActivePackageSession ActiveSession => _owner.State.ActiveSession;
    internal ActivePackageSession? GetStagedLifecycleSession(string stageId) => _sessions.GetStagedSession(stageId);
    internal ActivePackageSession? GetStagedStoreSession(string stageId) => _installed.GetStagedSession(stageId);
    public Task InitializeAsync(CancellationToken token = default) => _installed.InitializeAsync(token);
    public async Task ShutdownAsync()
    {
        _stackImport.Dispose();
        await _devWatcher.DisposeAsync();
        await _installed.ShutdownAsync(_sessions);
    }
    public Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken token = default) => _stackExport.ListItemsAsync(token);
    public Task<RuntimeStackExportResponse> ExportStackAsync(RuntimeStackExportRequest request, CancellationToken token = default) => _stackExport.ExportAsync(request, token);
    public Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(RuntimeStackImportPreviewRequest request, CancellationToken token = default) => _stackImport.PreviewAsync(request, token);
    public Task<RuntimeStackImportResponse> ImportStackAsync(RuntimeStackImportRequest request, CancellationToken token = default) => _stackImport.ImportAsync(request, token);

    public async Task<PackageSessionOperationResult> LoadPackageSessionAsync(PackageSessionLoadRequest request, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(request.PackageId)) return PackageSessionOperationResult.Failed("A package id is required.");
        if (request.SourceKind != PackageSourceKind.Installed) return PackageSessionOperationResult.Failed("Dev package paths are Runtime startup inputs and cannot be loaded through the Runtime API.");
        return await ToSessionResultAsync(await _installed.SetEnabledAsync(request.PackageId.Trim(), true, token), request.PackageId, token);
    }

    public async Task<PackageSessionOperationResult> UnloadPackageSessionAsync(string packageId, PackageSourceKind sourceKind, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return PackageSessionOperationResult.Failed("A package id is required.");
        if (sourceKind == PackageSourceKind.Dev) return await _sessions.UnloadDevPackageAsync(packageId, token);
        if (sourceKind != PackageSourceKind.Installed) return PackageSessionOperationResult.Failed($"Unsupported package session source kind '{sourceKind}'.");
        return await ToSessionResultAsync(await _installed.SetEnabledAsync(packageId, false, token), packageId, token);
    }

    public Task<PackageDataValueResponse?> GetPackageStateAsync(string packageId, string key, CancellationToken token = default) => _data.GetStateAsync(packageId, key, token);
    public Task<IReadOnlyList<string>?> ListPackageStateKeysAsync(string packageId, string? prefix, CancellationToken token = default) => _data.ListStateKeysAsync(packageId, prefix, token);
    public Task<bool> SetPackageStateAsync(string packageId, string key, string value, CancellationToken token = default) => _data.SetStateAsync(packageId, key, value, token);
    public Task<bool> DeletePackageStateAsync(string packageId, string key, CancellationToken token = default) => _data.DeleteStateAsync(packageId, key, token);
    public Task<PackageSettingValueResponse?> GetPackageSettingAsync(string packageId, string key, CancellationToken token = default) => _settings.GetValueAsync(packageId, key, token);
    public Task<bool> SetPackageSettingAsync(string packageId, string key, string value, CancellationToken token = default) => _settings.SetValueAsync(packageId, key, value, token);
    public Task<bool> DeletePackageSettingAsync(string packageId, string key, CancellationToken token = default) => _settings.DeleteValueAsync(packageId, key, token);
    public Task<PackageDataValueResponse?> GetPackageSecretAsync(string packageId, string key, CancellationToken token = default) => _data.GetSecretAsync(packageId, key, token);
    public Task<bool> SetPackageSecretAsync(string packageId, string key, string value, CancellationToken token = default) => _data.SetSecretAsync(packageId, key, value, token);
    public Task<bool> DeletePackageSecretAsync(string packageId, string key, CancellationToken token = default) => _data.DeleteSecretAsync(packageId, key, token);
    public Task<byte[]?> ReadPackageFileAsync(string packageId, string path, int maxLength, CancellationToken token = default) => _data.ReadFileAsync(packageId, path, maxLength, token);
    public Task<bool> WritePackageFileAsync(string packageId, string path, byte[] contents, CancellationToken token = default) => _data.WriteFileAsync(packageId, path, contents, token);
    public Task<bool> DeletePackageFileAsync(string packageId, string path, CancellationToken token = default) => _data.DeleteFileAsync(packageId, path, token);

    private async Task<PackageSessionOperationResult> ToSessionResultAsync(PackageOperationResult result, string packageId, CancellationToken token)
        => new PackageSessionOperationResult(result.Success, result.Message, result.Warnings, result.Errors, result.ImpactedPackageIds.Count == 0 ? [packageId] : result.ImpactedPackageIds, await _sessions.GetStatusAsync(packageId, token))
        {
            CommittedStamp = result.CommittedStamp,
        };
}
