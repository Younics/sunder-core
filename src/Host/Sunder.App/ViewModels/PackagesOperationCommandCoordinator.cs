using Sunder.App.Services;
using Sunder.Protocol;
using Sunder.Registry.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.App.ViewModels;

internal sealed class PackagesOperationCommandCoordinator(
    IRuntimeApiClient runtimeApiClient,
    IPackageArchivePicker packageArchivePicker,
    PackageRegistryClientProvider registryClientProvider,
    RegistryPackageInstallService registryInstallService,
    PackageOperationService? packageOperationService,
    Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync,
    NotificationCenterService? notificationCenter,
    Func<bool> getIsBusy,
    Action<bool> setIsBusy,
    Action<string> setStatusText,
    Action clearWarnings,
    Action<IReadOnlyList<string>> replaceWarnings,
    Action<string> addWarning,
    Func<int> getWarningCount,
    Func<string?, PackageOperationResult?, bool, Task> refreshInstalledAsync,
    Action refreshMarketplaceInstalledBadges,
    Action refreshPackageOperationState,
    Action markInstalledCatalogDirty)
{
    public bool HasActivePackageStoreOperation => packageOperationService?.GetActivePackageStoreOperation()?.IsActive == true;

    public async Task<bool> InstallFromDiskAsync()
    {
        if (getIsBusy())
        {
            return false;
        }

        var packagePath = await packageArchivePicker.PickPackagePathAsync();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return false;
        }

        if (packageOperationService is not null)
        {
            packageOperationService.EnqueueLocalInstall(packagePath);
            MarkQueued($"Queued install for {Path.GetFileName(packagePath)}.");
            return true;
        }

        await ExecuteLocalPackageOperationAsync(
            () => runtimeApiClient.InstallPackageFromPathAsync(packagePath),
            selectedPackageId: null,
            "Package installed",
            "Package installed from disk.");
        return true;
    }

    public async Task EnableInstalledPackageAsync(string packageId, string displayName)
    {
        if (packageOperationService is not null)
        {
            if (HasActivePackageStoreOperation)
            {
                return;
            }

            packageOperationService.EnqueueEnable(packageId, displayName);
            MarkQueued($"Queued enable for {packageId}.");
            return;
        }

        if (getIsBusy())
        {
            return;
        }

        await ExecuteLocalPackageOperationAsync(
            () => runtimeApiClient.EnableInstalledPackageAsync(packageId),
            packageId,
            "Package enabled",
            $"{packageId} was enabled.");
    }

    public async Task DisableInstalledPackageAsync(string packageId, string displayName)
    {
        if (packageOperationService is not null)
        {
            if (HasActivePackageStoreOperation)
            {
                return;
            }

            packageOperationService.EnqueueDisable(packageId, displayName);
            MarkQueued($"Queued disable for {packageId}.");
            return;
        }

        if (getIsBusy())
        {
            return;
        }

        await ExecuteLocalPackageOperationAsync(
            () => runtimeApiClient.DisableInstalledPackageAsync(packageId),
            packageId,
            "Package disabled",
            $"{packageId} was disabled.");
    }

    public async Task UninstallPackageAsync(string packageId, string displayName)
    {
        if (packageOperationService is not null)
        {
            packageOperationService.EnqueueUninstall(packageId, displayName);
            MarkQueued($"Queued uninstall for {packageId}.");
            return;
        }

        if (getIsBusy())
        {
            return;
        }

        await ExecuteLocalPackageOperationAsync(
            () => runtimeApiClient.UninstallPackageAsync(packageId),
            packageId,
            "Package uninstalled",
            $"{packageId} was uninstalled.");
    }

    public async Task InstallMarketplacePackageAsync(string packageId, string displayName, string selectedVersion)
    {
        if (packageOperationService is not null)
        {
            if (!TryResolveRegistryUrl(out var registryUrl) || registryUrl is null)
            {
                return;
            }

            packageOperationService.EnqueueMarketplaceInstall(packageId, displayName, registryUrl, selectedVersion, tag: null);
            MarkQueued($"Queued install for {packageId} {selectedVersion}.");
            return;
        }

        if (getIsBusy())
        {
            return;
        }

        await ExecuteRegistryInstallAsync(
            registryClient => registryInstallService.InstallPackageAsync(
                packageId,
                selectedVersion,
                tag: null,
                allowDowngrade: false,
                reinstall: false,
                registryClient,
                runtimeApiClient),
            selectedPackageIdForRefresh: null,
            "Package installed",
            $"{packageId} was installed from the marketplace.");
    }

    public async Task UpdateInstalledPackageAsync(RegistryPackageUpdate update, string displayName)
    {
        if (packageOperationService is not null)
        {
            if (!TryResolveRegistryUrl(out var registryUrl) || registryUrl is null)
            {
                return;
            }

            packageOperationService.EnqueueMarketplaceUpdate(update.PackageId, displayName, update.AvailableVersion, registryUrl);
            MarkQueued($"Queued update for {update.PackageId}.");
            return;
        }

        if (getIsBusy())
        {
            return;
        }

        await ExecuteRegistryInstallAsync(
            registryClient => registryInstallService.InstallPackageAsync(
                update.PackageId,
                update.AvailableVersion,
                tag: null,
                allowDowngrade: false,
                reinstall: false,
                registryClient,
                runtimeApiClient),
            update.PackageId,
            "Package updated",
            $"{update.PackageId} was updated to {update.AvailableVersion}.");
    }

    public async Task UpdateMarketplacePackageAsync(RegistryPackageUpdate update, string displayName, string? selectedInstalledPackageId)
    {
        if (packageOperationService is not null)
        {
            if (!TryResolveRegistryUrl(out var registryUrl) || registryUrl is null)
            {
                return;
            }

            packageOperationService.EnqueueMarketplaceUpdate(update.PackageId, displayName, update.AvailableVersion, registryUrl);
            MarkQueued($"Queued update for {update.PackageId}.");
            return;
        }

        if (getIsBusy())
        {
            return;
        }

        await ExecuteRegistryInstallAsync(
            registryClient => registryInstallService.InstallPackageAsync(
                update.PackageId,
                update.AvailableVersion,
                tag: null,
                allowDowngrade: false,
                reinstall: false,
                registryClient,
                runtimeApiClient),
            selectedInstalledPackageId,
            "Package updated",
            $"{update.PackageId} was updated to {update.AvailableVersion}.");
    }

    public async Task UpdateAllPackagesAsync(string? selectedInstalledPackageId)
    {
        if (packageOperationService is not null)
        {
            if (!TryResolveRegistryUrl(out var registryUrl) || registryUrl is null)
            {
                return;
            }

            packageOperationService.EnqueueUpdateAll(registryUrl);
            MarkQueued("Queued updates for installed packages.");
            return;
        }

        if (getIsBusy())
        {
            return;
        }

        await ExecuteRegistryInstallAsync(
            registryClient => registryInstallService.UpdateAllAsync(registryClient, runtimeApiClient),
            selectedInstalledPackageId,
            "Packages updated",
            "Installed packages were updated.");
    }

    private void MarkQueued(string statusText)
    {
        markInstalledCatalogDirty();
        refreshPackageOperationState();
        setStatusText(statusText);
    }

    private async Task ExecuteLocalPackageOperationAsync(
        Func<Task<PackageOperationResult>> operation,
        string? selectedPackageId,
        string successTitle,
        string successFallbackMessage)
    {
        setIsBusy(true);
        PackageOperationResult? operationResult = null;
        try
        {
            operationResult = await operation();
            if (operationResult.Success && operationResult.RuntimeSessionApplied && operationResult.ImpactedPackageIds.Count > 0)
            {
                setStatusText("Applying package changes to the running shell...");
                operationResult = await ApplyShellPackageChangesAsync(operationResult);
            }
        }
        catch (Exception ex)
        {
            operationResult = new PackageOperationResult(false, ex.Message, RuntimeSessionApplied: false, RequiresAppRestart: false, [], [ex.Message]);
        }
        finally
        {
            setIsBusy(false);
        }

        await refreshInstalledAsync(selectedPackageId, operationResult, true);
        if (operationResult is { Success: true, RuntimeSessionApplied: true, RequiresAppRestart: false }
            && operationResult.ImpactedPackageIds.Count > 0)
        {
            await PublishPackageOperationSuccessToastAsync(
                successTitle,
                PackageOperationMessageFormatter.BuildLocalPackageOperationToastMessage(operationResult, successFallbackMessage));
        }
    }

    private async Task<PackageOperationResult> ApplyShellPackageChangesAsync(PackageOperationResult operationResult)
    {
        try
        {
            await applyPackageLifecycleChangesAsync(operationResult.ImpactedPackageIds, CancellationToken.None);
            return operationResult with { AppShellApplied = true, RequiresAppRestart = false };
        }
        catch (Exception ex)
        {
            return operationResult with
            {
                RequiresAppRestart = true,
                Warnings = operationResult.Warnings.Concat([$"Package store updated, but the running shell did not apply the change: {ex.Message}"]).ToArray(),
            };
        }
    }

    private async Task ExecuteRegistryInstallAsync(
        Func<IRegistryApiClient, Task<RegistryPackageInstallExecutionResult>> executeAsync,
        string? selectedPackageIdForRefresh,
        string successTitle,
        string successFallbackMessage)
    {
        if (!TryCreateRegistryClient(out var registryClient))
        {
            return;
        }

        using (registryClient)
        {
            setIsBusy(true);
            RegistryPackageInstallExecutionResult? result = null;
            try
            {
                clearWarnings();
                setStatusText("Resolving registry install plan...");
                result = await executeAsync(registryClient);
                ApplyRegistryInstallResult(result);
                if (result.Success && result.ImpactedPackageIds.Count > 0)
                {
                    setStatusText("Applying package changes to the running shell...");
                    try
                    {
                        await applyPackageLifecycleChangesAsync(result.ImpactedPackageIds, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        addWarning($"Package store updated, but the running shell did not apply the change: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                result = RegistryPackageInstallExecutionResult.Failed(ex.Message);
                ApplyRegistryInstallResult(result);
            }
            finally
            {
                setIsBusy(false);
            }

            await refreshInstalledAsync(selectedPackageIdForRefresh, null, true);
            refreshMarketplaceInstalledBadges();
            setStatusText(result is null
                ? "Registry operation completed."
                : PackageOperationMessageFormatter.BuildRegistryResultStatusText(result, getWarningCount() > 0));
            if (result is { Success: true } && result.ImpactedPackageIds.Count > 0)
            {
                await PublishPackageOperationSuccessToastAsync(
                    successTitle,
                    PackageOperationMessageFormatter.BuildRegistryPackageOperationToastMessage(result, successFallbackMessage));
            }
        }
    }

    private bool TryCreateRegistryClient(out IRegistryApiClient registryClient)
    {
        if (!registryClientProvider.TryCreate(out registryClient, out var errorMessage))
        {
            setStatusText(errorMessage ?? "Enter a valid HTTP or HTTPS registry URL.");
            return false;
        }

        return true;
    }

    private bool TryResolveRegistryUrl(out Uri? registryUrl)
    {
        if (!registryClientProvider.TryResolve(out registryUrl, out var errorMessage))
        {
            setStatusText(errorMessage ?? "Enter a valid HTTP or HTTPS registry URL.");
            return false;
        }

        return true;
    }

    private void ApplyRegistryInstallResult(RegistryPackageInstallExecutionResult result)
        => replaceWarnings(result.Warnings.Concat(result.Errors).ToArray());

    private async ValueTask PublishPackageOperationSuccessToastAsync(string title, string message)
    {
        if (notificationCenter is null)
        {
            return;
        }

        await notificationCenter.PublishAsync(
            "sunder.app",
            "Sunder",
            new PackageNotificationRequest(
                title,
                message,
                PackageNotificationDisplayMode.ToastOnly,
                PackageNotificationSeverity.Success));
    }
}
