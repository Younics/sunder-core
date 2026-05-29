using Sunder.Protocol;
using Sunder.Registry.Shared;

namespace Sunder.App.Services;

public sealed record RegistryPackageInstallExecutionResult(
    bool Success,
    string Message,
    bool RuntimeSessionApplied,
    bool RequiresAppRestart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds,
    IReadOnlyList<RegistryPackageInstallPlanItem> PlanItems)
{
    public bool AppShellApplied { get; init; }

    public static RegistryPackageInstallExecutionResult Empty(string message)
        => new(true, message, RuntimeSessionApplied: true, RequiresAppRestart: false, [], [], [], []);

    public static RegistryPackageInstallExecutionResult Failed(
        string message,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<RegistryPackageInstallPlanItem>? planItems = null)
        => new(false, message, RuntimeSessionApplied: false, RequiresAppRestart: false, warnings ?? [], errors ?? [message], [], planItems ?? []);
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
        IRegistryApiClient registryClient,
        IRuntimeApiClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<PackageStoreStageResult, CancellationToken, Task>? preflightPackageStoreStageAsync = null)
    {
        progress?.Invoke(new RegistryPackageInstallProgress("Reading installed package state...", 5));
        var installedPackages = await runtimeApiClient.GetInstalledPackagesAsync(cancellationToken);
        progress?.Invoke(new RegistryPackageInstallProgress("Resolving registry install plan...", 15));
        var request = new RegistryResolveInstallPlanRequest(
            packageId,
            version,
            string.IsNullOrWhiteSpace(version) ? tag ?? "latest" : null,
            ToInstalledPackageStates(installedPackages),
            AllowDowngrade: allowDowngrade,
            Reinstall: reinstall);
        var plan = await registryClient.ResolveInstallPlanAsync(request, cancellationToken);
        return plan.Success
            ? await ExecutePlanAsync(plan, allowDowngrade, reinstall, registryClient, runtimeApiClient, progress, cancellationToken, preflightPackageStoreStageAsync)
            : ToPlanFailure(plan);
    }

    public async Task<RegistryPackageInstallExecutionResult> UpdateAllAsync(
        IRegistryApiClient registryClient,
        IRuntimeApiClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<PackageStoreStageResult, CancellationToken, Task>? preflightPackageStoreStageAsync = null)
    {
        progress?.Invoke(new RegistryPackageInstallProgress("Reading installed package state...", 5));
        var installedPackages = await runtimeApiClient.GetInstalledPackagesAsync(cancellationToken);
        if (installedPackages.Count == 0)
        {
            return RegistryPackageInstallExecutionResult.Empty("No packages are installed.");
        }

        progress?.Invoke(new RegistryPackageInstallProgress("Resolving registry update plan...", 15));
        var plan = await registryClient.ResolvePackageChangesAsync(
            new RegistryResolvePackageChangesRequest(
                installedPackages
                    .Select(package => new RegistryPackageChangeRequest(package.PackageId, Version: null, Tag: "latest"))
                    .ToArray(),
                ToInstalledPackageStates(installedPackages)),
            cancellationToken);
        if (IsBatchPackageChangeResolverUnsupported(plan))
        {
            plan = await ResolveCompatibilityUpdateAllPlanAsync(registryClient, installedPackages, cancellationToken);
        }

        if (!plan.Success)
        {
            return ToPlanFailure(plan);
        }

        if (plan.Items.Count == 0)
        {
            return RegistryPackageInstallExecutionResult.Empty("All installed packages are up to date.");
        }

        return await ExecutePlanAsync(plan, allowDowngrade: false, reinstall: false, registryClient, runtimeApiClient, progress, cancellationToken, preflightPackageStoreStageAsync);
    }

    private static async Task<RegistryResolveInstallPlanResponse> ResolveCompatibilityUpdateAllPlanAsync(
        IRegistryApiClient registryClient,
        IReadOnlyList<InstalledPackageDescriptor> installedPackages,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>
        {
            "Registry does not support batch update planning; using compatibility update planning.",
        };
        var updates = await registryClient.ResolveUpdatesAsync(
            new RegistryResolveUpdatesRequest(
                installedPackages.Select(package => new RegistryInstalledPackage(package.PackageId, package.Version)).ToArray()),
            cancellationToken);
        if (updates.Updates.Count == 0)
        {
            return new RegistryResolveInstallPlanResponse(true, [], warnings, [], []);
        }

        var installedPackageStates = ToInstalledPackageStates(installedPackages);
        var mergedItems = new Dictionary<string, RegistryPackageInstallPlanItem>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var conflicts = new List<RegistryPackageInstallPlanConflict>();
        foreach (var update in updates.Updates)
        {
            var plan = await registryClient.ResolveInstallPlanAsync(
                new RegistryResolveInstallPlanRequest(
                    update.PackageId,
                    update.AvailableVersion,
                    Tag: null,
                    installedPackageStates,
                    AllowDowngrade: false,
                    Reinstall: false),
                cancellationToken);
            warnings.AddRange(plan.Warnings);
            errors.AddRange(plan.Errors);
            conflicts.AddRange(plan.Conflicts);
            if (!plan.Success)
            {
                continue;
            }

            foreach (var item in plan.Items)
            {
                if (mergedItems.TryGetValue(item.PackageId, out var existingItem))
                {
                    if (!string.Equals(existingItem.Version, item.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"Compatibility update planning produced conflicting versions for '{item.PackageId}': '{existingItem.Version}' and '{item.Version}'.");
                    }

                    continue;
                }

                mergedItems[item.PackageId] = item;
            }
        }

        return errors.Count == 0 && conflicts.Count == 0
            ? new RegistryResolveInstallPlanResponse(true, mergedItems.Values.ToArray(), warnings, [], [])
            : new RegistryResolveInstallPlanResponse(false, mergedItems.Values.ToArray(), warnings, errors, conflicts);
    }

    private static bool IsBatchPackageChangeResolverUnsupported(RegistryResolveInstallPlanResponse plan)
    {
        if (plan.Success)
        {
            return false;
        }

        return plan.Errors.Any(IsUnsupportedBatchResolverMessage);
    }

    private static bool IsUnsupportedBatchResolverMessage(string message)
        => string.Equals(message, "Method Not Allowed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(message, "Not Found", StringComparison.OrdinalIgnoreCase)
           || message.Contains("404", StringComparison.OrdinalIgnoreCase)
           || message.Contains("405", StringComparison.OrdinalIgnoreCase)
           || message.Contains("does not support batch package change planning", StringComparison.OrdinalIgnoreCase);

    private static async Task<RegistryPackageInstallExecutionResult> ExecutePlanAsync(
        RegistryResolveInstallPlanResponse plan,
        bool allowDowngrade,
        bool reinstall,
        IRegistryApiClient registryClient,
        IRuntimeApiClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress,
        CancellationToken cancellationToken,
        Func<PackageStoreStageResult, CancellationToken, Task>? preflightPackageStoreStageAsync)
    {
        if (plan.Items.Count == 0)
        {
            return RegistryPackageInstallExecutionResult.Empty("No package changes required.");
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "sunder-app-registry", Guid.NewGuid().ToString("N"));
        var warnings = plan.Warnings.ToList();
        var mutations = new List<PackageStoreMutationRequest>();

        try
        {
            for (var index = 0; index < plan.Items.Count; index++)
            {
                var item = plan.Items[index];
                cancellationToken.ThrowIfCancellationRequested();
                var progressBase = 20 + 70d * index / plan.Items.Count;
                var packagePath = Path.Combine(tempDirectory, $"{SanitizeFileName(item.PackageId)}.{SanitizeFileName(item.Version)}.sunderpkg");
                progress?.Invoke(new RegistryPackageInstallProgress($"Downloading {item.PackageId} {item.Version}...", progressBase));
                await registryClient.DownloadArtifactAsync(item.Artifact, item.PackageId, item.Version, packagePath, cancellationToken);

                if (!string.IsNullOrWhiteSpace(item.DeprecatedMessage))
                {
                    warnings.Add($"{item.PackageId} {item.Version} is deprecated: {item.DeprecatedMessage}");
                }

                progress?.Invoke(new RegistryPackageInstallProgress($"Installing {item.PackageId} {item.Version}...", Math.Min(progressBase + 20, 90)));
                mutations.Add(item.CurrentVersion is null
                    ? new PackageStoreMutationRequest(PackageStoreMutationKind.Install, PackagePath: packagePath)
                    : new PackageStoreMutationRequest(PackageStoreMutationKind.Upgrade, item.PackageId, packagePath, allowDowngrade, reinstall));
            }

            progress?.Invoke(new RegistryPackageInstallProgress("Staging package changes...", 88));
            var stage = await runtimeApiClient.StagePackageStoreChangesAsync(new PackageStoreStageRequest(mutations), cancellationToken);
            if (!stage.Success || stage.StageId is null)
            {
                var errors = stage.Errors.Count == 0
                    ? [stage.OperationResult.Message ?? "Package store stage failed."]
                    : stage.Errors;
                return new RegistryPackageInstallExecutionResult(
                    false,
                    errors[0],
                    RuntimeSessionApplied: false,
                    RequiresAppRestart: false,
                    warnings.Concat(stage.Warnings).ToArray(),
                    errors,
                    stage.ImpactedPackageIds,
                    plan.Items);
            }

            var committed = false;
            try
            {
                if (preflightPackageStoreStageAsync is not null && stage.ImpactedPackageIds.Count > 0)
                {
                    progress?.Invoke(new RegistryPackageInstallProgress("Preflighting package changes...", 90));
                    await preflightPackageStoreStageAsync(stage, cancellationToken);
                }

                progress?.Invoke(new RegistryPackageInstallProgress("Loading installed packages...", 92));
                var commit = await runtimeApiClient.CommitPackageStoreStageAsync(stage.StageId, cancellationToken);
                committed = true;
                return ToExecutionResult(commit, warnings, plan.Items);
            }
            catch (OperationCanceledException)
            {
                if (!committed)
                {
                    await runtimeApiClient.DiscardPackageStoreStageAsync(stage.StageId, CancellationToken.None);
                }

                throw;
            }
            catch (Exception ex)
            {
                if (!committed)
                {
                    await runtimeApiClient.DiscardPackageStoreStageAsync(stage.StageId, CancellationToken.None);
                }

                return new RegistryPackageInstallExecutionResult(
                    false,
                    ex.Message,
                    RuntimeSessionApplied: false,
                    RequiresAppRestart: false,
                    warnings.Concat(stage.Warnings).ToArray(),
                    [ex.Message],
                    stage.ImpactedPackageIds,
                    plan.Items);
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static RegistryPackageInstallExecutionResult ToExecutionResult(
        PackageOperationResult operationResult,
        IReadOnlyList<string> planWarnings,
        IReadOnlyList<RegistryPackageInstallPlanItem> planItems)
    {
        if (!operationResult.Success)
        {
            var errors = operationResult.Errors.Count == 0
                ? [operationResult.Message ?? "Package operation failed."]
                : operationResult.Errors;
            return new RegistryPackageInstallExecutionResult(
                false,
                errors[0],
                operationResult.RuntimeSessionApplied,
                operationResult.RequiresAppRestart,
                planWarnings.Concat(operationResult.Warnings.Where(warning => !IsRuntimeSessionReloadWarning(warning))).ToArray(),
                errors,
                operationResult.ImpactedPackageIds,
                planItems);
        }

        return new RegistryPackageInstallExecutionResult(
            true,
            string.IsNullOrWhiteSpace(operationResult.Message)
                ? $"Installed {planItems.Count} package change(s)."
                : operationResult.Message.Trim(),
            operationResult.RuntimeSessionApplied,
            operationResult.RequiresAppRestart,
            planWarnings.Concat(operationResult.Warnings).ToArray(),
            [],
            operationResult.ImpactedPackageIds,
            planItems);
    }

    private static RegistryPackageInstallExecutionResult ToPlanFailure(RegistryResolveInstallPlanResponse plan)
    {
        var errors = plan.Errors
            .Concat(plan.Conflicts.Select(conflict => conflict.Message))
            .DefaultIfEmpty("Install plan resolution failed.")
            .ToArray();
        return RegistryPackageInstallExecutionResult.Failed(errors[0], errors, plan.Warnings);
    }

    private static bool IsRuntimeSessionReloadWarning(string warning)
        => warning.StartsWith("Installed package changes are saved, but the running package session ", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<RegistryInstalledPackageState> ToInstalledPackageStates(IReadOnlyList<InstalledPackageDescriptor> packages)
        => packages
            .Select(package => new RegistryInstalledPackageState(
                package.PackageId,
                package.Version,
                package.DependsOn
                    .Select(dependency => new RegistryPackageDependency(dependency.PackageId, dependency.VersionRange))
                    .ToArray()))
            .ToArray();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Temporary download cleanup should not hide the registry install result.
        }
    }
}
