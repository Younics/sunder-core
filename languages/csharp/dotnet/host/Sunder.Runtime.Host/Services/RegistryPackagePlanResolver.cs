using System.Net.Http.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPackagePlanResolver(
    RegistryHttpClient registryClient,
    InstalledPackageStore installedPackages,
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
                [],
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
        var installed = new List<RegistryInstalledPackageState>();
        foreach (var package in await installedPackages.ListAsync(cancellationToken))
        {
            var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(
                package.InstallPath,
                cancellationToken);
            if (!validation.Success || validation.Manifest is null)
            {
                throw new InvalidDataException(
                    $"Installed package '{package.PackageId}' cannot be sent to the Registry: {string.Join(" | ", validation.Errors)}");
            }

            var acquired = SunderPackageTargetResolver.EnumerateTargets(validation.Manifest)
                .Select(target => new RegistryPackageProjectionKey(target.Role, target.Rid))
                .Prepend(new RegistryPackageProjectionKey(SunderPackageProjectionFormat.SharedKind, null))
                .ToArray();
            installed.Add(new RegistryInstalledPackageState(
                package.PackageId,
                package.Version,
                package.DependsOn.Select(dependency => new RegistryPackageDependency(
                    dependency.PackageId,
                    dependency.VersionRange)).ToArray(),
                acquired));
        }
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
                [],
                []);
        }

        var result = await registryClient.ReadJsonAsync<RegistryResolveInstallPlanResponse>(response, cancellationToken);
        if (result is not null) return result;
        return new RegistryResolveInstallPlanResponse(false, [], [], [await registryClient.ReadErrorAsync(response, cancellationToken)], [], []);
    }
}

internal sealed record RegistryPackagePlanResolution(
    Uri Origin,
    RegistryResolveInstallPlanResponse Plan);
