using System.Text.Json.Serialization;

namespace Sunder.Registry.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageAvailabilityChange(
    [property: JsonRequired] RegistryPackageVersionIdentity Package,
    [property: JsonRequired] string ExpectedAvailability,
    [property: JsonRequired] string Availability);

/// <summary>A complete exact tag set/delete. Null target deletes the tag.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryCohortDistTagChange(
    [property: JsonRequired] string PackageId,
    [property: JsonRequired] string Tag,
    [property: JsonRequired] RegistryPackageVersionIdentity? Target);

/// <summary>One atomic owner-scoped transition, never a list of independently successful operations.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageVersionCohortAvailabilityTransitionV1(
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] long ExpectedPackagePolicyRevision,
    IReadOnlyList<RegistryPackageAvailabilityChange> Versions,
    IReadOnlyList<RegistryCohortDistTagChange> DistTags)
{
    private IReadOnlyList<RegistryPackageAvailabilityChange> _versions = PolicyCollections.Freeze(Versions);
    private IReadOnlyList<RegistryCohortDistTagChange> _distTags = PolicyCollections.Freeze(DistTags);
    [JsonRequired] public IReadOnlyList<RegistryPackageAvailabilityChange> Versions { get => _versions; init => _versions = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryCohortDistTagChange> DistTags { get => _distTags; init => _distTags = PolicyCollections.Freeze(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryCohortPackageTagSnapshot(
    [property: JsonRequired] string PackageId,
    IReadOnlyList<RegistryCohortDistTagChange> Tags)
{
    private IReadOnlyList<RegistryCohortDistTagChange> _tags = PolicyCollections.Freeze(Tags);
    [JsonRequired] public IReadOnlyList<RegistryCohortDistTagChange> Tags { get => _tags; init => _tags = PolicyCollections.Freeze(value); }
}

/// <summary>The immutable committed cohort result. Errors use Problem Details, not partial-success payloads.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackageVersionCohortAvailabilityResultV1(
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] string RequestSha256,
    [property: JsonRequired] RegistryPolicyIdentity PackagePolicy,
    [property: JsonRequired] RegistryPolicyCheckpointIdentity Checkpoint,
    [property: JsonRequired] DateTimeOffset CommittedAtUtc,
    IReadOnlyList<RegistryPackageAvailabilityChange> Versions,
    IReadOnlyList<RegistryCohortPackageTagSnapshot> PreviousTags,
    IReadOnlyList<RegistryCohortPackageTagSnapshot> ResultingTags)
{
    private IReadOnlyList<RegistryPackageAvailabilityChange> _versions = PolicyCollections.Freeze(Versions);
    private IReadOnlyList<RegistryCohortPackageTagSnapshot> _previousTags = PolicyCollections.Freeze(PreviousTags);
    private IReadOnlyList<RegistryCohortPackageTagSnapshot> _resultingTags = PolicyCollections.Freeze(ResultingTags);
    [JsonRequired] public IReadOnlyList<RegistryPackageAvailabilityChange> Versions { get => _versions; init => _versions = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryCohortPackageTagSnapshot> PreviousTags { get => _previousTags; init => _previousTags = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryCohortPackageTagSnapshot> ResultingTags { get => _resultingTags; init => _resultingTags = PolicyCollections.Freeze(value); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageSecurityBlockRequest(
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] long ExpectedPackagePolicyRevision,
    [property: JsonRequired] string PublicReason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPackageVersionSecurityBlockRequest(
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] long ExpectedPackagePolicyRevision,
    [property: JsonRequired] string ExpectedPackageContentIdentity,
    [property: JsonRequired] string PublicReason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryPolicyMutationResult(
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] string RequestSha256,
    [property: JsonRequired] RegistryPolicyIdentity Policy,
    [property: JsonRequired] RegistryPolicyCheckpointIdentity Checkpoint,
    [property: JsonRequired] DateTimeOffset CommittedAtUtc);

public static class RegistryStackVisibilityStates
{
    public const string Listed = "Listed";
    public const string Unlisted = "Unlisted";
    public const string SecurityBlocked = "SecurityBlocked";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryStackRevisionIdentity(
    [property: JsonRequired] string StackId,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] string ArtifactSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryStackRevisionDetails(
    [property: JsonRequired] RegistryStackRevisionIdentity Identity,
    [property: JsonRequired] string OwnerPrincipalId,
    [property: JsonRequired] string PublishedByPrincipalId,
    [property: JsonRequired] DateTimeOffset PublishedAtUtc,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string? Summary,
    [property: JsonRequired] string? ReadmeMarkdown,
    [property: JsonRequired] string ManifestSha256,
    [property: JsonRequired] long ArtifactBytes,
    [property: JsonRequired] string ArtifactDownloadUrl,
    IReadOnlyList<RegistryStackPackageRequirement> Packages,
    IReadOnlyList<RegistryStackFragmentSummary> Fragments,
    IReadOnlyList<RegistryStackRequiredInput> RequiredInputs,
    IReadOnlyList<RegistryStackRevisionMedia> Media)
{
    private IReadOnlyList<RegistryStackPackageRequirement> _packages = PolicyCollections.Freeze(Packages);
    private IReadOnlyList<RegistryStackFragmentSummary> _fragments = FreezeFragments(Fragments);
    private IReadOnlyList<RegistryStackRequiredInput> _requiredInputs = PolicyCollections.Freeze(RequiredInputs);
    private IReadOnlyList<RegistryStackRevisionMedia> _media = PolicyCollections.Freeze(Media);
    [JsonRequired] public IReadOnlyList<RegistryStackPackageRequirement> Packages { get => _packages; init => _packages = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryStackFragmentSummary> Fragments { get => _fragments; init => _fragments = FreezeFragments(value); }
    [JsonRequired] public IReadOnlyList<RegistryStackRequiredInput> RequiredInputs { get => _requiredInputs; init => _requiredInputs = PolicyCollections.Freeze(value); }
    [JsonRequired] public IReadOnlyList<RegistryStackRevisionMedia> Media { get => _media; init => _media = PolicyCollections.Freeze(value); }

    private static IReadOnlyList<RegistryStackFragmentSummary> FreezeFragments(IReadOnlyList<RegistryStackFragmentSummary> values)
        => Array.AsReadOnly(PolicyCollections.Freeze(values).Select(value => value with
        { DisplayDetails = value.DisplayDetails is null ? null : PolicyCollections.Freeze(value.DisplayDetails) }).ToArray());
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryStackRevisionMedia(
    [property: JsonRequired] Guid MediaId,
    [property: JsonRequired] string FileName,
    [property: JsonRequired] string ContentType,
    [property: JsonRequired] long Size,
    [property: JsonRequired] string Sha256,
    [property: JsonRequired] string DownloadUrl,
    [property: JsonRequired] int SortOrder,
    [property: JsonRequired] string? AltText);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryStackPolicySnapshot(
    [property: JsonRequired] string StackId,
    [property: JsonRequired] string OwnerPrincipalId,
    [property: JsonRequired] RegistryStackRevisionIdentity CurrentRevision,
    [property: JsonRequired] string Visibility,
    [property: JsonRequired] string? PublicReason,
    [property: JsonRequired] RegistryPolicyIdentity PublicationAuthorityPolicy,
    [property: JsonRequired] RegistryPolicyCheckpointIdentity Checkpoint);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryStackVisibilityRequest(
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] long ExpectedPublicationAuthorityRevision,
    [property: JsonRequired] string ExpectedVisibility,
    [property: JsonRequired] string Visibility);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegistryStackSecurityBlockRequest(
    [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] long ExpectedPublicationAuthorityRevision,
    [property: JsonRequired] string PublicReason);
