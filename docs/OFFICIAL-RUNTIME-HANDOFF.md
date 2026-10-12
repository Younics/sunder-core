# Official runtime downloads: contract handoff

## Status and ownership

The shared Core contract cutover and owning JSON/API snapshots are reconciled.
The normal API generator was restricted to `Sunder.Runtime.Contracts` and
`Sunder.Registry.Contracts`. Both contract projects previously built independently
with zero warnings/errors. Policy schema alignment is complete: focused
`PolicyContractTests` **85/0/0**, then full PublicApi **295/0/0**, using `--no-restore`
and `BuildProjectReferences=false` (the full run reused the focused build). The
existing Node/AJV policy-schema check passes **46/0/0**. All fourteen prior schema
failures are closed; all nine API assembly baselines and current JSON goldens pass.
Prior Registry.Contracts **98/0/0** and Runtime.Client **61/0/0** results are retained,
not rerun for this schema-only integration correction.

`specs/policy/v1/contracts.schema.json` now contains package/platform-only
synchronization, Registry readiness and watermark evidence. It retires
`runtimeIdentity`, `runtimeSubject`, `runtimeDecision` and their unused `moduleAbi`
helper, including runtime-only conditional branches. The conformance manifest
removes only `runtime-denial.json`; all 44 current policy golden vectors remain.
The same V1 schema identity, closed/required fields, nonce, subject bounds,
package/platform revocation, leases, authorization and checkpoint rules are preserved.
Existing rejection tests also prove the schema rejects removed runtime authority
even when explicitly null, while platform-only synchronization remains valid.

The refreshed API delta removes 32 Registry type names (four error types relocate
to Runtime.Contracts) and 10 obsolete Runtime types: 38 types retired, four relocated.
Tests retire the runtime-publication class and 13 obsolete JSON fixtures (11 Registry
runtime fixtures, one runtime-import result, one runtime-denial policy fixture).
Package/platform denial fixtures remain. New coverage checks requirement-only
planning, rejected removed authority, exact source/checksum/provenance, unknown-size
versus cache reuse, closed string enums, collection bounds/copying and the official
feature fence on basic as well as management client calls.

This is an unreleased, closed V1 replacement: **no migrations, compatibility DTOs,
old receipt readers, signed-runtime fallback, or dual production acquisition path**.
No user data was reset or deleted in these contract phases. Host, Registry, App/CLI,
Format and release integration consume the contract below through their own owners.

Ownership: Host owns official resolution/download/materialization, one installed
binding store, private per-user roots, receipts, health, process leases, retention,
safe updates and scheduling. Registry owns package metadata, publication,
requirements, package/platform permission and revocation. App/CLI are thin clients.
Authoring SDKs, canonical package manifests/schemas, target RIDs, logical runtime IDs,
module ABIs and the fifteen first-party package definitions are unchanged.

## Transport and request rules

- Protocol identity/revision remain `dev.sunder.runtime` / `1`, routes `/api/v1`.
- Host must advertise `official-runtime-downloads.v1`. Runtime.Client requires it
  at the base handshake, including package/policy calls whose DTOs changed. Remove
  `execution-runtime-management.v1` and `execution-runtime-updates.v1` advertisements.
- `package-install-resumption.v1` remains required additionally for retained uploads
  and source-adoption prerequisite calls. Upload-retention behavior is unchanged.
- Changed runtime, Registry planning and policy records reject unknown JSON members.
  Required members must be present, including nullable evidence fields. Optional
  selectors/defaults are explicit below. Host still validates nullability, ranges,
  timestamps, source/family/checksum pairing, string lengths and complete semantics.
- Source/checksum/archive/update/support enums are string-only; unknown strings and
  integer enum representations are rejected. No client supplies an acquisition URL,
  trust flag, signing material, distribution descriptor or replacement binding.
- Catalog accepts only `sourceId`, `skip`, `take`; Host must reject unknown query
  keys as well as malformed values. A source ID filters configured sources only.
- Shared contracts use only BCL types; no Host, Registry implementation or SDK dependency.

