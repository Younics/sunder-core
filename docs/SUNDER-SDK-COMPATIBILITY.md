# Sunder SDK Compatibility

This document defines the Sunder V1 compatibility contract for independently installed Apps/Hosts and packages.

## Compatibility Boundary

The compatibility boundary is each exact target's generated manifest metadata, the public `Sunder.Sdk*` assemblies, and Host support for those contracts. The Host validates target compatibility before loading an assembly or starting a process.

The App/CLI-to-Runtime HTTP protocol is a separate compatibility boundary. Authenticated clients first call the unversioned `/api/handshake` endpoint and require protocol identity `dev.sunder.runtime`, an overlapping supported revision range, a non-empty Runtime instance id, and required feature ids before using `/api/v1`. Product, file, and informational versions are diagnostic fields and are never interpreted as protocol SemVer. Unknown or malformed protocol data fails closed.

The current App and Runtime use protocol revision `1`. RPC V1 clients require the Runtime feature `schema-first-rpc.v1`; package web Apps additionally require `app-web-rpc.v1` and use only the injected `sunder.bridge.v1` browser protocol. Dev sessions require `dev-package-owner-leases.v1`: every mutation and heartbeat is fenced to one `RuntimeInstanceId`, owner mutations replace the complete desired folder/watch set, and mutation id plus owner revision make retries idempotent.

## Target Compatibility Fields

- `archiveFormatVersion` and `manifestVersion`: package archive and manifest schema versions.
- `sdkVersion`: required strict SemVer for Runtime and optional strict SemVer identifying the SDK used to build another SDK-backed target.
- `requiredHostCapabilities`: granular Host requirements for one exact target, inferred from authored SDK usage or declared by non-.NET tooling.

Every Manifest V1 Runtime target requires `worker-protocol.v1` and selects exact wire identity `sunder.worker.v1` before process launch. Missing V1 capability, Worker V3, `dotnet`, and `process` fail compatibility validation; there is no probing, downgrade, or handshake fallback.

Installed package catalogs accept only current schema 5 records with complete provenance, capability grants, content identity, and inventory. Older catalogs and package layouts are rejected without migration or rewrite. `Unknown` remains valid explicit provenance for current unmanaged packages, and those packages can adopt a Registry source through the normal source-adoption operation.

SDK-backed targets contain distinct V1-form capability ids. Targets built on the coordinated 1.1 line require `sdkVersion >=1.1.0 <1.2.0` and `sdk-baseline-1-1.v1`. Every Runtime target additionally requires `rpc.v1` and `worker-protocol.v1`. Missing, malformed, mismatched, or unsupported target metadata is rejected before target code runs.

Published Sunder-to-Sunder NuGet dependencies use `[1.1.0,1.2.0)`. Coordinated npm package dependencies, generated Node templates, and runtime package dependencies use `>=1.1.0 <1.2.0`. Because `1.1.0-beta.*` sorts before the stable lower bound, the initial 1.1 baseline does not publish prereleases. Prereleases after a stable 1.1 baseline may use the same minor range.

The six public developer NuGets exclude generated assembly XML documentation companions. Runtime package archives exclude generated XML companions and symbol files after publish-file computation while retaining explicitly authored XML content. Release package-set verification rejects a NuGet that reintroduces a DLL-matched XML companion.

## Registry Resolution Profile

Installable Registry resolution is filtered against the requesting Host, not only against the Registry's publication baseline. `RegistryClientCompatibilityProfile` carries bounded, non-empty sets of supported manifest versions, Runtime target kinds, and Worker capabilities plus one Worker protocol capability, one SDK baseline capability, and the exact current RID. The current Runtime sends Manifest V1, Runtime kind `worker`, `worker-protocol.v1`, `sdk-baseline-1-1.v1`, its complete Worker V1 capability allowlist, and the RID returned by the Runtime process.

