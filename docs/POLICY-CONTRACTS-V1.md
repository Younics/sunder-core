# V1 policy, installation and process-supervision contracts

This reference describes dependency-free Registry/Runtime policy DTOs and their
admission/recovery rules. DTOs contain no database implementation, storage key,
artifact root, process handle or bearer credential.

## Wire validation and corpus

`specs/policy/v1/contracts.schema.json` defines policy payloads. `conformance.json`
owns the current reviewed golden-vector inventory and required schema definitions.
The C# contract suite and the independent Node/Ajv Draft 2020-12 oracle execute the same
corpus, including required/expired/stale/conflicting/clock-invalid leases, terminal
denials, unknown subjects, denied load permits, process exit, and provider Problem Details.
The inventory envelope references the existing public-inventory schema by its `$id`;
it does not define a second inventory format.

Consumers must use a bounded strict JSON boundary: camelCase, case-sensitive names,
no duplicate or unmapped members, no quoted-number coercion, respected nullability and
required constructor parameters, and maximum depth 32. In .NET 10 these require explicit
`JsonSerializerOptions` settings; Web defaults alone are insufficient. Required nullable
members must be present with an explicit null. Collection snapshots reject null lists and
null elements. Referenced existing inventory types retain their stricter converter rules.

Schema validation checks shape, limits and local state relationships. The owning service
also validates canonical origin/ranges, sorted exact identities, cross-record equality,
complete graph/tag membership, nonce freshness, policy history, UTC arithmetic and
continuous-clock continuity. The DTO assembly is not that service. JavaScript consumers
must handle checked Int64 fields losslessly; native `JSON.parse`/Ajv shape tests using the
small golden values do not prove lossless parsing of large policy watermarks.

## Registry synchronization

`POST /api/v1/policy/synchronize` consumes `RegistryPolicySynchronizationRequest` and
returns one `RegistryPolicySynchronizationResponse`. Each list contains at most 128
subjects, sorted ordinally by the complete exact identity and with no duplicates.
Packages use package/version/content; platform catalogs use authority/release/catalog
SHA. Official execution runtimes are not Registry policy subjects.
At least one subject is required. Cross-origin data is never mixed in a response.

`nonce` is 16 cryptographically random bytes encoded as 32 lowercase hexadecimal
characters. The response must echo the outstanding request nonce and configured canonical
HTTPS Registry origin, and supply exactly one decision for every requested subject,
without extras. An unknown subject receives an explicit PolicyRequired denial; omission
is not permission. Positive policy revisions use checked Int64 and
must be handled losslessly by every consumer. Fingerprints are exact lowercase SHA-256.
The request's `minimumPackagePolicy` is explicit null before a cached observation exists.
Same revision with different fingerprint/overlapping
decision is a conflict; lower state cannot replace a retained watermark.

PostgreSQL is the Registry policy authority. The revision/fingerprint floor detects
responses older than or inconsistent with this client's retained observations; it does
not independently prove that the database has not rolled back. S3 replication is an
infrastructure concern and supplies no application policy authority or wire identity.

The response carries the database package-policy identity plus complete
package/platform decisions. `observedAtUtc`
and `validUntilUtc` are UTC and differ by exactly 24 hours. The Host binds the local
deadline to its earlier request-start continuous-clock tick; network time and failures
never extend the lease. No policy response or range selection creates use grants.

Package identity state is Active, SecurityBlocked or Unknown. Version availability is
Listed, Yanked, SecurityBlocked or Unknown. Disposition is Allowed, Denied or PolicyRequired.
Yanked excludes new selection but preserves an existing exact binding and repair; either
security block is terminal and denies install/update/repair/activation. Platform lifecycle
uses Supported/Revoked/Unknown. Every referenced platform
dependency receives a complete corresponding platform decision. Terminal denial cannot
be broadened by later snapshots or offline state.

## Compatible Installation

`RegistryPackageCompatibilityRequirement` contains reviewed range/prerelease/presence
intent and exact RPC ID/version/descriptor/ABI requirements. Presence is explicitly
required or optional; provider imports contribute required constraints. Package content
hashes are deliberately absent from this requirement. `RegistryPackageRestoreProvenance`
separately retains the selected artifact's complete public inventory/hash and exact
Registry origin, including reviewed ranges for any distinct ultimate package authority.
Those selections are authenticated restore/build evidence, not installation pins.

`RegistryPackageResolutionPolicy` supplies the complete normalized requirements,
provenance, optional-branch decisions and platform policies for an install/update/repair
plan. Included optional branches name the actual selected package identity. Omitted
branches have no selected identity and an explicit diagnostic; they grant no uses and
do not hide independently authorized providers. All included versions must satisfy
all reviewed ranges and exact RPC tuples. Required wins compatible coalescing, while
unreviewed transitive authority ranges and conflicting tuples fail closed.

