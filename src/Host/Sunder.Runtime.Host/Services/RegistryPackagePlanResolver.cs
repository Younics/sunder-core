using System.Net.Http.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPackagePlanResolver(
    RegistryHttpClient registryClient,
    InstalledPackageLifecycleService installedPackages,
    ILogger<RegistryPackagePlanResolver> logger)
{
    public async Task<RuntimeRegistryResolveInstallPlanResponse> ResolveAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
    {
        var origin = RegistryOrigin.Normalize(request.RegistryOrigin);
        try
        {
            return RuntimeRegistryContractMapper.ToRuntime(
                await ResolveCoreAsync(origin, request, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            logger.LogWarning(
                "Package plan failed: Registry is not reachable at {RegistryOrigin}.",
                origin.AbsoluteUri);
            return new RuntimeRegistryResolveInstallPlanResponse(
                false,
                [],
                [],
                [$"Registry is not reachable at {origin.AbsoluteUri}."],
                []);
        }
    }

    public async Task<RegistryPackagePlanResolution> ResolveForExecutionAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
    {
        var origin = RegistryOrigin.Normalize(request.RegistryOrigin);
        return new RegistryPackagePlanResolution(
            origin,
            await ResolveCoreAsync(origin, request, cancellationToken));
    }

    private async Task<RegistryResolveInstallPlanResponse> ResolveCoreAsync(
        Uri origin,
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
    {
        var installed = (await installedPackages.GetInstalledAsync(cancellationToken))
            .Select(package => new RegistryInstalledPackageState(
                package.PackageId,
                package.Version,
                package.DependsOn.Select(dependency => new RegistryPackageDependency(dependency.PackageId, dependency.VersionRange)).ToArray()))
            .ToArray();
        using var response = await registryClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(origin, "api/v1/packages/resolve-package-changes"))
            {
                Content = JsonContent.Create(new RegistryResolvePackageChangesRequest(
                    RuntimeRegistryContractMapper.ToRegistry(request.Packages),
                    installed,
                    request.IncludePrerelease,
                    request.AllowDowngrade,
                    request.Reinstall)),
            },
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new RegistryResolveInstallPlanResponse(
                false,
                [],
                [],
                [await registryClient.ReadErrorAsync(response, cancellationToken)],
                []);
        }

        var result = await registryClient.ReadJsonAsync<RegistryResolveInstallPlanResponse>(response, cancellationToken);
        if (result is not null) return result;
        return new RegistryResolveInstallPlanResponse(false, [], [], [await registryClient.ReadErrorAsync(response, cancellationToken)], []);
    }
}

internal sealed record RegistryPackagePlanResolution(
    Uri Origin,
    RegistryResolveInstallPlanResponse Plan);