`RegistryResolveUpdatesRequest`, `RegistryResolveInstallPlanRequest`, and `RegistryResolvePackageChangesRequest` require this profile in their `compatibility` property. Package exact/tag GET resolution uses the same camel-case property names as query parameters: repeat `manifestVersions`, `runtimeTargetKinds`, and `workerCapabilities`; send `workerProtocol`, `sdkBaseline`, and `rid` once. `version` and `tag` remain mutually exclusive selection parameters. The desired target RIDs must agree with the profile RID.

Missing, malformed, empty, oversized, internally inconsistent, or unsupported compatibility data fails resolution. Candidate filtering applies to exact versions, explicit tags, `latest` fallback, updates, transitive dependencies, install plans, and installed-version reuse. Public search, package details, version history, and immutable version details remain historical inspection APIs and do not require a compatibility profile.

Compatibility is request data, not an ambient or client-selected HTTP header. App and CLI install/update operations delegate to Runtime; Runtime authors the profile from its own implementation before calling Registry. This prevents an App, CLI, proxy, or stale persisted option from claiming capabilities on the Host's behalf.

## Compatibility Rules

- **V1 is a compatibility designation, not a requirement to number every artifact
  `1.0.0`.** The coordinated SDK line is currently `1.1.x`. App/Host releases and
  package-owned domain contract versions have independent meanings. Preserve
  their real versions and immutable contract tuples; do not reset them for V1.
- Until release, current API snapshots describe an editable, unshipped candidate.
  Once released, supported references are immutable, separately retained inputs.
- Compatible SDK changes may add usable APIs, but must preserve supported source,
  CLR identity/shape and behavior. An added abstract interface/base-class member,
  static abstract member, or required initializer can break existing implementers
  even when no old declaration was removed.
- Newer compatible Hosts must continue admitting and running the supported V1
  baseline. Advancing the Host/SDK product version cannot silently replace its
  supported baseline with only the new minor line.
- A new Host-bound feature needs a capability and fail-closed admission on older
  Hosts before assembly load/process launch. SDK SemVer alone is not a feature
  negotiation protocol. Pure package-local additions need not invent Host capabilities.
- An old Host must reject unsupported package SDK requirements before assembly load.
- Runtime and Registry projection records are structurally ratcheted. Silent projection drift is not permitted.

### Editable snapshots and immutable released references

The authoritative status is
[`eng/compatibility/supported-releases.json`](../eng/compatibility/supported-releases.json).
It currently says **`UNFROZEN` / `UNRELEASED`**, with no release records. The
historical `core-compatibility-candidate.json`, Stack migration oracle and Agent
captures are inert byte-preserved archives, not current runtime/build gates or
sources of current expected identities/hashes. They are not refreshed to create
release evidence. A separately collected `core-current-compatibility-candidate.json`
remains UNFROZEN / UNRELEASED and cannot replace independent released references.
The supported-release ledger does not claim that SDK `1.1.0` or any retained
package has shipped.

The two checks are complementary:

1. `PublicApiBaselineTests` compares the actual built assemblies with editable
   `languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/PublicApi/*.txt`.
   `PublicApiFidelityTests` keeps the Build task property/functional-attribute
   ratchet. This catches code drift even if a comparison manifest is unchanged.
2. `scripts/compatibility/check-supported-releases.mjs` compares those current
   surfaces against every separately retained supported release. It authenticates
   retained snapshot, artifact and corpus-recipe bytes by SHA-256 and size, then
   compares API records rather than requiring the entire current snapshot hash
   to equal the released hash.

The closed, bounded [manifest schema](../eng/compatibility/supported-releases.schema.json)
has at most 64 release records, 256 artifacts/surfaces per release and 128 corpus
entries. Manifests are limited to 1 MiB; snapshots/recipes to 8 MiB; artifacts to
8 GiB and hashed incrementally. Each release declares:

- its actual version, repository HTTPS identity and full nonzero source commit;
- artifacts with local retained path, immutable retention locator, source commit,
  size and SHA-256 (including original packages, SDK/tooling, source and Host inputs);
- surfaces with artifact identity, immutable snapshot record, comparison format,
  and a separate current path;
- all three corpus directions below, explicit artifact inputs, an owning test
  suite and a hashed reproduction recipe.

