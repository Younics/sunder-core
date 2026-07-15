using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed record RegistryPackageInstallExecutionResult(
    bool Success,
    string Message,
    bool RuntimeSessionApplied,
    bool RequiresAppRestart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds,
    IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> PlanItems)
{
    public bool AppShellApplied { get; init; }

    public RuntimePackageStamp? CommittedStamp { get; init; }

    public static RegistryPackageInstallExecutionResult Empty(string message)
        => new(true, message, true, false, [], [], [], []);

    public static RegistryPackageInstallExecutionResult Failed(
        string message,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanItem>? planItems = null)
        => new(false, message, false, false, warnings ?? [], errors ?? [message], [], planItems ?? []);
}

public sealed record RegistryPackageInstallProgress(string StatusText, double? ProgressPercent);

public sealed class RegistryPackageInstallService
{
    public async Task<RegistryPackageInstallExecutionResult> InstallPackageAsync(
        string packageId,
        string? version,
        string? tag,
        bool allowDowngrade,
        bool reinstall,
        IRegistryClient registryClient,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new("Runtime is resolving and applying the package transaction...", 10));
        var result = await runtimeApiClient.InstallRegistryPackageAsync(
            new RuntimeRegistryPackageRequest(
                registryClient.RegistryUrl.AbsoluteUri,
                packageId,
                version,
                string.IsNullOrWhiteSpace(version) ? tag ?? "latest" : null,
                AllowDowngrade: allowDowngrade,
                Reinstall: reinstall),
            cancellationToken);
        progress?.Invoke(new(result.Message, 100));
        return ToAppResult(result);
    }

    public Task<RuntimeRegistryResolveInstallPlanResponse> ResolveInstallPlanForPackagesAsync(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        IRegistryClient registryClient,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new("Runtime is resolving the package graph...", 10));
        return runtimeApiClient.ResolveRegistryPackagePlanAsync(ToBatchRequest(packages, registryClient.RegistryUrl), cancellationToken);
    }

    public async Task<RegistryPackageInstallExecutionResult> InstallPackagesAsync(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        IRegistryClient registryClient,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new("Runtime is applying the package transaction...", 10));
        var result = await runtimeApiClient.ApplyRegistryPackagePlanAsync(ToBatchRequest(packages, registryClient.RegistryUrl), cancellationToken);
        progress?.Invoke(new(result.Message, 100));
        return ToAppResult(result);
    }

    public async Task<RegistryPackageInstallExecutionResult> UpdateAllAsync(
        IRegistryClient registryClient,
        IRuntimeRegistryPackageClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new("Runtime is resolving and applying updates...", 10));
        var result = await runtimeApiClient.UpdateRegistryPackagesAsync(
            new RuntimeRegistryUpdateRequest(registryClient.RegistryUrl.AbsoluteUri),
            cancellationToken);
        progress?.Invoke(new(result.Message, 100));
        return ToAppResult(result);
    }

    private static RuntimeRegistryPackageBatchRequest ToBatchRequest(
        IReadOnlyList<SunderStackPackageRequirement> packages,
        Uri registryUrl)
        => new RuntimeRegistryPackageBatchRequest(
            registryUrl.AbsoluteUri,
            packages
                .Where(package => !string.IsNullOrWhiteSpace(package.PackageId))
                .Select(package => new RuntimeRegistryPackageChangeRequest(
                    package.PackageId!,
                    null,
                    string.IsNullOrWhiteSpace(package.InstallTag) ? "latest" : package.InstallTag))
                .ToArray());

    private static RegistryPackageInstallExecutionResult ToAppResult(RuntimeRegistryPackageChangeResult result)
        => new RegistryPackageInstallExecutionResult(
            result.Success,
            result.Message,
            result.RuntimeSessionApplied,
            result.RequiresAppRestart,
            result.Warnings,
            result.Errors,
            result.ImpactedPackageIds,
            result.PlanItems)
        {
            CommittedStamp = result.CommittedStamp,
        };
}
