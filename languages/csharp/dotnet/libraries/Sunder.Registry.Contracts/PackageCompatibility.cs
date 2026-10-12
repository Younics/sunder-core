using System.Text.Json.Serialization;

namespace Sunder.Registry.Contracts;

public static class RegistryPackagePresence
{
    public const string Required = "required";
    public const string Optional = "optional";
}

public static class RegistryOptionalPackageDispositions
{
    public const string Included = "included";
    public const string Omitted = "omitted";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryExactRpcRequirement(
    [property: JsonRequired] string ContractId,
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string DescriptorSha256,
    [property: JsonRequired] string AbiSha256);

/// <summary>Installation constraints. Restore-selected package hashes are not installation or routing pins.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageCompatibilityRequirement(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string VersionRange,
    [property: JsonRequired] bool IncludePrerelease,
    [property: JsonRequired] string Presence,
    IReadOnlyList<RegistryExactRpcRequirement> Contracts,
    [property: JsonRequired] string? RequiredByPackageId)
{
    private IReadOnlyList<RegistryExactRpcRequirement> _contracts = PolicyCollections.Freeze(Contracts);
    [JsonRequired] public IReadOnlyList<RegistryExactRpcRequirement> Contracts { get => _contracts; init => _contracts = PolicyCollections.Freeze(value); }
}

/// <summary>Reviewed range for a distinct ultimate package authority, never inferred from its selected release.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageAuthorityRequirement(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string VersionRange,
    [property: JsonRequired] bool IncludePrerelease);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageRestoreProvenance(
    [property: JsonRequired] string RegistryOrigin,
    [property: JsonRequired] RegistryPackagePublicApiInventoryResponseV1 Artifact,
    [property: JsonRequired] string RequestedVersionRange,
    [property: JsonRequired] bool IncludePrerelease,
    IReadOnlyList<RegistryPackageAuthorityRequirement> AuthorityRequirements)
{
    private IReadOnlyList<RegistryPackageAuthorityRequirement> _authorityRequirements = PolicyCollections.Freeze(AuthorityRequirements);
    [JsonRequired] public IReadOnlyList<RegistryPackageAuthorityRequirement> AuthorityRequirements { get => _authorityRequirements; init => _authorityRequirements = PolicyCollections.Freeze(value); }
}

/// <summary>Complete optional-branch decision. Omission does not grant uses or hide otherwise authorized providers.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryOptionalPackageDecision(
    [property: JsonRequired] string ConsumerPackageId,
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string Disposition,
    [property: JsonRequired] RegistryPackageVersionIdentity? SelectedPackage,
    [property: JsonRequired] string? ErrorCode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageCompatibilityConflict(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string ErrorCode,
    IReadOnlyList<RegistryPackageCompatibilityRequirement> Requirements)
{
    private IReadOnlyList<RegistryPackageCompatibilityRequirement> _requirements = PolicyCollections.Freeze(Requirements);
    [JsonRequired] public IReadOnlyList<RegistryPackageCompatibilityRequirement> Requirements { get => _requirements; init => _requirements = PolicyCollections.Freeze(value); }
}

/// <summary>The exact policy and normalized constraint portion of a complete install/update/repair plan.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageResolutionPolicy(
    [property: JsonRequired] RegistryPolicyIdentity PackagePolicy,
    [property: JsonRequired] RegistryPolicyCheckpointIdentity Checkpoint,
    IReadOnlyList<RegistryPackageCompatibilityRequirement> Requirements,
    IReadOnlyList<RegistryPackageRestoreProvenance> RestoreProvenance,
    IReadOnlyList<RegistryOptionalPackageDecision> OptionalPackages,
    IReadOnlyList<RegistryPlatformPolicyDecision> PlatformCatalogs)
{
    private IReadOnlyList<RegistryPackageCompatibilityRequirement> _requirements = PolicyCollections.Freeze(Requirements);
    private IReadOnlyList<RegistryPackageRestoreProvenance> _restoreProvenance = PolicyCollections.Freeze(RestoreProvenance);
    private IReadOnlyList<RegistryOptionalPackageDecision> _optionalPackages = PolicyCollections.Freeze(OptionalPackages);
    private IReadOnlyList<RegistryPlatformPolicyDecision> _platformCatalogs = PolicyCollections.Freeze(PlatformCatalogs);
    [JsonRequired] public IReadOnlyList<RegistryPackageCompatibilityRequirement> Requirements { get => _requirements; init => _requirements = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryPackageRestoreProvenance> RestoreProvenance { get => _restoreProvenance; init => _restoreProvenance = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryOptionalPackageDecision> OptionalPackages { get => _optionalPackages; init => _optionalPackages = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryPlatformPolicyDecision> PlatformCatalogs { get => _platformCatalogs; init => _platformCatalogs = PolicyCollections.Freeze(value); }
}

/// <summary>Administrator-only policy diagnostics, excluding credentials, public keys and storage locations.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPolicyStatusV1(
    [property: JsonRequired] string Code,
    [property: JsonRequired] bool PublicReady,
    [property: JsonRequired] bool ExternalAligned,
    [property: JsonRequired] RegistryPolicyCheckpointIdentity? AppliedCheckpoint,
    [property: JsonRequired] RegistryPolicyCheckpointIdentity? PrimaryCheckpoint,
    [property: JsonRequired] RegistryPolicyCheckpointIdentity? MirrorCheckpoint,
    [property: JsonRequired] RegistryPolicyIdentity? PackagePolicy,
    [property: JsonRequired] RegistryPolicyIdentity? PublicationAuthorityPolicy,
    [property: JsonRequired] RegistryPolicyIdentity? AuthorizationPolicy,
    [property: JsonRequired] long? AuthorizationGeneration);
