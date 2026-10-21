using System.Text.Json.Serialization;

namespace Sunder.Registry.Contracts;

public static class RegistryPolicyLimits
{
    public const int MaximumPackageSubjects = 128;
    public const int MaximumPlatformSubjects = 128;
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

/// <summary>Exact requested subjects and the client's cached package-policy consistency floor. Nonce is fresh 128-bit lowercase hexadecimal.</summary>
/// <remarks>The Registry database is authoritative; a cached revision is not independent proof against database rollback.</remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPolicySynchronizationRequest(
    [property: JsonRequired] string Nonce,
    IReadOnlyList<RegistryPackageVersionIdentity> Packages,
    IReadOnlyList<RegistryPlatformCatalogIdentity> PlatformCatalogs,
    [property: JsonRequired] RegistryPolicyIdentity? MinimumPackagePolicy)
{
    private IReadOnlyList<RegistryPackageVersionIdentity> _packages = PolicyCollections.FreezeBounded(Packages, RegistryPolicyLimits.MaximumPackageSubjects);
    private IReadOnlyList<RegistryPlatformCatalogIdentity> _platformCatalogs = PolicyCollections.FreezeBounded(PlatformCatalogs, RegistryPolicyLimits.MaximumPlatformSubjects);
    [JsonRequired] public IReadOnlyList<RegistryPackageVersionIdentity> Packages { get => _packages; init => _packages = PolicyCollections.FreezeBounded(value, RegistryPolicyLimits.MaximumPackageSubjects); }
    [JsonRequired] public IReadOnlyList<RegistryPlatformCatalogIdentity> PlatformCatalogs { get => _platformCatalogs; init => _platformCatalogs = PolicyCollections.FreezeBounded(value, RegistryPolicyLimits.MaximumPlatformSubjects); }
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

/// <summary>Atomic package/platform policy snapshot. ValidUntilUtc is ObservedAtUtc plus 24 hours; failure cannot renew it.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPolicySynchronizationResponse(
    [property: JsonRequired] string Nonce,
    [property: JsonRequired] string RegistryOrigin,
    [property: JsonRequired] RegistryPolicyIdentity PackagePolicy,
    [property: JsonRequired] DateTimeOffset ObservedAtUtc,
    [property: JsonRequired] DateTimeOffset ValidUntilUtc,
    IReadOnlyList<RegistryPackagePolicyDecision> Packages,
    IReadOnlyList<RegistryPlatformPolicyDecision> PlatformCatalogs)
{
    private IReadOnlyList<RegistryPackagePolicyDecision> _packages = PolicyCollections.FreezeBounded(Packages, RegistryPolicyLimits.MaximumPackageSubjects);
    private IReadOnlyList<RegistryPlatformPolicyDecision> _platformCatalogs = PolicyCollections.FreezeBounded(PlatformCatalogs, RegistryPolicyLimits.MaximumPlatformSubjects);
    [JsonRequired] public IReadOnlyList<RegistryPackagePolicyDecision> Packages { get => _packages; init => _packages = PolicyCollections.FreezeBounded(value, RegistryPolicyLimits.MaximumPackageSubjects); }
    [JsonRequired] public IReadOnlyList<RegistryPlatformPolicyDecision> PlatformCatalogs { get => _platformCatalogs; init => _platformCatalogs = PolicyCollections.FreezeBounded(value, RegistryPolicyLimits.MaximumPlatformSubjects); }
}

internal static class PolicyCollections
{
    internal static IReadOnlyList<T> FreezeBounded<T>(IReadOnlyList<T> values, int maximumCount = RegistryPackagePlanLimits.MaximumItems)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > maximumCount) throw new ArgumentException($"The collection exceeds its {maximumCount}-item bound.", nameof(values));
        var snapshot = values.Take(maximumCount + 1).ToArray();
        if (snapshot.Length > maximumCount || snapshot.Any(static value => value is null))
            throw new ArgumentException("The collection exceeds its bound or contains a null item.", nameof(values));
        return Array.AsReadOnly(snapshot);
    }

    internal static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(value => value is null)) throw new ArgumentException("Policy collections cannot contain null elements.", nameof(values));
        return Array.AsReadOnly(values.ToArray());
    }
}
