# Sunder SDK Compatibility

This document defines the Sunder V1 compatibility contract for independently installed Apps/Hosts and packages.

## Compatibility Boundary

The compatibility boundary is each exact target's generated manifest metadata, the public `Sunder.Sdk*` assemblies, and Host support for those contracts. The Host validates target compatibility before loading an assembly or starting a process.

The App/CLI-to-Runtime HTTP protocol is a separate compatibility boundary. Authenticated clients first call the unversioned `/api/handshake` endpoint and require protocol identity `dev.sunder.runtime`, an overlapping supported revision range, a non-empty Runtime instance id, and required feature ids before using `/api/v1`. Product, file, and informational versions are diagnostic fields and are never interpreted as protocol SemVer. Unknown or malformed protocol data fails closed.

The V1 App and Runtime use protocol revision `3`. Dev sessions require `dev-package-owner-leases.v1`: every mutation and heartbeat is fenced to one `RuntimeInstanceId`, owner mutations replace the complete desired folder/watch set, and mutation id plus owner revision make retries idempotent.

## Target Compatibility Fields

- `archiveFormatVersion` and `manifestVersion`: package archive and manifest schema versions.
- `sdkVersion`: optional strict SemVer identifying the SDK used to build one SDK-backed target.
- `requiredHostCapabilities`: granular Host requirements for one exact target, inferred from authored SDK usage or declared by non-.NET tooling.

V1 targets contain distinct V1-form capability ids. SDK-backed targets built on the coordinated 1.1 line require `sdkVersion >=1.1.0 <1.2.0` and `sdk-baseline-1-1.v1`. Missing, malformed, or unsupported target metadata is rejected before target code runs.

Published Sunder-to-Sunder NuGet dependencies use `[1.1.0,1.2.0)`. Coordinated npm package dependencies, generated Node templates, and runtime package dependencies use `>=1.1.0 <1.2.0`. Because `1.1.0-beta.*` sorts before the stable lower bound, the initial 1.1 baseline does not publish prereleases. Prereleases after a stable 1.1 baseline may use the same minor range.

## Compatibility Rules

- The current public API snapshots are the unreleased V1 source baseline.
- Compatible 1.1 patches may extend public SDK contracts but must not break the snapshots.
- An old Host must reject unsupported package SDK requirements before assembly load.
- Runtime and Registry projection records are structurally ratcheted. Silent projection drift is not permitted.

## Host-Owned Type Identity

Host-owned SDK/framework boundary assemblies are authoritative. App and Runtime substitute only assemblies on explicit Host allowlists; package-authored assembly names never grant shared identity. Cross-package behavior uses schema-first RPC rather than shared CLR types. For an allowlisted Host assembly:

- simple name, culture, and public-key token must match exactly;
- substitution is allowed only within the requested assembly major version;
- the Host-provided version must be greater than or equal to the requested version.

The Avalonia resource-assembly registry is separate from contract assembly resolution and does not alter these rules.

## Capabilities

Current SDK capabilities are:

| Capability | SDK Surface |
| --- | --- |
| `sdk-baseline-1-1.v1` | clean-break 1.1 contract identity required on every package |
| `core.v1` | `ISunderRuntimePackageModule`, `ISunderAppPackageModule`, `IPackageContext` |
| `packaging.v1` | canonical package id, strict SemVer, version-range primitives, and package metadata attributes |
| `contributions.v1` | `ISunderRuntimeContributionRegistry`, `ISunderAppContributionRegistry` |
| `views.v1` | package view registration and placement |
| `settings-views.v1` | settings view registration |
| `settings-navigation.v1` | package settings navigation service |
| `background-services.v1` | package background services |
| `runtime-generations.v1` | post-publication Runtime generation participants |
| `background-processes.v1` | queued background process API, progress reporting, cancellation, and indicator placement |
| `settings.schema.v1` | host-rendered package settings schema contracts |
| `settings.v1` | validated writable package settings, stored independently from opaque state |
| `storage.v1` | package storage/file/key-value abstractions |
| `storage.key-migration.v1` | portable physical-key derivation and host-atomic storage-key migration |
| `role-local-workspace.v1` | activation-owned App/Runtime role-local workspace capability |
| `secrets.v1` | package secret storage abstraction |
| `logging.v1` | package logging abstractions |
| `notifications.v1` | package notifications |
| `shell-view.v1` | shell view/hotbar/navigation services |
| `view-navigation-preparation.v1` | hidden package-view preparation and post-presentation acknowledgement |
| `runtime-operations.v1` | package-scoped typed App-to-Runtime operations and streams |
| `runtime-invocation-errors.v1` | sanitized package-visible Runtime invocation failure metadata |
| `rpc.v1` | schema-first cross-package RPC descriptors, discovery, invocation, subscription, and provider registration |
| `stacks.v1` | `Sunder.Sdk.Stacks` Stack import/export data contracts |
| `stacks.rpc.v1` | `Sunder.Sdk.Stacks` package-local provider/client adapters over the Stack RPC descriptor |
| `callbacks.v1` | generic callback sessions |
| `auth.v1` | auth status/disconnect integration |
| `theming.v1` | semantic Sunder theme keys |

