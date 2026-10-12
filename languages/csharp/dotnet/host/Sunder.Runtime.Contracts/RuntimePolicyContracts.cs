using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public static class RuntimePolicyLeaseStates
{
    public const string Valid = "valid";
    public const string Required = "required";
    public const string Expired = "expired";
    public const string ClockInvalid = "clock-invalid";
}

public static class RuntimeAppPolicySessionStates
{
    public const string Active = "active";
    public const string ShutdownRequested = "shutdown-requested";
    public const string TerminationRequired = "termination-required";
    public const string ExitProven = "exit-proven";
    public const string Closed = "closed";
}

public static class RuntimePolicyTiming
{
    public const int LeaseSeconds = 24 * 60 * 60;
    public const int RefreshSeconds = 5 * 60;
    public const int MaximumRefreshJitterSeconds = 30;
    public const int RequestDeadlineSeconds = 15;
    public const int MaximumRetrySeconds = 15 * 60;
    public const int AppHeartbeatGraceSeconds = 5;
    public const int AppGracefulShutdownSeconds = 5;
    public const int AppForcedExitVerificationSeconds = 10;
    public const int AppForcedExitRetrySeconds = 5;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePackagePolicySubject(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string Version,
    [property: JsonRequired] string PackageContentIdentity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePolicyWatermark(
    [property: JsonRequired] string RegistryOrigin,
    [property: JsonRequired] long PackagePolicyRevision,
    [property: JsonRequired] string PackagePolicyFingerprint,
    [property: JsonRequired] long CheckpointSequence,
    [property: JsonRequired] string CheckpointDigest,
    [property: JsonRequired] long TopologyGeneration,
    [property: JsonRequired] string TopologyFingerprint);

/// <summary>Continuous-clock evidence; wall UTC is display/audit data and never authorizes execution.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePolicyClockEvidence(
    [property: JsonRequired] string BootSessionId,
    [property: JsonRequired] string ClockSource,
    [property: JsonRequired] long TicksPerSecond,
    [property: JsonRequired] long RequestStartedTicks,
    [property: JsonRequired] long DeadlineTicks,
    [property: JsonRequired] long HighWaterTicks,
    [property: JsonRequired] string? VirtualMachineGeneration);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePolicyLeaseStatus(
    [property: JsonRequired] string State,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] RuntimePolicyWatermark? Watermark,
    [property: JsonRequired] string? ResponseNonce,
    [property: JsonRequired] string? ResponseSha256,
    [property: JsonRequired] DateTimeOffset? ObservedAtUtc,
    [property: JsonRequired] DateTimeOffset? ValidUntilUtc,
    [property: JsonRequired] RuntimePolicyClockEvidence? Clock);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePackagePolicyStatus(
    [property: JsonRequired] RuntimePackagePolicySubject Package,
    [property: JsonRequired] string PackageState,
    [property: JsonRequired] string VersionAvailability,
    [property: JsonRequired] string Disposition,
    [property: JsonRequired] string? BlockScope,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string? PublicReason,
    [property: JsonRequired] RuntimePolicyLeaseStatus Lease,
    [property: JsonRequired] string AppTerminationState);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePlatformCatalogIdentity(
    [property: JsonRequired] string AuthorityId,
    [property: JsonRequired] string ReleaseVersion,
    [property: JsonRequired] string CatalogSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePlatformPolicyStatus(
    [property: JsonRequired] RuntimePlatformCatalogIdentity Catalog,
    [property: JsonRequired] string LifecycleState,
    [property: JsonRequired] string Disposition,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] DateTimeOffset? RevokedAtUtc,
    [property: JsonRequired] RuntimePolicyLeaseStatus Lease);

/// <summary>Host-validated process identity. No filesystem path, bearer credential or package-controlled launch input.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeAppProcessIdentity(
    [property: JsonRequired] int ProcessId,
    [property: JsonRequired] string ProcessBirthIdentity,
    [property: JsonRequired] string ExecutableSha256,
    [property: JsonRequired] string AppBuildIdentity,
    [property: JsonRequired] Guid AppGenerationId,
    [property: JsonRequired] Guid RuntimeInstanceId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeAppPolicySessionOpenRequest([property: JsonRequired] RuntimeAppProcessIdentity Process);

