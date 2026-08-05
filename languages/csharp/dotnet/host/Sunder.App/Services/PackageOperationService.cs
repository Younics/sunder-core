using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal enum PackageOperationKind
{
    InstallMarketplace,
    UpdateMarketplace,
    UpdateAll,
    InstallLocal,
    Enable,
    Disable,
    Uninstall,
}

internal sealed record PackageOperationMetadata(
    string? PackageId,
    PackageOperationKind Kind,
    string DisplayName)
{
    private const string OperationMetadataKey = "sunder.packageOperation";
    private const string PackageIdMetadataKey = "packageId";
    private const string KindMetadataKey = "kind";
    private const string DisplayNameMetadataKey = "displayName";

    public static bool TryCreate(IReadOnlyDictionary<string, string> metadata, out PackageOperationMetadata operationMetadata)
    {
        if (metadata.TryGetValue(OperationMetadataKey, out var operationMarker)
            && bool.TryParse(operationMarker, out var isPackageOperation)
            && isPackageOperation
            && metadata.TryGetValue(KindMetadataKey, out var kindValue)
            && Enum.TryParse<PackageOperationKind>(kindValue, ignoreCase: true, out var kind)
            && metadata.TryGetValue(DisplayNameMetadataKey, out var displayName))
        {
            operationMetadata = new PackageOperationMetadata(
                metadata.TryGetValue(PackageIdMetadataKey, out var packageId) && !string.IsNullOrWhiteSpace(packageId) ? packageId : null,
                kind,
                displayName);
            return true;
        }

        operationMetadata = null!;
        return false;
    }

    public IReadOnlyDictionary<string, string> ToMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [OperationMetadataKey] = bool.TrueString,
            [KindMetadataKey] = Kind.ToString(),
            [DisplayNameMetadataKey] = DisplayName,
        };

        if (!string.IsNullOrWhiteSpace(PackageId))
        {
            metadata[PackageIdMetadataKey] = PackageId;
        }

        return metadata;
    }
}

internal sealed class PackageOperationChangedEventArgs(BackgroundProcessSnapshot snapshot) : EventArgs
{
    public BackgroundProcessSnapshot Snapshot { get; } = snapshot;

    public PackageOperationMetadata Metadata => PackageOperationMetadata.TryCreate(Snapshot.Metadata, out var metadata)
        ? metadata
        : throw new InvalidOperationException("Background process is not a package operation.");
}

internal sealed class PackageOperationService : IPackageOperationExecutor, IDisposable
{
    public const string PackageStoreGroupKey = "package-store";
    private static readonly TimeSpan DefaultPresentationWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommitReconciliationTimeout = TimeSpan.FromSeconds(2);
    private readonly BackgroundProcessQueueService _backgroundProcesses;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory;
    private readonly Func<Uri, IRegistryClient> _createRegistryClient;
    private readonly RegistryPackageInstallService _registryInstallService;
    private readonly PackageOperationFinalizer _operationFinalizer;
    private readonly object _operationGate = new();
    private readonly Dictionary<Guid, string> _temporaryLocalArchives = [];
    private volatile bool _disposed;

    public PackageOperationService(
        BackgroundProcessQueueService backgroundProcesses,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        Func<RuntimePackageStamp, CancellationToken, Task> waitUntilPresentationAppliedAsync,
        NotificationCenterService notificationCenter,
        RegistryPackageInstallService? registryInstallService = null,
        Func<Uri, IRegistryClient>? registryClientFactory = null,
        Func<RuntimePackageStamp, CancellationToken, Task<PackagePresentationResult>>? waitForPresentationAsync = null,
        TimeSpan? presentationWaitTimeout = null)
    {
        _backgroundProcesses = backgroundProcesses;
        _runtimeApiClientFactory = runtimeApiClientFactory;
        _operationFinalizer = new PackageOperationFinalizer(
            waitForPresentationAsync ?? (async (stamp, cancellationToken) =>
            {
                try
                {
                    await waitUntilPresentationAppliedAsync(stamp, cancellationToken).ConfigureAwait(false);
                    return PackagePresentationResult.Applied;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return PackagePresentationResult.Failed(ex.Message, ex);
                }
            }),
            notificationCenter,
            presentationWaitTimeout ?? DefaultPresentationWaitTimeout);
        _registryInstallService = registryInstallService ?? new RegistryPackageInstallService();
        _createRegistryClient = registryClientFactory ?? (registryUrl => new RegistryApiClient(registryUrl));
        _backgroundProcesses.ProcessChanged += BackgroundProcesses_OnProcessChanged;
    }

