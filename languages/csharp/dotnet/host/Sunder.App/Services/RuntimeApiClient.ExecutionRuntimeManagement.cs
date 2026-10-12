using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient : IRuntimeExecutionRuntimeManagementClient
{
    public Task<RuntimePackageRuntimePrerequisitePlan> PreviewPackageRuntimePrerequisitesAsync(
        RuntimePackageRuntimePrerequisiteRequest request, CancellationToken cancellationToken = default)
        => _management.PreviewPackageRuntimePrerequisitesAsync(request, cancellationToken);

    public Task<RuntimeExecutionRuntimeInstallPlan> PreviewExecutionRuntimeInstallAsync(
        RuntimeExecutionRuntimeInstallPreviewRequest request, CancellationToken cancellationToken = default)
        => _management.PreviewExecutionRuntimeInstallAsync(request, cancellationToken);

    public Task<RuntimeExecutionRuntimeOperationResult> ApplyExecutionRuntimeInstallAsync(
        RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken = default)
        => _management.ApplyExecutionRuntimeInstallAsync(request, cancellationToken);

    public Task<RuntimeExecutionRuntimeSettings> GetExecutionRuntimeSettingsAsync(CancellationToken cancellationToken = default)
        => _management.GetExecutionRuntimeSettingsAsync(cancellationToken);

    public Task<RuntimeExecutionRuntimeSettings> UpdateExecutionRuntimeSettingsAsync(
        RuntimeExecutionRuntimeSettingsUpdateRequest request, CancellationToken cancellationToken = default)
        => _management.UpdateExecutionRuntimeSettingsAsync(request, cancellationToken);

    public Task<RuntimeExecutionRuntimeUpdateStatus> GetExecutionRuntimeUpdateStatusAsync(CancellationToken cancellationToken = default)
        => _management.GetExecutionRuntimeUpdateStatusAsync(cancellationToken);

    public Task<RuntimeExecutionRuntimeCatalogPage> GetExecutionRuntimeCatalogAsync(
        RuntimeExecutionRuntimeCatalogRequest request, CancellationToken cancellationToken = default)
        => _management.GetExecutionRuntimeCatalogAsync(request, cancellationToken);

    public Task<IReadOnlyList<RuntimeExecutionRuntimePackageReference>> GetExecutionRuntimeReferencesAsync(
        RuntimeExecutionRuntimeIdentity identity, CancellationToken cancellationToken = default)
        => _management.GetExecutionRuntimeReferencesAsync(identity, cancellationToken);

    public Task<RuntimeExecutionRuntimeOperationResult> VerifyExecutionRuntimeAsync(
        RuntimeExecutionRuntimeIdentity identity, CancellationToken cancellationToken = default)
        => _management.VerifyExecutionRuntimeAsync(identity, cancellationToken);

    public Task<RuntimeExecutionRuntimeUpdatePlan> PreviewExecutionRuntimeUpdateAsync(
        RuntimeExecutionRuntimeUpdatePreviewRequest request, CancellationToken cancellationToken = default)
        => _management.PreviewExecutionRuntimeUpdateAsync(request, cancellationToken);

    public Task<RuntimeExecutionRuntimeUpdateResult> PrepareExecutionRuntimeUpdateAsync(
        RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken = default)
        => _management.PrepareExecutionRuntimeUpdateAsync(request, cancellationToken);

    public Task<RuntimeExecutionRuntimeUpdateResult> ApplyExecutionRuntimeUpdateAsync(
        RuntimeExecutionRuntimeUpdateApplyRequest request, CancellationToken cancellationToken = default)
        => _management.ApplyExecutionRuntimeUpdateAsync(request, cancellationToken);

    public Task<RuntimeExecutionRuntimeUninstallPlan> PreviewExecutionRuntimeUninstallAsync(
        RuntimeExecutionRuntimeUninstallPreviewRequest request, CancellationToken cancellationToken = default)
        => _management.PreviewExecutionRuntimeUninstallAsync(request, cancellationToken);

    public Task<RuntimeExecutionRuntimeUninstallResult> ApplyExecutionRuntimeUninstallAsync(
        RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken = default)
        => _management.ApplyExecutionRuntimeUninstallAsync(request, cancellationToken);
}
