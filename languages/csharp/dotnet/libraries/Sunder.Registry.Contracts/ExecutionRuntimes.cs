namespace Sunder.Registry.Contracts;

public static class ExecutionRuntimeLifecycleStates
{
    public const string Staged = "staged";
    public const string Supported = "supported";
    public const string Deprecated = "deprecated";
    public const string Retired = "retired";
    public const string Revoked = "revoked";
}

public static class RegistryPackageActionKinds
{
    public const string PackageInstall = "package-install";
    public const string PackageUpdate = "package-update";
    public const string PackageRemove = "package-remove";
    public const string Keep = "keep";
    public const string RuntimeOnlyRebind = "runtime-only-rebind";
}

public sealed record RegistryExecutionRuntimeRequirement(
    string RuntimeId,
    string VersionRange,
    string ModuleAbi);

public sealed record RegistryExecutionRuntimeIdentity(
    string RuntimeId,
    string Version,
    int PackagingRevision,
    string Rid,
    string ModuleAbi);

public sealed record RegistryExecutionRuntimeLifecycle(
    string State,
    DateTimeOffset StagedAtUtc,
    DateTimeOffset? SupportedFromUtc,
    DateTimeOffset? DeprecatedAtUtc,
    DateTimeOffset? InstallBlockedAtUtc,
    DateTimeOffset? ActivationBlockedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? Message);

public sealed record RegistryResolvedExecutionRuntimeArtifact(
    RegistryExecutionRuntimeIdentity Identity,
    string ManifestSha256,
    string ArtifactSha256,
    long ArtifactSize,
    string SigningKeyId,
    long MinimumHostTrustGeneration,
    string NativeAttestationSha256,
    string NativeAttestationKeyId,
    RegistryExecutionRuntimeLifecycle Lifecycle,
    long ObservedRuntimePolicyRevision,
    string DownloadUrl);

public sealed record RegistryExecutionRuntimeArtifactDetails(
    RegistryResolvedExecutionRuntimeArtifact Artifact,
    string ContentIdentity,
    DateTimeOffset PublishedAtUtc);

public sealed record RegistryExecutionRuntimeReleaseSummary(
    string RuntimeId,
    string Version,
    int PackagingRevision,
    string ModuleAbi,
    string WorkerProtocol,
    long MinimumHostTrustGeneration,
    RegistryExecutionRuntimeLifecycle Lifecycle,
    IReadOnlyList<string> SupportedRids,
    DateTimeOffset PublishedAtUtc);

public sealed record RegistryExecutionRuntimeReleaseDetails(
    RegistryExecutionRuntimeReleaseSummary Release,
    IReadOnlyList<RegistryExecutionRuntimeArtifactDetails> Artifacts);

public sealed record RegistryExecutionRuntimeSummary(
    string RuntimeId,
    string DisplayName,
    string ModuleAbi,
    string? LatestSupportedVersion,
    IReadOnlyList<RegistryExecutionRuntimeReleaseSummary> Releases);

public sealed record RegistryExecutionRuntimeSearchResult(
    IReadOnlyList<RegistryExecutionRuntimeSummary> Items,
    int TotalCount,
    int Skip,
    int Take,
    long RuntimePolicyRevision);

public sealed record RegistryHostRuntimeTrustProfile(
    string ProfileId,
    long Generation);

public sealed record RegistryPackageTargetRuntimeBinding(
    string Role,
    string Rid,
    RegistryExecutionRuntimeRequirement Requirement,
    RegistryExecutionRuntimeIdentity Runtime);

public sealed record RegistryRuntimeOnlyRebindDetails(
    string PackageVersion,
    string PackageContentIdentity,
    IReadOnlyList<RegistryPackageTargetRuntimeBinding> PreviousBindings,
    IReadOnlyList<RegistryPackageTargetRuntimeBinding> NewBindings);