## Official identity and evidence

`ConfiguredOfficialSourceId` has exactly `DotNet` (`"dotnet"`) and `Node` (`"node"`).
`RuntimeExecutionRuntimeChecksumAlgorithm` has `Sha512` (`"sha512"`) and `Sha256`
(`"sha256"`). `RuntimeExecutionRuntimeArchiveFormat` has `Zip` (`"zip"`) and
`TarGzip` (`"tar.gz"`). Host enforces these pairs:

| Source | Logical runtime / module ABI | Selection | Vendor checksum |
| --- | --- | --- | --- |
| dotnet | `sunder.runtime.dotnet` / `sunder.dotnet-worker-module.v1` | Supported stable .NET Runtime 10, satisfying the package range | SHA-512, 128 lowercase hex digits |
| node | `sunder.runtime.node` / `sunder.node-worker-module.v1` | Supported stable Node 24 LTS, satisfying the package range; generated floor remains `>=24.18.1 <25.0.0` | SHA-256, 64 lowercase hex digits |

The .NET distribution is the Runtime, not the SDK. Metadata, checksums, HTTPS
downloads and every redirect are validated against Host-configured official
sources. Their URLs below are **Host output/evidence**, not client input. Runtime
resolution never uses Registry credentials or Registry-advertised artifact origins.

Exact new record constructors (all properties required on wire unless a default is shown):

```csharp
RuntimeExecutionRuntimeRequirement(string RuntimeId, string VersionRange, string ModuleAbi)
RuntimeExecutionRuntimeIdentity(ConfiguredOfficialSourceId SourceId, string RuntimeId,
    string VendorVersion, string Rid, string ModuleAbi,
    RuntimeExecutionRuntimeChecksumAlgorithm VendorChecksumAlgorithm, string VendorChecksumDigest)
RuntimeExecutionRuntimeDistribution(string DownloadUrl, string MetadataUrl,
    string MetadataSha256, string? ChecksumUrl, string? ChecksumDocumentSha256,
    RuntimeExecutionRuntimeArchiveFormat ArchiveFormat, string MaterializationProfile,
    long? DownloadSize)
RuntimeExecutionRuntimeSupportStatus(RuntimeExecutionRuntimeSupportState State,
    string SupportLine, DateTimeOffset ObservedAtUtc, DateTimeOffset? EndOfLifeAtUtc, string? Message)
RuntimeExecutionRuntimeVendorArtifact(RuntimeExecutionRuntimeIdentity Identity,
    RuntimeExecutionRuntimeDistribution Distribution, RuntimeExecutionRuntimeSupportStatus Support)
RuntimeExecutionRuntimeProvenance(RuntimeExecutionRuntimeDistribution Distribution,
    string FinalDownloadUrl, DateTimeOffset RetrievedAtUtc)
RuntimeExecutionRuntimeInstalledArtifact(string ArchiveSha256, long ArchiveSize,
    string ContentIdentity, RuntimeExecutionRuntimeProvenance Provenance)
```

`MetadataSha256` hashes the fetched metadata document. If a separate checksum
document is used, record its URL and SHA-256 together; otherwise both are null.
`MaterializationProfile` identifies the fixed Host extraction/layout recipe, not a
runtime packaging revision. Provenance and `InstalledArtifact` are created only
after verification. `ArchiveSha256` hashes original downloaded archive bytes;
`ArchiveSize` is their actual length. `ContentIdentity` commits the separate private
Host inventory of extracted paths, hashes, sizes and permitted executable modes.
Do not expose the full file inventory in every management response.

Support states are `Unknown`, `Supported`, `EndOfLife`. They are observed vendor
information, never runtime execution leases, Registry revocations or authorizations.
Health stays separate. Existing verified launch does not need a 24-hour runtime
policy renewal. Platform/native OS dependency checks belong in Host prerequisites;
use `unsupported`, `rid-unavailable` or `platform-prerequisite-missing` diagnostics,
not an implicit system installer. Exact RID alone is not proof of native readiness.