All retained paths are under `eng/compatibility/releases/<release-id>/` in the
retention root. Ordinary candidate snapshot paths cannot point there. Unknown
fields/formats, duplicate identities, traversal, symlinks, missing inputs and
hash/size mismatches fail. `dotnet-public-api-v2` permits additive concrete members,
default interface members and new types while rejecting released declaration
removal/shape changes and new mandatory obligations on old types.
`exact-bytes-v1` is a conservative option for a surface without a semantic
comparator; it does **not** claim to understand TypeScript or JSON compatibility.

Every gate invocation requires an independent anchor:

```sh
# Use the protected PR base or pre-push commit, not an editable current document.
node scripts/compatibility/check-supported-releases.mjs --base-ref "$BASE_COMMIT"

# An externally retained manifest and an already downloaded, byte-exact bundle:
node scripts/compatibility/check-supported-releases.mjs \
  --base-manifest "$TRUSTED_BASE_MANIFEST" --retained-root "$RETAINED_ROOT"
```

`--base-ref` requires a full Git commit identity. The gate reads the base manifest
from that Git object. An absent base manifest is allowed only when introducing
the empty, unreleased ledger. With a released base, editing both code and current
snapshots, changing a released hash/path/source/recipe, removing an old release,
or resetting the ledger to unreleased fails. The caller must supply a trusted
anchor; supplying an attacker-edited second file is not independent protection.
Release entries are append-only, including their original surface selection and
corpus. This schema has no release-history deletion or retirement escape hatch.

Core CI uses the PR base or pre-push commit; manual/tag/scheduled invocations use
the protected main reference (its parent when it is the checked-out commit).
Normal CI needs no Registry or publication credentials. Once real releases are
registered, CI must provision their retained bundle before this step; absent
artifacts are failures, not optional skipped compatibility checks. The gate
verifies identities and API comparison, not publication authenticity or execution
of the corpus recipes. The release workflow establishes provenance; owning test
lanes must execute the retained corpus and record actual results.

### Mandatory SDK release qualification

Ordinary `check-supported-releases.mjs --base-ref ...` success is **not release
qualification**. The empty `UNFROZEN` / `UNRELEASED` ledger remains valid for PR/dev
checks. Both stable and prerelease SDK tag workflows additionally require
`sdk_release_compatibility.py prepare` before staging, publishing or promotion.
Absent genuine inputs fail with `SDK release qualification missing`; no release
has been qualified by the synthetic script fixtures.

The candidate input is `compatibility/` inside the same-run `sunder-tested-sdk`
artifact, transported alongside the tested `sdk/` and `npm/` files. The reusable
CI upload includes `artifacts/compatibility/` when present. **Genuine retained
inputs and reviewed owning-test recipes are still required; ordinary source-test
success cannot manufacture them.** The integrated SDK tag workflow's
`compatibility-corpus` matrix executes the bounded owned collector on portable
Linux and all six native RIDs, then uploads **separate**
`sunder-sdk-compatibility-lane-<lane>` artifacts. Staging downloads those artifacts
from the same run into an independent directory. Candidate-bundled reports or
self-authored execution labels do not substitute for this transport.

The directory must contain:

- `qualification.json`, following the closed
  [SDK qualification schema](../eng/compatibility/sdk-release-qualification.schema.json);
- `supported-releases.json`, the exact tagged-source ledger bytes;
- `base-supported-releases.json`, the exact independently trusted Git-base ledger;
- every referenced artifact, snapshot and recipe, at its
  `eng/compatibility/releases/<reference-id>/...` path.

The version-2 candidate envelope has `status: freeze-qualified`; that assertion
alone grants no qualification. Its `reference` uses the supported
release reference shape, but is separate from the **published** ledger. It binds
the exact SDK version, `artifactSourceCommit`, SDK tag workflow/run/attempt, source
and base manifest SHA-256 values, all six NuGet/three npm package-to-artifact
bindings. It has **no `results`, report choice or required-test selectors**. Those
fields and the former version-1 report-claim format fail closed. It contains no
publication timestamp or claim that the candidate has shipped.

