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

internal interface ICliHostClient : ICliHostLifecycleClient;

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
    Task<RuntimeResetDrainResponse> DrainForResetAsync(string challenge, CancellationToken token);
    Task<RuntimeV1ResetResult> ResetLocalStateAsync(CancellationToken token);
}

internal interface ICliRuntimePackageClient : ICliRuntimeClientRole
{
    Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken token);
    Task<RuntimePackageSnapshot> GetPackageSnapshotAsync(CancellationToken token);
    Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token);
    Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token);
    Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(RuntimeRegistrySourceAdoptionRequest request, CancellationToken token);
    Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, CancellationToken token);
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
    Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken token);
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
    ICliRuntimeManagementClient;

internal sealed class CliRuntimeClient : ICliRuntimeClient
{
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
    public Task<RuntimeResetDrainResponse> DrainForResetAsync(string challenge, CancellationToken token) => _client.DrainForResetAsync(challenge, token);
    public Task<RuntimeV1ResetResult> ResetLocalStateAsync(CancellationToken token)
        => RuntimeV1StateReset.ResetAsync(_resetLeaseWait, token);
    public Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken token) => _client.GetInstalledPackagesAsync(token);
    public Task<RuntimePackageSnapshot> GetPackageSnapshotAsync(CancellationToken token) => _client.GetPackageSnapshotAsync(token);
    public Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken token) => _client.StartRegistryAuthAsync(request, token);
    public Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken token) => _client.GetRegistryAuthSessionAsync(sessionId, token);
    public Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string origin, CancellationToken token) => _client.GetRegistryAuthStatusAsync(origin, token);
    public Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string origin, CancellationToken token) => _client.LogoutRegistryAsync(origin, token);
    public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token) => _client.InstallRegistryPackageAsync(request, token);
    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token) => _client.UpdateRegistryPackagesAsync(request, token);
    public Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(RuntimeRegistrySourceAdoptionRequest request, CancellationToken token) => _client.AdoptRegistryPackageSourceAsync(request, token);
    public Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, CancellationToken token) => _client.ApplyLocalPackageAsync(path, packageId, allowDowngrade, reinstall, token);
    public Task<PackageOperationResult> SetPackageEnabledAsync(string packageId, bool enabled, CancellationToken token) => _client.SetPackageEnabledAsync(packageId, enabled, token);
    public Task<PackageUninstallPlan> GetPackageUninstallPlanAsync(string packageId, CancellationToken token) => _client.GetPackageUninstallPlanAsync(packageId, token);
    public Task<PackageOperationResult> UninstallPackageAsync(string packageId, PackageUninstallRequest request, CancellationToken token) => _client.UninstallPackageAsync(packageId, request, token);
    public Task<RegistryPublishPackageResponse> PublishRegistryPackageAsync(string origin, string path, bool setLatest, CancellationToken token) => _client.PublishRegistryPackageAsync(origin, path, setLatest, token);
    public Task<RegistryPublishStackResponse> PublishRegistryStackAsync(string origin, string path, CancellationToken token) => _client.PublishRegistryStackAsync(origin, path, token);
    public Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken token) => _client.SetYankAsync(request, token);
    public Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken token) => _client.SetDeprecationAsync(request, token);
    public Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken token) => _client.SetDistTagAsync(request, token);
    public Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken token) => _client.DeleteRegistryStackAsync(request, token);
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
