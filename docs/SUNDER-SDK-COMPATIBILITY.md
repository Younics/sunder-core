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

- The current public API snapshots are the unreleased V1 source baseline.
- Compatible 1.1 patches may extend public SDK contracts but must not break the snapshots.
- An old Host must reject unsupported package SDK requirements before assembly load.
- Runtime and Registry projection records are structurally ratcheted. Silent projection drift is not permitted.

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