The protected `sunder-core-release` environment must pin
`SUNDER_SDK_COMPATIBILITY_BASE_COMMIT` to the approved pre-release history commit.
It must be a full nonzero ancestor of, and different from, the artifact source.
There is no release fallback to the current document, `origin/main` or an absent
base. Source history must equal that trusted history; earlier release confirmation
belongs in its own evidence step before choosing the next base.

Release qualification requires:

1. An entirely new candidate reference ID **and version**, absent from both source
   and trusted history. Passing against previous releases alone cannot release a
   later SDK. The candidate must retain every previously supported surface
   selection and its own exact current snapshots, artifacts and corpus recipes.
2. All historical references unchanged and all retained hashes/sizes verified.
   Historical API comparisons remain additive-compatible; the new candidate's
   reference must match its own current surface bytes exactly.
3. Exact candidate package inventory and bytes matching the retained reference,
   from the approved source. A rebuild/repack or package substitution fails.
4. Executed passing test selections for every corpus in every historical reference
   **and** the candidate: portable old-source/new-SDK, and all six runtime RIDs for
   old-package/new-Host and new-feature/old-Host. Missing, skipped or failed results,
   unknown or duplicate coverage, and another run/source/future attempt fail.
   TRX selectors are fully qualified declaration IDs; JUnit selectors are
   `<file>::<test-name>`. Required selectors come exclusively from the immutable
   reviewed corpus recipe. Python's actual XML parser returns both selectors and
   outcomes from real attributes; raw-XML regex attribute scanning has no authority.
   A failed TRX result remains failed without summary/counters, including when a
   quoted note contains `outcome="Passed"`.

#### Reviewed recipes and owned execution evidence

Every corpus recipe is JSON conforming to the closed
[recipe schema](../eng/compatibility/sdk-corpus-recipe.schema.json). Its retained
bytes/hash must equal the reviewed file
`eng/compatibility/recipes/<reference-id>/<corpus-id>.json` at the exact artifact
source. The collector checks the Git blobs of both the recipe and its owning
source-test entry point. Historical recipe hashes remain immutable.

The recipe fixes the reference/corpus identity, direction, owner, command, report
format, required selectors and timeout. Commands use `dotnet-test`, `node` or
`python3`, with contained source-test entry points and bounded literal arguments.
`dotnet-test` uses a fixed command and exact selector filter. There is no shell,
external scenario executable, caller-selected driver or separate dispatch.

`sdk_compatibility_execution.py collect` measures the process's actual OS and
architecture, compares them with `RUNNER_OS`/`RUNNER_ARCH`, and rejects a mismatched
lane or emulated runner. Portable source cases run on Linux x64; binary/Host cases
execute on each matching native RID. The protected SDK tag, workflow, source,
owning job, run and attempt are stamped by the collector, not supplied by a report.

Each invocation gets a new isolated work directory and copies of the exact corpus
artifacts and all nine candidate packages. The owning command receives
`SUNDER_COMPATIBILITY_CONTEXT` (collector-written JSON with identities, input paths,
digests and execution context) and `SUNDER_COMPATIBILITY_REPORT` (a fresh report
path). Owning tests must consume these selected bytes; source cases compile the
retained source against the candidate feed, while binary cases run unchanged
retained package/Host inputs. These assertions belong to the reviewed test code.
For binary cases, `candidateHostInputs` separately selects reserved
`candidate-host-<rid>` and `candidate-app-<rid>` artifact records from the current candidate reference, with
that reference's exact source identity. These are copied and hashed with candidate
SDK inputs; historical corpus inputs continue naming their original Host. This
keeps historical recipes immutable while testing against a genuinely newer Host.
The collector verifies both original and working-copy hashes before/after execution,
requires a zero exit code and actual passing required tests, and only then emits a
closed [lane receipt](../eng/compatibility/sdk-corpus-lane.schema.json).

