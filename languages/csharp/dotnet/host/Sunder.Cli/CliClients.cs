using Sunder.Host.Client;
using Sunder.Host.Contracts;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal interface ICliHostClientRole : IDisposable;

internal interface ICliHostStatusClient : ICliHostClientRole
{
    Task<HostRuntimeStatus> GetStatusAsync(CancellationToken token);
}

internal interface ICliHostLifecycleClient : ICliHostStatusClient
{
    Task<HostLifecycleSubmission> SubmitStartRuntimeAsync(HostLifecycleRequest request, CancellationToken token);
    Task<HostLifecycleSubmission> SubmitStopRuntimeAsync(HostLifecycleRequest request, CancellationToken token);
    Task<HostLifecycleSubmission> SubmitRestartRuntimeAsync(HostLifecycleRequest request, CancellationToken token);
    Task<HostOperationDescriptor> WaitForOperationAsync(HostOperationDescriptor operation, CancellationToken token);
}

internal interface ICliManagedRuntimeClient : ICliHostClientRole
{
    Task<HostRuntimeStatus> EnsureRuntimeReadyAsync(CancellationToken token);
}

internal interface ICliHostClient : ICliHostLifecycleClient, ICliManagedRuntimeClient;

internal sealed class CliHostClient : ICliHostClient
{
    private readonly HostManagementClient _client;
    private HostHandshakeResponse? _handshake;

    public CliHostClient(Uri runtimeUrl)
    {
        _client = new HostManagementClient(() => HostConnectionInfoStore.LoadPreferredFor(runtimeUrl));
    }

    internal CliHostClient(HostManagementClient client)
    {
        _client = client;
    }

    public async Task<HostRuntimeStatus> GetStatusAsync(CancellationToken token)
    {
        await EnsureCompatibleAsync(token).ConfigureAwait(false);
        return await _client.GetStatusAsync(token).ConfigureAwait(false);
    }

    public async Task<HostLifecycleSubmission> SubmitStartRuntimeAsync(HostLifecycleRequest request, CancellationToken token)
    {
        await EnsureCompatibleAsync(token).ConfigureAwait(false);
        return await _client.SubmitStartRuntimeAsync(request, token).ConfigureAwait(false);
    }

    public async Task<HostLifecycleSubmission> SubmitStopRuntimeAsync(HostLifecycleRequest request, CancellationToken token)
    {
        await EnsureCompatibleAsync(token).ConfigureAwait(false);
        return await _client.SubmitStopRuntimeAsync(request, token).ConfigureAwait(false);
    }

    public async Task<HostLifecycleSubmission> SubmitRestartRuntimeAsync(HostLifecycleRequest request, CancellationToken token)
    {
        await EnsureCompatibleAsync(token).ConfigureAwait(false);
        return await _client.SubmitRestartRuntimeAsync(request, token).ConfigureAwait(false);
    }

    public async Task<HostOperationDescriptor> WaitForOperationAsync(HostOperationDescriptor operation, CancellationToken token)
    {
        await EnsureCompatibleAsync(token).ConfigureAwait(false);
        return await _client.WaitForOperationAsync(operation, token).ConfigureAwait(false);
    }

    public async Task<HostRuntimeStatus> EnsureRuntimeReadyAsync(CancellationToken token)
    {
        await EnsureCompatibleAsync(token).ConfigureAwait(false);
        while (true)
        {
            var status = await _client.GetStatusAsync(token).ConfigureAwait(false);
            if (status.ActiveOperationId is { } activeOperationId)
            {
                var active = await _client.GetOperationAsync(activeOperationId, token).ConfigureAwait(false);
                await _client.WaitForOperationAsync(active, token).ConfigureAwait(false);
                continue;
            }
            if (status.State == HostRuntimeState.Ready
                && status.DesiredState == HostRuntimeDesiredState.Running)
            {
                return status;
            }
            if (status.State is HostRuntimeState.Starting
                or HostRuntimeState.Stopping
                or HostRuntimeState.Restarting)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
                continue;
            }

            HostLifecycleSubmission submission;
            try
            {
                submission = await _client.SubmitStartRuntimeAsync(
                    new HostLifecycleRequest(Guid.NewGuid(), status.DeploymentGeneration),
                    token).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
                when (exception.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                continue;
            }

            var operation = await _client.WaitForOperationAsync(submission.Operation, token).ConfigureAwait(false);
            if (operation.State != HostOperationState.Succeeded)
            {
                throw new CliUnavailableException(
                    operation.Message ?? "Sunder Host could not start its Runtime worker.");
            }
        }
    }

