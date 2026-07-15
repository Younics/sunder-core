using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public Task<RuntimeRegistryAuthStartResponse> StartRegistryAuthAsync(RuntimeRegistryAuthStartRequest request, CancellationToken cancellationToken = default)
        => _management.StartRegistryAuthAsync(request, cancellationToken);

    public Task<RuntimeRegistryAuthSessionStatus?> GetRegistryAuthSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _management.GetRegistryAuthSessionAsync(sessionId, cancellationToken);

    public Task<RuntimeRegistryAuthStatus> GetRegistryAuthStatusAsync(string registryOrigin, CancellationToken cancellationToken = default)
        => _management.GetRegistryAuthStatusAsync(registryOrigin, cancellationToken);

    public Task<RuntimeRegistryAuthStatus> LogoutRegistryAsync(string registryOrigin, CancellationToken cancellationToken = default)
        => _management.LogoutRegistryAsync(registryOrigin, cancellationToken);

    public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
        => _management.ResolveRegistryPackagePlanAsync(request, cancellationToken);

    public Task<RuntimeRegistryPackageChangeResult> InstallRegistryPackageAsync(RuntimeRegistryPackageRequest request, CancellationToken cancellationToken = default)
        => _management.InstallRegistryPackageAsync(request, cancellationToken);

    public Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(RuntimeRegistryPackageBatchRequest request, CancellationToken cancellationToken = default)
        => _management.ApplyRegistryPackagePlanAsync(request, cancellationToken);

    public Task<RuntimeRegistryPackageChangeResult> UpdateRegistryPackagesAsync(RuntimeRegistryUpdateRequest request, CancellationToken cancellationToken = default)
        => _management.UpdateRegistryPackagesAsync(request, cancellationToken);

    public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
        => _management.SetRegistryPackageStarAsync(request, cancellationToken);

    public Task<RegistryStackStarResponse> SetRegistryStackStarAsync(RuntimeRegistryStarRequest request, CancellationToken cancellationToken = default)
        => _management.SetRegistryStackStarAsync(request, cancellationToken);

    public Task<RegistryPublishStackResponse> PublishRegistryStackAsync(RuntimeRegistryPublishRequest request, CancellationToken cancellationToken = default)
        => _management.PublishRegistryStackAsync(request, cancellationToken);

    public Task<RegistryStackManagementOperationResponse> DeleteRegistryStackAsync(RuntimeRegistryDeleteStackRequest request, CancellationToken cancellationToken = default)
        => _management.DeleteRegistryStackAsync(request, cancellationToken);
}
