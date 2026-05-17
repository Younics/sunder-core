using Sunder.Protocol;
using Sunder.Registry.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public enum PackageOperationKind
{
    InstallMarketplace,
    UpdateMarketplace,
    UpdateAll,
    InstallLocal,
    Enable,
    Disable,
    Uninstall,
}

public sealed record PackageOperationMetadata(
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

public sealed class PackageOperationChangedEventArgs(BackgroundProcessSnapshot snapshot) : EventArgs
{
    public BackgroundProcessSnapshot Snapshot { get; } = snapshot;

    public PackageOperationMetadata Metadata => PackageOperationMetadata.TryCreate(Snapshot.Metadata, out var metadata)
        ? metadata
        : throw new InvalidOperationException("Background process is not a package operation.");
}

public sealed class PackageOperationService : IDisposable
{
    public const string PackageStoreGroupKey = "package-store";
    private readonly BackgroundProcessQueueService _backgroundProcesses;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory;
    private readonly Func<Uri, IRegistryApiClient> _createRegistryClient;
    private readonly RegistryPackageInstallService _registryInstallService;
    private readonly PackageOperationFinalizer _operationFinalizer;
    private readonly object _operationGate = new();
    private volatile bool _disposed;

    public PackageOperationService(
        BackgroundProcessQueueService backgroundProcesses,
        IRuntimeApiClientFactory runtimeApiClientFactory,
        Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync,
        NotificationCenterService notificationCenter,
        RegistryPackageInstallService? registryInstallService = null,
        Func<Uri, IRegistryApiClient>? registryClientFactory = null)
    {
        _backgroundProcesses = backgroundProcesses;
        _runtimeApiClientFactory = runtimeApiClientFactory;
        _operationFinalizer = new PackageOperationFinalizer(applyPackageLifecycleChangesAsync, notificationCenter);
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
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
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

    public BackgroundProcessSnapshot EnqueueLocalInstall(string packagePath)
    {
        var displayName = Path.GetFileName(packagePath);
        return EnqueuePackageStoreOperation(
            packageId: null,
            displayName,
            PackageOperationKind.InstallLocal,
            $"Install {displayName}",
            canCancel: true,
            async context =>
            {
                context.ReportIndeterminate($"Installing {displayName}...");
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
                var result = await runtimeApiClient.InstallPackageFromPathAsync(packagePath, context.CancellationToken).ConfigureAwait(false);
                await _operationFinalizer.FinishLocalOperationAsync(context, result, "Package installed", "Package installed from disk.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueMarketplaceUpdate(
        string packageId,
        string displayName,
        string version,
        Uri registryUrl)
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
                using var registryClient = _createRegistryClient(registryUrl);
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
                var result = await _registryInstallService.InstallPackageAsync(
                    packageId,
                    version,
                    tag: null,
                    allowDowngrade: false,
                    reinstall: false,
                    registryClient,
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
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
                var result = await runtimeApiClient.EnableInstalledPackageAsync(packageId, context.CancellationToken).ConfigureAwait(false);
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
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
                var result = await runtimeApiClient.DisableInstalledPackageAsync(packageId, context.CancellationToken).ConfigureAwait(false);
                await _operationFinalizer.FinishLocalOperationAsync(context, result, "Package disabled", $"{packageId} was disabled.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueUninstall(string packageId, string displayName)
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
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
                var result = await runtimeApiClient.UninstallPackageAsync(packageId, context.CancellationToken).ConfigureAwait(false);
                await _operationFinalizer.FinishLocalOperationAsync(context, result, "Package uninstalled", $"{packageId} was uninstalled.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueUpdateAll(Uri registryUrl)
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
                using var registryClient = _createRegistryClient(registryUrl);
                using var runtimeApiClient = _runtimeApiClientFactory.CreateClient();
                var result = await _registryInstallService.UpdateAllAsync(
                    registryClient,
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
        if (!_disposed && IsPackageOperation(e.Snapshot))
        {
            OperationChanged?.Invoke(this, new PackageOperationChangedEventArgs(e.Snapshot));
        }
    }

    private static bool IsPackageOperation(BackgroundProcessSnapshot snapshot)
        => PackageOperationMetadata.TryCreate(snapshot.Metadata, out _);
}
