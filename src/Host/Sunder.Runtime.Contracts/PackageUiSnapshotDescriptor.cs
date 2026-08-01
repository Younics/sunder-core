namespace Sunder.Runtime.Contracts;

public sealed record PackageUiSnapshotDescriptor(
    string PackageId,
    PackageSourceKind SourceKind,
    long SessionGeneration,
    PackageTargetDescriptor Target,
    string ContentHash,
    string SnapshotId,
    string SnapshotUri);

public sealed record PackageUiSnapshotRequest(string AppRid);
