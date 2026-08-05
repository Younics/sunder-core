using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeContentTransferService(
    RuntimeSessionOwner sessions,
    RuntimeContentTransferStore store)
{
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