    private async Task EnsureCompatibleAsync(CancellationToken token)
    {
        _handshake ??= await _client.GetHandshakeAsync(token).ConfigureAwait(false);
        if (HostProtocolCompatibility.GetManagedSupervisorIncompatibility(_handshake) is { } incompatibility)
            throw new CliUnavailableException(incompatibility);
    }

    public void Dispose() => _client.Dispose();
}

internal interface ICliRuntimeClientRole : IDisposable;

internal interface ICliRuntimeSystemClient : ICliRuntimeClientRole
{
    Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken token);
}

internal interface ICliRuntimeResetClient : ICliRuntimeClientRole
{
    Task<RuntimeResetChallengeResponse> PrepareResetAsync(CancellationToken token);
    Task<RuntimeV1ResetResult> DrainAndResetLocalStateAsync(RuntimeResetChallengeResponse challenge, CancellationToken token);
    Task<RuntimeV1ResetResult> DrainAndResetStartupCacheAsync(RuntimeResetChallengeResponse challenge, CancellationToken token);
}

internal interface ICliRuntimePackageClient : ICliRuntimeClientRole
{
    Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken token);
    Task<RuntimePackageSnapshot> GetPackageSnapshotAsync(CancellationToken token);
    Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token);
    Task<RuntimeRegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken token);
    Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token);
    Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(RuntimeRegistrySourceAdoptionRequest request, CancellationToken token);
    Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, CancellationToken token);
    Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, PackageCapabilityConsent? capabilityConsent, CancellationToken token);
    Task<PackageOperationResult> SetPackageEnabledAsync(string packageId, bool enabled, CancellationToken token);
    Task<PackageUninstallPlan> GetPackageUninstallPlanAsync(string packageId, CancellationToken token);
    Task<PackageOperationResult> UninstallPackageAsync(string packageId, PackageUninstallRequest request, CancellationToken token);
}

internal interface ICliRuntimePackageSettingsClient : ICliRuntimeClientRole
{
    Task<IReadOnlyList<PackageSettingsSchemaDescriptor>> GetPackageSettingsSchemasAsync(CancellationToken token);
    Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(string packageId, CancellationToken token);
    Task<PackageSettingValueResponse> GetPackageSettingValueAsync(string packageId, string key, CancellationToken token);
    Task SetPackageSettingValueAsync(string packageId, string key, string value, CancellationToken token);
    Task DeletePackageSettingValueAsync(string packageId, string key, CancellationToken token);
}

internal interface ICliRuntimePackageAuthClient : ICliRuntimeClientRole
{
    Task<PackageAuthStatusResponse> GetPackageAuthStatusAsync(string packageId, CancellationToken token);
    Task<PackageAuthSessionStartResponse> StartPackageAuthAsync(string packageId, CancellationToken token);
    Task<PackageAuthSessionStatusResponse> GetPackageAuthSessionStatusAsync(string packageId, string authSessionId, CancellationToken token);
    Task<bool> CancelPackageAuthSessionAsync(string packageId, string authSessionId, CancellationToken token);
    Task<PackageAuthStatusResponse> DisconnectPackageAuthAsync(string packageId, CancellationToken token);
}

internal interface ICliRuntimeStackClient : ICliRuntimeClientRole
{
    Task<ContentUploadDescriptor> UploadStackAsync(string filePath, CancellationToken token);
    Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(RuntimeStackImportPreviewRequest request, CancellationToken token);
    Task DiscardStackImportPlanAsync(string planId, CancellationToken token);
    Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken token);
    Task<RuntimeStackExportResponse> ExportStackAsync(RuntimeStackExportRequest request, CancellationToken token);
    Task DownloadContentAsync(ContentDownloadDescriptor download, string destinationPath, CancellationToken token);
}