The receipt binds the measured OS/RID and workflow context, an execution ID, exact
reference/corpus, before/after corpus and candidate inventory hashes, recipe and
owning entry-point hashes, normalized command hash, report hash/size and normalized
XML-result hash. Commands are bounded to 1,800 seconds, logs/reports to 8 MiB, and
each copied input set to 2 GiB. Traversal, linked inputs/reports, missing executions,
relabelled/reused slots, altered bytes and unrelated passing tests fail.

The release gate consumes all seven independently transported lane receipts and
recomputes these bindings and required tests. It does not accept a candidate's own
choice of reports/selectors or merely require unique report hashes. After this
check, `prepare` retains those same lane bytes under `executions/` in the stage-pinned
archive. Direct CLI/library callers must supply an independently authenticated
`--execution-root`; an arbitrary local folder is not proof of CI execution.

Receipts/manifests are bounded to 1 MiB and depth 32, with duplicate/case-colliding
JSON members rejected. Reports are bounded to 8 MiB; the qualification profile
rejects DTD/entity/comment/CDATA markup. The uncompressed USTAR retention container
is bounded to 2 GiB and 8,192 regular files, without links, extensions, traversal,
duplicate/case-colliding members or unchecked extra files. These release transport
bounds are stricter than the general retained-reference artifact size limit.

`prepare` builds `sunder-sdk-compatibility.tar` only from verified inputs and replays
the gate on the completed container. The normal numeric GitHub draft transport
retains it with the SDK checksum/provenance/SBOM inventory. Publication and promotion
recheck it against the stage's pinned container hash, base commit and base manifest
hash, and against the actual packages being published. Equal failed-job retries
preserve the original qualification attempt; another run or changed bytes fail.

For a local read-only check using independently authenticated inputs:

```sh
node scripts/compatibility/check-supported-releases.mjs \
  --base-ref "$TRUSTED_HISTORY_COMMIT" \
  --release-version "$SDK_VERSION" --artifact-source-commit "$ARTIFACT_SOURCE_COMMIT" \
  --qualification-root "$TESTED_CANDIDATE/compatibility" \
  --candidate-root "$FLAT_SDK_PACKAGE_DIRECTORY" \
  --execution-root "$DOWNLOADED_SAME_RUN_LANE_ARTIFACTS" \
  --run-id "$SDK_RUN_ID" --run-attempt "$SDK_RUN_ATTEMPT"
```

#### First freeze and later publication confirmation

The first release starts from a genuine, independently retained **empty** base
manifest. Test the candidate packages/source/Host/App bytes and the prospective
baseline's unsupported-feature rejection controls, then retain that candidate's
reference in the qualification bundle. There is no earlier published V1 baseline
to claim. This establishes a tested seed for later compatibility comparisons
**before publication**, without editing the tagged ledger or creating a source-SHA
self-reference. It requires neither a public package version nor a published
GitHub release to qualify.

Only after actual publication can `sdk_release_compatibility.py confirm` verify the
same numeric non-draft release, remote assets, provenance and tag, and prepare
external evidence-only files. It appends the **exact** retained `reference` to the
ledger and sets the proposed ledger to `FROZEN` / `RELEASED`; it never edits the
artifact checkout. Review those files in the existing approved, direct untagged
`publicationEvidenceCommit` descendant of `artifactSourceCommit`. The descendant
validator allows this append only with
`eng/release/publication-evidence/sdk-compatibility.json` and verifies its numeric
asset identity against the published qualification container. Previously retained
references and the candidate reference cannot be rewritten during confirmation.
Catalog publication remains independently verified when included in that same
evidence commit. See [release handoffs](../scripts/release/RETAINED-HANDOFFS.md).

### Candidate snapshot workflow and formatter scope

For unshipped candidates, `SUNDER_UPDATE_PUBLIC_API_ROOT` and the optional
comma-separated `SUNDER_UPDATE_PUBLIC_API_ASSEMBLIES` keep the existing update
workflow, including temporary output directories. Unselected assemblies still
run their snapshot checks. `SUNDER_UPDATE_PUBLIC_API_FIDELITY_ROOT` updates the
Build fidelity snapshot. Update roots cannot target `eng/compatibility` history.
Update variables fail in CI and frozen mode; they cannot turn released checks
into an update-and-pass operation. After freeze, current snapshot changes require
review alongside the separate released-surface comparison; released files remain
untouched.

