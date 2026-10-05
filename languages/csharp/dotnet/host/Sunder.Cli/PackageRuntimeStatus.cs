using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed record CliPackageRuntimeStatus(
    PackageTargetExecutionRuntimeBinding Binding,
    InstalledExecutionRuntimeSummary? InstalledRuntime,
    RuntimeExecutionRuntimeLifecycle Lifecycle,
    string LifecycleSource,
    bool EnsureRequired,
    bool RepairRequired,
    bool RebindRequired,
    bool UpdateRecommended,
    string? EnsureCommand,
    string? RepairCommand,
    string? RebindPreflightCommand,
    string? RebindApplyCommand,
    IReadOnlyList<string> ImpactedPackageIds,
    string ActionMessage)
{
    public static IReadOnlyList<CliPackageRuntimeStatus> Create(
        InstalledPackageDescriptor package,
        IReadOnlyList<InstalledPackageDescriptor> installedPackages,
        IReadOnlyList<InstalledExecutionRuntimeSummary> installedRuntimes)
        => package.ExecutionRuntimeBindings
            .OrderBy(static binding => binding.Role, StringComparer.Ordinal)
            .ThenBy(static binding => binding.Rid, StringComparer.Ordinal)
            .Select(binding => Create(package, binding, installedPackages, installedRuntimes))
            .ToArray();

    private static CliPackageRuntimeStatus Create(
        InstalledPackageDescriptor package,
        PackageTargetExecutionRuntimeBinding binding,
        IReadOnlyList<InstalledPackageDescriptor> installedPackages,
        IReadOnlyList<InstalledExecutionRuntimeSummary> installedRuntimes)
    {
        var installedRuntime = installedRuntimes.FirstOrDefault(runtime => SameIdentity(
            runtime.Identity,
            binding.Runtime));
        var lifecycle = installedRuntime?.Lifecycle ?? binding.Lifecycle;
        var ensureRequired = installedRuntime is null;
        var repairRequired = installedRuntime is not null
                             && (installedRuntime.Health.RepairRecommended
                                 || installedRuntime.Status is RuntimeExecutionRuntimeStatuses.Unhealthy
                                     or RuntimeExecutionRuntimeStatuses.Quarantined);
        var rebindRequired = ensureRequired
                             || lifecycle.State is ExecutionRuntimeLifecycleStates.Retired
                                 or ExecutionRuntimeLifecycleStates.Revoked
                                 or RuntimeExecutionRuntimeLifecycleStates.PolicyRequired;
        var updateRecommended = rebindRequired
                                || lifecycle.State == ExecutionRuntimeLifecycleStates.Deprecated;
        var impactedPackageIds = installedPackages
            .Where(candidate => candidate.ExecutionRuntimeBindings.Any(candidateBinding => SameIdentity(
                candidateBinding.Runtime,
                binding.Runtime)))
            .Select(static candidate => candidate.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var (preflightCommand, applyCommand) = RebindCommands(package);

        return new CliPackageRuntimeStatus(
            binding,
            installedRuntime,
            lifecycle,
            installedRuntime is null ? "package-binding" : "installed-runtime",
            ensureRequired,
            repairRequired,
            rebindRequired,
            updateRecommended,
            ensureRequired
                ? $"sunder runtime ensure {binding.Requirement.RuntimeId} \"{binding.Requirement.VersionRange}\" {binding.Requirement.ModuleAbi}"
                : null,
            repairRequired
                ? $"sunder runtime repair {binding.Runtime.RuntimeId} {binding.Runtime.Version} "
                  + $"{binding.Runtime.PackagingRevision} {binding.Runtime.Rid} {binding.Runtime.ModuleAbi} --yes"
                : null,
            updateRecommended ? preflightCommand : null,
            updateRecommended ? applyCommand : null,
            Array.AsReadOnly(impactedPackageIds),
            BuildActionMessage(
                ensureRequired,
                repairRequired,
                rebindRequired,
                lifecycle.State,
                impactedPackageIds,
                preflightCommand is not null));
    }

    private static (string? Preflight, string? Apply) RebindCommands(InstalledPackageDescriptor package)
    {
        if (package.Provenance.SourceKind != InstalledPackageSourceKind.Registry)
        {
            return (null, null);
        }
        return package.Provenance.VersionPolicy switch
        {
            InstalledPackageVersionPolicy.FollowTag => (
                $"sunder package update {package.PackageId} --dry-run",
                $"sunder package update {package.PackageId} --yes"),
            InstalledPackageVersionPolicy.ExplicitVersion => (
                $"sunder package install {package.Provenance.SourcePackageId ?? package.PackageId} --version {package.Version} --reinstall --dry-run",
                $"sunder package install {package.Provenance.SourcePackageId ?? package.PackageId} --version {package.Version} --reinstall --yes"),
            _ => (null, null),
        };
    }

    private static string BuildActionMessage(
        bool ensureRequired,
        bool repairRequired,
        bool rebindRequired,
        string lifecycle,
        IReadOnlyList<string> impactedPackageIds,
        bool canRebindFromRegistry)
    {
        if (ensureRequired)
        {
            return canRebindFromRegistry
                ? "The exact bound runtime is missing. Ensure or import runtime bytes, then use package preflight to select and rebind a compatible exact runtime."
                : "The exact bound runtime is missing. Ensure a compatible runtime or import the exact artifact, then reinstall this package from its source to rebind it.";
        }
        if (repairRequired)
        {
            return $"Repair may stop Workers for {impactedPackageIds.Count} package(s): {string.Join(", ", impactedPackageIds)}.";
        }
        if (rebindRequired)
        {
            return canRebindFromRegistry
                ? "This lifecycle state requires a new exact binding. Run package preflight to see the complete transaction impact before applying it."
                : "This lifecycle state requires a new exact binding. Reinstall this package from an authoritative source after ensuring a supported runtime.";
        }
        if (lifecycle == ExecutionRuntimeLifecycleStates.Deprecated)
        {
            return canRebindFromRegistry
                ? "The runtime remains usable, but package update preflight should be used to select a supported binding."
                : "The runtime remains usable, but the package should be rebuilt or reinstalled against a supported binding.";
        }
        return "No execution-runtime action is required.";
    }

    private static bool SameIdentity(
        RuntimeExecutionRuntimeIdentity first,
        RuntimeExecutionRuntimeIdentity second)
        => string.Equals(first.RuntimeId, second.RuntimeId, StringComparison.Ordinal)
           && string.Equals(first.Version, second.Version, StringComparison.Ordinal)
           && first.PackagingRevision == second.PackagingRevision
           && string.Equals(first.Rid, second.Rid, StringComparison.Ordinal)
           && string.Equals(first.ModuleAbi, second.ModuleAbi, StringComparison.Ordinal)
           && string.Equals(first.ManifestSha256, second.ManifestSha256, StringComparison.Ordinal)
           && string.Equals(first.ArtifactSha256, second.ArtifactSha256, StringComparison.Ordinal);
}