`VendorVersion` is the actual upstream SemVer, including patch. Strip only vendor
notation such as a leading `v`; do not substitute requirement floors or App versions.
Different vendor digests are different identities even at the same version. Ordinary
updates are strictly forward within the eligible line; changed same-version bytes
produce `execution-runtime.artifact-changed`, not an invented packaging revision.
No automatic major change, downgrade, floor relaxation or ambient executable fallback.

### Changed installed/management record constructors

```csharp
InstalledExecutionRuntimeSummary(RuntimeExecutionRuntimeIdentity Identity, string Status,
    long Size, int PackageReferenceCount, RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact,
    RuntimeExecutionRuntimeHealth Health, RuntimeExecutionRuntimeSupportStatus Support,
    DateTimeOffset InstalledAtUtc, bool RetainedUntilUninstall = false)
PackageTargetExecutionRuntimeBinding(string Role, string Rid,
    RuntimeExecutionRuntimeRequirement Requirement, RuntimeExecutionRuntimeIdentity Runtime,
    RuntimeExecutionRuntimeInstalledArtifact InstalledArtifact, DateTimeOffset ResolvedAtUtc)
RuntimeExecutionRuntimeStoreFault(string Code, string Message, bool Retryable, string Operation,
    RuntimeExecutionRuntimeIdentity? Runtime, string? PackageId, string? TargetRole, DateTimeOffset OccurredAtUtc)
RuntimeExecutionRuntimeEnsureRequest(RuntimeExecutionRuntimeRequirement Requirement, bool RetainUntilUninstall = true)
RuntimeExecutionRuntimeCatalogRequest(ConfiguredOfficialSourceId? SourceId = null, int Skip = 0, int Take = 50)
RuntimeExecutionRuntimeCatalogEntry(string DisplayName, RuntimeExecutionRuntimeVendorArtifact Artifact, bool Installed)
RuntimeExecutionRuntimeCatalogPage(string Rid, int Skip, int Take, int TotalCount,
    IReadOnlyList<RuntimeExecutionRuntimeCatalogEntry> Items)
RuntimeExecutionRuntimeResolution(RuntimeExecutionRuntimeRequirement Requirement,
    RuntimeExecutionRuntimeVendorArtifact Artifact, string ResolutionFingerprint,
    DateTimeOffset ExpiresAtUtc, bool AlreadyInstalled, long? DownloadSize)
RuntimeExecutionRuntimeInstallPreviewRequest(RuntimeExecutionRuntimeRequirement Requirement, bool RetainUntilUninstall = true)
RuntimePackageRuntimePrerequisiteRequest(RuntimeRegistryPackageBatchRequest? Registry = null,
    PackageStoreStageRequest? Local = null, string? ExpectedSourceFingerprint = null)
```

`Summary.Size` is installed content bytes; `InstalledArtifact.ArchiveSize` is download
bytes. Preview/distribution `DownloadSize=null` means unknown, never zero; resolution
`DownloadSize=0` means verified cache reuse. Nullable metadata size is not an actual
receipt size. Identity has no local archive hash, manifest hash or content inventory.
Package projection/prerequisite `ManifestSha256` still means the **package** manifest.

Prerequisite request also retains optional init property `Adoption`; select exactly
one Registry/Local/Adoption source. Its result and expectation retain their names and
fields, now carrying official runtime identities/previews. Settings, operation/rebind
results, reference/impact/usage, plan confirmation, update candidates/binding changes,
prepared updates, uninstall and update result field names remain as in
[`ExecutionRuntimeManagementContracts.cs`](../languages/csharp/dotnet/host/Sunder.Runtime.Contracts/ExecutionRuntimeManagementContracts.cs).
Their nested identities/resolutions are the new records above. `ExecutionRuntimeRebindResult`
is local Host output only. Update states are `NoChanges`, `Prepared`, `Deferred`,
`Applied`, `ReconciliationPending`, `Stale`, `Blocked`, `Error`.

## Frozen Runtime.Client methods and routes