public sealed record RegistryExecutionRuntimeResolutionConflict(
    string Code,
    string Message,
    RegistryExecutionRuntimeRequirement Requirement,
    string Rid,
    string? PackageId = null,
    string? TargetRole = null,
    RegistryExecutionRuntimeIdentity? CurrentRuntime = null,
    long? RequiredHostTrustGeneration = null);

public sealed record RegistryResolveExecutionRuntimeRequest(
    RegistryExecutionRuntimeRequirement Requirement,
    string Rid,
    RegistryHostRuntimeTrustProfile RuntimeTrust,
    RegistryExecutionRuntimeIdentity? CurrentRuntime = null,
    string? ExpectedResolutionFingerprint = null);

public sealed record RegistryResolveExecutionRuntimeResponse(
    bool Success,
    RegistryResolvedExecutionRuntimeArtifact? RuntimeArtifact,
    string ResolutionFingerprint,
    long RuntimePolicyRevision,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RegistryExecutionRuntimeResolutionConflict> Conflicts);

public sealed record RegistryExecutionRuntimeNativeAttestationExpectation(
    string Rid,
    string NativeAttestationKeyId);

public sealed record RegistryStageExecutionRuntimeReleaseRequest(
    string RuntimeId,
    string Version,
    int PackagingRevision,
    string ModuleAbi,
    string WorkerProtocol,
    long MinimumHostTrustGeneration,
    string ExpectedReleaseSigningKeyId,
    IReadOnlyList<RegistryExecutionRuntimeNativeAttestationExpectation> ExpectedNativeAttestations,
    IReadOnlyList<string> ExpectedRids);

public sealed record RegistryExecutionRuntimePublicationArtifactExpectation(
    string Rid,
    string IdempotencyKey,
    string ArtifactSha256,
    long ArtifactSize,
    string ManifestSha256,
    string ContentIdentity,
    string SigningKeyId,
    string NativeAttestationSha256,
    string NativeAttestationKeyId);

public sealed record RegistryLookupExecutionRuntimePublicationRequest(
    RegistryStageExecutionRuntimeReleaseRequest Release,
    IReadOnlyList<RegistryExecutionRuntimePublicationArtifactExpectation> ExpectedArtifacts);

public sealed record RegistryPublishExecutionRuntimeArtifactRequest(
    string IdempotencyKey,
    string Rid,
    string ExpectedArtifactSha256,
    long ExpectedArtifactSize);

public sealed record RegistryPublishLocalExecutionRuntimeArtifactRequest(
    string RuntimePath,
    string IdempotencyKey,
    string ExpectedArtifactSha256,
    long ExpectedArtifactSize);

public sealed record RegistryExecutionRuntimePublicationArtifact(
    string Rid,
    string ArtifactSha256,
    long ArtifactSize,
    string ManifestSha256,
    string ContentIdentity,
    string SigningKeyId,
    string NativeAttestationSha256,
    string NativeAttestationKeyId);

public sealed record RegistryStagedExecutionRuntimePublicationResponse(
    string StageId,
    RegistryExecutionRuntimeReleaseSummary Release,
    string ExpectedReleaseSigningKeyId,
    IReadOnlyList<RegistryExecutionRuntimeNativeAttestationExpectation> ExpectedNativeAttestations,
    IReadOnlyList<string> ExpectedRids,
    IReadOnlyList<string> PublishedRids,
    IReadOnlyList<string> MissingRids,
    bool ReadyToPromote,
    IReadOnlyList<RegistryExecutionRuntimePublicationArtifact> Artifacts);

public sealed record RegistryTransitionExecutionRuntimeLifecycleRequest(
    string State,
    DateTimeOffset? DeprecatedAtUtc,
    DateTimeOffset? InstallBlockedAtUtc,
    DateTimeOffset? ActivationBlockedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string? Message,
    long ExpectedRuntimePolicyRevision);

public sealed record RegistryExecutionRuntimeLifecycleOperationResponse(
    bool Success,
    RegistryExecutionRuntimeReleaseDetails? Release,
    long RuntimePolicyRevision,
    IReadOnlyList<string> Errors);
