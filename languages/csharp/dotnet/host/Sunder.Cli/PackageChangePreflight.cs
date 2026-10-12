using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed record CliExecutionRuntimeDownload(
    RuntimeExecutionRuntimeResolution Resolution)
{
    public long? FullBytes => Resolution.DownloadSize;
    public long? IncrementalBytes => Resolution.AlreadyInstalled ? 0 : Resolution.DownloadSize;
    public bool AlreadyInstalled => Resolution.AlreadyInstalled;
}

internal sealed class CliPackageChangePreflight
{
    private CliPackageChangePreflight(
        bool success,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> items,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> conflicts,
        IReadOnlyList<PackageCapabilityConsent> requiredCapabilityConsents,
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation> planExpectations,
        IReadOnlyList<CliExecutionRuntimeDownload> runtimeDownloads,
        IReadOnlyList<string> skippedPackageIds,
        long packageDownloadBytes)
    {
        Success = success;
        Items = items;
        Warnings = warnings;
        Errors = errors;
        Conflicts = conflicts;
        RequiredCapabilityConsents = requiredCapabilityConsents;
        PlanExpectations = planExpectations;
        RuntimeDownloads = runtimeDownloads;
        SkippedPackageIds = skippedPackageIds;
        PackageDownloadBytes = packageDownloadBytes;
        FullRuntimeDownloadBytes = Sum(runtimeDownloads.Select(static item => item.FullBytes));
        IncrementalRuntimeDownloadBytes = Sum(runtimeDownloads.Select(static item => item.IncrementalBytes));
    }

