using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Tests.TestSupport;

// Existing non-prerequisite fixtures explicitly model a source with no missing runtimes.
// Acquisition remains an error: only the prerequisite tests may supply that behavior.
internal interface IReadyRuntimePrerequisiteTestClient : IRuntimePackageRuntimePrerequisiteClient
{
    Task<RuntimePackageRuntimePrerequisitePlan> IRuntimePackageRuntimePrerequisiteClient.PreviewPackageRuntimePrerequisitesAsync(
        RuntimePackageRuntimePrerequisiteRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new RuntimePackageRuntimePrerequisitePlan(true, true, null, "Ready", new('a', 64), "osx-arm64", [], []));

    Task<RuntimeExecutionRuntimeInstallPlan> IRuntimePackageRuntimePrerequisiteClient.PreviewExecutionRuntimeInstallAsync(
        RuntimeExecutionRuntimeInstallPreviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<RuntimeExecutionRuntimeOperationResult> IRuntimePackageRuntimePrerequisiteClient.ApplyExecutionRuntimeInstallAsync(
        RuntimeExecutionRuntimePlanConfirmation request, CancellationToken cancellationToken) => throw new NotSupportedException();
}
