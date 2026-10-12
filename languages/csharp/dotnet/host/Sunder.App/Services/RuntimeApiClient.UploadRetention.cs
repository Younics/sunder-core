using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

public sealed partial class RuntimeApiClient
{
    public Task<RuntimePackageUploadRetentionResult> RetainPackageUploadAsync(
        RuntimePackageUploadRetentionRequest request, CancellationToken cancellationToken = default)
        => _management.RetainPackageUploadAsync(request, cancellationToken);

    public Task<RuntimePackageUploadRetentionResult> RenewPackageUploadRetentionAsync(
        RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default)
        => _management.RenewPackageUploadRetentionAsync(handle, cancellationToken);

    public Task<RuntimePackageUploadRetentionResult> GetPackageUploadRetentionAsync(
        RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default)
        => _management.GetPackageUploadRetentionAsync(handle, cancellationToken);

    public Task<RuntimePackageUploadRetentionResult> ReleasePackageUploadRetentionAsync(
        RuntimePackageUploadRetentionHandle handle, CancellationToken cancellationToken = default)
        => _management.ReleasePackageUploadRetentionAsync(handle, cancellationToken);
}