All methods are on `RuntimeManagementClient`, return `Task<T>` and accept trailing
`CancellationToken token = default`. Table request names are the non-token argument;
`identity` means `RuntimeExecutionRuntimeIdentity`. All routes start `/api/v1`.

`E(identity)` is the shared `RuntimeApiRoutes.ExecutionRuntime(identity)` path:
`/execution-runtimes/sources/{sourceId}/{runtimeId}/versions/{vendorVersion}/{rid}/abis/{moduleAbi}/checksums/{algorithm}/{digest}`.
Every identity component participates. No revision or version-only lookup remains.
The References/Impact/Verify/Repair shared route helpers each take only `identity`.

| HTTP / suffix | Method | Request -> T |
| --- | --- | --- |
| GET `/execution-runtimes` | `GetExecutionRuntimesAsync` | none -> `IReadOnlyList<InstalledExecutionRuntimeSummary>` |
| GET `E` | `GetExecutionRuntimeAsync` | identity -> `InstalledExecutionRuntimeSummary` |
| GET `E/references` | `GetExecutionRuntimeReferencesAsync` | identity -> `IReadOnlyList<RuntimeExecutionRuntimePackageReference>` |
| GET `E/impact` | `GetExecutionRuntimeImpactAsync` | identity -> `RuntimeExecutionRuntimeImpact` |
| GET `/packages/{packageId}/execution-runtime-bindings` | `GetPackageExecutionRuntimeBindingsAsync` | string packageId -> `IReadOnlyList<PackageTargetExecutionRuntimeBinding>` |
| POST `/execution-runtimes/ensure` | `EnsureExecutionRuntimeAsync` | `RuntimeExecutionRuntimeEnsureRequest` -> `RuntimeExecutionRuntimeOperationResult` |
| POST `E/verify` | `VerifyExecutionRuntimeAsync` | identity, no body -> operation result |
| POST `E/repair` | `RepairExecutionRuntimeAsync` | identity + `RuntimeExecutionRuntimeImpact`, body `RuntimeExecutionRuntimeActionRequest(ExpectedImpactFingerprint)` -> operation result |
| POST `/execution-runtimes/gc` | `CollectExecutionRuntimeGarbageAsync` | none -> operation result |
| GET `/execution-runtimes/settings` | `GetExecutionRuntimeSettingsAsync` | none -> `RuntimeExecutionRuntimeSettings` |
| PUT `/execution-runtimes/settings` | `UpdateExecutionRuntimeSettingsAsync` | `RuntimeExecutionRuntimeSettingsUpdateRequest` -> settings |
| GET `/execution-runtimes/updates/status` | `GetExecutionRuntimeUpdateStatusAsync` | none -> `RuntimeExecutionRuntimeUpdateStatus` |
| GET `/execution-runtimes/catalog` | `GetExecutionRuntimeCatalogAsync` | catalog request as sourceId/skip/take query -> `RuntimeExecutionRuntimeCatalogPage` |
| POST `/execution-runtimes/install/preview` | `PreviewExecutionRuntimeInstallAsync` | install preview request -> `RuntimeExecutionRuntimeInstallPlan` |
| POST `/execution-runtimes/install/apply` | `ApplyExecutionRuntimeInstallAsync` | `RuntimeExecutionRuntimePlanConfirmation` -> operation result |
| POST `/packages/runtime-prerequisites` | `PreviewPackageRuntimePrerequisitesAsync` | prerequisite request -> `RuntimePackageRuntimePrerequisitePlan` |
| POST `/execution-runtimes/updates/preview` | `PreviewExecutionRuntimeUpdateAsync` | `RuntimeExecutionRuntimeUpdatePreviewRequest` -> `RuntimeExecutionRuntimeUpdatePlan` |
| POST `/execution-runtimes/updates/prepare` | `PrepareExecutionRuntimeUpdateAsync` | plan confirmation -> `RuntimeExecutionRuntimeUpdateResult` |
| POST `/execution-runtimes/updates/apply` | `ApplyExecutionRuntimeUpdateAsync` | `RuntimeExecutionRuntimeUpdateApplyRequest` -> update result |
| POST `/execution-runtimes/uninstall/preview` | `PreviewExecutionRuntimeUninstallAsync` | `RuntimeExecutionRuntimeUninstallPreviewRequest` -> `RuntimeExecutionRuntimeUninstallPlan` |
| POST `/execution-runtimes/uninstall/apply` | `ApplyExecutionRuntimeUninstallAsync` | plan confirmation -> `RuntimeExecutionRuntimeUninstallResult` |

