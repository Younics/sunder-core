# Sunder SDK Compatibility

This document defines the Sunder V1 compatibility contract for independently installed Apps/Hosts and packages.

## Compatibility Boundary

SDK `1.0.0` is the first supported SDK release. Each exact target records its resolved
project SDK version and the Host features it needs. App and Runtime validate these
requirements before loading an assembly or starting a process. SDK minor/patch
updates preserve supported older packages; supported SDK majors coexist until
explicit retirement. Developers update ordinary NuGet/npm references, with no
authored SDK-version list or version-switching code.

SDK implementations and dependency-injection containers belong to each package.
Managed App packages communicate with the shell through `Sunder.App.Bridge` V1:
BCL/Avalonia signatures, JSON operations, scoped child channels, view factories and
SDK-owned navigation callbacks. The bridge assembly is bundled in the
`Sunder.Sdk.Avalonia` developer package, supplied by App at runtime and omitted from
App package payloads. SDK and DI assemblies remain package-local. Shared themes
belong to the bridge; package resources resolve in their owning load context.

SDK, Worker/RPC/HTTP/browser protocols, App bridge, language runtime, package and
domain-contract versions are independent. A new SDK major does not by itself
require a new protocol, nor does a protocol revision reset package versions.

The App/CLI-to-Runtime HTTP protocol is a separate compatibility boundary. Authenticated clients first call the unversioned `/api/handshake` endpoint and require protocol identity `dev.sunder.runtime`, a valid supported range containing the client's implemented revision, a non-empty Runtime instance id, and required feature ids before using `/api/v1`. A server advertising revisions 1–2 is accepted by a V1 client, which continues using V1 routes and payloads. The server's preferred revision must itself lie inside its advertised range. Supervisor `/api/host/handshake` follows the same rule for `/api/host/v1` clients. Product, file, and informational versions are diagnostic fields and are never interpreted as protocol SemVer. Unknown or malformed protocol data fails closed.

The current App and Runtime use protocol revision `1`. RPC V1 clients require the Runtime feature `schema-first-rpc.v1`; package web Apps additionally require `app-web-rpc.v1` and use only the injected `sunder.bridge.v1` browser protocol. Dev sessions require `dev-package-owner-leases.v1`: every mutation and heartbeat is fenced to one `RuntimeInstanceId`, owner mutations replace the complete desired folder/watch set, and mutation id plus owner revision make retries idempotent.

## Target Compatibility Fields

- `archiveFormatVersion` and `manifestVersion`: package archive and manifest schema versions.
- `sdkVersion`: required exact strict SemVer identifying the project-resolved SDK; its major is checked against implemented support.
- `requiredHostCapabilities`: granular Host requirements for one exact target, inferred from authored SDK usage or declared by non-.NET tooling.
- Managed App targets also declare `targetFramework`, the resolved `avaloniaVersion`, and `app-bridge.v1`. App implements explicit target frameworks and supports an Avalonia requirement only within the same major and at least the required version.
- Web App targets require `web-bridge.v1`; Worker targets require `worker-protocol.v1`. These capability names identify independent implementation boundaries.

Every Manifest V1 Runtime target requires `worker-protocol.v1` and selects exact wire identity `sunder.worker.v1` before process launch. Missing V1 capability, Worker V3, `dotnet`, and `process` fail compatibility validation; there is no probing, downgrade, or handshake fallback.

Installed package catalogs accept only current schema 5 records with complete provenance, capability grants, content identity, and inventory. Older catalogs and package layouts are rejected without migration or rewrite. `Unknown` remains valid explicit provenance for current unmanaged packages, and those packages can adopt a Registry source through the normal source-adoption operation.

SDK-backed targets contain distinct versioned capability ids. SDK-baseline tokens
are removed. Every Runtime target additionally requires `rpc.v1`,
`execution-runtime.v1` and `worker-protocol.v1`. Missing, malformed, mismatched or
unsupported metadata is rejected before target code runs.

Developer packages own their release versions and dependency ranges. Current Node
templates select `>=1.0.0 <2.0.0`; generation resolves the project's SDK rather than
stamping the tooling process's SDK. Platform catalog releases are also independent:
new selections default to stable `>=1.0.0`, explicit ranges are supported, restore
preserves exact pins and update preserves the reviewed range unless overridden.

The four `Sunder.Sdk*` developer NuGets include matching XML API documentation beside
their consumer DLLs for IntelliSense. Release verification requires the expected
documentation paths and matching assembly identity. Build-task/tooling assets have
no blanket documentation exception. Runtime package archives continue excluding
generated XML companions and symbol files after publish-file computation while
retaining explicitly authored XML content.

## Registry Resolution Profile

