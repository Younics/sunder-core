using Sunder.Protocol;
using Sunder.Registry.Shared;

namespace Sunder.App.Services;

public sealed record RegistryPackageInstallExecutionResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ImpactedPackageIds,
    IReadOnlyList<RegistryPackageInstallPlanItem> PlanItems)
{
    public static RegistryPackageInstallExecutionResult Empty(string message)
        => new(true, message, [], [], [], []);

    public static RegistryPackageInstallExecutionResult Failed(
        string message,
        IReadOnlyList<string>? errors = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<RegistryPackageInstallPlanItem>? planItems = null)
        => new(false, message, warnings ?? [], errors ?? [message], [], planItems ?? []);
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
        CancellationToken cancellationToken = default)
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
            ? await ExecutePlanAsync(plan, allowDowngrade, reinstall, registryClient, runtimeApiClient, progress, cancellationToken)
            : ToPlanFailure(plan);
    }

    public async Task<RegistryPackageInstallExecutionResult> UpdateAllAsync(
        IRegistryApiClient registryClient,
        IRuntimeApiClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new RegistryPackageInstallProgress("Reading installed package state...", 5));
        var installedPackages = await runtimeApiClient.GetInstalledPackagesAsync(cancellationToken);
        if (installedPackages.Count == 0)
        {
            return RegistryPackageInstallExecutionResult.Empty("No packages are installed.");
        }

        var updates = await registryClient.ResolveUpdatesAsync(
            new RegistryResolveUpdatesRequest(
                installedPackages
                    .Select(package => new RegistryInstalledPackage(package.PackageId, package.Version))
                    .ToArray()),
            cancellationToken);

        if (updates.Updates.Count == 0)
        {
            return RegistryPackageInstallExecutionResult.Empty("All installed packages are up to date.");
        }

        var warnings = new List<string>();
        var errors = new List<string>();
        var impactedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planItems = new List<RegistryPackageInstallPlanItem>();

        foreach (var update in updates.Updates)
        {
            var result = await InstallPackageAsync(
                update.PackageId,
                update.AvailableVersion,
                tag: null,
                allowDowngrade: false,
                reinstall: false,
            registryClient,
            runtimeApiClient,
            progress,
            cancellationToken);

            warnings.AddRange(result.Warnings);
            planItems.AddRange(result.PlanItems);
            foreach (var impactedPackageId in result.ImpactedPackageIds)
            {
                impactedPackageIds.Add(impactedPackageId);
            }

            if (!result.Success)
            {
                errors.AddRange(result.Errors.Count == 0 ? [result.Message] : result.Errors);
                break;
            }
        }

        if (errors.Count > 0)
        {
            return new RegistryPackageInstallExecutionResult(
                false,
                errors[0],
                warnings,
                errors,
                impactedPackageIds.ToArray(),
                planItems);
        }

        return new RegistryPackageInstallExecutionResult(
            true,
            $"Updated {planItems.Select(item => item.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} package(s).",
            warnings,
            [],
            impactedPackageIds.ToArray(),
            planItems);
    }

    private static async Task<RegistryPackageInstallExecutionResult> ExecutePlanAsync(
        RegistryResolveInstallPlanResponse plan,
        bool allowDowngrade,
        bool reinstall,
        IRegistryApiClient registryClient,
        IRuntimeApiClient runtimeApiClient,
        Action<RegistryPackageInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (plan.Items.Count == 0)
        {
            return RegistryPackageInstallExecutionResult.Empty("No package changes required.");
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "sunder-app-registry", Guid.NewGuid().ToString("N"));
        var warnings = plan.Warnings.ToList();
        var impactedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                var operationResult = item.CurrentVersion is null
                    ? await runtimeApiClient.InstallPackageFromPathAsync(packagePath, cancellationToken)
                    : await runtimeApiClient.UpgradePackageFromPathAsync(item.PackageId, packagePath, allowDowngrade, reinstall, cancellationToken);

                if (!operationResult.Success)
                {
                    var errors = operationResult.Errors.Count == 0
                        ? [operationResult.Message ?? $"Package operation failed for {item.PackageId}."]
                        : operationResult.Errors;
                    return new RegistryPackageInstallExecutionResult(
                        false,
                        errors[0],
                        warnings.Concat(operationResult.Warnings).ToArray(),
                        errors,
                        impactedPackageIds.ToArray(),
                        plan.Items);
                }

                warnings.AddRange(operationResult.Warnings);
                foreach (var impactedPackageId in operationResult.ImpactedPackageIds)
                {
                    impactedPackageIds.Add(impactedPackageId);
                }
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }

        return new RegistryPackageInstallExecutionResult(
            true,
            $"Installed {plan.Items.Count} package change(s).",
            warnings,
            [],
            impactedPackageIds.ToArray(),
            plan.Items);
    }

    private static RegistryPackageInstallExecutionResult ToPlanFailure(RegistryResolveInstallPlanResponse plan)
    {
        var errors = plan.Errors
            .Concat(plan.Conflicts.Select(conflict => conflict.Message))
            .DefaultIfEmpty("Install plan resolution failed.")
            .ToArray();
        return RegistryPackageInstallExecutionResult.Failed(errors[0], errors, plan.Warnings);
    }

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
