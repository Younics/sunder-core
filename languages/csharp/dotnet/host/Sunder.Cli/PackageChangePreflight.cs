using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed record CliExecutionRuntimeDownload(
    RuntimeRegistryResolvedExecutionRuntimeArtifact Artifact,
    bool AlreadyInstalled)
{
    public long IncrementalBytes => AlreadyInstalled ? 0 : Artifact.ArtifactSize;
}

internal sealed class CliPackageChangePreflight
{
    private CliPackageChangePreflight(
        bool success,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> items,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> conflicts,
        IReadOnlyList<RuntimeRegistryExecutionRuntimeResolutionConflict> runtimeConflicts,
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
        RuntimeConflicts = runtimeConflicts;
        RequiredCapabilityConsents = requiredCapabilityConsents;
        PlanExpectations = planExpectations;
        RuntimeDownloads = runtimeDownloads;
        SkippedPackageIds = skippedPackageIds;
        PackageDownloadBytes = packageDownloadBytes;
        FullRuntimeDownloadBytes = Sum(runtimeDownloads.Select(static item => item.Artifact.ArtifactSize));
        IncrementalRuntimeDownloadBytes = Sum(runtimeDownloads.Select(static item => item.IncrementalBytes));
    }

    public bool Success { get; }
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanItem> Items { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<RuntimeRegistryPackageInstallPlanConflict> Conflicts { get; }
    public IReadOnlyList<RuntimeRegistryExecutionRuntimeResolutionConflict> RuntimeConflicts { get; }
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
    public bool HasConflicts => Conflicts.Count > 0 || RuntimeConflicts.Count > 0;

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
            .SelectMany(static plan => plan.RuntimeArtifacts)
            .DistinctBy(RuntimeArtifactKey, StringComparer.Ordinal)
            .Select(artifact => new CliExecutionRuntimeDownload(
                artifact,
                installedRuntimes.Any(installed => IsHealthyExactMatch(installed, artifact))))
            .OrderBy(static item => item.Artifact.Identity.RuntimeId, StringComparer.Ordinal)
            .ThenBy(static item => item.Artifact.Identity.Version, StringComparer.Ordinal)
            .ThenBy(static item => item.Artifact.Identity.PackagingRevision)
            .ThenBy(static item => item.Artifact.Identity.Rid, StringComparer.Ordinal)
            .ToArray();
        var warnings = plans.SelectMany(static plan => plan.Warnings)
            .Concat(additionalWarnings ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var errors = plans.SelectMany(static plan => plan.Errors)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var conflicts = plans.SelectMany(static plan => plan.Conflicts).ToArray();
        var runtimeConflicts = plans.SelectMany(static plan => plan.RuntimeConflicts).ToArray();
        var consents = plans.SelectMany(static plan => plan.RequiredCapabilityConsents)
            .DistinctBy(
                static consent => $"{consent.PackageId}\n{consent.PackageVersion}\n{consent.ManifestSha256}",
                StringComparer.Ordinal)
            .ToArray();
        var success = plans.Count > 0
                      && plans.All(static plan => plan.Success)
                      && errors.Length == 0
                      && conflicts.Length == 0
                      && runtimeConflicts.Length == 0;

        return new CliPackageChangePreflight(
            success,
            Array.AsReadOnly(items),
            Array.AsReadOnly(warnings),
            Array.AsReadOnly(errors),
            Array.AsReadOnly(conflicts),
            Array.AsReadOnly(runtimeConflicts),
            Array.AsReadOnly(consents),
            Array.AsReadOnly((planExpectations ?? []).ToArray()),
            Array.AsReadOnly(runtimeDownloads),
            Array.AsReadOnly((skippedPackageIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            Sum(packageArtifacts.Select(static artifact => artifact.Size)));
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
            RuntimeArtifacts: preview.RuntimeArtifacts,
            RuntimeConflicts: preview.RuntimeConflicts);
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

    private static string RuntimeArtifactKey(RuntimeRegistryResolvedExecutionRuntimeArtifact artifact)
        => $"{artifact.Identity.RuntimeId}\n{artifact.Identity.Version}\n{artifact.Identity.PackagingRevision}\n"
           + $"{artifact.Identity.Rid}\n{artifact.Identity.ModuleAbi}\n{artifact.ArtifactSha256}";

    private static bool IsHealthyExactMatch(
        InstalledExecutionRuntimeSummary installed,
        RuntimeRegistryResolvedExecutionRuntimeArtifact artifact)
        => string.Equals(installed.Status, RuntimeExecutionRuntimeStatuses.Ready, StringComparison.Ordinal)
           && string.Equals(installed.Health.Status, RuntimeExecutionRuntimeStatuses.Ready, StringComparison.Ordinal)
           && string.Equals(installed.Identity.RuntimeId, artifact.Identity.RuntimeId, StringComparison.Ordinal)
           && string.Equals(installed.Identity.Version, artifact.Identity.Version, StringComparison.Ordinal)
           && installed.Identity.PackagingRevision == artifact.Identity.PackagingRevision
           && string.Equals(installed.Identity.Rid, artifact.Identity.Rid, StringComparison.Ordinal)
           && string.Equals(installed.Identity.ModuleAbi, artifact.Identity.ModuleAbi, StringComparison.Ordinal)
           && string.Equals(installed.Identity.ManifestSha256, artifact.ManifestSha256, StringComparison.Ordinal)
           && string.Equals(installed.Identity.ArtifactSha256, artifact.ArtifactSha256, StringComparison.Ordinal);

    private static long Sum(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            total = checked(total + value);
        }
        return total;
    }
}
