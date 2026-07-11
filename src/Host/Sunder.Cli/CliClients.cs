using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal interface ICliRuntimeClient : IDisposable
{
    Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken token);
    Task<RuntimeResetChallengeResponse> PrepareResetAsync(CancellationToken token);
    Task<RuntimeResetDrainResponse> DrainForResetAsync(string challenge, CancellationToken token);
    Task<RuntimeV1ResetResult> ResetLocalStateAsync(CancellationToken token);
    Task<IReadOnlyList<InstalledPackageDescriptor>> GetInstalledPackagesAsync(CancellationToken token);
    Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken token);
    Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken token);
    Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string origin, CancellationToken token);
    Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string origin, CancellationToken token);
    Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token);
    Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token);
    Task<PackageOperationResult> ApplyLocalPackageAsync(string path, string packageId, bool allowDowngrade, bool reinstall, CancellationToken token);
    Task<RegistryPublishPackageResponse> PublishRegistryPackageAsync(string origin, string path, bool setLatest, CancellationToken token);
    Task<RegistryPublishStackResponse> PublishRegistryStackAsync(string origin, string path, CancellationToken token);
    Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken token);
    Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken token);
    Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken token);
    Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken token);
}

internal sealed class CliRuntimeClient : ICliRuntimeClient
{
    private readonly RuntimeManagementClient _client;

    public CliRuntimeClient(Uri runtimeUrl) => _client = new(runtimeUrl);
    internal CliRuntimeClient(RuntimeManagementClient client) => _client = client;

    public Task<SystemStatusResponse> GetSystemStatusAsync(CancellationToken token) => _client.GetSystemStatusAsync(token);
    public Task<RuntimeResetChallengeResponse> PrepareResetAsync(CancellationToken token) => _client.PrepareResetAsync(token);
    public Task<RuntimeResetDrainResponse> DrainForResetAsync(string challenge, CancellationToken token) => _client.DrainForResetAsync(challenge, token);
    public Task<RuntimeV1ResetResult> ResetLocalStateAsync(CancellationToken token)
        => RuntimeV1StateReset.ResetAsync(TimeSpan.FromSeconds(15), token);
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
