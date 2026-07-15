# Sunder SDK Compatibility

This document defines the Sunder V1 compatibility contract for independently installed Apps/Hosts and packages.

## Compatibility Boundary

The compatibility boundary is the generated package manifest, public `Sunder.Sdk*` contract assemblies, and Host support for those contracts. The Runtime Host must validate SDK compatibility before loading a package assembly because packages use Host-bundled SDK contract assemblies.

The App/CLI-to-Runtime HTTP protocol is a separate compatibility boundary. Authenticated clients first call the unversioned `/api/handshake` endpoint and require protocol identity `dev.sunder.runtime`, an overlapping supported revision range, a non-empty Runtime instance id, and required feature ids before using `/api/v1`. Product, file, and informational versions are diagnostic fields and are never interpreted as protocol SemVer. Unknown or malformed protocol data fails closed.

The unreleased 1.1 App and Runtime use clean-break protocol revision `3`. Dev sessions require `dev-package-owner-leases.v1`: every mutation and heartbeat is fenced to one `RuntimeInstanceId`, owner mutations replace the complete desired folder/watch set, and mutation id plus owner revision make retries idempotent. There is no revision-2 or global-watch compatibility path.

## Version Fields

- `manifestVersion`: package manifest/archive schema version. This is not the SDK API version.
- `sdkApiVersion`: broad SDK activation generation. Current value is `1`.
- `sdkPackageVersion`: required strict SemVer 2.0 `Sunder.Sdk` package/build version used by `Sunder.Package.Build`.
- `requiredSdkCapabilities`: granular Host-required SDK features inferred from SDK contract usage.
- `sdkVersion`: optional SDK version metadata when supplied by build properties. Compatibility decisions use `sdkApiVersion` and `requiredSdkCapabilities`.

V1 is a clean format boundary: `sdkApiVersion` must be exactly `1`, `sdkPackageVersion` must be valid SemVer 2.0, and `requiredSdkCapabilities` must contain distinct V1-form ids. The shipped 1.1 baseline additionally requires `sdkPackageVersion >=1.1.0 <1.2.0` and `sdk-baseline-1-1.v1`. Missing or mixed 1.0/1.1 compatibility metadata is rejected before assembly load.

## Compatibility Rules

- The immutable 1.1 API snapshots are the compatibility baseline. Future 1.1 patches may extend but must not break them.
- Current `Sunder.Sdk.*` contracts are SDK API `1`.
- An old Host must reject unsupported package SDK requirements before assembly load.

## Shared Contract Assemblies

Host-owned SDK/framework boundary assemblies are authoritative. Package-shared assemblies are eligible for session-wide sharing only when their simple name ends in `.Contracts`; their dependency closure may then be loaded into the same collectible shared context. App and Runtime use the same host-neutral identity policy:

- simple name, culture, and public-key token must match exactly;
- substitution is allowed only within the requested assembly major version;
- the loaded version must be greater than or equal to the requested version;
- two files claiming the same unsigned assembly identity must be byte-identical definitions;
- different public keys/cultures with the same simple name, cross-major substitution, and post-load identity changes are rejected.

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
| `background-processes.v1` | queued background process API, progress reporting, cancellation, and indicator placement |
| `extensions.v1` | extension points, contribution registration, extension catalog queries |
| `extensions.changes.v1` | extension catalog change monitoring |
| `settings.schema.v1` | host-rendered package settings schema contracts |
| `settings.v1` | validated writable package settings, stored independently from opaque state |
| `storage.v1` | package storage/file/key-value abstractions |
| `role-local-workspace.v1` | activation-owned App/Runtime role-local workspace capability |
| `secrets.v1` | package secret storage abstraction |
| `logging.v1` | package logging abstractions |
| `notifications.v1` | package notifications |
| `shell-view.v1` | shell view/hotbar/navigation services |
| `development-package-sessions.v1` | optional development session control, availability, and structured outcomes |
| `runtime-operations.v1` | package-scoped typed App-to-Runtime operations and streams |
| `stacks.v1` | `Sunder.Sdk.Stacks` Stack import/export data contracts |
| `stacks.contributions.v1` | `Sunder.Sdk.Stacks` Stack contributor extension contracts |
| `callbacks.v1` | generic callback sessions |
| `auth.v1` | auth status/disconnect integration |
| `theming.v1` | semantic Sunder theme keys |

`Sunder.Package.Build` infers required capabilities from type/member/property/event `SunderSdkCapability` metadata annotations in the actual resolved `Sunder.Sdk*` assemblies. It scans the entry assembly and authored project-reference outputs, including compiler-generated async/iterator/lambda bodies, but does not classify arbitrary copy-local dependencies as package-authored code. It resolves constant assembly-qualified reflection declarations, detects Sunder resources in source/compiled Avalonia XAML, and closes capability dependencies such as `auth.v1` requiring `callbacks.v1`. Inference is fail-closed: unreadable metadata, unresolved IL tokens, or unclassified dynamic SDK access produce diagnostics rather than an incomplete requirement set.

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

`callbacks.v1` is the generic host-owned callback-session capability. It includes Runtime handler registration, immutable bounded start parameters, App-side start/poll/launch access through `IPackageContext.Callbacks`, single callback completion, expiry, and activation/shutdown cancellation. Package-owned network listeners are outside V1.

`auth.v1` is only for auth-specific Host/App integration, including status and disconnect behavior. It always implies `callbacks.v1`; non-auth callback packages require only `callbacks.v1`.

## Extension Catalog Changes

Use `IPackageExtensionCatalogMonitor` for structured extension catalog changes. It exposes `Changed` with `PackageExtensionCatalogChangedEventArgs` including revision, reason, and per-extension-point additions/removals.

`IPackageExtensionCatalog.GetExtensionContributions` is mandatory. Hosts and test catalogs must supply the canonical non-empty id of the package that registered each contribution. The extension-point definition or contracts assembly does not own contributions from other packages; explicit ownership prevents Stack and other dependency-producing consumers from silently omitting package requirements.