Installable Registry resolution is filtered against the requesting Host.
`RegistryClientCompatibilityProfile` carries the exact current `rid` and 1–8
complete `profiles`. Each profile describes one `role`/`kind`, `manifestVersions`,
positive `sdkMajors`, `hostCapabilities`, `targetFrameworks` and optional
`avaloniaVersion`. Managed App profiles require shared-framework metadata. Each
selected target must match one whole profile; capabilities from different profiles
are never combined. Duplicate profiles are rejected. Profiles and their collections
are normalized before fingerprinting, so input order does not affect plan identity.

Current profiles implement Runtime `worker`, App `avalonia` and App `web`, each with
supported SDK majors `[1]` and its own capabilities. Local admission and Registry
declarations derive from the same Host-owned profile list. A future implementation
can add another supported major while retaining existing implementations.

`RegistryResolveUpdatesRequest`, `RegistryResolveInstallPlanRequest`, and `RegistryResolvePackageChangesRequest` require the envelope in their `compatibility` property. Package exact/tag GET resolution repeats `profiles`, with each value a URL-encoded JSON `RegistrySupportedPackageProfile`, and sends `rid` once. Each JSON query value is bounded to 16,384 characters and uses the same closed JSON reader as POST requests. `version` and `tag` remain mutually exclusive selection parameters. The desired target RIDs must agree with the envelope RID. The former flat, singular pre-release wire shape is not accepted as an alternative.

Missing, malformed, empty, oversized, internally inconsistent, or unsupported compatibility data fails resolution. Candidate filtering applies to exact versions, explicit tags, `latest` fallback, updates, transitive dependencies, install plans, and installed-version reuse. Public search, package details, version history, and immutable version details remain historical inspection APIs and do not require a compatibility profile.

Compatibility is request data, not an ambient or client-selected HTTP header. App and CLI install/update operations delegate to Runtime; Runtime authors the profile from its own implementation before calling Registry. This prevents an App, CLI, proxy, or stale persisted option from claiming capabilities on the Host's behalf.

## Imported API compatibility

A provider ships one current descriptor. A consumer retains its original complete
import inventory, descriptor bytes/hashes and restore provenance; those are not
rewritten to resemble a newer provider. Its reviewed provider-package version
ranges still control installation and selection.

Current tooling separately emits `usesContracts[].consumerRequirements` for
imported package APIs. It records the methods the consumer references and selectors
over its original schemas. Registry resolution, local graph checks, discovery,
required-provider readiness and call admission check those requirements against
the provider's current descriptor. Unrelated contracts/methods need not survive;
the selected methods, accepted requests, declared errors and known output members
must remain compatible. An unchanged consumer archive needs no lock refresh or
rebuild just because a provider adds an unrelated API or advances its contract major.
Missing requirement metadata retains exact matching, including an older role in
a mixed-role package. Implementing an imported provider contract remains exact.

Typed calls and method captures are collected from authored code, not generated
bodies. A full response model requires **all declared members**, including optional
members' definitions; tooling does not infer a smaller model from property reads.
Raw/reflection/dynamic calls require explicit method declarations. Their output
definitions are exact by default; an explicit raw consumer view can select a
smaller set of ordinary record members. A view cannot narrow a model also used by
typed calls or a service-reference protocol value. See the C# and Node SDK guides
for `SunderRpcDynamicUse` / `SunderRpcConsumerView` and `declareRpcUse` /
`declareRpcView`.

The broker validates each request against both original caller and current provider
schemas. It validates provider results, stream events and domain-error details in
full **before** reading the caller's output shape. Only unknown ordinary record
members are dropped; known members, maps, unions and protocol/resource values remain
validated. Referenced services contribute their target requirements too. Grants
remain keyed to the caller's original contract/manifest identity; permissions,
package ranges, exact provider activations, cancellation/deadlines and reference
ownership are unchanged. Compatibility is not an authorization grant.

Tooling stamps `rpc.consumer-requirements.v1` on consuming targets. Older Hosts
without that capability reject those targets before activation. This is an additive
feature in SDK `1.0.0`, independent of Worker or App framework versions.
Supporting another major requires a complete implementation; explicitly
removing an old profile/revision stops admitting it rather than silently upgrading
its packages. Retained release evidence is never deleted by that runtime choice.

## Compatibility Rules

- **V1 is a compatibility designation, not a requirement to number every artifact
  `1.0.0`.** Developer packages own independent versions. App/Host releases and
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
The supported-release ledger does not claim that SDK `1.0.0` or any retained
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

Core CI uses the PR base or pre-push commit; manual/scheduled invocations use
the protected main reference (its parent when it is the checked-out commit).
Normal CI needs no Registry or publication credentials. Once real releases are
registered, CI must provision their retained bundle before this step; absent
artifacts are failures, not optional skipped compatibility checks. The gate
verifies identities and API comparison, not publication authenticity or execution
of the corpus recipes. The release workflow establishes provenance; owning test
lanes must execute the retained corpus and record actual results.