`Sunder.Package.Build` infers target requirements from type/member/property/event `SunderSdkCapability` metadata annotations in the actual resolved `Sunder.Sdk*` assemblies. It scans the target assembly and authored project-reference outputs, including compiler-generated async/iterator/lambda bodies, but does not classify arbitrary copy-local dependencies as package-authored code. It resolves constant assembly-qualified reflection declarations, detects Sunder resources in source/compiled Avalonia XAML, and closes capability dependencies such as `auth.v1` requiring `callbacks.v1`. Stack provider registration and client-only use of `StackContributorRpcClient` both infer `stacks.rpc.v1`. Inference is fail-closed: unreadable metadata, unresolved IL tokens, or unclassified dynamic SDK access produce diagnostics rather than an incomplete requirement set.

Manual MSBuild capability entries are required for unusual dynamic/reflection scenarios. Declare every capability that dynamically reached code can use and add the exact `SunderSdkDynamicAccess` call-site acknowledgment printed by the build. A capability declaration does not acknowledge unrelated unresolved sites:

```xml
<ItemGroup>
  <SunderSdkCapability Include="callbacks.v1" />
  <SunderSdkDynamicAccess Include="MyCompany.Package.DynamicFactory.CreateHandler" />
</ItemGroup>
```

## Runtime Streams

Typed Runtime streams are bounded newline-framed JSON. Each frame is exactly one `event`, `completed`, or `error` envelope. A terminal frame is mandatory; EOF, an oversized frame, malformed JSON, or a trailing partial frame fails the subscription. Authentication, negotiated protocol revision/features, request deadlines, package-generation leases, and cancellation apply for the complete stream lifetime.

## Callback And Auth

`callbacks.v1` is the generic host-owned callback-session capability. It includes Runtime handler registration, immutable bounded start parameters, App-side start/status/bounded-wait/cancel/launch access through `IPackageContext.Callbacks`, single callback completion, expiry, and activation/shutdown cancellation. Package-owned network listeners are outside V1.

`auth.v1` is only for auth-specific Host/App integration, including status and disconnect behavior. It always implies `callbacks.v1`; non-auth callback packages require only `callbacks.v1`.

## Schema-First RPC

Cross-package Runtime contracts are strict, versioned JSON descriptors bundled in package content and identified by canonical SHA-256. Manifests declare imported contracts, requested actions, and provided endpoints. Installing or updating consents to every declared action for that exact package version and manifest; undeclared actions remain default-deny. Runtime validates descriptor compatibility and payload schemas before dispatch.

Discovery and watch results contain Host-stamped package, provider, contract, and activation identities. An endpoint reference remains bound to that exact activation and never retargets a replacement provider.

Content-bearing callers create an `ISunderRpcCallScope`. Request content is bound to that scope and exact target endpoint; response content is bound to the originating caller scope. Disposing the scope cancels its active work and revokes unconsumed references.

Only the Host creates `SunderRpcInvocationContext`. Its content authority is valid only while the provider handler or subscription is active, regardless of retained CLR references. Provider code may return contract-level `Domain` errors; only Host-authenticated paths may originate infrastructure error kinds.

## RPC Invocation Leases

Invocations and subscriptions acquire provider-activation leases. Once retirement starts, the endpoint admits no new work, active calls receive retirement cancellation, and a same-id replacement cannot satisfy an old reference.

The Host removes a retiring endpoint from discovery before waiting for its leases. A call that exceeds the bounded cleanup deadline keeps the provider and load context quarantined; the Host does not dispose provider-owned resources while leased work remains active.