`RegistryResolveInstallPlanResponse` and `RegistryResolveUpdatesResponse`
also expose `PackagePolicy`. This identity participates in plan
fingerprint fencing. Its absence in an unsuccessful or unverified result conveys no
policy authorization; consumers must not infer a default current revision.

The required package API/provider Registry consumer now populates `ResolutionPolicy`
and `CompatibilityConflicts` on install/update responses. The former retains complete
restore inventories separately from normalized solve constraints; the latter contains
sorted package requirements for `contract-authority.release-conflict`. Collections are
detached snapshots. Missing `ResolutionPolicy` is not proof of compatible imported
content. A selected artifact not yet accepted by Registry is
`package-api.candidate-authority-unconfirmed` (HTTP 409). Platform decisions and optional
branch dispositions come from the Registry consumers. Host admission enforces this
evidence against the actual package graph and acquired targets.

Installed target acquisition is recorded separately from the complete source manifest.
Removing a target from the acquired set does not remove package-wide import provenance,
reviewed dependencies or public inventory. Active uses, required-provider predicates,
runtime bindings and App loading are restricted to acquired roles/targets. The original
manifest SHA remains the consent and source-metadata fence. Acquisition scope is part
of the durable installed/journal identity even when shared payload bytes are unchanged.

## Atomic publication policy and Stack revisions

`RegistryPackagePublicationEvidence` freezes exact accepted package/version/content,
immutable owner principal, complete public inventory SHA and original Listed/Yanked
availability. Exact version details expose it under nullable `Publication`; absent
evidence cannot confirm an ambiguous upload. Original publication policy and current
availability are distinct. See [package publication](REGISTRY-PACKAGE-PUBLICATION.md).

`PackageVersionCohortAvailabilityTransitionV1` is one owner-scoped operation with a
bounded idempotency key, expected package-policy revision, 1–64 exact version changes,
and 0–64 tag changes. Entries are sorted/distinct. Each version transitions between
Listed and Yanked with differing expected/resulting state. Null tag target deletes a
tag; non-null target must name the same package and remain Listed. Requests cannot
contain SecurityBlocked, owner transfer or independently successful suboperations.
The service validates the entire proposed graph and resulting tag maps before commit.

The committed result contains canonical request SHA, exact database policy identity, audit time,
the complete version transition and prior/resulting tag maps. Exact owner/idempotency/
request retries return the original result before checking the now-stale expected
revision. Conflicting key reuse fails. Validation failures use Problem Details, so the
result has no partial-success shape. Package/version security block is a separate
administrator operation with a bounded public reason and expected policy revision.
Tag snapshots contain actual non-null targets; null is an operation only in a tag change.

`RegistryStackRevisionDetails` names one immutable positive checked revision, owner,
publisher, manifest/artifact identity and immutable presentation/media. Live statistics
and mutable user attribution are excluded. Current Stack policy names that revision
and Listed/Unlisted/SecurityBlocked visibility. Ordinary visibility changes are owner-
only; terminal security block is administrator-only. No revision or policy DTO supplies
a hard-delete or ownership-transfer operation.

## Package management preconditions

Stack publication/revision/visibility consumers use the separate
[Stack handoff](REGISTRY-STACKS-V1.md), including the authority-policy preflight.

The existing tag set/delete, version yank/deprecation and maintainer add/remove routes
require the constants in `RegistryPolicyMutationHeaders`:

| Operation | Revision header | Source response |
| --- | --- | --- |
| Tags, yank/unyank, deprecation | `X-Sunder-Package-Policy-Revision` | `RegistryPackageDistTagsResponse.PackagePolicy` |
| Maintainer add/remove | `X-Sunder-Publication-Authority-Revision` | `RegistryPackageMaintainersResponse.PublicationAuthorityPolicy` |

Every mutation also requires exactly one `X-Sunder-Idempotency-Key`, 1–128 ASCII
letters/digits or `.`, `_`, `-`. Revisions are positive canonical decimal Int64, without
signs, leading zeros or overflow. The two revision domains are not interchangeable.
Absent policy evidence grants no mutation permission. Clients fetch the matching policy
before dispatch and must not synthesize a revision. `isYanked` is a required JSON member;
explicit false requests unyank, while omission is invalid.

Committed actor/key/request retries return success before the stale-revision fence;
conflicting key reuse fails. Already-requested state is a no-write success that reserves
no key and must pass live preconditions again on a later request. Only the owner changes
membership or availability; current maintainers may change tags/deprecation. Membership
does not transfer package/contract ownership or permit new public contract versions.
The existing operation-response shapes remain; errors use standard bounded Problem
Details. Web and Runtime clients refuse to dispatch when policy metadata is missing.