### Publication and retained compatibility evidence

NuGet/npm publication is selected from each project's authored version and actual
published history. It does not require a manually configured compatibility-base
commit, Registry origin, qualification container, approval reference or a later
publication-evidence commit. An empty compatibility ledger is not a publication
blocker. The single protected-main workflow retains the exact tested package bytes
and source provenance; see [Core releases](SUNDER-CORE-RELEASES.md).

This does not turn source tests into retained-consumer compatibility evidence.
Published GitHub/NuGet/npm history answers whether a version was published;
`supported-releases.json` separately describes the genuine retained surfaces and
corpora registered for comparison. Never invent a historical source, hash, recipe,
publication record or passing result to populate that ledger. Existing registered
history remains immutable and append-only.

The standalone corpus tooling and reviewed recipe templates remain available for
explicit compatibility work. They are not called by the normal package release
workflow and their historical cohort envelopes do not force independent projects
to share versions. Owning corpus tests must consume the selected artifacts and
record actual source/process/native execution; a mocked collector receipt or an
unchanged declaration file does not prove behavioral compatibility.

Bounded parsing utilities in `scripts/compatibility/sdk_corpus.py` reject ambiguous
XML outcomes, hidden/zero results, unsafe tar members, links, case collisions,
truncation and oversized inputs. The corpus collector separately checks real
OS/architecture, reviewed commands, required selectors and before/after bytes.
Its evidence describes only the explicit corpus/run it exercised, not an automatic
production release approval.

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

The existing admission tests cover compatible SDK `1.x` metadata and unknown or
reserved capability rejection. Existing App tests cover older requested assembly
versions and identity/major mismatches. These are useful current-source safeguards,
not a retained older-Host binary corpus. Register genuine inputs, then require the
owning lanes (including applicable native RIDs) at the release checkpoint. Static
API comparison cannot substitute for their source, wire or behavioral results.

## Host-Owned Type Identity

The Host owns App bridge and Avalonia framework type identity. SDK, DI and logging
assemblies remain package-local. App substitutes only assemblies on explicit Host
allowlists; package-authored assembly names never grant shared identity. Workers
carry package-owned modules and dependencies while exact trusted execution runtimes
provide language-runtime bytes; cross-package behavior uses schema-first RPC rather
than shared CLR types. For an allowlisted Host assembly:

- simple name, culture, and public-key token must match exactly;
- substitution is allowed only within the requested assembly major version;
- the Host-provided version must be greater than or equal to the requested version.

The Avalonia resource-assembly registry is separate from contract assembly resolution and does not alter these rules.

## Capabilities

Current SDK capabilities are:

| Capability | SDK Surface |
| --- | --- |
| `app-bridge.v1` | managed App bridge activation and scoped host channels |
| `web-bridge.v1` | browser App bridge |
| `core.v1` | `ISunderAppPackageModule`, `IPackageContext`, and Worker context contracts |
| `packaging.v1` | canonical package id, strict SemVer, version-range primitives, and package metadata attributes |
| `contributions.v1` | `ISunderAppContributionRegistry` and immutable Worker contribution options |
| `views.v1` | package view registration and placement |
| `settings-views.v1` | settings view registration |
| `settings-navigation.v1` | package settings navigation service |
| `package-development-revisions.v1` | managed .NET App-only `RuntimeRevision` / `AppliedRevision` status members and validated full-source revision values |
| `package-development-reload.v1` | managed .NET App-only exact-owner, source-pinned finite development reload and recovery |
| `package-development-logs.v1` | managed .NET App-only bounded package-scoped Runtime/App log reads |
| `package-development-log-queries.v1` | managed .NET App-only filtered, byte-bounded paging and activation-retained diagnostics after target unload; requires explicit App implementation proof, not the legacy logs capability |
| `package-development-views.v1` | managed .NET App-only bounded inspection and opaque-element input for an exact owned development view |
| `app-activation-services.v1` | publication-triggered, activation-owned managed App service roots without page navigation |
| `worker-execution-observations.v1` | managed .NET App-only exact Worker execution capture, owner-authorized cleanup evidence and member-gated `SunderRpcException.HasTrustedNoDispatchEvidence` |
| `background-processes.v1` | queued background task API, progress reporting, cancellation, and indicator placement; existing capability identity retained |
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

The observation and App-service capabilities above require both the Host and the managed App which
implements them. A Host advertising an SDK capability does not substitute for the
App implementation: its exact load-permit admission checks the authenticated App's
implemented observation capabilities before package code loads. Older App requests
omit this additive metadata and are refused for targets requiring the additions;
ordinary older targets retain their original request shape. These capabilities are not
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
