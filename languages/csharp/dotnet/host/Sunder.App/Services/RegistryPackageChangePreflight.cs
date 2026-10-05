using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed record RegistryExecutionRuntimeDownload(
    RuntimeRegistryResolvedExecutionRuntimeArtifact Artifact,
    bool AlreadyInstalled);

public sealed class RegistryPackageChangePreflight
{
    private RegistryPackageChangePreflight(
        RuntimeRegistryResolveInstallPlanResponse plan,
        IReadOnlyList<RegistryExecutionRuntimeDownload> executionRuntimes,
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation> expectedPlans,
        long packageDownloadBytes)
    {
        Plan = plan;
        ExecutionRuntimes = executionRuntimes;
        ExpectedPlans = expectedPlans;
        PackageDownloadBytes = packageDownloadBytes;
        SharedRuntimeDownloadBytes = executionRuntimes
            .Where(static runtime => !runtime.AlreadyInstalled)
            .Sum(static runtime => runtime.Artifact.ArtifactSize);
        ReusedSharedRuntimeBytes = executionRuntimes
            .Where(static runtime => runtime.AlreadyInstalled)
            .Sum(static runtime => runtime.Artifact.ArtifactSize);
    }

    public RuntimeRegistryResolveInstallPlanResponse Plan { get; }

    public IReadOnlyList<RegistryExecutionRuntimeDownload> ExecutionRuntimes { get; }

    public IReadOnlyList<RuntimeRegistryPackagePlanExpectation> ExpectedPlans { get; }

    public long PackageDownloadBytes { get; }

    public long SharedRuntimeDownloadBytes { get; }

    public long ReusedSharedRuntimeBytes { get; }

    public long TotalDownloadBytes => checked(PackageDownloadBytes + SharedRuntimeDownloadBytes);

    public static RegistryPackageChangePreflight Create(
        RuntimeRegistryResolveInstallPlanResponse plan,
        IReadOnlyList<InstalledExecutionRuntimeSummary> installedExecutionRuntimes,
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation>? expectedPlans = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(installedExecutionRuntimes);

        var packageArtifacts = plan.Items
            .Where(static item => item.ActionKind is RegistryPackageActionKinds.PackageInstall
                or RegistryPackageActionKinds.PackageUpdate)
            .SelectMany(static item => item.Artifacts)
            .DistinctBy(PackageArtifactKey)
            .ToArray();
        var runtimeArtifacts = plan.RuntimeArtifacts
            .DistinctBy(RuntimeArtifactKey)
            .Select(artifact => new RegistryExecutionRuntimeDownload(
                artifact,
                installedExecutionRuntimes.Any(installed => IsHealthyExactMatch(installed, artifact))))
            .ToArray();
        return new RegistryPackageChangePreflight(
            plan,
            Array.AsReadOnly(runtimeArtifacts),
            Array.AsReadOnly((expectedPlans ?? []).ToArray()),
            packageArtifacts.Sum(static artifact => artifact.Size));
    }

    public static async Task<RegistryPackageChangePreflight> CreateAsync(
        RuntimeRegistryResolveInstallPlanResponse plan,
        IRuntimeRegistryPackageClient runtimeApiClient,
        CancellationToken cancellationToken,
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation>? expectedPlans = null)
    {
        var installed = runtimeApiClient is IRuntimeExecutionRuntimeClient executionRuntimeClient
            ? await executionRuntimeClient.GetExecutionRuntimesAsync(cancellationToken).ConfigureAwait(false)
            : [];
        return Create(plan, installed, expectedPlans);
    }

    public static string FormatBytes(long bytes)
        => bytes switch
        {
            >= 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d * 1024d):0.##} GB",
            >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.##} MB",
            >= 1024L => $"{bytes / 1024d:0.##} KB",
            _ => $"{bytes} bytes",
        };

    private static string PackageArtifactKey(RuntimeRegistryPackageProjectionArtifact artifact)
        => string.IsNullOrWhiteSpace(artifact.Sha256)
            ? $"{artifact.DownloadUrl}\n{artifact.Size}"
            : artifact.Sha256;

    private static string RuntimeArtifactKey(RuntimeRegistryResolvedExecutionRuntimeArtifact artifact)
        => $"{artifact.Identity.RuntimeId}\n{artifact.Identity.Version}\n{artifact.Identity.PackagingRevision}\n{artifact.Identity.Rid}\n{artifact.Identity.ModuleAbi}\n{artifact.ArtifactSha256}";

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
}
