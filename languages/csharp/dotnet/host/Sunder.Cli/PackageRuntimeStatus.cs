using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed record CliPackageRuntimeStatus(
    PackageTargetExecutionRuntimeBinding Binding,
    InstalledExecutionRuntimeSummary? InstalledRuntime,
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
        var ensureRequired = installedRuntime is null;
        var repairRequired = installedRuntime is not null
                              && (installedRuntime.Health.RepairRecommended
                                  || installedRuntime.Status is RuntimeExecutionRuntimeStatuses.Unhealthy
                                      or RuntimeExecutionRuntimeStatuses.Quarantined);
        var rebindRequired = ensureRequired || installedRuntime?.Support.State == RuntimeExecutionRuntimeSupportState.EndOfLife;
        var updateRecommended = rebindRequired;
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
            ensureRequired,
            repairRequired,
            rebindRequired,
            updateRecommended,
            ensureRequired
                ? $"sunder runtime ensure {binding.Requirement.RuntimeId} \"{binding.Requirement.VersionRange}\" {binding.Requirement.ModuleAbi}"
                : null,
            repairRequired
                ? $"sunder runtime repair {SourceId(binding.Runtime.SourceId)} {binding.Runtime.RuntimeId} {binding.Runtime.VendorVersion} "
                  + $"{binding.Runtime.Rid} {binding.Runtime.ModuleAbi} {ChecksumAlgorithm(binding.Runtime.VendorChecksumAlgorithm)} {binding.Runtime.VendorChecksumDigest} --yes"
                : null,
            updateRecommended ? preflightCommand : null,
            updateRecommended ? applyCommand : null,
            Array.AsReadOnly(impactedPackageIds),
            BuildActionMessage(
                ensureRequired,
                repairRequired,
                rebindRequired,
                installedRuntime?.Support.State,
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
        RuntimeExecutionRuntimeSupportState? support,
        IReadOnlyList<string> impactedPackageIds,
        bool canRebindFromRegistry)
    {
        if (ensureRequired)
        {
            return canRebindFromRegistry
                ? "The exact bound runtime is missing. Ensure runtime bytes, then use package preflight to select and rebind a compatible exact runtime."
                : "The exact bound runtime is missing. Ensure a compatible runtime, then reinstall this package from its source to rebind it.";
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
        if (support == RuntimeExecutionRuntimeSupportState.EndOfLife)
        {
            return canRebindFromRegistry
                ? "The runtime support line has ended; package update preflight should be used to select a supported binding."
                : "The runtime support line has ended; the package should be rebuilt or reinstalled against a supported binding.";
        }
        return "No execution-runtime action is required.";
    }

    private static bool SameIdentity(
        RuntimeExecutionRuntimeIdentity first,
        RuntimeExecutionRuntimeIdentity second)
        => string.Equals(first.RuntimeId, second.RuntimeId, StringComparison.Ordinal)
           && first.SourceId == second.SourceId
           && string.Equals(first.VendorVersion, second.VendorVersion, StringComparison.Ordinal)
           && string.Equals(first.Rid, second.Rid, StringComparison.Ordinal)
           && string.Equals(first.ModuleAbi, second.ModuleAbi, StringComparison.Ordinal)
           && first.VendorChecksumAlgorithm == second.VendorChecksumAlgorithm
           && string.Equals(first.VendorChecksumDigest, second.VendorChecksumDigest, StringComparison.Ordinal);

    private static string SourceId(ConfiguredOfficialSourceId source) => source switch
    {
        ConfiguredOfficialSourceId.DotNet => "dotnet",
        ConfiguredOfficialSourceId.Node => "node",
        _ => source.ToString(),
    };

    private static string ChecksumAlgorithm(RuntimeExecutionRuntimeChecksumAlgorithm algorithm) => algorithm switch
    {
        RuntimeExecutionRuntimeChecksumAlgorithm.Sha512 => "sha512",
        RuntimeExecutionRuntimeChecksumAlgorithm.Sha256 => "sha256",
        _ => algorithm.ToString(),
    };
}
