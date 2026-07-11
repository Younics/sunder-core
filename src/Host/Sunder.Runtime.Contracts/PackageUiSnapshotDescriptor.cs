namespace Sunder.Runtime.Contracts;

public sealed record PackageUiSnapshotDescriptor(
    string PackageId,
    PackageSourceKind SourceKind,
    long SessionGeneration,
    string ContentHash,
    string SnapshotId,
    string SnapshotUri);