Repair also retains its `(identity, token)` convenience overload, which obtains
fresh impact first. Ensure and consented Install share one Host acquisition service;
Ensure is an explicit acquisition command, never a package-install or launch bypass.
Upload/import methods and routes have been removed, with no replacement wrapper.

## Fingerprints, sequencing, retained state

- Package `SourceFingerprint` pins original package origin/content/manifest/targets,
  not vendor feed availability. Inspect real immutable package inputs, not client
  requirement claims; a Registry target requirement must match its package manifest.
- `ResolutionFingerprint` pins normalized requirement, exact selected vendor identity,
  relevant official evidence, source/materialization policy and platform selection;
  updates also bind the original exact identity. Do not hash observation-time churn
  or unrelated feed entries. `ExpiresAtUtc` is local consent validity, not execution authority.
- Consent/impact fingerprints additionally bind operation, settings/retention intent,
  installed package identities and complete affected usage. Reconfirm before download
  and durable commit; drift requires a fresh plan. Client cannot rewrite a plan.
- User sequencing is ordinary FIFO: runtime acquisition first, package install second
  waits. On acquisition failure/cancel, do not run the package step. Re-preview the
  original package source and attach its prerequisite expectation before the ordinary
  package mutation. No generic job DTO, job SDK, explicit dependency graph or parallel
  package/runtime acquisition protocol is introduced.
- Retain/renew/status/release package-upload methods and handles are unchanged.
  Preserve immutable uploads while paused; adoption requires its original source
  fingerprint. Resume still revalidates package policy and capability consent.
- Default automatic updates remain ON; settings use `ExpectedRevision` CAS. Prepare
  verified side-by-side bytes, then apply only when stopped or after explicit fresh
  `RestartAffectedPackages` impact consent. `CurrentRuntime.VendorVersion` describes
  the current bound release; package-plan `CurrentVersion` still describes a package.
- Installed package state is the single binding authority. Retention transfers at
  committed update only. Process leases/pending transactions/manual roots still block
  unsafe uninstall/GC. Honor `StoreCommitted`, `RuntimeSessionApplied`, `CommittedStamp`.
- Retain private per-user roots. Host receipts are new-only; no data migration/read
  fallback. A store reset, if needed, is a separate explicit operation, not this change.

## Registry and policy constructor changes

These are ordered parameter names; their CLR types remain in the linked contracts.
Optional defaults are retained except for the removed runtime-specific parameters.

