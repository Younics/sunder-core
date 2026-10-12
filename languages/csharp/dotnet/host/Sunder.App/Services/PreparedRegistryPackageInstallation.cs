using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed record RegistryInstallationPreview(
    RuntimeRegistryResolveInstallPlanResponse Plan,
    RuntimePackageRuntimePrerequisitePlan Runtimes,
    IReadOnlyList<RuntimeRegistryPackagePlanExpectation> ExpectedPlans);

internal sealed class PreparedRegistryPackageInstallation(
    IRuntimeRegistryPackageClient client,
    RegistryInstallationPreview approved,
    Func<CancellationToken, Task<RegistryInstallationPreview>> preview,
    Func<RegistryInstallationPreview, RuntimePackageRuntimePrerequisiteExpectation, CancellationToken, Task<RuntimeRegistryPackageChangeResult>> apply)
{
    private readonly PackageInstallationApproval _runtimes = new(approved.Runtimes);
    private readonly string _packageSemantics = PackageInstallationApproval.PackageSemantics(approved.Plan);
    public bool NeedsRuntimeInstallation => _runtimes.NeedsRuntimeInstallation;
    public IReadOnlyList<string> PackageIds => approved.Plan.Items.Select(item => item.PackageId).Distinct(StringComparer.Ordinal).ToArray();

    private async Task<RegistryInstallationPreview> RefreshAsync(CancellationToken token)
    {
        var current = await preview(token).ConfigureAwait(false);
        if (PackageInstallationApproval.PackageSemantics(current.Plan) != _packageSemantics
            || !approved.ExpectedPlans.Select(item => (item.RegistryOrigin, item.IncludePrerelease)).OrderBy(item => item.RegistryOrigin, StringComparer.Ordinal).ThenBy(item => item.IncludePrerelease)
                .SequenceEqual(current.ExpectedPlans.Select(item => (item.RegistryOrigin, item.IncludePrerelease)).OrderBy(item => item.RegistryOrigin, StringComparer.Ordinal).ThenBy(item => item.IncludePrerelease)))
            throw PackageInstallationApproval.Changed();
        _runtimes.Validate(current.Runtimes);
        return current;
    }

    public Task InstallRuntimesAsync(BackgroundProcessContext context)
        => _runtimes.InstallRuntimesAsync(client, async token => (await RefreshAsync(token).ConfigureAwait(false)).Runtimes, context);

    public async Task<RuntimeRegistryPackageChangeResult> ApplyAsync(BackgroundProcessContext context)
    {
        context.ReportIndeterminate("Rechecking the approved package changes…");
        var current = await RefreshAsync(context.CancellationToken).ConfigureAwait(false);
        _runtimes.Validate(current.Runtimes, requireReady: true);
        context.ReportIndeterminate("Downloading and installing packages…");
        var result = await apply(current, new(current.Runtimes.SourceFingerprint, current.Runtimes.Rid), context.CancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.Errors.FirstOrDefault() ?? result.Message);
        return result;
    }
}