internal interface ICliRuntimeAuthClient : ICliRuntimeClientRole
{
    Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken token);
    Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken token);
    Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string origin, CancellationToken token);
    Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string origin, CancellationToken token);
}

internal interface ICliRuntimePublishClient : ICliRuntimeClientRole
{
    Task<RegistryPublishPackageResponse> PublishRegistryPackageAsync(string origin, string path, bool setLatest, CancellationToken token);
    Task<RegistryPublishStackResponse> PublishRegistryStackAsync(string origin, string path, CancellationToken token);
}

internal interface ICliRuntimeManagementClient : ICliRuntimeClientRole
{
    Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken token);
    Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken token);
    Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken token);
    Task<RegistryStackManagementOperationResponse> SetRegistryStackVisibilityAsync(RuntimeRegistryStackVisibilityRequest request, CancellationToken token);
}

internal interface ICliRuntimeExecutionRuntimeClient : ICliRuntimeClientRole
{
    Task<RuntimeExecutionRuntimeSettings> GetExecutionRuntimeSettingsAsync(CancellationToken token);
    Task<RuntimeExecutionRuntimeSettings> UpdateExecutionRuntimeSettingsAsync(RuntimeExecutionRuntimeSettingsUpdateRequest request, CancellationToken token);
    Task<RuntimeExecutionRuntimeInstallPlan> PreviewExecutionRuntimeInstallAsync(RuntimeExecutionRuntimeInstallPreviewRequest request, CancellationToken token);
    Task<RuntimeExecutionRuntimeOperationResult> ApplyExecutionRuntimeInstallAsync(RuntimeExecutionRuntimePlanConfirmation request, CancellationToken token);
    Task<RuntimeExecutionRuntimeUpdatePlan> PreviewExecutionRuntimeUpdateAsync(RuntimeExecutionRuntimeUpdatePreviewRequest request, CancellationToken token);
    Task<RuntimeExecutionRuntimeUpdateResult> PrepareExecutionRuntimeUpdateAsync(RuntimeExecutionRuntimePlanConfirmation request, CancellationToken token);
    Task<RuntimeExecutionRuntimeUpdateResult> ApplyExecutionRuntimeUpdateAsync(RuntimeExecutionRuntimeUpdateApplyRequest request, CancellationToken token);
    Task<RuntimeExecutionRuntimeUninstallPlan> PreviewExecutionRuntimeUninstallAsync(RuntimeExecutionRuntimeUninstallPreviewRequest request, CancellationToken token);
    Task<RuntimeExecutionRuntimeUninstallResult> ApplyExecutionRuntimeUninstallAsync(RuntimeExecutionRuntimePlanConfirmation request, CancellationToken token);
    Task<IReadOnlyList<InstalledExecutionRuntimeSummary>> GetExecutionRuntimesAsync(CancellationToken token);
    Task<InstalledExecutionRuntimeSummary> GetExecutionRuntimeAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken token);
    Task<IReadOnlyList<RuntimeExecutionRuntimePackageReference>> GetExecutionRuntimeReferencesAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken token);
    Task<RuntimeExecutionRuntimeOperationResult> EnsureExecutionRuntimeAsync(RuntimeExecutionRuntimeEnsureRequest request, CancellationToken token);
    Task<RuntimeExecutionRuntimeOperationResult> VerifyExecutionRuntimeAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken token);
    Task<RuntimeExecutionRuntimeOperationResult> RepairExecutionRuntimeAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken token);
    Task<RuntimeExecutionRuntimeOperationResult> CollectExecutionRuntimeGarbageAsync(CancellationToken token);
}

internal interface ICliRuntimeDevPackageClient : ICliRuntimeClientRole
{
    Task<RuntimeDevPackageImportsPrepared> PrepareDevPackageImportsAsync(RuntimePrepareDevPackageImportsRequest request, CancellationToken token);
    IDevPackageOwnerSession CreateDevPackageOwnerSession(Action<string, Exception>? reportFault = null);
}