The C# formatter emits **`# sunder-public-api-v2`**, an assembly identity line and
declarations including protected constructors/methods/fields/nested types,
individual accessor visibility/modifiers, generic variance, abstract/static/final
and override shape, required members, operators, enum underlying types, array
rank and relevant Build/SDK attributes. Existing candidate `PublicApi/*.txt`
files need one coordinated regeneration after the in-flight SDK edits finish.
`ApiFidelity/*.txt` keeps its format. Never apply this regeneration to a released
or historical oracle. A later formatter version needs explicit retained-format
support rather than rewriting released snapshots.

This is a conservative declaration ratchet, not a complete C# or binary
compatibility theorem. Overload-resolution ambiguity, compiler-specific/custom
modifiers and annotations, layout/marshalling details, dependency/runtime changes,
serialization semantics and behavioral regressions still need consumer compilation
and execution. Likewise, matching TypeScript declaration bytes is not sufficient
to prove JavaScript behavior or all source consumers. The current synthetic
`PublicApiCompatibilityTests` compile an unchanged consumer against additive and
breaking contract variants, and execute unchanged consumer bytes against a
replacement contract. They uniquely exercise protected-member binary failure and
old-implementer source failure; they are not released-package acceptance evidence.

### Required compatibility corpus at freeze

The bounded C# Worker owners, selected-input layout and reviewed recipe templates
are documented in [SDK corpus owners](testing/sdk-compatibility-corpus.md). Their
activation/admission and source-build checks are narrower than the full behavioral
corpus below; retain that distinction when recording qualification.
The [expanded profiles](testing/sdk-compatibility-probes.md) additionally own
unchanged TS/checked-JS source builds, explicit .NET/Node RPC and cancellation
probes, and provided App/SDK assembly identity with retained App package activation.

| Direction | Retained inputs and required assertion | Existing owners to extend |
| --- | --- | --- |
| Old package / new Host | Original `.sunderpkg`, original SDK/runtime dependency closure, exact source/build identity and RID. Install, activate, invoke, stream, cancel and shut down unchanged bytes on each supported applicable RID; include in-process App assembly substitution and Worker isolation. Do not rebuild the old package during this check. | `Sunder.Runtime.Host.Tests` generated-template/cross-language/Worker suites; `Sunder.App.Tests.PackageViewHostServiceTests` |
| Old source / new SDK | Source archive, original locks/toolchain/options and original NuGet/npm packages. Compile unchanged C#, TS and checked-JS consumers against the new SDK/tooling: implement interfaces, derive extensible classes, use generic variance and properties/events, build generated bindings and package outputs, then execute representative behavior. | `Sunder.Sdk.PublicApi.Tests`, `Sunder.Package.Build.Tests`, template tests and Node SDK/package-tool tests |
| Newer feature / older Host | Original older Host/App/runtime binaries plus a current package that requires the newer capability. Prove precise rejection before loading/launching target code; a baseline-only control still succeeds. Also test current Host acceptance. | `RuntimePackageManifestValidatorTests`, `RuntimeArchitectureRatchetTests`, App load-context and Host process integration suites |

The existing admission tests cover compatible `1.1.x` metadata and unknown or
reserved capability rejection. Existing App tests cover older requested assembly
versions and identity/major mismatches. These are useful current-source safeguards,
not a retained older-Host binary corpus. Register genuine inputs, then require the
owning lanes (including applicable native RIDs) at the release checkpoint. Static
API comparison cannot substitute for their source, wire or behavioral results.

## Host-Owned Type Identity

Host-owned SDK/framework boundary assemblies are authoritative for in-process App targets. App substitutes only assemblies on explicit Host allowlists; package-authored assembly names never grant shared identity. Workers carry package-owned modules and dependencies while exact trusted execution runtimes provide language-runtime bytes; cross-package behavior uses schema-first RPC rather than shared CLR types. For an allowlisted Host assembly:

- simple name, culture, and public-key token must match exactly;
- substitution is allowed only within the requested assembly major version;
- the Host-provided version must be greater than or equal to the requested version.

The Avalonia resource-assembly registry is separate from contract assembly resolution and does not alter these rules.

## Capabilities

Current SDK capabilities are:

| Capability | SDK Surface |
| --- | --- |
| `sdk-baseline-1-1.v1` | clean-break 1.1 contract identity required on every package |
| `core.v1` | `ISunderAppPackageModule`, `IPackageContext`, and Worker context contracts |
| `packaging.v1` | canonical package id, strict SemVer, version-range primitives, and package metadata attributes |
| `contributions.v1` | `ISunderAppContributionRegistry` and immutable Worker contribution options |
| `views.v1` | package view registration and placement |
| `settings-views.v1` | settings view registration |
| `settings-navigation.v1` | package settings navigation service |
| `package-development-revisions.v1` | managed .NET App-only `RuntimeRevision` / `AppliedRevision` status members and validated full-source revision values |
| `worker-execution-observations.v1` | managed .NET App-only exact Worker execution capture, owner-authorized cleanup evidence and member-gated `SunderRpcException.HasTrustedNoDispatchEvidence` |
| `background-processes.v1` | queued background process API, progress reporting, cancellation, and indicator placement |
| `background-services.v1` | package-owned `ISunderWorkerLifecycleService` composition and monitored active work |
| `runtime-generations.v1` | staged Worker candidate preparation, exact generation commit, activation, completion, and shutdown |
| `settings.schema.v1` | host-rendered package settings schema contracts |
| `settings.v1` | validated writable package settings, stored independently from opaque state |
| `storage.v1` | package storage/file/key-value abstractions |
| `storage.key-migration.v1` | portable physical-key derivation and host-atomic storage-key migration |
| `role-local-workspace.v1` | activation-owned App/Runtime role-local workspace capability |
| `docker-environment.v1` | consent-gated propagation of the bounded Docker CLI connection environment to a Runtime worker |
| `secrets.v1` | package secret storage abstraction |
| `logging.v1` | package logging abstractions |
| `notifications.v1` | package notifications |
| `shell-view.v1` | shell view/hotbar/navigation services |
| `view-navigation-preparation.v1` | hidden package-view preparation and post-presentation acknowledgement |
| `rpc.v1` | canonical descriptor/ABI identities, RPC V1 bindings, discovery, invocation, subscription, and provider registration |
| `rpc.extended-deadline.v1` | target opt-in for Host-clamped caller deadlines beyond the default RPC deadline |
| `stacks.v1` | `Sunder.Sdk.Stacks` Stack import/export data contracts |
| `stacks.rpc.v1` | `Sunder.Sdk.Stacks` package-local provider/client adapters over the Stack RPC descriptor |
| `callbacks.v1` | generic callback sessions |
| `auth.v1` | auth status/disconnect integration |
| `theming.v1` | semantic Sunder theme keys |
| `worker-protocol.v1` | mandatory isolated Runtime worker lifecycle and canonical RPC V1 identities |

`Sunder.Package.Build` infers target requirements from type/member/property/event `SunderSdkCapability` metadata annotations in the actual resolved `Sunder.Sdk*` assemblies. It scans the target assembly and authored project-reference outputs, including compiler-generated async/iterator/lambda bodies, but does not classify arbitrary copy-local dependencies as package-authored code. It resolves constant assembly-qualified reflection declarations, detects Sunder resources in source/compiled Avalonia XAML, and closes capability dependencies such as `auth.v1` requiring `callbacks.v1`. Stack provider registration and client-only use of `StackContributorRpcClient` both infer `stacks.rpc.v1`. Inference is fail-closed: unreadable metadata, unresolved IL tokens, or unclassified dynamic SDK access produce diagnostics rather than an incomplete requirement set.