    public event EventHandler<PackageOperationChangedEventArgs>? OperationChanged;

    public void Dispose()
    {
        lock (_operationGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _backgroundProcesses.ProcessChanged -= BackgroundProcesses_OnProcessChanged;
        GC.SuppressFinalize(this);
    }

    public IReadOnlyList<BackgroundProcessSnapshot> ListOperations()
    {
        ThrowIfDisposed();
        return ListOperationsCore();
    }

    public BackgroundProcessSnapshot? GetActiveOperationForPackage(string packageId)
    {
        ThrowIfDisposed();
        return GetActiveOperationForPackageCore(packageId);
    }

    public BackgroundProcessSnapshot? GetActivePackageStoreOperation()
    {
        ThrowIfDisposed();
        return ListOperationsCore().FirstOrDefault(snapshot => snapshot.IsActive);
    }

    public bool CancelOperation(Guid processId)
    {
        ThrowIfDisposed();
        var snapshot = _backgroundProcesses.GetProcess(processId);
        return snapshot is not null
               && IsPackageOperation(snapshot)
               && _backgroundProcesses.Cancel(processId);
    }

    public bool CancelActiveOperationForPackage(string packageId)
    {
        ThrowIfDisposed();
        var operation = GetActiveOperationForPackage(packageId);
        return operation is not null && _backgroundProcesses.Cancel(operation.ProcessId);
    }

    public BackgroundProcessSnapshot EnqueueMarketplaceInstall(
        string packageId,
        string displayName,
        Uri registryUrl,
        string? version = null,
        string? tag = "latest")
    {
        return EnqueuePackageStoreOperation(
            packageId,
            displayName,
            PackageOperationKind.InstallMarketplace,
            $"Install {displayName}",
            canCancel: true,
            async context =>
            {
                context.ReportProgress(0, $"Installing {displayName}...");
                using var registryClient = _createRegistryClient(registryUrl);
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient<IRuntimePackageChangeClient>();
                var result = await _registryInstallService.InstallPackageAsync(
                    packageId,
                    version,
                    tag,
                    allowDowngrade: false,
                    reinstall: false,
                    registryClient,
                    runtimeApiClient,
                    progress => ReportRegistryProgress(context, progress),
                    context.CancellationToken).ConfigureAwait(false);

                await _operationFinalizer.FinishRegistryOperationAsync(context, result, "Package installed", $"{packageId} was installed from the marketplace.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueLocalInstall(
        string packagePath,
        string expectedSha256,
        bool deleteAfterUse)
    {
        var displayName = Path.GetFileName(packagePath);
        BackgroundProcessSnapshot operation;
        try
        {
            operation = EnqueuePackageStoreOperation(
                packageId: null,
                displayName,
                PackageOperationKind.InstallLocal,
                $"Install {displayName}",
                canCancel: true,
                async context =>
                {
                    context.ReportIndeterminate($"Installing {displayName}...");
                    using var runtimeApiClient = _runtimeApiClientFactory.CreateClient<IRuntimePackageChangeClient>();
                    var upload = await runtimeApiClient.UploadPackageAsync(
                        packagePath,
                        context.CancellationToken).ConfigureAwait(false);
                    if (!string.Equals(upload.ContentHash, expectedSha256, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The package archive changed after review. Select it again before installing.");
                    }
                    var result = await StageCommitPackageStoreAsync(
                        runtimeApiClient,
                        new PackageStoreStageRequest([new PackageStoreMutationRequest(PackageStoreMutationKind.Install, UploadId: upload.UploadId)]),
                        context.CancellationToken).ConfigureAwait(false);
                    await _operationFinalizer.FinishLocalOperationAsync(context, result, "Package installed", "Package installed from disk.").ConfigureAwait(false);
                });
        }
        catch
        {
            if (deleteAfterUse)
            {
                PackageArchivePicker.DeleteReviewSnapshot(packagePath);
            }
            throw;
        }

        if (deleteAfterUse)
        {
            TrackTemporaryLocalArchive(operation.ProcessId, packagePath);
        }
        return operation;
    }

    public BackgroundProcessSnapshot EnqueueMarketplaceUpdate(
        string packageId,
        string displayName,
        string version)
    {
        return EnqueuePackageStoreOperation(
            packageId,
            displayName,
            PackageOperationKind.UpdateMarketplace,
            $"Update {displayName}",
            canCancel: true,
            async context =>
            {
                context.ReportProgress(0, $"Updating {displayName}...");
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient<IRuntimePackageChangeClient>();
                var result = await _registryInstallService.UpdatePackageAsync(
                    packageId,
                    runtimeApiClient,
                    progress => ReportRegistryProgress(context, progress),
                    context.CancellationToken).ConfigureAwait(false);

                await _operationFinalizer.FinishRegistryOperationAsync(context, result, "Package updated", $"{packageId} was updated to {version}.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueEnable(string packageId, string displayName)
    {
        return EnqueuePackageStoreOperation(
            packageId,
            displayName,
            PackageOperationKind.Enable,
            $"Enable {displayName}",
            canCancel: false,
            async context =>
            {
                context.ReportIndeterminate($"Enabling {displayName}...");
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient<IRuntimePackageChangeClient>();
                var result = await StageCommitPackageStoreAsync(
                    runtimeApiClient,
                    new PackageStoreStageRequest([new PackageStoreMutationRequest(PackageStoreMutationKind.Enable, packageId)]),
                    context.CancellationToken).ConfigureAwait(false);
                await _operationFinalizer.FinishLocalOperationAsync(context, result, "Package enabled", $"{packageId} was enabled.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueDisable(string packageId, string displayName)
    {
        return EnqueuePackageStoreOperation(
            packageId,
            displayName,
            PackageOperationKind.Disable,
            $"Disable {displayName}",
            canCancel: false,
            async context =>
            {
                context.ReportIndeterminate($"Disabling {displayName}...");
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient<IRuntimePackageChangeClient>();
                var result = await StageCommitPackageStoreAsync(
                    runtimeApiClient,
                    new PackageStoreStageRequest([new PackageStoreMutationRequest(PackageStoreMutationKind.Disable, packageId)]),
                    context.CancellationToken).ConfigureAwait(false);
                await _operationFinalizer.FinishLocalOperationAsync(context, result, "Package disabled", $"{packageId} was disabled.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueUninstall(
        string packageId,
        string displayName,
        bool allowCascade = false)
    {
        return EnqueuePackageStoreOperation(
            packageId,
            displayName,
            PackageOperationKind.Uninstall,
            $"Uninstall {displayName}",
            canCancel: false,
            async context =>
            {
                context.ReportIndeterminate($"Uninstalling {displayName}...");
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient<IRuntimePackageChangeClient>();
                var plan = await runtimeApiClient.GetPackageUninstallPlanAsync(
                    packageId,
                    context.CancellationToken).ConfigureAwait(false);
                var result = await StageCommitPackageStoreAsync(
                    runtimeApiClient,
                    new PackageStoreStageRequest([new PackageStoreMutationRequest(
                        PackageStoreMutationKind.Uninstall,
                        packageId,
                        AllowCascade: allowCascade,
                        ConfirmationToken: plan.ConfirmationToken)]),
                    context.CancellationToken).ConfigureAwait(false);
                await _operationFinalizer.FinishLocalOperationAsync(context, result, "Package uninstalled", $"{packageId} was uninstalled.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueUpdateAll()
    {
        return EnqueuePackageStoreOperation(
            packageId: null,
            displayName: "All packages",
            PackageOperationKind.UpdateAll,
            "Update all packages",
            canCancel: true,
            async context =>
            {
                context.ReportProgress(0, "Updating installed packages...");
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient<IRuntimePackageChangeClient>();
                var result = await _registryInstallService.UpdateAllAsync(
                    runtimeApiClient,
                    progress => ReportRegistryProgress(context, progress),
                    context.CancellationToken).ConfigureAwait(false);

                await _operationFinalizer.FinishRegistryOperationAsync(context, result, "Packages updated", "Installed packages were updated.").ConfigureAwait(false);
            });
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _backgroundProcesses.CancelMatchingAsync(IsPackageOperation, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PackageOperationResult> StageCommitPackageStoreAsync(
        IRuntimePackageStoreClient runtimeApiClient,
        PackageStoreStageRequest request,
        CancellationToken cancellationToken)
    {
        var stage = await runtimeApiClient.StagePackageStoreChangesAsync(request, cancellationToken).ConfigureAwait(false);
        if (!stage.Success || stage.StageId is null)
        {
            return stage.OperationResult;
        }

        var committed = false;
        try
        {
            var commit = await runtimeApiClient.CommitPackageStoreStageAsync(stage.StageId, cancellationToken).ConfigureAwait(false);
            committed = true;
            return commit;
        }
        catch (OperationCanceledException ex)
        {
            var reconciliation = await ReconcileAmbiguousCommitAsync(
                runtimeApiClient,
                stage,
                ex).ConfigureAwait(false);
            if (reconciliation.Result is not null)
            {
                return reconciliation.Result;
            }
            if (!committed && reconciliation.CanDiscard)
            {
                await TryDiscardStageAsync(runtimeApiClient, stage.StageId, ex).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            var reconciliation = await ReconcileAmbiguousCommitAsync(
                runtimeApiClient,
                stage,
                ex).ConfigureAwait(false);
            if (reconciliation.Result is not null)
            {
                return reconciliation.Result;
            }
            if (!committed && reconciliation.CanDiscard)
            {
                await TryDiscardStageAsync(runtimeApiClient, stage.StageId, ex).ConfigureAwait(false);
            }

            return new PackageOperationResult(false, ex.Message, RuntimeSessionApplied: false, RequiresAppRestart: false, stage.Warnings, [ex.Message])
            {
                ImpactedPackageIds = stage.ImpactedPackageIds,
            };
        }
    }

    private static async Task<CommitReconciliation> ReconcileAmbiguousCommitAsync(
        IRuntimePackageStoreClient runtimeApiClient,
        PackageStoreStageResult stage,
        Exception primaryException)
    {
        try
        {
            using var deadline = new CancellationTokenSource(CommitReconciliationTimeout);
            var status = await runtimeApiClient.GetPackageStoreStageStatusAsync(
                stage.StageId!,
                deadline.Token).ConfigureAwait(false);
            if (status.Kind != RuntimePackageStageKind.PackageStore)
            {
                return CommitReconciliation.NotDiscardable;
            }
            if (status.State == RuntimePackageStageState.Committed)
            {
                return new CommitReconciliation(
                    stage.OperationResult with
                    {
                        Success = true,
                        Message = status.Message ?? stage.OperationResult.Message,
                        RuntimeSessionApplied = status.RuntimeSessionApplied,
                        RequiresAppRestart = false,
                        Errors = [],
                        CommittedStamp = status.CommittedStamp,
                        StoreCommitted = true,
                        RuntimeSessionReconciliationPending = status.ReconciliationPending,
                    },
                    CanDiscard: false);
            }

            return status.State == RuntimePackageStageState.Pending
                ? CommitReconciliation.Discardable
                : CommitReconciliation.NotDiscardable;
        }
        catch (Exception reconciliationException)
        {
            AppSessionLog.WriteError(
                $"Could not reconcile package store stage '{stage.StageId}' after ambiguous commit failure '{primaryException.Message}'. The stage was left intact.",
                reconciliationException);
            return CommitReconciliation.NotDiscardable;
        }
    }

    private static async Task TryDiscardStageAsync(
        IRuntimePackageStoreClient runtimeApiClient,
        string stageId,
        Exception primaryException)
    {
        try
        {
            await runtimeApiClient.DiscardPackageStoreStageAsync(stageId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception discardException)
        {
            AppSessionLog.WriteError(
                $"Failed to discard package store stage '{stageId}' after '{primaryException.Message}'. The primary operation failure is preserved.",
                discardException);
        }
    }

    private BackgroundProcessSnapshot EnqueuePackageStoreOperation(
        string? packageId,
        string displayName,
        PackageOperationKind kind,
        string title,
        bool canCancel,
        Func<BackgroundProcessContext, Task> executeAsync)
    {
        lock (_operationGate)
        {
            ThrowIfDisposed();

            if (!string.IsNullOrWhiteSpace(packageId)
                && GetActiveOperationForPackageCore(packageId) is { } packageOperation)
            {
                return packageOperation;
            }

            if (kind == PackageOperationKind.UpdateAll
                && GetActiveUpdateAllOperationCore() is { } updateAllOperation)
            {
                return updateAllOperation;
            }

            return _backgroundProcesses.Enqueue(new BackgroundProcessRequest(
                title,
                PackageStoreGroupKey,
                BackgroundProcessIndicator.Packages,
                BackgroundProcessConcurrencyMode.SequentialWithinGroup,
                canCancel,
                executeAsync,
                new PackageOperationMetadata(packageId, kind, displayName).ToMetadata()));
        }
    }

    private IReadOnlyList<BackgroundProcessSnapshot> ListOperationsCore()
        => _backgroundProcesses.ListProcesses()
            .Where(IsPackageOperation)
            .ToArray();

    private BackgroundProcessSnapshot? GetActiveOperationForPackageCore(string packageId)
        => ListOperationsCore()
            .Where(snapshot => snapshot.IsActive)
            .FirstOrDefault(snapshot => PackageOperationMetadata.TryCreate(snapshot.Metadata, out var metadata)
                                        && string.Equals(metadata.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    private BackgroundProcessSnapshot? GetActiveUpdateAllOperationCore()
        => ListOperationsCore().FirstOrDefault(snapshot => snapshot.IsActive
            && PackageOperationMetadata.TryCreate(snapshot.Metadata, out var metadata)
            && metadata.Kind == PackageOperationKind.UpdateAll);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ReportRegistryProgress(BackgroundProcessContext context, RegistryPackageInstallProgress progress)
    {
        if (progress.ProgressPercent is null)
        {
            context.ReportIndeterminate(progress.StatusText);
            return;
        }

        context.ReportProgress(progress.ProgressPercent.Value, progress.StatusText);
    }

    private void BackgroundProcesses_OnProcessChanged(object? sender, BackgroundProcessChangedEventArgs e)
    {
        if (!e.Snapshot.IsActive)
        {
            CleanupTemporaryLocalArchive(e.Snapshot.ProcessId);
        }
        if (!_disposed && IsPackageOperation(e.Snapshot))
        {
            OperationChanged?.Invoke(this, new PackageOperationChangedEventArgs(e.Snapshot));
        }
    }

    private void TrackTemporaryLocalArchive(Guid processId, string packagePath)
    {
        lock (_operationGate)
        {
            _temporaryLocalArchives[processId] = packagePath;
        }
        if (_backgroundProcesses.GetProcess(processId) is not { IsActive: true })
        {
            CleanupTemporaryLocalArchive(processId);
        }
    }

    private void CleanupTemporaryLocalArchive(Guid processId)
    {
        string? packagePath;
        lock (_operationGate)
        {
            _temporaryLocalArchives.Remove(processId, out packagePath);
        }
        if (packagePath is not null)
        {
            PackageArchivePicker.DeleteReviewSnapshot(packagePath);
        }
    }

    private static bool IsPackageOperation(BackgroundProcessSnapshot snapshot)
        => PackageOperationMetadata.TryCreate(snapshot.Metadata, out _);

    private sealed record CommitReconciliation(PackageOperationResult? Result, bool CanDiscard)
    {
        public static CommitReconciliation Discardable { get; } = new(null, true);
        public static CommitReconciliation NotDiscardable { get; } = new(null, false);
    }
}
