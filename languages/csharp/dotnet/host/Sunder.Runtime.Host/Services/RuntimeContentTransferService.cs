using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeContentTransferService(
    RuntimeSessionOwner sessions,
    RuntimeContentTransferStore store)
{
    public RuntimePackageUploadRetentionResult RetainPackageUpload(RuntimePackageUploadRetentionRequest request)
        => store.RetainPackageUpload(request, sessions.Generation);
    public RuntimePackageUploadRetentionResult RenewPackageUploadRetention(RuntimePackageUploadRetentionHandle request)
        => store.GetPackageUploadRetention(request, renew: true);
    public RuntimePackageUploadRetentionResult GetPackageUploadRetention(RuntimePackageUploadRetentionHandle request)
        => store.GetPackageUploadRetention(request);
    public RuntimePackageUploadRetentionResult ReleasePackageUploadRetention(RuntimePackageUploadRetentionHandle request)
        => store.ReleasePackageUploadRetention(request);

    public Task<ContentUploadDescriptor> CreateUploadAsync(
        RuntimeUploadKind kind,
        Stream source,
        long? contentLength,
        string? expectedHash,
        string? fileName,
        string? contentType,
        CancellationToken cancellationToken)
        => store.CreateUploadAsync(
            kind,
            source,
            contentLength,
            expectedHash,
            fileName,
            contentType,
            sessions.Generation,
            cancellationToken);

    public RuntimeUploadLease AcquireDownload(string downloadId)
        => store.AcquireDownload(downloadId, sessions.Generation)
            ?? throw new RuntimeNotFoundException("The download was not found or belongs to a stale Runtime generation.");

    public void Release(RuntimeUploadLease lease) => store.ReleaseUpload(lease);
}
