using System.Text.Json.Serialization;

namespace Sunder.Registry.Contracts;

public static class RegistryPolicyLimits
{
    public const int MaximumPackageSubjects = 128;
    public const int MaximumPlatformSubjects = 128;
    public const int MaximumRuntimeSubjects = 128;
    public const int MaximumCohortVersions = 64;
    public const int MaximumCohortTagChanges = 64;
    public const int NonceBytes = 16;
    public const int LeaseSeconds = 24 * 60 * 60;
}

public static class RegistryPackagePolicyStates
{
    public const string Active = "Active";
    public const string SecurityBlocked = "SecurityBlocked";
    public const string Unknown = "Unknown";
}

public static class RegistryPackageVersionAvailability
{
    public const string Listed = "Listed";
    public const string Yanked = "Yanked";
    public const string SecurityBlocked = "SecurityBlocked";
    public const string Unknown = "Unknown";
}

public static class RegistryPolicyDispositions
{
    public const string Allowed = "Allowed";
    public const string Denied = "Denied";
    public const string PolicyRequired = "PolicyRequired";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPolicyIdentity(
    [property: JsonRequired] long Revision,
    [property: JsonRequired] string Fingerprint);

/// <summary>Non-secret external checkpoint identity; storage topology locations are not exposed.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPolicyCheckpointIdentity(
    [property: JsonRequired] long Sequence,
    [property: JsonRequired] string Digest,
    [property: JsonRequired] long TopologyGeneration,
    [property: JsonRequired] string TopologyFingerprint);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageVersionIdentity(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string Version,
    [property: JsonRequired] string PackageContentIdentity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPlatformCatalogIdentity(
    [property: JsonRequired] string AuthorityId,
    [property: JsonRequired] string ReleaseVersion,
    [property: JsonRequired] string CatalogSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryRuntimePolicySubject(
    [property: JsonRequired] RegistryExecutionRuntimeIdentity Runtime,
    [property: JsonRequired] string ManifestSha256,
    [property: JsonRequired] string ArtifactSha256);

/// <summary>Exact requested subjects and client rollback floors. Nonce is fresh 128-bit lowercase hexadecimal.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPolicySynchronizationRequest(
    [property: JsonRequired] string Nonce,
    IReadOnlyList<RegistryPackageVersionIdentity> Packages,
    IReadOnlyList<RegistryPlatformCatalogIdentity> PlatformCatalogs,
    IReadOnlyList<RegistryRuntimePolicySubject> Runtimes,
    [property: JsonRequired] RegistryPolicyIdentity? MinimumPackagePolicy,
    [property: JsonRequired] long MinimumRuntimePolicyRevision,
    [property: JsonRequired] RegistryPolicyCheckpointIdentity? MinimumCheckpoint)
{
    private IReadOnlyList<RegistryPackageVersionIdentity> _packages = PolicyCollections.Freeze(Packages);
    private IReadOnlyList<RegistryPlatformCatalogIdentity> _platformCatalogs = PolicyCollections.Freeze(PlatformCatalogs);
    private IReadOnlyList<RegistryRuntimePolicySubject> _runtimes = PolicyCollections.Freeze(Runtimes);
    [JsonRequired] public IReadOnlyList<RegistryPackageVersionIdentity> Packages { get => _packages; init => _packages = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryPlatformCatalogIdentity> PlatformCatalogs { get => _platformCatalogs; init => _platformCatalogs = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryRuntimePolicySubject> Runtimes { get => _runtimes; init => _runtimes = PolicyCollections.Freeze(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackagePolicyDecision(
    [property: JsonRequired] RegistryPackageVersionIdentity Package,
    [property: JsonRequired] string PackageState,
    [property: JsonRequired] string VersionAvailability,
    [property: JsonRequired] string Disposition,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string? PublicReason,
    IReadOnlyList<RegistryPlatformCatalogIdentity> PlatformDependencies)
{
    private IReadOnlyList<RegistryPlatformCatalogIdentity> _platformDependencies = PolicyCollections.Freeze(PlatformDependencies);
    [JsonRequired] public IReadOnlyList<RegistryPlatformCatalogIdentity> PlatformDependencies { get => _platformDependencies; init => _platformDependencies = PolicyCollections.Freeze(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPlatformPolicyDecision(
    [property: JsonRequired] RegistryPlatformCatalogIdentity Catalog,
    [property: JsonRequired] string LifecycleState,
    [property: JsonRequired] string Disposition,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] DateTimeOffset? RevokedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryRuntimePolicyDecision(
    [property: JsonRequired] RegistryRuntimePolicySubject Subject,
    [property: JsonRequired] string LifecycleState,
    [property: JsonRequired] string Disposition,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] DateTimeOffset? ActivationBlockedAtUtc,
    [property: JsonRequired] DateTimeOffset? RevokedAtUtc);

/// <summary>Complete atomic policy snapshot. ValidUntilUtc is exactly ObservedAtUtc plus 24 hours; failure cannot renew it.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPolicySynchronizationResponse(
    [property: JsonRequired] string Nonce,
    [property: JsonRequired] string RegistryOrigin,
    [property: JsonRequired] RegistryPolicyIdentity PackagePolicy,
    [property: JsonRequired] long RuntimePolicyRevision,
    [property: JsonRequired] RegistryPolicyCheckpointIdentity Checkpoint,
    [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    [property: JsonRequired] DateTimeOffset ValidUntilUtc,
    IReadOnlyList<RegistryPackagePolicyDecision> Packages,
    IReadOnlyList<RegistryPlatformPolicyDecision> PlatformCatalogs,
    IReadOnlyList<RegistryRuntimePolicyDecision> Runtimes)
{
    private IReadOnlyList<RegistryPackagePolicyDecision> _packages = PolicyCollections.Freeze(Packages);
    private IReadOnlyList<RegistryPlatformPolicyDecision> _platformCatalogs = PolicyCollections.Freeze(PlatformCatalogs);
    private IReadOnlyList<RegistryRuntimePolicyDecision> _runtimes = PolicyCollections.Freeze(Runtimes);
    [JsonRequired] public IReadOnlyList<RegistryPackagePolicyDecision> Packages { get => _packages; init => _packages = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryPlatformPolicyDecision> PlatformCatalogs { get => _platformCatalogs; init => _platformCatalogs = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryRuntimePolicyDecision> Runtimes { get => _runtimes; init => _runtimes = PolicyCollections.Freeze(value); }
}

internal static class PolicyCollections
{
    internal static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(value => value is null)) throw new ArgumentException("Policy collections cannot contain null elements.", nameof(values));
        return Array.AsReadOnly(values.ToArray());
    }
}
