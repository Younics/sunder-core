using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Loads built canonical development folders in the connected Host. Available to published App activations only.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentV1)]
public interface IPackageDevelopmentService
{
    /// <summary>Reviews runtime prerequisites and acquires an activation-owned development overlay.</summary>
    Task<IPackageDevelopmentSession> LoadAsync(string developmentFolder, bool watch, CancellationToken cancellationToken = default);
    /// <summary>Reads diagnostics for a package previously loaded by this App activation, including after unload. Never acquires or reloads a development owner.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1)]
    Task<PackageDevelopmentLogs> GetLogsAsync(string packageId, PackageDevelopmentLogQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The connected App does not support retained scoped development logs.");
}

/// <summary>Owns one development folder. Disposal releases only this owner's overlay and watch request.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentV1)]
public interface IPackageDevelopmentSession : IAsyncDisposable
{
    /// <summary>Lists only this development package's registered views.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
    Task<IReadOnlyList<PackageDevelopmentView>> GetViewsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The App does not support development view discovery.");
    /// <summary>Explicitly opens one registered view of this exact development package.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
    Task OpenViewAsync(string viewId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The App does not support development view opening.");
    /// <summary>Queries only the requested semantic portion of this development view.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
    Task<PackageDevelopmentViewPage> QueryViewAsync(string viewId, PackageDevelopmentViewQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The App does not support development view queries.");
    /// <summary>Captures only this view or an inspected element, including native surfaces. The bounded PNG must be consumed before its scope retires.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentFeedbackV1)]
    Task<Sunder.Sdk.Services.SunderContent> CaptureViewAsync(string viewId, PackageDevelopmentViewQuery scope, int maximumEdge = 1280, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The App does not support complete development view capture.");
    /// <summary>The validated package identity of this session.</summary>
    string PackageId { get; }
    /// <summary>Observes the connected Host, including readiness and runtime failures.</summary>
    Task<PackageDevelopmentStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    /// <summary>Changes this owner's watch request without releasing other owners.</summary>
    Task SetWatchAsync(bool watch, CancellationToken cancellationToken = default);
    /// <summary>Stages and applies one exact built output, without building or enabling watch. Retry the same operation id to resolve a lost acknowledgement.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentReloadV1)]
    Task<PackageDevelopmentRevision> ReloadAsync(string operationId, string sourceContentIdentity, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The connected App does not support explicit development reload.");
    /// <summary>Reads bounded recent Runtime and App logs for this exact development package, never other packages or shell logs.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentLogsV1)]
    Task<PackageDevelopmentLogs> GetLogsAsync(int maximumEntries = 50, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The connected App does not support scoped development logs.");
    /// <summary>Filters package diagnostics before selecting the latest bounded entries.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1)]
    Task<PackageDevelopmentLogs> GetLogsAsync(PackageDevelopmentLogQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The connected App does not support filtered development logs.");
    /// <summary>Inspects an already open view of this exact development package. Returned labels are untrusted; references expire and never follow replacement views.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentViewsV1)]
    Task<PackageDevelopmentViewSnapshot> InspectViewAsync(string viewId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The connected App does not support scoped development view inspection.");
    /// <summary>Requests one reference-scoped UI action. Repeating the same operation identity only observes its original receipt; dispatched is not proof of the resulting application state.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentViewsV1)]
    Task<PackageDevelopmentViewInputResult> InteractViewAsync(PackageDevelopmentViewInput request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The connected App does not support scoped development view input.");
}

/// <summary>Untrusted package diagnostics, bounded by both entry count and aggregate text size.</summary>
/// <param name="PackageId">The development owner's exact package identity.</param>
/// <param name="Entries">Recent package-filtered diagnostics, oldest first.</param>
/// <param name="WasTruncated">Whether selected diagnostic text was shortened. HasMore and HistoryGap describe separate omissions.</param>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentLogsV1)]
public sealed record PackageDevelopmentLogs(string PackageId, IReadOnlyList<PackageDevelopmentLogEntry> Entries, bool WasTruncated)
{
    /// <summary>More retained matching entries exist before this page.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1)]
    public bool HasMore { get; init; }
    /// <summary>Some source history is no longer retained, or the cursor's observation origin changed.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1)]
    public bool HistoryGap { get; init; }
    /// <summary>Opaque continuation for older matching entries. Never grants package access.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1)]
    public string? NextCursor { get; init; }
}

/// <summary>One untrusted Runtime or App diagnostic belonging to the selected development package.</summary>
/// <param name="Timestamp">The recorded diagnostic time.</param>
/// <param name="Source">The Runtime or App observation source.</param>
/// <param name="Level">The diagnostic severity label.</param>
/// <param name="Category">The bounded package diagnostic category.</param>
/// <param name="Message">The bounded diagnostic message.</param>
/// <param name="WasTruncated">Whether this diagnostic was shortened.</param>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentLogsV1)]
public sealed record PackageDevelopmentLogEntry(DateTimeOffset Timestamp, string Source, string Level, string Category, string Message, bool WasTruncated)
{
    /// <summary>Bounded, sanitized diagnostic identity fields; never raw payloads, credentials or exception dumps.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentLogQueriesV1)]
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

/// <summary>Observed Host state. Runtime readiness does not imply that App views have finished activating.</summary>
[SunderSdkCapability(SunderSdkCapabilities.PackageDevelopmentV1)]
public sealed record PackageDevelopmentStatus(
    string PackageId,
    bool IsLoaded,
    bool WatchEnabled,
    PackageDevelopmentRuntimeReadiness RuntimeReadiness,
    string? ErrorMessage,
    bool OverridesInstalledPackage)
{
    /// <summary>This session's watch request; WatchEnabled is the Host's aggregate effective watch state.</summary>
    public bool RequestedWatch { get; init; }
    /// <summary>The observed Host connection/bootstrap state, independent of this package's Runtime and App readiness.</summary>
    public PackageDevelopmentHostState HostState { get; init; }
    /// <summary>The observed App activation state. Activated does not assert that every lazy view was opened.</summary>
    public PackageDevelopmentAppReadiness AppReadiness { get; init; }
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
