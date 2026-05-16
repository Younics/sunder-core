using Sunder.Protocol;
using Sunder.Registry.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

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

public sealed class PackageOperationService
{
    public const string PackageStoreGroupKey = "package-store";
    private readonly BackgroundProcessQueueService _backgroundProcesses;
    private readonly IRuntimeApiClientFactory _runtimeApiClientFactory;
    private readonly Func<Uri, IRegistryApiClient> _createRegistryClient;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _applyPackageLifecycleChangesAsync;
    private readonly RegistryPackageInstallService _registryInstallService;
    private readonly NotificationCenterService _notificationCenter;

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
        _applyPackageLifecycleChangesAsync = applyPackageLifecycleChangesAsync;
        _notificationCenter = notificationCenter;
        _registryInstallService = registryInstallService ?? new RegistryPackageInstallService();
        _createRegistryClient = registryClientFactory ?? (registryUrl => new RegistryApiClient(registryUrl));
        _backgroundProcesses.ProcessChanged += BackgroundProcesses_OnProcessChanged;
    }

    public event EventHandler<PackageOperationChangedEventArgs>? OperationChanged;

    public IReadOnlyList<BackgroundProcessSnapshot> ListOperations()
        => _backgroundProcesses.ListProcesses()
            .Where(IsPackageOperation)
            .ToArray();

    public BackgroundProcessSnapshot? GetActiveOperationForPackage(string packageId)
        => ListOperations()
            .Where(snapshot => snapshot.IsActive)
            .FirstOrDefault(snapshot => PackageOperationMetadata.TryCreate(snapshot.Metadata, out var metadata)
                                        && string.Equals(metadata.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    public BackgroundProcessSnapshot? GetActivePackageStoreOperation()
        => ListOperations().FirstOrDefault(snapshot => snapshot.IsActive);

    public bool CancelOperation(Guid processId)
        => _backgroundProcesses.Cancel(processId);

    public bool CancelActiveOperationForPackage(string packageId)
    {
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
        if (TryReturnActivePackageOperation(packageId, out var existing))
        {
            return existing;
        }

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

                await FinishRegistryOperationAsync(context, result, "Package installed", $"{packageId} was installed from the marketplace.").ConfigureAwait(false);
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
                await FinishLocalOperationAsync(context, result, "Package installed", "Package installed from disk.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueMarketplaceUpdate(
        string packageId,
        string displayName,
        string version,
        Uri registryUrl)
    {
        if (TryReturnActivePackageOperation(packageId, out var existing))
        {
            return existing;
        }

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

                await FinishRegistryOperationAsync(context, result, "Package updated", $"{packageId} was updated to {version}.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueUninstall(string packageId, string displayName)
    {
        if (TryReturnActivePackageOperation(packageId, out var existing))
        {
            return existing;
        }

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
                await FinishLocalOperationAsync(context, result, "Package uninstalled", $"{packageId} was uninstalled.").ConfigureAwait(false);
            });
    }

    public BackgroundProcessSnapshot EnqueueUpdateAll(Uri registryUrl)
    {
        var activeUpdateAll = ListOperations().FirstOrDefault(snapshot => snapshot.IsActive
            && PackageOperationMetadata.TryCreate(snapshot.Metadata, out var metadata)
            && metadata.Kind == PackageOperationKind.UpdateAll);
        if (activeUpdateAll is not null)
        {
            return activeUpdateAll;
        }

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

                await FinishRegistryOperationAsync(context, result, "Packages updated", "Installed packages were updated.").ConfigureAwait(false);
            });
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
        => await _backgroundProcesses.CancelAllAsync(cancellationToken).ConfigureAwait(false);

    private BackgroundProcessSnapshot EnqueuePackageStoreOperation(
        string? packageId,
        string displayName,
        PackageOperationKind kind,
        string title,
        bool canCancel,
        Func<BackgroundProcessContext, Task> executeAsync)
    {
        return _backgroundProcesses.Enqueue(new BackgroundProcessRequest(
            title,
            PackageStoreGroupKey,
            BackgroundProcessIndicator.Packages,
            BackgroundProcessConcurrencyMode.SequentialWithinGroup,
            canCancel,
            executeAsync,
            new PackageOperationMetadata(packageId, kind, displayName).ToMetadata()));
    }

    private bool TryReturnActivePackageOperation(string packageId, out BackgroundProcessSnapshot snapshot)
    {
        snapshot = GetActiveOperationForPackage(packageId)!;
        return snapshot is not null;
    }

    private async Task FinishRegistryOperationAsync(
        BackgroundProcessContext context,
        RegistryPackageInstallExecutionResult result,
        string successTitle,
        string successFallbackMessage)
    {
        if (!result.Success)
        {
            var message = result.Errors.FirstOrDefault() ?? result.Message ?? "Package operation failed.";
            await PublishFailureAsync(message).ConfigureAwait(false);
            throw new InvalidOperationException(message);
        }

        if (result.ImpactedPackageIds.Count > 0)
        {
            context.ReportProgress(92, "Applying package changes to the running shell...");
            await ApplyPackageLifecycleChangesAsync(result.ImpactedPackageIds, context.CancellationToken).ConfigureAwait(false);
        }

        context.ReportProgress(100, result.Message ?? successFallbackMessage);
        if (result.ImpactedPackageIds.Count > 0)
        {
            await PublishSuccessAsync(successTitle, BuildRegistryPackageOperationToastMessage(result, successFallbackMessage)).ConfigureAwait(false);
        }
    }

    private async Task FinishLocalOperationAsync(
        BackgroundProcessContext context,
        PackageOperationResult result,
        string successTitle,
        string successFallbackMessage)
    {
        if (!result.Success)
        {
            var message = result.Errors.FirstOrDefault() ?? result.Message ?? "Package operation failed.";
            await PublishFailureAsync(message).ConfigureAwait(false);
            throw new InvalidOperationException(message);
        }

        if (result.ImpactedPackageIds.Count > 0)
        {
            context.ReportProgress(92, "Applying package changes to the running shell...");
            await ApplyPackageLifecycleChangesAsync(result.ImpactedPackageIds, context.CancellationToken).ConfigureAwait(false);
        }

        context.ReportProgress(100, result.Message ?? successFallbackMessage);
        if (result.ImpactedPackageIds.Count > 0)
        {
            await PublishSuccessAsync(successTitle, string.IsNullOrWhiteSpace(result.Message) ? successFallbackMessage : result.Message.Trim()).ConfigureAwait(false);
        }
    }

    private async Task ApplyPackageLifecycleChangesAsync(IReadOnlyList<string> impactedPackageIds, CancellationToken cancellationToken)
    {
        try
        {
            await _applyPackageLifecycleChangesAsync(impactedPackageIds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Package store updated, but the running shell did not apply the change.", ex);
        }
    }

    private static void ReportRegistryProgress(BackgroundProcessContext context, RegistryPackageInstallProgress progress)
    {
        if (progress.ProgressPercent is null)
        {
            context.ReportIndeterminate(progress.StatusText);
            return;
        }

        context.ReportProgress(progress.ProgressPercent.Value, progress.StatusText);
    }

    private async Task PublishSuccessAsync(string title, string message)
        => await _notificationCenter.PublishAsync(
            "sunder.app",
            "Sunder",
            new PackageNotificationRequest(
                title,
                message,
                PackageNotificationDisplayMode.ToastOnly,
                PackageNotificationSeverity.Success)).ConfigureAwait(false);

    private async Task PublishFailureAsync(string message)
        => await _notificationCenter.PublishAsync(
            "sunder.app",
            "Sunder",
            new PackageNotificationRequest(
                "Package operation failed",
                string.IsNullOrWhiteSpace(message) ? "Package operation failed." : message,
                PackageNotificationDisplayMode.ToastAndTray,
                PackageNotificationSeverity.Error)).ConfigureAwait(false);

    private static string BuildRegistryPackageOperationToastMessage(RegistryPackageInstallExecutionResult result, string fallback)
    {
        if (result.PlanItems.Count == 1)
        {
            var item = result.PlanItems[0];
            if (item.CurrentVersion is null)
            {
                return $"{item.PackageId} {item.Version} was installed.";
            }

            if (string.Equals(item.CurrentVersion, item.Version, StringComparison.OrdinalIgnoreCase))
            {
                return $"{item.PackageId} {item.Version} was reinstalled.";
            }

            return $"{item.PackageId} was updated from {item.CurrentVersion} to {item.Version}.";
        }

        var packageChangeCount = result.PlanItems
            .Select(item => item.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (packageChangeCount > 0)
        {
            return $"{packageChangeCount} package change(s) completed.";
        }

        return string.IsNullOrWhiteSpace(result.Message) ? fallback : result.Message.Trim();
    }

    private void BackgroundProcesses_OnProcessChanged(object? sender, BackgroundProcessChangedEventArgs e)
    {
        if (IsPackageOperation(e.Snapshot))
        {
            OperationChanged?.Invoke(this, new PackageOperationChangedEventArgs(e.Snapshot));
        }
    }

    private static bool IsPackageOperation(BackgroundProcessSnapshot snapshot)
        => PackageOperationMetadata.TryCreate(snapshot.Metadata, out _);
}
