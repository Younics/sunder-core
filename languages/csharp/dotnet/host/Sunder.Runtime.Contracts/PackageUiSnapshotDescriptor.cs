namespace Sunder.Runtime.Contracts;

public sealed record PackageUiSnapshotDescriptor(
    string PackageId,
    PackageSourceKind SourceKind,
    long SessionGeneration,
    PackageTargetDescriptor Target,
    string ContentHash,
    string SnapshotId,
    string SnapshotUri)
{
    public PackageUiProjectionReferenceDescriptor? ProjectionReference { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RuntimePackagePolicySubject? PolicySubject { get; init; }
}

public sealed record PackageUiProjectionReferenceDescriptor(
    int FormatRevision,
    string CacheKey,
    string ConsumerRole,
    string SourceContentIdentity,
    string ProjectionRulesIdentity,
    IReadOnlyList<string> TargetKeys);

public sealed record PackageUiSnapshotRequest(string AppRid);
