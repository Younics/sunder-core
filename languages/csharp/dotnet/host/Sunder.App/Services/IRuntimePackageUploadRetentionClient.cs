using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public interface IRuntimePackageUploadRetentionClient : IRuntimeClient
{
    Task<RuntimePackageUploadRetentionResult> RetainPackageUploadAsync(
        RuntimePackageUploadRetentionRequest request, CancellationToken cancellationToken = default);
    Task<RuntimePackageUploadRetentionResult> RenewPackageUploadRetentionAsync(
        RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default);
    Task<RuntimePackageUploadRetentionResult> GetPackageUploadRetentionAsync(
        RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default);
    Task<RuntimePackageUploadRetentionResult> ReleasePackageUploadRetentionAsync(
        RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default);
}
