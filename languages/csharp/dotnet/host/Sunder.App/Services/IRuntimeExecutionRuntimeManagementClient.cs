using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public interface IRuntimePackageRuntimePrerequisiteClient : IRuntimeClient
{
    Task<RuntimePackageRuntimePrerequisitePlan> PreviewPackageRuntimePrerequisitesAsync(
        RuntimePackageRuntimePrerequisiteRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeInstallPlan> PreviewExecutionRuntimeInstallAsync(
        RuntimeExecutionRuntimeInstallPreviewRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeOperationResult> ApplyExecutionRuntimeInstallAsync(
        RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken = default);
}

public interface IRuntimeExecutionRuntimeManagementClient : IRuntimeExecutionRuntimeClient, IRuntimePackageRuntimePrerequisiteClient
{
    Task<RuntimeExecutionRuntimeSettings> GetExecutionRuntimeSettingsAsync(CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeSettings> UpdateExecutionRuntimeSettingsAsync(
        RuntimeExecutionRuntimeSettingsUpdateRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeUpdateStatus> GetExecutionRuntimeUpdateStatusAsync(CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeCatalogPage> GetExecutionRuntimeCatalogAsync(
        RuntimeExecutionRuntimeCatalogRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RuntimeExecutionRuntimePackageReference>> GetExecutionRuntimeReferencesAsync(
        RuntimeExecutionRuntimeIdentity identity, CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeOperationResult> VerifyExecutionRuntimeAsync(
        RuntimeExecutionRuntimeIdentity identity, CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeUpdatePlan> PreviewExecutionRuntimeUpdateAsync(
        RuntimeExecutionRuntimeUpdatePreviewRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeUpdateResult> PrepareExecutionRuntimeUpdateAsync(
        RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeUpdateResult> ApplyExecutionRuntimeUpdateAsync(
        RuntimeExecutionRuntimeUpdateApplyRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeUninstallPlan> PreviewExecutionRuntimeUninstallAsync(
        RuntimeExecutionRuntimeUninstallPreviewRequest request, CancellationToken cancellationToken = default);
    Task<RuntimeExecutionRuntimeUninstallResult> ApplyExecutionRuntimeUninstallAsync(
        RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken = default);
}