internal interface ICliRuntimeClient :
    ICliRuntimeSystemClient,
    ICliRuntimeResetClient,
    ICliRuntimePackageClient,
    ICliRuntimePackageSettingsClient,
    ICliRuntimePackageAuthClient,
    ICliRuntimeStackClient,
    ICliRuntimeAuthClient,
    ICliRuntimePublishClient,
    ICliRuntimeManagementClient,
    ICliRuntimeExecutionRuntimeClient,
    ICliRuntimeDevPackageClient;

internal sealed class CliRuntimeClient : ICliRuntimeClient
{
    public Task<RuntimeExecutionRuntimeSettings> GetExecutionRuntimeSettingsAsync(CancellationToken token) => _client.GetExecutionRuntimeSettingsAsync(token);
    public Task<RuntimeExecutionRuntimeSettings> UpdateExecutionRuntimeSettingsAsync(RuntimeExecutionRuntimeSettingsUpdateRequest request, CancellationToken token) => _client.UpdateExecutionRuntimeSettingsAsync(request, token);
    public Task<RuntimeExecutionRuntimeInstallPlan> PreviewExecutionRuntimeInstallAsync(RuntimeExecutionRuntimeInstallPreviewRequest request, CancellationToken token) => _client.PreviewExecutionRuntimeInstallAsync(request, token);
    public Task<RuntimeExecutionRuntimeOperationResult> ApplyExecutionRuntimeInstallAsync(RuntimeExecutionRuntimePlanConfirmation request, CancellationToken token) => _client.ApplyExecutionRuntimeInstallAsync(request, token);
    public Task<RuntimeExecutionRuntimeUpdatePlan> PreviewExecutionRuntimeUpdateAsync(RuntimeExecutionRuntimeUpdatePreviewRequest request, CancellationToken token) => _client.PreviewExecutionRuntimeUpdateAsync(request, token);
    public Task<RuntimeExecutionRuntimeUpdateResult> PrepareExecutionRuntimeUpdateAsync(RuntimeExecutionRuntimePlanConfirmation request, CancellationToken token) => _client.PrepareExecutionRuntimeUpdateAsync(request, token);
    public Task<RuntimeExecutionRuntimeUpdateResult> ApplyExecutionRuntimeUpdateAsync(RuntimeExecutionRuntimeUpdateApplyRequest request, CancellationToken token) => _client.ApplyExecutionRuntimeUpdateAsync(request, token);
    public Task<RuntimeExecutionRuntimeUninstallPlan> PreviewExecutionRuntimeUninstallAsync(RuntimeExecutionRuntimeUninstallPreviewRequest request, CancellationToken token) => _client.PreviewExecutionRuntimeUninstallAsync(request, token);
    public Task<RuntimeExecutionRuntimeUninstallResult> ApplyExecutionRuntimeUninstallAsync(RuntimeExecutionRuntimePlanConfirmation request, CancellationToken token) => _client.ApplyExecutionRuntimeUninstallAsync(request, token);
    private readonly RuntimeManagementClient _client;
    private readonly TimeSpan _resetLeaseWait;

    public CliRuntimeClient(Uri runtimeUrl, TimeSpan requestTimeout)
    {
        _client = new RuntimeManagementClient(
            () => HostConnectionInfoStore.LoadPreferredFor(runtimeUrl),
            policy: new RuntimeClientPolicyOptions
            {
                RequestTimeout = Timeout.InfiniteTimeSpan,
                StreamLifetimeTimeout = Timeout.InfiniteTimeSpan,
            });
        _resetLeaseWait = requestTimeout;
    }

    internal CliRuntimeClient(RuntimeManagementClient client, TimeSpan requestTimeout)
    {
        _client = client;
        _resetLeaseWait = requestTimeout;
    }