/// <summary>Session ID is an identifier within the existing authenticated local transport, not a bearer credential.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeAppPolicySessionDescriptor(
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] RuntimeAppProcessIdentity Process,
    [property: JsonRequired] string State);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeAppPolicySessionRequest(
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] Guid AppGenerationId,
    [property: JsonRequired] Guid RuntimeInstanceId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeAppPolicySessionStatus(
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] string State,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] bool ShutdownRequired,
    IReadOnlyList<RuntimePackagePolicySubject> LoadedPackages)
{
    private IReadOnlyList<RuntimePackagePolicySubject> _loadedPackages = RuntimePolicyCollections.Freeze(LoadedPackages);
    [JsonRequired] public IReadOnlyList<RuntimePackagePolicySubject> LoadedPackages { get => _loadedPackages; init => _loadedPackages = RuntimePolicyCollections.Freeze(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeAppPackageLoadPermitRequest(
    [property: JsonRequired] RuntimeAppPolicySessionRequest Session,
    [property: JsonRequired] RuntimePackagePolicySubject Package)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SnapshotId { get; init; }
}

/// <summary>A permit follows durable session registration of the exact package and current policy validation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeAppPackageLoadPermitResult(
    [property: JsonRequired] bool Allowed,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string? PermitId,
    [property: JsonRequired] RuntimeAppPolicySessionRequest Session,
    [property: JsonRequired] RuntimePackagePolicySubject Package,
    [property: JsonRequired] RuntimePolicyLeaseStatus? Lease)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Development { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRpcContractIdentity(
    [property: JsonRequired] string ContractId,
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string DescriptorSha256,
    [property: JsonRequired] string AbiSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRequiredProviderPredicate(
    [property: JsonRequired] string ConsumerPackageId,
    [property: JsonRequired] string ConsumerRole,
    [property: JsonRequired] RuntimeRpcContractIdentity Contract,
    IReadOnlyList<string> Actions)
{
    private IReadOnlyList<string> _actions = RuntimePolicyCollections.Freeze(Actions);
    [JsonRequired] public IReadOnlyList<string> Actions { get => _actions; init => _actions = RuntimePolicyCollections.Freeze(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRequiredProviderUnsatisfiedDetails(IReadOnlyList<RuntimeRequiredProviderPredicate> Predicates)
{
    private IReadOnlyList<RuntimeRequiredProviderPredicate> _predicates = RuntimePolicyCollections.Freeze(Predicates);
    [JsonRequired] public IReadOnlyList<RuntimeRequiredProviderPredicate> Predicates { get => _predicates; init => _predicates = RuntimePolicyCollections.Freeze(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRequiredProviderCycleDetails(
    IReadOnlyList<string> PackageIds,
    IReadOnlyList<RuntimeRequiredProviderPredicate> Predicates)
{
    private IReadOnlyList<string> _packageIds = RuntimePolicyCollections.Freeze(PackageIds);
    private IReadOnlyList<RuntimeRequiredProviderPredicate> _predicates = RuntimePolicyCollections.Freeze(Predicates);
    [JsonRequired] public IReadOnlyList<string> PackageIds { get => _packageIds; init => _packageIds = RuntimePolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RuntimeRequiredProviderPredicate> Predicates { get => _predicates; init => _predicates = RuntimePolicyCollections.Freeze(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeRpcProviderSelectionDetails(
    [property: JsonRequired] RuntimeRpcContractIdentity Contract,
    [property: JsonRequired] string? SelectedPackageId,
    [property: JsonRequired] int MatchCount);

/// <summary>Local policy Problem Details with a closed, operation-specific diagnostic payload.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimePolicyProblemDetails<TDetails>(
    [property: JsonRequired] string Type,
    [property: JsonRequired] string Title,
    [property: JsonRequired] int Status,
    [property: JsonRequired] string Detail,
    [property: JsonRequired] string Instance,
    [property: JsonRequired] string Code,
    [property: JsonRequired] string TraceId,
    [property: JsonRequired] string CorrelationId,
    [property: JsonRequired] TDetails Details) where TDetails : class;

public static partial class RuntimeApiRoutes
{
    public const string PolicyStatus = Root + "/policy";
    public const string PolicyRefresh = Root + "/policy/refresh";
    public const string AppPolicySessions = Root + "/app-policy-sessions";

    public static string AppPolicySession(string sessionId) => $"{AppPolicySessions}/{Segment(sessionId, nameof(sessionId))}";
    public static string AppPolicyHeartbeat(string sessionId) => $"{AppPolicySession(sessionId)}/heartbeat";
    public static string AppPolicyLoadPermit(string sessionId) => $"{AppPolicySession(sessionId)}/load-permit";
    public static string AppPolicyShutdown(string sessionId) => $"{AppPolicySession(sessionId)}/shutdown";
    public static string PackagePolicy(string packageId) => $"{Root}/packages/{Segment(packageId, nameof(packageId))}/policy";
}

internal static class RuntimePolicyCollections
{
    internal static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(value => value is null)) throw new ArgumentException("Policy collections cannot contain null elements.", nameof(values));
        return RuntimeContractCollections.Freeze(values);
    }
}
