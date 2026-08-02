using System.Net;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryAuthStartRequest, RuntimeRegistryAuthStartResponse>("registry/auth/start", request, token);

    public Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string origin, CancellationToken token = default)
        => GetRequiredAsync<RuntimeRegistryAuthStatus>($"registry/auth/status?origin={Uri.EscapeDataString(origin)}", token);

    public async Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken token = default)
    {
        using var response = await _httpClient.GetAsync(CreateUri($"registry/auth/sessions/{Uri.EscapeDataString(sessionId)}"), token).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound
            ? null
            : await _responses.ReadRequiredJsonAsync<RuntimeRegistryAuthSessionStatus>(response, token).ConfigureAwait(false);
    }

    public Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string origin, CancellationToken token = default)
        => PostAsync<RuntimeRegistryOriginRequest, RuntimeRegistryAuthStatus>("registry/auth/logout", new(origin), token);

    public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryPackageRequest, RuntimeRegistryPackageChangeResult>("registry/packages/install", request, token);

    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryUpdateRequest, RuntimeRegistryPackageChangeResult>("registry/packages/update", request, token);

    public Task<RuntimeRegistryPackageChangeResult> AdoptRegistryPackageSourceAsync(RuntimeRegistrySourceAdoptionRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistrySourceAdoptionRequest, RuntimeRegistryPackageChangeResult>("registry/packages/adopt-source", request, token);

    public async Task<RegistryPublishPackageResponse> PublishRegistryPackageAsync(
        string origin, string packagePath, bool setLatest, CancellationToken token = default)
    {
        var upload = await UploadAsync(packagePath, "uploads/packages", "application/vnd.sunder.package", token).ConfigureAwait(false);
        return await PostAsync<RuntimeRegistryPublishRequest, RegistryPublishPackageResponse>(
            "registry/packages/publish", new(origin, upload.UploadId, setLatest), token).ConfigureAwait(false);
    }

    public async Task<RegistryPublishStackResponse> PublishRegistryStackAsync(
        string origin, string stackPath, CancellationToken token = default)
    {
        var upload = await UploadAsync(stackPath, "uploads/stacks", "application/vnd.sunder.stack", token).ConfigureAwait(false);
        return await PostAsync<RuntimeRegistryPublishRequest, RegistryPublishStackResponse>(
            "registry/stacks/publish", new(origin, upload.UploadId), token).ConfigureAwait(false);
    }

    public Task<RegistryPackageManagementOperationResponse> SetYankAsync(RuntimeRegistryYankRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryYankRequest, RegistryPackageManagementOperationResponse>("registry/packages/yank", request, token);

    public Task<RegistryPackageManagementOperationResponse> SetDeprecationAsync(RuntimeRegistryDeprecateRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryDeprecateRequest, RegistryPackageManagementOperationResponse>("registry/packages/deprecate", request, token);

    public Task<RegistryPackageManagementOperationResponse> SetDistTagAsync(RuntimeRegistryDistTagRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryDistTagRequest, RegistryPackageManagementOperationResponse>("registry/packages/dist-tag", request, token);

    public Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryDeleteStackRequest, RegistryStackManagementOperationResponse>("registry/stacks/delete", request, token);
}