    public Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken token) => _client.GetSystemStatusAsync(token);
    public Task<RuntimeResetChallengeResponse> PrepareResetAsync(CancellationToken token) => _client.PrepareResetAsync(token);
    public Task<RuntimeV1ResetResult> DrainAndResetLocalStateAsync(
        RuntimeResetChallengeResponse challenge,
        CancellationToken token)
        => _client.DrainAndResetLocalStateAsync(challenge, _resetLeaseWait, token);
    public Task<RuntimeV1ResetResult> DrainAndResetStartupCacheAsync(
        RuntimeResetChallengeResponse challenge,
        CancellationToken token)
        => _client.DrainAndResetStartupCacheAsync(challenge, _resetLeaseWait, token);
    public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken token) => _client.GetInstalledPackagesAsync(token);
    public Task<RuntimePackageSnapshot> GetPackageSnapshotAsync(CancellationToken token) => _client.GetPackageSnapshotAsync(token);
    public Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken token) => _client.StartRegistryAuthAsync(request, token);
    public Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken token) => _client.GetRegistryAuthSessionAsync(sessionId, token);
    public Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string origin, CancellationToken token) => _client.GetRegistryAuthStatusAsync(origin, token);
    public Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string origin, CancellationToken token) => _client.LogoutRegistryAsync(origin, token);
    public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token) => _client.InstallRegistryPackageAsync(request, token);
    public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken token) => _client.ResolveRegistryPackagePlanAsync(request, token);
    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token) => _client.UpdateRegistryPackagesAsync(request, token);
    public Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(RuntimeRegistrySourceAdoptionRequest request, CancellationToken token) => _client.AdoptRegistryPackageSourceAsync(request, token);
    public Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, CancellationToken token) => _client.ApplyLocalPackageAsync(path, packageId, allowDowngrade, reinstall, token);
    public Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, PackageCapabilityConsent? capabilityConsent, CancellationToken token) => _client.ApplyLocalPackageAsync(path, packageId, allowDowngrade, reinstall, capabilityConsent, token);
    public Task<PackageOperationResult> SetPackageEnabledAsync(string packageId, bool enabled, CancellationToken token) => _client.SetPackageEnabledAsync(packageId, enabled, token);
    public Task<PackageUninstallPlan> GetPackageUninstallPlanAsync(string packageId, CancellationToken token) => _client.GetPackageUninstallPlanAsync(packageId, token);
    public Task<PackageOperationResult> UninstallPackageAsync(string packageId, PackageUninstallRequest request, CancellationToken token) => _client.UninstallPackageAsync(packageId, request, token);
    public Task<RegistryPublishPackageResponse> PublishRegistryPackageAsync(string origin, string path, bool setLatest, CancellationToken token) => _client.PublishRegistryPackageAsync(origin, path, setLatest, token);
    public Task<RegistryPublishStackResponse> PublishRegistryStackAsync(string origin, string path, CancellationToken token) => _client.PublishRegistryStackAsync(origin, path, token);
    public Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken token) => _client.SetYankAsync(request, token);
    public Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken token) => _client.SetDeprecationAsync(request, token);
    public Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken token) => _client.SetDistTagAsync(request, token);
    public Task<RegistryStackManagementOperationResponse> SetRegistryStackVisibilityAsync(RuntimeRegistryStackVisibilityRequest request, CancellationToken token) => _client.SetRegistryStackVisibilityAsync(request, token);
    public Task<IReadOnlyList<PackageSettingsSchemaDescriptor>> GetPackageSettingsSchemasAsync(CancellationToken token) => _client.GetPackageSettingsSchemasAsync(token);
    public Task<PackageSettingsValuesResponse?> GetPackageSettingsValuesAsync(string packageId, CancellationToken token) => _client.GetPackageSettingsValuesAsync(packageId, token);
    public Task<PackageSettingValueResponse> GetPackageSettingValueAsync(string packageId, string key, CancellationToken token) => _client.GetPackageSettingValueAsync(packageId, key, token);
    public Task SetPackageSettingValueAsync(string packageId, string key, string value, CancellationToken token) => _client.SetPackageSettingValueAsync(packageId, key, value, token);
    public Task DeletePackageSettingValueAsync(string packageId, string key, CancellationToken token) => _client.DeletePackageSettingValueAsync(packageId, key, token);
    public Task<PackageAuthStatusResponse> GetPackageAuthStatusAsync(string packageId, CancellationToken token) => _client.GetPackageAuthStatusAsync(packageId, token);
    public Task<PackageAuthSessionStartResponse> StartPackageAuthAsync(string packageId, CancellationToken token) => _client.StartPackageAuthAsync(packageId, token);
    public Task<PackageAuthSessionStatusResponse> GetPackageAuthSessionStatusAsync(string packageId, string authSessionId, CancellationToken token) => _client.GetPackageAuthSessionStatusAsync(packageId, authSessionId, token);
    public Task<bool> CancelPackageAuthSessionAsync(string packageId, string authSessionId, CancellationToken token) => _client.CancelPackageAuthSessionAsync(packageId, authSessionId, token);
    public Task<PackageAuthStatusResponse> DisconnectPackageAuthAsync(string packageId, CancellationToken token) => _client.DisconnectPackageAuthAsync(packageId, token);
    public Task<ContentUploadDescriptor> UploadStackAsync(string filePath, CancellationToken token) => _client.UploadStackAsync(filePath, token);
    public Task<RuntimeStackImportPreviewResponse> PreviewStackImportAsync(RuntimeStackImportPreviewRequest request, CancellationToken token) => _client.PreviewStackImportAsync(request, token);
    public Task DiscardStackImportPlanAsync(string planId, CancellationToken token) => _client.DiscardStackImportPlanAsync(planId, token);
    public Task<RuntimeStackExportDiscoveryResponse> ListStackExportItemsAsync(CancellationToken token) => _client.ListStackExportItemsAsync(token);
    public Task<RuntimeStackExportResponse> ExportStackAsync(RuntimeStackExportRequest request, CancellationToken token) => _client.ExportStackAsync(request, token);
    public Task DownloadContentAsync(ContentDownloadDescriptor download, string destinationPath, CancellationToken token) => _client.DownloadContentAsync(download, destinationPath, token);
    public Task<IReadOnlyList<InstalledExecutionRuntimeSummary>> GetExecutionRuntimesAsync(CancellationToken token) => _client.GetExecutionRuntimesAsync(token);
    public Task<InstalledExecutionRuntimeSummary> GetExecutionRuntimeAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken token) => _client.GetExecutionRuntimeAsync(identity, token);
    public Task<IReadOnlyList<RuntimeExecutionRuntimePackageReference>> GetExecutionRuntimeReferencesAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken token) => _client.GetExecutionRuntimeReferencesAsync(identity, token);
    public Task<RuntimeExecutionRuntimeOperationResult> EnsureExecutionRuntimeAsync(RuntimeExecutionRuntimeEnsureRequest request, CancellationToken token) => _client.EnsureExecutionRuntimeAsync(request, token);
    public Task<RuntimeExecutionRuntimeOperationResult> VerifyExecutionRuntimeAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken token) => _client.VerifyExecutionRuntimeAsync(identity, token);
    public Task<RuntimeExecutionRuntimeOperationResult> RepairExecutionRuntimeAsync(RuntimeExecutionRuntimeIdentity identity, CancellationToken token) => _client.RepairExecutionRuntimeAsync(identity, token);
    public Task<RuntimeExecutionRuntimeOperationResult> CollectExecutionRuntimeGarbageAsync(CancellationToken token) => _client.CollectExecutionRuntimeGarbageAsync(token);
    public IDevPackageOwnerSession CreateDevPackageOwnerSession(Action<string, Exception>? reportFault = null)
        => _client.CreateDevPackageOwnerSession("cli", reportFault);
    public Task<RuntimeDevPackageImportsPrepared> PrepareDevPackageImportsAsync(RuntimePrepareDevPackageImportsRequest request, CancellationToken token)
        => _client.PrepareDevPackageImportsAsync(request, token);
    public void Dispose() => _client.Dispose();
}

internal interface IBrowserLauncher
{
    bool TryOpen(Uri uri);
}

internal sealed class BrowserLauncher : IBrowserLauncher
{
    public bool TryOpen(Uri uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
