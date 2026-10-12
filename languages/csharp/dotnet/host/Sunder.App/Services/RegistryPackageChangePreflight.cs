using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed record RegistryExecutionRuntimeDownload(RuntimeExecutionRuntimeIdentity Identity,
    bool AlreadyInstalled, long? DownloadSize, string SourceText);

public sealed class RegistryPackageChangePreflight
{
    private RegistryPackageChangePreflight(RuntimeRegistryResolveInstallPlanResponse plan,
        IReadOnlyList<RegistryExecutionRuntimeDownload> runtimes, IReadOnlyList<RuntimeRegistryPackagePlanExpectation> expectedPlans,
        long packageBytes)
    {
        Plan = plan;
        ExecutionRuntimes = runtimes;
        ExpectedPlans = expectedPlans;
        PackageDownloadBytes = packageBytes;
    }

    public RuntimeRegistryResolveInstallPlanResponse Plan { get; }
    public IReadOnlyList<RegistryExecutionRuntimeDownload> ExecutionRuntimes { get; }
    public IReadOnlyList<RuntimeRegistryPackagePlanExpectation> ExpectedPlans { get; }
    public long PackageDownloadBytes { get; }
    public long? SharedRuntimeDownloadBytes => ExecutionRuntimes.Any(item => !item.AlreadyInstalled && item.DownloadSize is null)
        ? null : ExecutionRuntimes.Where(item => !item.AlreadyInstalled).Sum(item => item.DownloadSize ?? 0);
    public long? TotalDownloadBytes => SharedRuntimeDownloadBytes is { } runtimes ? checked(PackageDownloadBytes + runtimes) : null;

    public static RegistryPackageChangePreflight Create(RuntimeRegistryResolveInstallPlanResponse plan,
        IReadOnlyList<InstalledExecutionRuntimeSummary> installed,
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation>? expectedPlans = null,
        RuntimePackageRuntimePrerequisitePlan? prerequisites = null)
    {
        var bytes = plan.Items.Where(item => item.ActionKind is RegistryPackageActionKinds.PackageInstall or RegistryPackageActionKinds.PackageUpdate)
            .SelectMany(item => item.Artifacts).DistinctBy(item => item.Sha256).Sum(item => item.Size);
        var runtimes = (prerequisites ?? plan.RuntimePrerequisites)?.Requirements.Select(item =>
        {
            var resolution = item.Installation?.Resolution;
            var identity = resolution?.Artifact.Identity ?? item.InstalledRuntime;
            return identity is null ? null : new RegistryExecutionRuntimeDownload(identity, item.Satisfied,
                item.Satisfied ? 0 : resolution?.DownloadSize, RuntimeManagementUi.Source(identity));
        }).OfType<RegistryExecutionRuntimeDownload>().DistinctBy(item => item.Identity).ToArray() ?? [];
        return new(plan, runtimes, expectedPlans ?? [], bytes);
    }

    public static Task<RegistryPackageChangePreflight> CreateAsync(RuntimeRegistryResolveInstallPlanResponse plan,
        IRuntimeRegistryPackageClient client, CancellationToken token,
        IReadOnlyList<RuntimeRegistryPackagePlanExpectation>? expectedPlans = null,
        RuntimePackageRuntimePrerequisitePlan? prerequisites = null)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Create(plan, [], expectedPlans, prerequisites));
    }

    public static string FormatBytes(long? bytes) => bytes switch
    {
        null => "Size available during download",
        >= 1024L * 1024L * 1024L => $"{bytes / (1024d * 1024d * 1024d):0.##} GB",
        >= 1024L * 1024L => $"{bytes / (1024d * 1024d):0.##} MB",
        >= 1024L => $"{bytes / 1024d:0.##} KB",
        _ => $"{bytes} bytes",
    };
}