| Record | New constructor/property shape |
| --- | --- |
| `RegistryExecutionRuntimeRequirement` | RuntimeId, VersionRange, ModuleAbi; relocated to `PackageRuntimeRequirements.cs` |
| `RegistryResolveUpdatesRequest` | InstalledPackages, DesiredTargets, Compatibility, ExpectedResolutionFingerprint=null, IncludePrerelease=false |
| `RegistryInstalledPackage` | PackageId, Version, AcquiredProjections, PackageContentIdentity |
| `RegistryResolveInstallPlanRequest` | PackageId, Version, Tag, DesiredTargets, InstalledPackages, Compatibility, ExpectedResolutionFingerprint=null, IncludePrerelease=false, AllowDowngrade=false, Reinstall=false, VersionRange=null, Required=true |
| `RegistryResolvePackageChangesRequest` | Packages, InstalledPackages, Compatibility, ExpectedResolutionFingerprint=null, IncludePrerelease=false, AllowDowngrade=false, Reinstall=false |
| `RegistryInstalledPackageState` | PackageId, Version, DependsOn, AcquiredProjections, PackageContentIdentity |
| `RegistryPackageInstallPlanItem` / `RuntimeRegistryPackageInstallPlanItem` | PackageId, CurrentVersion, Version, PackageContentIdentity, ActionKind, DeprecatedMessage, DependsOn, Targets, Artifacts |
| `RegistryResolveUpdatesResponse` | Updates, Errors, TrustedArtifactOrigins, Actions=null, ResolutionFingerprint="", ExpiresAtUtc=default |
| `RegistryResolveInstallPlanResponse` | Success, Items, Warnings, Errors, Conflicts, TrustedArtifactOrigins, ResolutionFingerprint="", ExpiresAtUtc=default |
| `RuntimeRegistryResolveInstallPlanResponse` | Success, Items, Warnings, Errors, Conflicts, TrustedArtifactOrigins, RequiredCapabilityConsents=null, ResolutionFingerprint="", ExpiresAtUtc=default |
| `RuntimeRegistryPackageChangeResult` | Constructor unchanged; remove RuntimeArtifacts and RuntimeConflicts properties |
| `RegistryPolicySynchronizationRequest` | Nonce, Packages, PlatformCatalogs, MinimumPackagePolicy, MinimumCheckpoint |
| `RegistryPolicySynchronizationResponse` | Nonce, RegistryOrigin, PackagePolicy, Checkpoint, ObservedAtUtc, ValidUntilUtc, Packages, PlatformCatalogs |
| `RuntimePolicyWatermark` | RegistryOrigin, PackagePolicyRevision, PackagePolicyFingerprint, CheckpointSequence, CheckpointDigest, TopologyGeneration, TopologyFingerprint |
| `RegistryPolicyStatusV1` | Code, PublicReady, ExternalAligned, AppliedCheckpoint, PrimaryCheckpoint, MirrorCheckpoint, PackagePolicy, PublicationAuthorityPolicy, AuthorizationPolicy, AuthorizationGeneration |

`Targets[].ExecutionRuntime` is the sole Registry runtime-requirement shape, attached
to `Role`/`Rid`; no duplicate runtime requirements array or selected-artifact list.
Both target DTOs retain their existing constructor and optional requirement; those
DTOs and the requirement now reject unknown fields. Runtime prerequisite diagnostics
are Host-local, not Registry runtime resolution conflicts. Registry action kinds are
exactly PackageInstall/PackageUpdate/PackageRemove/Keep. Local rebind is not a Registry action.

Keep package `PackagePolicy`, `PolicyCheckpoint`, `ResolutionPolicy`, compatibility
conflicts, package artifact hashes/origins and package/platform policy functions.
The 24-hour policy lease, nonce, checkpoints, rollback floors, platform revocation,
package permissions and credential/publication authority still protect their same
subjects. Remove runtime policy data from fingerprint roots/readiness/checkpoints
in the Registry implementation; no consumer migration format is required.

### Collection/wire changes

Runtime management collections copy input and reject null items/overflow, including
`with`/init replacement: 1,024 items for usage/impact/bindings/prerequisites, 128 catalog
entries, 32 prepared updates, 32 prerequisite package sources. Newly changed Registry
plan/installed-state/target collections use `RegistryPackagePlanLimits.MaximumItems=1024`;
policy synchronization uses 128 packages and 128 platforms. `InstalledPackageDescriptor`
bindings are now required on wire and bounded; an empty list is explicit.
Required collection properties have init-time freezing as well as constructor freezing.
Required fields remain required even where C# failure-result convenience defaults exist.
Host applies overall JSON/byte and semantic bounds; overflow never truncates authority.

## Removed inventory and next-owner work

Removed from `Sunder.Registry.Contracts`:

- `ExecutionRuntimeLifecycleStates`; `RegistryHostRuntimeTrustProfile`.
- `RegistryExecutionRuntimeIdentity`, `RegistryExecutionRuntimeLifecycle`,
  `RegistryResolvedExecutionRuntimeArtifact`, `RegistryExecutionRuntimeArtifactDetails`,
  `RegistryExecutionRuntimeReleaseSummary`, `RegistryExecutionRuntimeReleaseDetails`,
  `RegistryExecutionRuntimeSummary`, `RegistryExecutionRuntimeSearchResult`.
