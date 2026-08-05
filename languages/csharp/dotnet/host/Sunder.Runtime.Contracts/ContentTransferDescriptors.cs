namespace Sunder.Runtime.Contracts;

public sealed record ContentUploadDescriptor(
    string UploadId,
    string ContentHash,
    long Length,
    string FileName,
    string ContentType);

public sealed record ContentDownloadDescriptor(
    string DownloadId,
    string ContentHash,
    long Length,
    string FileName,
    string ContentType,
    string DownloadUri);