    public bool Success { get; }
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> Items { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts { get; }
    public IReadOnlyList<PackageCapabilityConsent> RequiredCapabilityConsents { get; }
    public IReadOnlyList<RuntimeRegistryPackagePlanExpectation> PlanExpectations { get; }
    public IReadOnlyList<CliExecutionRuntimeDownload> RuntimeDownloads { get; }
    public IReadOnlyList<string> SkippedPackageIds { get; }
    public long PackageDownloadBytes { get; }
    public long FullRuntimeDownloadBytes { get; }
    public long IncrementalRuntimeDownloadBytes { get; }
    public long ReusedRuntimeBytes => FullRuntimeDownloadBytes - IncrementalRuntimeDownloadBytes;
    public long FullDownloadBytes => checked(PackageDownloadBytes + FullRuntimeDownloadBytes);
    public long IncrementalDownloadBytes => checked(PackageDownloadBytes + IncrementalRuntimeDownloadBytes);
    public bool HasConflicts => Conflicts.Count > 0;

    public static CliPackageChangePreflight Create(
        RuntimeRegistryResolveInstallPlanResponse plan,
        IReadOnlyList<InstalledExecutionRuntimeSummary> installedRuntimes,
        IReadOnlyList<string>? additionalWarnings = null,
        IReadOnlyList<string>? skippedPackageIds = null,
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation>? planExpectations = null)
        => Create([plan], installedRuntimes, additionalWarnings, skippedPackageIds, planExpectations);

    public static CliPackageChangePreflight Create(
        IReadOnlyList<RuntimeRegistryResolveInstallPlanResponse> plans,
        IReadOnlyList<InstalledExecutionRuntimeSummary> installedRuntimes,
        IReadOnlyList<string>? additionalWarnings = null,
        IReadOnlyList<string>? skippedPackageIds = null,
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation>? planExpectations = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(installedRuntimes);

        var items = plans.SelectMany(static plan => plan.Items)
            .DistinctBy(static item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var packageArtifacts = items
            .Where(static item => item.ActionKind is RegistryPackageActionKinds.PackageInstall
                or RegistryPackageActionKinds.PackageUpdate)
            .SelectMany(static item => item.Artifacts)
            .DistinctBy(PackageArtifactKey, StringComparer.Ordinal)
            .ToArray();
        var runtimeDownloads = plans
            .SelectMany(static plan => plan.RuntimePrerequisites?.Requirements ?? [])
            .Select(static prerequisite => prerequisite.Installation?.Resolution)
            .Where(static resolution => resolution is not null)
            .Cast<RuntimeExecutionRuntimeResolution>()
            .DistinctBy(RuntimeArtifactKey, StringComparer.Ordinal)
            .Select(static resolution => new CliExecutionRuntimeDownload(resolution))
            .OrderBy(static item => item.Resolution.Artifact.Identity.RuntimeId, StringComparer.Ordinal)
            .ThenBy(static item => item.Resolution.Artifact.Identity.VendorVersion, StringComparer.Ordinal)
            .ThenBy(static item => item.Resolution.Artifact.Identity.Rid, StringComparer.Ordinal)
            .ThenBy(static item => item.Resolution.Artifact.Identity.VendorChecksumDigest, StringComparer.Ordinal)
            .ToArray();
        var warnings = plans.SelectMany(static plan => plan.Warnings)
            .Concat(additionalWarnings ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var errors = plans.SelectMany(static plan => plan.Errors)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var conflicts = plans.SelectMany(static plan => plan.Conflicts).ToArray();
        var consents = plans.SelectMany(static plan => plan.RequiredCapabilityConsents)
            .DistinctBy(
                static consent => $"{consent.PackageId}\n{consent.PackageVersion}\n{consent.ManifestSha256}",
                StringComparer.Ordinal)
            .ToArray();
        var success = plans.Count > 0
                      && plans.All(static plan => plan.Success)
                      && errors.Length == 0
                      && conflicts.Length == 0;

        return new CliPackageChangePreflight(
            success,
            Array.AsReadOnly(items),
            Array.AsReadOnly(warnings),
            Array.AsReadOnly(errors),
            Array.AsReadOnly(conflicts),
            Array.AsReadOnly(consents),
            Array.AsReadOnly((planExpectations ?? []).ToArray()),
            Array.AsReadOnly(runtimeDownloads),
            Array.AsReadOnly((skippedPackageIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            Sum(packageArtifacts.Select(static artifact => (long?)artifact.Size)));
    }

    public static CliPackageChangePreflight FromPreview(
        RuntimeRegistryPackageChangeResult preview,
        IReadOnlyList<InstalledExecutionRuntimeSummary> installedRuntimes,
        string? additionalError = null)
    {
        var errors = string.IsNullOrWhiteSpace(additionalError)
            ? preview.Errors
            : preview.Errors.Concat([additionalError]).ToArray();
        var plan = new RuntimeRegistryResolveInstallPlanResponse(
            preview.Success && string.IsNullOrWhiteSpace(additionalError),
            preview.PlanItems,
            preview.Warnings,
            errors,
            preview.Conflicts,
            [],
            preview.RequiredCapabilityConsents,
            ResolutionFingerprint: string.Empty,
            ExpiresAtUtc: default)
        {
            RuntimePrerequisites = preview.RuntimePrerequisites,
        };
        return Create(
            plan,
            installedRuntimes,
            skippedPackageIds: preview.SkippedPackageIds,
            planExpectations: preview.PlanExpectations);
    }

    private static string PackageArtifactKey(RuntimeRegistryPackageProjectionArtifact artifact)
        => string.IsNullOrWhiteSpace(artifact.Sha256)
            ? $"{artifact.DownloadUrl}\n{artifact.Size}"
            : artifact.Sha256;

    private static string RuntimeArtifactKey(RuntimeExecutionRuntimeResolution resolution)
    {
        var identity = resolution.Artifact.Identity;
        return $"{identity.SourceId}\n{identity.RuntimeId}\n{identity.VendorVersion}\n{identity.Rid}\n"
               + $"{identity.ModuleAbi}\n{identity.VendorChecksumAlgorithm}\n{identity.VendorChecksumDigest}";
    }

    private static long Sum(IEnumerable<long?> values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            if (value is not null) total = checked(total + value.Value);
        }
        return total;
    }
}