## Runtime policy and App process supervision

`RuntimePolicyLeaseStatus` carries the server observation, exact response identity,
watermark and local continuous-clock evidence. Boot identity, tick frequency, request
start, deadline/high-water and optional VM generation are kept together. Wall UTC is
display/audit data only. Required, expired and uncertain-continuity states fail closed.
The immutable timing contract is 24-hour lease, five-minute refresh with up to 30 seconds
jitter, 15-second request deadline and retry delay capped at 15 minutes.

`RuntimePolicyWatermark` contains only Registry origin and package-policy revision/
fingerprint. The current pre-V1 private policy-cache schema is rebaselined at version 1:
origin snapshots, retained installed/journal snapshots and compatibility evidence use
the same database-only identities. There are no external checkpoint/topology members,
null placeholders or old-field readers. Strict codecs reject those obsolete members.
This is a coordinated fresh baseline, not a new migration or user-data reset step.
The local durable generation, intent recovery copy and continuous-clock high-water
remain consistency/lease mechanisms; they do not establish independent Registry authority.

A configured Registry with an initialized empty database is ready. Catalog and authority
requirements are enforced by their consuming operations, independently of service health.
This contract surface has no Registry-wide startup/status/recovery payload. See
[Registry availability and operation errors](REGISTRY-AVAILABILITY.md).

`RuntimePlatformPolicyStatus` separately names the exact authority/release/catalog SHA
and effective disposition, revocation audit time and local lease. An expired lease
cannot broaden a retained terminal platform denial.

Before loading any package App code, the shell opens an authenticated policy session
with PID, OS birth identity, executable SHA/build, App generation and Runtime instance.
The service validates the real process and records exact package identity before issuing
a load permit. Session/permit IDs are identifiers inside the existing authenticated
local transport, not credentials. No new bearer credential is exposed through these DTOs.

The product shell's control and App RPC clients connect directly to Runtime's private
Unix socket or named pipe. A TCP proxy's peer identity is the proxy, so it cannot stand
in for the App process. Private local connection discovery carries the worker endpoint;
the shell verifies that direct and gateway handshakes name the same Runtime instance.
Missing private discovery fails closed. Package service providers receive neither the
process-policy controller nor its control-session identifiers.

The load request carries `snapshotId` and copies the snapshot's `policySubject` into
`package`. This names full published/source package content, not the UI projection hash. A missing,
retired or mismatched snapshot cannot authorize loading. Production permits carry a
valid lease; an explicit development permit has `development=true` and a null lease
unless it requires published platform policy, and cannot authorize an installed package. Recording the same bytes in development
does not authorize a later installed/origin context.

Heartbeat loss has a five-second grace; a changed Host instance triggers fail-fast.
A late response cannot revive an already elapsed grace, including after suspend.
Security denial/lease
expiry withdraws endpoints and requests whole-App shutdown; after five seconds the Host
forces termination, allows ten seconds for proven exit and retries every five seconds.
Replacement code stays denied while exit is unproven. The DTOs distinguish active,
shutdown-requested, termination-required, exit-proven and closed state. Package APIs
receive no supervision control or termination override.

Window inactivity is not supervision loss. The App renews policy on a dedicated
background thread with bounded synchronous local HTTP/1.1 exchanges, independent
of the UI dispatcher, package RPC, managed timer callbacks and the HTTP connection
pool's shared worker queue. Credentials, OS-peer identity, Runtime/session identity,
protocol compatibility and negotiated payload bounds still apply; the private
channel never falls back to TCP. The gateway channel permits local IPC or literal
HTTP loopback only, without DNS, proxies or redirects. On macOS, a scoped process
activity keeps this required supervision eligible while the window is inactive;
it does not prevent display or system sleep. The independent suspend-aware watchdog
and the fixed grace above are unchanged, not renewed by scheduling activity alone.

Loaded package/platform subjects remain in Host-owned synchronization after uninstall,
snapshot retirement or UI generation replacement until process exit is proven. Permit,
status, heartbeat and App RPC gates recheck the process's loaded policy; one denied
loaded package blocks further permits and RPC before the periodic enforcement pass.

Required-provider loss is an availability failure, not a security denial or a reason
to terminate the desktop. The Host fences only affected tracked App activations and
returns their exact retirement requirements through heartbeat/status. Their shell
cleanup and Host acknowledgement remain required, including after the provider has
recovered; a fenced activation cannot be revived. Legacy untracked callers fail
package-local availability checks. Unrelated package RPC and permits remain usable.
Security/package/platform denial and missing process-supervision proof retain the
independent process-containment rules above.

### Acknowledged App activation retirement

