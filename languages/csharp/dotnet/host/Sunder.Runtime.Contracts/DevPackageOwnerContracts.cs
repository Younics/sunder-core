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

public sealed record DevPackageOwnerPackage(
    string PackageId,
    bool Watch);

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
