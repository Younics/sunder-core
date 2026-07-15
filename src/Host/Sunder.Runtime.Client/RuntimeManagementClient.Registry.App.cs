using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveRegistryPackagePlanAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeRegistryPackageBatchRequest, RuntimeRegistryResolveInstallPlanResponse>("registry/packages/plan", request, token);

    public Task<RuntimeRegistryPackageChangeResult> ApplyRegistryPackagePlanAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeRegistryPackageBatchRequest, RuntimeRegistryPackageChangeResult>("registry/packages/apply", request, token);

    public Task<RegistryPackageStarResponse> SetRegistryPackageStarAsync(RuntimeRegistryStarRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryStarRequest, RegistryPackageStarResponse>("registry/packages/star", request, token);

    public Task<RegistryStackStarResponse> SetRegistryStackStarAsync(RuntimeRegistryStarRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryStarRequest, RegistryStackStarResponse>("registry/stacks/star", request, token);

    public Task<RegistryPublishStackResponse> PublishRegistryStackAsync(RuntimeRegistryPublishRequest request, CancellationToken token = default)
        => PostAsync<RuntimeRegistryPublishRequest, RegistryPublishStackResponse>("registry/stacks/publish", request, token);
}