The two observation capabilities require both the Host and the managed App which
implements them. A Host advertising an SDK capability does not substitute for the
App implementation: its exact load-permit admission checks the authenticated App's
implemented observation capabilities before package code loads. Older App requests
omit this additive metadata and are refused for targets requiring the additions;
ordinary older targets retain their original request shape. Neither capability is
implemented by Worker V1 or Node/web targets. Reading only the original development
status members does not infer the member-gated revision capability. See
[App observations](package-development/APP-DEVELOPMENT-SESSIONS.md).

Manual MSBuild capability entries are required for unusual dynamic/reflection scenarios. Declare every capability that dynamically reached code can use and add the exact `SunderSdkDynamicAccess` call-site acknowledgment printed by the build. A capability declaration does not acknowledge unrelated unresolved sites:

```xml
<ItemGroup>
  <SunderSdkCapability Include="callbacks.v1" />
  <SunderSdkDynamicAccess Include="MyCompany.Package.DynamicFactory.CreateHandler" />
</ItemGroup>
```

## RPC Streams

RPC server-stream methods use descriptor-validated events and exact provider-generation leases. A terminal completion or error is mandatory; malformed, oversized, or out-of-schema events fail the subscription. Authentication, request deadlines, package-generation leases, and cancellation apply for the complete stream lifetime.

## Callback And Auth

`callbacks.v1` is the generic host-owned callback-session capability. It includes immutable Worker handler registration, bounded start parameters, App-side start/status/bounded-wait/cancel/launch access through `IPackageContext.Callbacks`, single callback completion, expiry, and activation/shutdown cancellation. Package-owned network listeners are outside V1.

`auth.v1` is only for auth-specific Host/App integration, including status and disconnect behavior. It always implies `callbacks.v1`; non-auth callback packages require only `callbacks.v1`.

## Schema-First RPC

This heading describes the wire/runtime validation boundary, not an instruction to
author schemas. Current package source uses ordinary C# service declarations or
Node `defineService` definitions with TS/checked JSDoc; tooling derives the same
descriptor V1 bytes and managed clients. See
[Implementation-First RPC Authoring](package-development/IMPLEMENTATION-FIRST-RPC.md)
and [Node services](SUNDER-NODE-PROCESS-PACKAGES.md). The capability ID
`schema-first-rpc.v1` and advanced descriptor/wire APIs keep their existing meaning.

Cross-package Runtime contracts are strict, versioned JSON descriptors bundled in package content and identified by canonical SHA-256. Manifests declare imported contracts, requested actions, and provided endpoints. Installing or updating consents to every declared action for that exact package version and manifest; undeclared actions remain default-deny. Runtime validates descriptor compatibility and payload schemas before dispatch.

Descriptor schema version and contract semantic version are separate identities. Descriptor V1 is the only supported schema, and the unversioned `SunderRpcProtocol.DescriptorVersion` constant selects it. Manifest V1 package authoring, parsing, generation, and validation all require its strict keyed shape. The value-schema profile remains the current `sunder.rpc.values.v1` contract and is a separate identity from the descriptor version.

Discovery and watch results contain Host-stamped package, provider, contract, and activation identities. An endpoint reference remains bound to that exact activation and never retargets a replacement provider.

Content-bearing callers create an `ISunderRpcCallScope`. Request content is bound to that scope and exact target endpoint; response content is bound to the originating caller scope. Disposing the scope cancels its active work and revokes unconsumed references.

Only the Host creates `SunderRpcInvocationContext`. Its content authority is valid only while the provider handler or subscription is active, regardless of retained CLR references. Provider code may return contract-level `Domain` errors; only Host-authenticated paths may originate infrastructure error kinds.

## RPC Invocation Leases

Invocations and subscriptions acquire provider-activation leases. Once retirement starts, the endpoint admits no new work, active calls receive retirement cancellation, and a same-id replacement cannot satisfy an old reference.

The Host removes a retiring endpoint from discovery before waiting for its leases. A call that exceeds the bounded cleanup deadline keeps the worker generation quarantined; the Host does not terminate provider-owned resources while leased work remains active.
