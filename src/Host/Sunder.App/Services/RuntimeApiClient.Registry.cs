using System.Net.Http.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryAuthStartRequest, RuntimeRegistryAuthStartResponse>("registry/auth/start", request, cancellationToken);

    public async Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(CreateRequestUri($"registry/auth/sessions/{Uri.EscapeDataString(sessionId)}"), cancellationToken);
        return response.StatusCode == System.Net.HttpStatusCode.NotFound
            ? null
            : await ReadRequiredAsync<RuntimeRegistryAuthSessionStatus>(response, cancellationToken);
    }

    public async Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string registryOrigin, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(CreateRequestUri($"registry/auth/status?origin={Uri.EscapeDataString(registryOrigin)}"), cancellationToken);
        return await ReadRequiredAsync<RuntimeRegistryAuthStatus>(response, cancellationToken);
    }

    public Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string registryOrigin, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryOriginRequest, RuntimeRegistryAuthStatus>("registry/auth/logout", new(registryOrigin), cancellationToken);

    public Task<RegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryPackageBatchRequest, RegistryResolveInstallPlanResponse>("registry/packages/plan", request, cancellationToken);

    public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryPackageRequest, RuntimeRegistryPackageChangeResult>("registry/packages/install", request, cancellationToken);

    public Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryPackageBatchRequest, RuntimeRegistryPackageChangeResult>("registry/packages/apply", request, cancellationToken);

    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryUpdateRequest, RuntimeRegistryPackageChangeResult>("registry/packages/update", request, cancellationToken);

    public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryStarRequest, RegistryPackageStarResponse>("registry/packages/star", request, cancellationToken);

    public Task<RegistryStackStarResponse> SetRegistryStackStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryStarRequest, RegistryStackStarResponse>("registry/stacks/star", request, cancellationToken);

    public Task<RegistryPublishStackResponse> PublishRegistryStackAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryPublishRequest, RegistryPublishStackResponse>("registry/stacks/publish", request, cancellationToken);

    public Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken cancellationToken = default)
        => PostRegistryAsync<RuntimeRegistryDeleteStackRequest, RegistryStackManagementOperationResponse>("registry/stacks/delete", request, cancellationToken);

    private async Task<TResponse> PostRegistryAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(CreateRequestUri(path), request, cancellationToken);
        return await ReadRequiredAsync<TResponse>(response, cancellationToken);
    }
}
