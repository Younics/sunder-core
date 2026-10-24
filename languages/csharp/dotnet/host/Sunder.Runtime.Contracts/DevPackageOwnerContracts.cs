namespace Sunder.Runtime.Contracts;

/// <summary>Prepares exact local or Registry-backed imports for an unpublished development manifest.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePrepareDevPackageImportsRequest(string Folder, string? RegistryOrigin = null)
{
    private IReadOnlyList<string> _candidateArchives = [];

    public IReadOnlyList<string> CandidateArchives
    {
        get => _candidateArchives;
        init => _candidateArchives = RuntimeContractCollections.Freeze(value);
    }

    public string? PlatformCatalogPath { get; init; }
}

/// <summary>Identifies retained development import evidence; this is not a publication or installation receipt.</summary>
public sealed record RuntimeDevPackageImportsPrepared(string PackageId, string Version, string ManifestSha256, int PackageArtifacts, int PlatformCatalogs);

public sealed record DevPackageOwnerFolder(string Folder, bool Watch);

public sealed record DevPackageOwnerMutationRequest(
    Guid RuntimeInstanceId,
    string OwnerToken,
    string MutationId,
    long Revision,
    IReadOnlyList<DevPackageOwnerFolder> Folders,
    int? ProcessId = null,
    DateTimeOffset? ProcessStartedAtUtc = null)
{
    private IReadOnlyList<DevPackageOwnerFolder> _folders = RuntimeContractCollections.Freeze(Folders);

    public IReadOnlyList<DevPackageOwnerFolder> Folders
    {
        get => _folders;
        init => _folders = RuntimeContractCollections.Freeze(value);
    }
}

public sealed record DevPackageOwnerHeartbeatRequest(
    Guid RuntimeInstanceId,
    string OwnerToken);

public sealed record DevPackageOwnerReleaseRequest(
    Guid RuntimeInstanceId,
    string OwnerToken);

public sealed record DevPackageOwnerReleaseStageRequest(
    Guid RuntimeInstanceId, string OwnerToken, long Revision, string MutationId);

public sealed record DevPackageOwnerReleaseCommitRequest(
    Guid RuntimeInstanceId, string OwnerToken, string StageId);

public sealed record DevPackageOwnerReleaseResult(
    Guid RuntimeInstanceId, string OwnerId, string StageId, RuntimePackageSnapshot Snapshot);

/// <summary>A finite reload of one package already held by this exact development owner.</summary>
public sealed record DevPackageOwnerReloadStageRequest(
    Guid RuntimeInstanceId, string OwnerToken, long Revision, string MutationId, string PackageId, string SourceContentIdentity);

public sealed record DevPackageOwnerReloadStageResult(
    string PackageId, string SourceContentIdentity, PackageLifecycleStageResult Stage);

public sealed record DevPackageOwnerReloadCommitRequest(
    Guid RuntimeInstanceId, string OwnerToken, string StageId);

public sealed record DevPackageOwnerReloadResult(
    Guid RuntimeInstanceId, string OwnerId, string StageId, string PackageId, string SourceContentIdentity, RuntimePackageSnapshot Snapshot);

public sealed record DevPackageOwnerPackage(
    string PackageId,
    bool Watch)
{
    /// <summary>Effective Host watch state, including other owners. Null on older Hosts.</summary>
    public bool? EffectiveWatch { get; init; }
}

public sealed record DevPackageOwnerLeaseResponse(
    Guid RuntimeInstanceId,
    string OwnerId,
    string MutationId,
    long Revision,
    DateTimeOffset ExpiresAtUtc,
    long SessionGeneration,
    IReadOnlyList<DevPackageOwnerPackage> Packages)
{
    private IReadOnlyList<DevPackageOwnerPackage> _packages = RuntimeContractCollections.Freeze(Packages);

    public IReadOnlyList<DevPackageOwnerPackage> Packages
    {
        get => _packages;
        init => _packages = RuntimeContractCollections.Freeze(value);
    }
}
