using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Loads built canonical development folders in the connected Host. Available to published App activations only.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentV1)]
public interface IPackageDevelopmentService
{
    /// <summary>Reviews runtime prerequisites and acquires an activation-owned development overlay.</summary>
    Task<IPackageDevelopmentSession> LoadAsync(string developmentFolder, bool watch, CancellationToken cancellationToken = default);
}

/// <summary>Owns one development folder. Disposal releases only this owner's overlay and watch request.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentV1)]
public interface IPackageDevelopmentSession : IAsyncDisposable
{
    /// <summary>The validated package identity of this session.</summary>
    string PackageId { get; }
    /// <summary>Observes the connected Host, including readiness and runtime failures.</summary>
    Task<PackageDevelopmentStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    /// <summary>Changes this owner's watch request without releasing other owners.</summary>
    Task SetWatchAsync(bool watch, CancellationToken cancellationToken = default);
}

/// <summary>Observed Host state. Runtime readiness does not imply that App views have finished activating.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentV1)]
public sealed record PackageDevelopmentStatus(
    string PackageId,
    bool IsLoaded,
    bool WatchEnabled,
    string Readiness,
    string? ErrorMessage,
    bool OverridesInstalledPackage)
{
    /// <summary>This session's watch request; WatchEnabled is the Host's aggregate effective watch state.</summary>
    public bool RequestedWatch { get; init; }
    /// <summary>App activation observation: Pending, Activated, Quarantined, Unavailable, or NotApplicable. Activated does not assert that every lazy view was opened.</summary>
    public string AppReadiness { get; init; } = "Pending";
    /// <summary>The exact validated source sampled from the Runtime, not proof that App activation has applied it. Null on older Hosts.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentRevisionsV1)]
    public PackageDevelopmentRevision? RuntimeRevision { get; init; }
    /// <summary>The sampled source whose ready Runtime and applicable published App activation match. Does not assert that every lazy view was opened.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentRevisionsV1)]
    public PackageDevelopmentRevision? AppliedRevision { get; init; }
}

/// <summary>Host-observed validated source identity. This is comparison data, not dispatch, liveness or cleanup authority.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentRevisionsV1)]
public sealed record PackageDevelopmentRevision(Guid RuntimeInstanceId, long SessionGeneration, string SourceContentIdentity)
{
    /// <summary>The Runtime which observed the validated source. A replacement Runtime is a different observation origin.</summary>
    public Guid RuntimeInstanceId { get; } = RuntimeInstanceId != Guid.Empty ? RuntimeInstanceId
        : throw new ArgumentException("A development revision requires its Runtime identity.", nameof(RuntimeInstanceId));
    /// <summary>The sampled Runtime session generation. Unrelated generation changes do not imply a new source revision.</summary>
    public long SessionGeneration { get; } = SessionGeneration >= 0 ? SessionGeneration
        : throw new ArgumentOutOfRangeException(nameof(SessionGeneration));
    /// <summary>The lower-case SHA-256 identity of the complete validated package source, not a path, timestamp or role-only hash.</summary>
    public string SourceContentIdentity { get; } = SourceContentIdentity is { Length: 64 }
        && SourceContentIdentity.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? SourceContentIdentity : throw new ArgumentException("A development revision requires a canonical source SHA-256 identity.", nameof(SourceContentIdentity));
}