- `RegistryPackageTargetRuntimeBinding`, `RegistryRuntimeOnlyRebindDetails`,
  `RegistryExecutionRuntimeResolutionConflict`, `RegistryResolveExecutionRuntimeRequest`,
  `RegistryResolveExecutionRuntimeResponse`.
- `RegistryExecutionRuntimeNativeAttestationExpectation`, `RegistryStageExecutionRuntimeReleaseRequest`,
  `RegistryExecutionRuntimePublicationArtifactExpectation`, `RegistryLookupExecutionRuntimePublicationRequest`,
  `RegistryPublishExecutionRuntimeArtifactRequest`, `RegistryPublishLocalExecutionRuntimeArtifactRequest`,
  `RegistryExecutionRuntimePublicationArtifact`, `RegistryStagedExecutionRuntimePublicationResponse`,
  `RegistryTransitionExecutionRuntimeLifecycleRequest`, `RegistryExecutionRuntimeLifecycleOperationResponse`.
- `RegistryRuntimePolicySubject`, `RegistryRuntimePolicyDecision`, `MaximumRuntimeSubjects`;
  `RegistryPackageActionKinds.RuntimeOnlyRebind`; all execution-runtime route constants/helpers
  and the runtime-only `RegistryApiOperationNames` class.

Removed from `Sunder.Runtime.Contracts`:

- `RuntimeExecutionRuntimeLifecycleStates`, `RuntimeExecutionRuntimeLifecycle`,
  `RuntimeExecutionRuntimeAcquisitionSources`, `RuntimeExecutionRuntimeImportRequest`.
- `RuntimeRegistryExecutionRuntimeIdentity`, `RuntimeRegistryExecutionRuntimeLifecycle`,
  `RuntimeRegistryResolvedExecutionRuntimeArtifact`, `RuntimeRegistryPackageTargetRuntimeBinding`,
  `RuntimeRegistryRuntimeOnlyRebindDetails`, `RuntimeRegistryExecutionRuntimeResolutionConflict`.
- Import action/route/upload size limit; `BlockedRuntimeSecurityRecovery` update state;
  old management/updates protocol feature constants. No legacy equivalents remain.

`ExecutionRuntimeErrorCodes`, `ExecutionRuntimeResolutionConflictCodes`,
`ExecutionRuntimeErrorDescriptor`, `ExecutionRuntimeErrorCatalog` moved to
`Sunder.Runtime.Contracts` with Host ownership. Retired error members: PolicyRequired,
Deprecated, Retired, Revoked, HostTrustUpdateRequired, CurrentRuntimeTrustDenied.
New errors: SourceNotAllowed, ArtifactChanged, PlatformPrerequisiteMissing. All other
runtime error members retain their wire strings; diagnostics now name vendorVersion
and archiveSha256 appropriately. Package/platform policy errors are unchanged.

Runtime.Client removes `UploadExecutionRuntimeAsync` and all three
`ImportExecutionRuntimeAsync` overloads. CLI owner removes its import command/request;
App owner removes import picker/UI and runtime Registry origin selection.

Host owner: implement new-only receipts/source resolver and update endpoint bindings,
`RuntimeProtocolDescriptor`, `RuntimeRegistryContractMapper`, prerequisite/binding
preparers and policy consumers. Mapper must remove RuntimeTrustProfile and old
runtime artifact/lifecycle/binding/conflict mappings; map target requirements only.
Registry owner: remove runtime endpoints/storage/publication/resolve and package
publish/catalog availability gates; preserve package target requirement validation.
App/CLI owner: consume frozen methods above; show official source, actual version,
nullable preview size and health/support independently, preserving FIFO/resumption.
Final integration owner: refresh affected JSON/API snapshots and targeted protocol,
store, platform, package-policy and safe-update tests after implementation cutover.
