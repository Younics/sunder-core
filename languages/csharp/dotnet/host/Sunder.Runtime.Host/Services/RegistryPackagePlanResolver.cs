using System.Net;
using System.Net.Http.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPackagePlanResolver(
    RegistryHttpClient registryClient,
    InstalledPackageStore installedPackages,
    ILogger<RegistryPackagePlanResolver> logger,
    TimeProvider timeProvider,
    RegistryPackagePolicySynchronizer? policySynchronizer = null)
{
    private static readonly TimeSpan MaximumRateLimitWait = TimeSpan.FromMinutes(1);

    public async Task<RegistryPackagePlanResolution> ResolveAsync(
        RuntimeRegistryPackageBatchRequest request,
        CancellationToken cancellationToken)
    {
        var origin = RegistryOrigin.Normalize(request.RegistryOrigin);
        try
        {
            return await ResolveForExecutionAsync(request, request.ExpectedResolutionFingerprint, cancellationToken);
        }
        catch (HttpRequestException error) when (error.StatusCode.HasValue)
        {
            return Failed(error.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            logger.LogWarning(
                "Package plan failed: Registry is not reachable at {RegistryOrigin}.",
                origin.AbsoluteUri);
            return Failed($"Registry is not reachable at {origin.AbsoluteUri}.");
        }
        catch (PackagePolicyDeniedException error)
        {
            return Failed(error.Code);
        }

        RegistryPackagePlanResolution Failed(string message) => new(origin, new(false, [], [], [message], [], []));
    }

    public async Task<RegistryPackagePlanResolution> ResolveForExecutionAsync(
        RuntimeRegistryPackageBatchRequest request,
        string? expectedResolutionFingerprint,
        CancellationToken cancellationToken)
    {
        var origin = RegistryOrigin.Normalize(request.RegistryOrigin);
        try
        {
            return await ResolveCoreAsync(origin, request, expectedResolutionFingerprint, cancellationToken);
        }
        catch (RegistryRateLimitException error) when (error.RetryAfter is { } delay && delay <= MaximumRateLimitWait)
        {
            // Repeat only the read, with the same expectation and a fresh policy observation.
            logger.LogInformation("Registry at {RegistryOrigin} rate limited package planning. Retrying once after {Delay}.", origin.AbsoluteUri, delay);
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
        return await ResolveCoreAsync(origin, request, expectedResolutionFingerprint, cancellationToken);
    }

    private async Task<RegistryPackagePlanResolution> ResolveCoreAsync(
        Uri origin,
        RuntimeRegistryPackageBatchRequest request,
        string? expectedResolutionFingerprint,
        CancellationToken cancellationToken)
    {
        var installed = new List<RegistryInstalledPackageState>();
        foreach (var package in await installedPackages.ListAsync(cancellationToken))
        {
            var validation = await InstalledPackageProjection.ValidateAsync(package, cancellationToken);
            if (!validation.Success || validation.Manifest is null)
            {
                throw new InvalidDataException(
                    $"Installed package '{package.PackageId}' cannot be sent to the Registry: {string.Join(" | ", validation.Errors)}");
            }

            var acquired = (InstalledPackageProjection.Keys(package) ?? SunderPackageTargetResolver.EnumerateTargets(validation.Manifest))
                .Select(target => new RegistryPackageProjectionKey(target.Role, target.Rid))
                .Prepend(new RegistryPackageProjectionKey(SunderPackageProjectionFormat.SharedKind, null))
                .ToArray();
            ValidateRuntimeBindings(package, validation.Manifest);
            installed.Add(new RegistryInstalledPackageState(
                package.PackageId,
                package.Version,
                package.DependsOn.Select(dependency => new RegistryPackageDependency(
                    dependency.PackageId,
                    dependency.VersionRange)).ToArray(),
                acquired,
                GetRegistryPackageContentIdentity(package)));
        }
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(origin, RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.PackageResolvePackageChanges)))
        {
            Content = JsonContent.Create(new RegistryResolvePackageChangesRequest(
                RuntimeRegistryContractMapper.ToRegistry(request.Packages),
                installed,
                RuntimeRegistryContractMapper.CompatibilityProfile,
                ExpectedResolutionFingerprint: expectedResolutionFingerprint,
                IncludePrerelease: request.IncludePrerelease,
                AllowDowngrade: request.AllowDowngrade,
                Reinstall: request.Reinstall)),
        };
        using var response = await registryClient.SendAsync(message, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            await registryClient.EnsureSuccessAsync(response, cancellationToken);
        }

        var result = await registryClient.ReadJsonAsync<RegistryResolveInstallPlanResponse>(response, cancellationToken);
        if (result is not null)
        {
            if (result.Success && policySynchronizer is not null)
                await policySynchronizer.RefreshPlanAsync(origin.AbsoluteUri, result, cancellationToken);
            return new(origin, result);
        }
        return new(origin, new(false, [], [], [await registryClient.ReadErrorAsync(response, cancellationToken)], [], []));
    }

    internal static void ValidateRuntimeBindings(
        InstalledPackageRecord package,
        SunderPackageManifest manifest)
    {
        var bindingsByTarget = new Dictionary<(string Role, string Rid), InstalledPackageExecutionRuntimeBindingRecord>();
        foreach (var binding in package.ExecutionRuntimeBindings)
        {
            if (binding is null || !bindingsByTarget.TryAdd((binding.Role, binding.Rid), binding))
            {
                throw RuntimeBindingMismatch(package.PackageId);
            }
        }

        InstalledPackageProjection.ValidateScope(InstalledPackageProjection.Keys(package), manifest);
        foreach (var target in InstalledPackageProjection.Targets(package, manifest)
                     .Where(static target => target?.ExecutionRuntime is not null)
                     .Select(static target => target!)
                     .OrderBy(static target => target.Role, StringComparer.Ordinal)
                     .ThenBy(static target => target.Rid, StringComparer.Ordinal))
        {
            var key = (target.Role!, target.Rid!);
            var requirement = target.ExecutionRuntime!;
            if (!bindingsByTarget.Remove(key, out var binding)
                || !string.Equals(binding.Requirement.RuntimeId, requirement.Id, StringComparison.Ordinal)
                || !string.Equals(binding.Requirement.VersionRange, requirement.VersionRange, StringComparison.Ordinal)
                || !string.Equals(binding.Requirement.ModuleAbi, requirement.ModuleAbi, StringComparison.Ordinal)
                || !string.Equals(binding.Runtime.RuntimeId, requirement.Id, StringComparison.Ordinal)
                || !string.Equals(binding.Runtime.Rid, target.Rid, StringComparison.Ordinal)
                || !string.Equals(binding.Runtime.ModuleAbi, requirement.ModuleAbi, StringComparison.Ordinal)
                || !PackageVersionRange.IsSatisfiedBy(binding.Runtime.VendorVersion, requirement.VersionRange!))
            {
                throw RuntimeBindingMismatch(package.PackageId);
            }
        }

        if (bindingsByTarget.Count != 0)
        {
            throw RuntimeBindingMismatch(package.PackageId);
        }
    }

    private static InvalidDataException RuntimeBindingMismatch(string packageId)
        => new(
            $"Installed package '{packageId}' execution-runtime bindings do not exactly match its strict manifest target requirements.");

    private static string GetRegistryPackageContentIdentity(InstalledPackageRecord package)
        => package.Provenance.SourceKind == InstalledPackageSourceKind.Registry
            ? package.Provenance.SourceIdentity
              ?? throw new InvalidDataException(
                  $"Installed Registry package '{package.PackageId}' has no source package content identity.")
            : package.SourcePackageContentIdentity ?? package.ContentIdentity;
}

internal sealed record RegistryPackagePlanResolution(
    Uri Origin,
    RegistryResolveInstallPlanResponse Plan);
