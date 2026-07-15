using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

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
    Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token);
    Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token);
    Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, CancellationToken token);
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
    ICliRuntimeAuthClient,
    ICliRuntimePublishClient,
    ICliRuntimeManagementClient;

internal sealed class CliRuntimeClient : ICliRuntimeClient
{
    private readonly RuntimeManagementClient _client;
    private readonly TimeSpan _resetLeaseWait;

    public CliRuntimeClient(Uri runtimeUrl, TimeSpan requestTimeout)
    {
        _client = new(runtimeUrl, new RuntimeClientPolicyOptions
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
    public Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken token) => _client.StartRegistryAuthAsync(request, token);
    public Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken token) => _client.GetRegistryAuthSessionAsync(sessionId, token);
    public Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string origin, CancellationToken token) => _client.GetRegistryAuthStatusAsync(origin, token);
    public Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string origin, CancellationToken token) => _client.LogoutRegistryAsync(origin, token);
    public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token) => _client.InstallRegistryPackageAsync(request, token);
    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token) => _client.UpdateRegistryPackagesAsync(request, token);
    public Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, CancellationToken token) => _client.ApplyLocalPackageAsync(path, packageId, allowDowngrade, reinstall, token);
    public Task<RegistryPublishPackageResponse> PublishRegistryPackageAsync(string origin, string path, bool setLatest, CancellationToken token) => _client.PublishRegistryPackageAsync(origin, path, setLatest, token);
    public Task<RegistryPublishStackResponse> PublishRegistryStackAsync(string origin, string path, CancellationToken token) => _client.PublishRegistryStackAsync(origin, path, token);
    public Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken token) => _client.SetYankAsync(request, token);
    public Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken token) => _client.SetDeprecationAsync(request, token);
    public Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken token) => _client.SetDistTagAsync(request, token);
    public Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken token) => _client.DeleteRegistryStackAsync(request, token);
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
