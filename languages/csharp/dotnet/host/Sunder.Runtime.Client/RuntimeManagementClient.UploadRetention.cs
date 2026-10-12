using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public Task<RuntimePackageUploadRetentionResult> RetainPackageUploadAsync(
        RuntimePackageUploadRetentionRequest request, CancellationToken token = default)
        => PostAsync<RuntimePackageUploadRetentionRequest, RuntimePackageUploadRetentionResult>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.PackageUploadRetention), request, RuntimeProtocolFeatures.PackageInstallResumptionV1, token);

    public Task<RuntimePackageUploadRetentionResult> RenewPackageUploadRetentionAsync(
        RuntimePackageUploadRetentionHandle request, CancellationToken token = default)
        => PostAsync<RuntimePackageUploadRetentionHandle, RuntimePackageUploadRetentionResult>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.PackageUploadRetentionRenew), request, RuntimeProtocolFeatures.PackageInstallResumptionV1, token);

    public Task<RuntimePackageUploadRetentionResult> GetPackageUploadRetentionAsync(
        RuntimePackageUploadRetentionHandle request, CancellationToken token = default)
        => PostAsync<RuntimePackageUploadRetentionHandle, RuntimePackageUploadRetentionResult>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.PackageUploadRetentionStatus), request, RuntimeProtocolFeatures.PackageInstallResumptionV1, token);

    public Task<RuntimePackageUploadRetentionResult> ReleasePackageUploadRetentionAsync(
        RuntimePackageUploadRetentionHandle request, CancellationToken token = default)
        => PostAsync<RuntimePackageUploadRetentionHandle, RuntimePackageUploadRetentionResult>(
            ExecutionRuntimeApiPath(RuntimeApiRoutes.PackageUploadRetentionRelease), request, RuntimeProtocolFeatures.PackageInstallResumptionV1, token);
}
