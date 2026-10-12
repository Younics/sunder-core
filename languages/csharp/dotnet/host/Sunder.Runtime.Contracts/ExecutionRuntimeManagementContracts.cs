using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public static class RuntimeExecutionRuntimeManagementLimits
{
    public const int MaximumPackageSources = 32;
    public const int MaximumItems = 1024;
    public const int MaximumCatalogPageSize = 128;
    public const int MaximumPreparedUpdates = 32;
}

/// <summary>Host-owned binary-update preference. Package/platform policy refresh remains independent.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeSettings(
    [property: JsonRequired] long Revision,
    [property: JsonRequired] bool AutomaticUpdatesEnabled = true);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeSettingsUpdateRequest(
    [property: JsonRequired] long ExpectedRevision,
    [property: JsonRequired] bool AutomaticUpdatesEnabled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeUpdateStatus(
    [property: JsonRequired] bool CheckInProgress,
    [property: JsonRequired] DateTimeOffset? LastCheckedAtUtc,
    [property: JsonRequired] DateTimeOffset? NextCheckAtUtc,
    [property: JsonRequired] string? LastErrorCode,
    IReadOnlyList<RuntimeExecutionRuntimePreparedUpdate> PreparedUpdates)
{
    private IReadOnlyList<RuntimeExecutionRuntimePreparedUpdate> _preparedUpdates
        = RuntimeContractCollections.FreezeBounded(PreparedUpdates, RuntimeExecutionRuntimeManagementLimits.MaximumPreparedUpdates);
    [JsonRequired]
    public IReadOnlyList<RuntimeExecutionRuntimePreparedUpdate> PreparedUpdates
    {
        get => _preparedUpdates;
        init => _preparedUpdates = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumPreparedUpdates);
    }
}

/// <summary>Catalog metadata is advisory; installation always requires a resolver-backed preview.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeCatalogRequest(ConfiguredOfficialSourceId? SourceId = null, int Skip = 0, int Take = 50);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeCatalogEntry(
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] RuntimeExecutionRuntimeVendorArtifact Artifact,
    [property: JsonRequired] bool Installed);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeCatalogPage(
    [property: JsonRequired] string Rid,
    [property: JsonRequired] int Skip,
    [property: JsonRequired] int Take,
    [property: JsonRequired] int TotalCount,
    IReadOnlyList<RuntimeExecutionRuntimeCatalogEntry> Items)
{
    private IReadOnlyList<RuntimeExecutionRuntimeCatalogEntry> _items
        = RuntimeContractCollections.FreezeBounded(Items, RuntimeExecutionRuntimeManagementLimits.MaximumCatalogPageSize);
    [JsonRequired]
    public IReadOnlyList<RuntimeExecutionRuntimeCatalogEntry> Items
    {
        get => _items;
        init => _items = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumCatalogPageSize);
    }
}

/// <summary>Opaque local consent handle bound to official selection and local impact; expiry is not an execution lease.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimePlanIdentity(
    [property: JsonRequired] string PlanId,
    [property: JsonRequired] string Fingerprint,
    [property: JsonRequired] DateTimeOffset ExpiresAtUtc,
    [property: JsonRequired] string Rid);

/// <summary>Confirms a Host plan; never supplies replacement artifacts or client-authored bindings.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimePlanConfirmation(
    [property: JsonRequired] string PlanId,
    [property: JsonRequired] string ExpectedFingerprint);

/// <summary>The same exact resolver evidence is used by install, prerequisites and runtime-only updates.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeResolution(
    [property: JsonRequired] RuntimeExecutionRuntimeRequirement Requirement,
    [property: JsonRequired] RuntimeExecutionRuntimeVendorArtifact Artifact,
    [property: JsonRequired] string ResolutionFingerprint,
    [property: JsonRequired] DateTimeOffset ExpiresAtUtc,
    [property: JsonRequired] bool AlreadyInstalled,
    [property: JsonRequired] long? DownloadSize);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeInstallPreviewRequest(
    [property: JsonRequired] RuntimeExecutionRuntimeRequirement Requirement,
    bool RetainUntilUninstall = true);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeInstallPlan(
    [property: JsonRequired] bool Success,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string Message,
    [property: JsonRequired] RuntimeExecutionRuntimePlanIdentity? Plan,
    [property: JsonRequired] RuntimeExecutionRuntimeResolution? Resolution,
    [property: JsonRequired] bool RetainUntilUninstall);