Managed and web App loads supply `appActivationId` (the actual activation owner, not
the presentation generation) and the exact `appTarget` with their snapshot request.
The Host durably binds its returned `permitId` to the process policy session, activation,
package content and target. App RPC open supplies that activation as `appGenerationId`
and the issued `loadPermitId`; missing, mismatched and retired authority fails closed.
An untracked legacy load retains conservative process-lifetime provider obligations.

After local view/DI/RPC cleanup succeeds, the shell posts
`RuntimeAppActivationRetirementRequest(Session, AppActivationId, LoadPermitId)` to
`/api/v1/app-policy-sessions/{sessionId}/activations/{activationId}/retire` over the
private process-authenticated transport. Any permit issued to that actual activation
is a valid anchor, including an older permit retained through a transport replacement.
The Host fences reopening, revokes all associated callers/scopes/content authority,
and waits for opening callers, discovery/watch/invocation/subscription work, content
registrations, reads and resource teardown. Cancellation or stream disposal alone
does not prove completion of cancellation-ignoring work. The drain wait is bounded
to five seconds; failure leaves retirement fenced and unacknowledged, and may be
queried again with the same identity. A cleanup failure cannot produce success.

Only the exact typed result with `state="retired"` ends that activation's required-provider
liveness. Transport close ends only its opaque caller handle. Candidate discard of a
retained activation closes the candidate replacement, while final activation retirement
covers all its old/new permits. Retirement neither removes durable `loadedPackages`
nor proves OS exit, and never reverses a denial or restarts its five-second shutdown clock.

### Staged development-owner withdrawal and reload

`DevPackageOwnerSession.ReleaseStagedAsync` prepares the prospective merged source
graph, calls the shell's retirement callback, then commits authenticated owner withdrawal.
The callback receives `PackageLifecycleStageResult.BaseStamp` and exact `Retirements`.
Owner heartbeat remains live during cleanup. The returned snapshot is a checked server
acknowledgement; null means there is no owned lease. Cleanup/timeout/conflict failures
propagate. Ambiguous commit responses keep their exact control identity for an explicit
receipt retry and suppress automatic owner reacquisition; no service RPC is replayed.

The Host uses the existing lifecycle-stage expiry, discard and terminal status machinery.
Stage/commit requests bind Runtime instance, owner credentials and lease revision; commit
also checks the owner's mutation identity, all-owner source revision and base generation.
Every source ownership change advances its revision even if the effective graph and
generation stay unchanged. Last-owner withdrawal retires affected live App activations;
non-last co-owners retain the effective Worker, and installed fallback is evaluated as
the actual prospective graph. Fresh incompatible permits block commit. An admission fence
covers final liveness validation through publication; withdrawn Workers are then quiesced
and disposed by the ordinary lifecycle.

Watched/manual stages publish retirement requirements through process policy status and
heartbeat as well as the stage result, so stopped ordinary SSE cannot strand the handshake.
The old source graph stays active until acknowledgement. Shell cleanup runs outside its
heartbeat/generation locks; after acknowledgement it waits for terminal `StageId` status
before reconciliation, including reconciliation of the same generation after failure or
discard. Expired/lost-owner withdrawal without acknowledgement fences and requests
retirement of affected App activations without terminating the process. An
unproven cleanup remains quarantined; recovery requires fresh activation authority.

The authenticated shell uses `POST /api/v1/app-policy-sessions` to open a session,
`GET /api/v1/app-policy-sessions/{sessionId}` for status, and `POST` on the session's
`heartbeat`, `load-permit` and `shutdown` subresources. Heartbeat and shutdown carry
`RuntimeAppPolicySessionRequest`; shutdown returns `RuntimeAppPolicySessionStatus`.
A shutdown request records intent, not client-supplied proof of process exit. Only the
Host's observed OS process identity/exit proof permits exit-proven or closed state.

Required-provider diagnostics contain sorted consumer package/role/exact-RPC/action
predicates; cycle details additionally contain the sorted strongly connected package
set. They contain no bearer endpoints or automatically chosen provider. The shared
policy error catalog freezes ownership, safe message, HTTP status, retry classification
and bounded diagnostic field names. Retryable means a fresh explicit policy/graph
operation may succeed; it never authorizes replaying a non-idempotent RPC call.

`RuntimePolicyProblemDetails<TDetails>` freezes the local Problem Details envelope with
operation-specific `details`: provider selection for not-found/ambiguous, unsatisfied
predicates, or cycle predicates/package IDs. Not-found is HTTP 404 with zero matches;
ambiguous is HTTP 409 with at least two. Both required-provider graph errors are HTTP
409. Registry operation errors use the shared Problem Details envelope and the relevant
operation's status/code; execution-runtime conflicts use the existing execution-runtime catalog.