/// <summary>Authoritative usage, including disabled installed packages and non-package GC roots.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeUsage(
    [property: JsonRequired] string Fingerprint,
    IReadOnlyList<RuntimeExecutionRuntimePackageReference> PackageReferences,
    IReadOnlyList<string> AffectedPackageIds,
    IReadOnlyList<string> RunningPackageIds,
    [property: JsonRequired] int ActiveLeaseCount,
    [property: JsonRequired] int PendingTransactionCount,
    [property: JsonRequired] int TemporaryRetentionCount,
    [property: JsonRequired] int ManualRetentionCount,
    [property: JsonRequired] bool RequiresAppRestart)
{
    private IReadOnlyList<RuntimeExecutionRuntimePackageReference> _packageReferences
        = RuntimeContractCollections.FreezeBounded(PackageReferences, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<string> _affectedPackageIds
        = RuntimeContractCollections.FreezeBounded(AffectedPackageIds, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<string> _runningPackageIds
        = RuntimeContractCollections.FreezeBounded(RunningPackageIds, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    [JsonRequired]
    public IReadOnlyList<RuntimeExecutionRuntimePackageReference> PackageReferences
    {
        get => _packageReferences;
        init => _packageReferences = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
    [JsonRequired]
    public IReadOnlyList<string> AffectedPackageIds
    {
        get => _affectedPackageIds;
        init => _affectedPackageIds = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
    [JsonRequired]
    public IReadOnlyList<string> RunningPackageIds
    {
        get => _runningPackageIds;
        init => _runningPackageIds = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
}

/// <summary>Both bindings refer to unchanged package content; this is not a package-version selection.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeBindingChange(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string PackageVersion,
    [property: JsonRequired] string PackageContentIdentity,
    [property: JsonRequired] PackageTargetExecutionRuntimeBinding PreviousBinding,
    [property: JsonRequired] RuntimeExecutionRuntimeResolution Resolution);

/// <summary>Also describes updates of manually retained runtimes that have no package references.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeUpdateCandidate(
    [property: JsonRequired] RuntimeExecutionRuntimeIdentity CurrentRuntime,
    [property: JsonRequired] RuntimeExecutionRuntimeResolution Resolution,
    [property: JsonRequired] bool TransferManualRetention);

/// <summary>Neither selector means all managed runtimes; the two selectors are mutually exclusive.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeUpdatePreviewRequest(
    RuntimeExecutionRuntimeIdentity? Runtime = null,
    string? PreparedUpdateId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeUpdatePlan(
    [property: JsonRequired] bool Success,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string Message,
    [property: JsonRequired] RuntimeExecutionRuntimePlanIdentity? Plan,
    [property: JsonRequired] string? PreparedUpdateId,
    IReadOnlyList<RuntimeExecutionRuntimeUpdateCandidate> Runtimes,
    IReadOnlyList<RuntimeExecutionRuntimeBindingChange> BindingChanges,
    [property: JsonRequired] RuntimeExecutionRuntimeUsage Usage)
{
    private IReadOnlyList<RuntimeExecutionRuntimeUpdateCandidate> _runtimes
        = RuntimeContractCollections.FreezeBounded(Runtimes, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<RuntimeExecutionRuntimeBindingChange> _bindingChanges
        = RuntimeContractCollections.FreezeBounded(BindingChanges, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    [JsonRequired]
    public IReadOnlyList<RuntimeExecutionRuntimeUpdateCandidate> Runtimes
    {
        get => _runtimes;
        init => _runtimes = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
    [JsonRequired]
    public IReadOnlyList<RuntimeExecutionRuntimeBindingChange> BindingChanges
    {
        get => _bindingChanges;
        init => _bindingChanges = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
}

[JsonConverter(typeof(RuntimeContractStringEnumConverter<RuntimeExecutionRuntimeUpdateState>))]
public enum RuntimeExecutionRuntimeUpdateState
{
    NoChanges,
    Prepared,
    Deferred,
    Applied,
    ReconciliationPending,
    Stale,
    Blocked,
    Error,
}

/// <summary>Durable pending runtime transition, not a live package stage or an execution-policy lease.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimePreparedUpdate(
    [property: JsonRequired] string UpdateId,
    [property: JsonRequired] string Fingerprint,
    [property: JsonRequired] RuntimeExecutionRuntimeUpdateState State,
    [property: JsonRequired] DateTimeOffset PreparedAtUtc,
    IReadOnlyList<RuntimeExecutionRuntimeUpdateCandidate> Runtimes,
    IReadOnlyList<RuntimeExecutionRuntimeBindingChange> BindingChanges)
{
    private IReadOnlyList<RuntimeExecutionRuntimeUpdateCandidate> _runtimes
        = RuntimeContractCollections.FreezeBounded(Runtimes, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    private IReadOnlyList<RuntimeExecutionRuntimeBindingChange> _bindingChanges
        = RuntimeContractCollections.FreezeBounded(BindingChanges, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    [JsonRequired]
    public IReadOnlyList<RuntimeExecutionRuntimeUpdateCandidate> Runtimes
    {
        get => _runtimes;
        init => _runtimes = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
    [JsonRequired]
    public IReadOnlyList<RuntimeExecutionRuntimeBindingChange> BindingChanges
    {
        get => _bindingChanges;
        init => _bindingChanges = RuntimeContractCollections.FreezeBounded(value, RuntimeExecutionRuntimeManagementLimits.MaximumItems);
    }
}

[JsonConverter(typeof(RuntimeContractStringEnumConverter<RuntimeExecutionRuntimeApplyMode>))]
public enum RuntimeExecutionRuntimeApplyMode
{
    /// <summary>Never stops work. Defer until the affected graph is stopped, before its next activation.</summary>
    WhenStopped,
    /// <summary>User explicitly confirmed the fresh preview's complete restart impact.</summary>
    RestartAffectedPackages,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeUpdateApplyRequest(
    [property: JsonRequired] RuntimeExecutionRuntimePlanConfirmation Confirmation,
    [property: JsonRequired] string ExpectedImpactFingerprint,
    RuntimeExecutionRuntimeApplyMode Mode = RuntimeExecutionRuntimeApplyMode.WhenStopped);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeUpdateResult(
    [property: JsonRequired] bool Success,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string Message,
    [property: JsonRequired] RuntimeExecutionRuntimeUpdateState State,
    [property: JsonRequired] RuntimeExecutionRuntimePreparedUpdate? Update,
    bool StoreCommitted = false,
    bool RuntimeSessionApplied = false,
    RuntimePackageStamp? CommittedStamp = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeUninstallPreviewRequest(
    [property: JsonRequired] RuntimeExecutionRuntimeIdentity Runtime);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeUninstallPlan(
    [property: JsonRequired] bool Success,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string Message,
    [property: JsonRequired] RuntimeExecutionRuntimePlanIdentity? Plan,
    [property: JsonRequired] RuntimeExecutionRuntimeIdentity Runtime,
    [property: JsonRequired] RuntimeExecutionRuntimeUsage Usage,
    [property: JsonRequired] bool CanUninstall);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionRuntimeUninstallResult(
    [property: JsonRequired] bool Success,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string Message,
    [property: JsonRequired] RuntimeExecutionRuntimeIdentity Runtime,
    [property: JsonRequired] bool RetentionRemoved,
    [property: JsonRequired] bool RemovalPending);
