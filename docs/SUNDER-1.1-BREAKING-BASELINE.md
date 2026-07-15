# Sunder 1.1 Breaking Baseline

`1.1.0` intentionally replaces the unused public `1.0.0` developer package line. It is not binary compatible with 1.0. The unreleased configuration-shaped SDK adapters have been removed; package code targets settings terminology only.

## Breaking Inventory

- Package identity/version/range primitives moved to `Sunder.Sdk.Packaging`; duplicate Format implementations were removed.
- Package configuration and session abstractions were replaced by settings, role-local workspace, optional development session control, callbacks, and typed Runtime operations.
- Canonical settings schemas no longer repeat package id or display name, reject duplicate identifiers and secret defaults during construction, and register through `RegisterSettingsSchema`.
- Stack export/import capabilities use separate `IPackageStackExporter` and `IPackageStackImporter` extension points. The combined contributor and filesystem path payload records were removed; payloads use bounded stream handles.
- Stack action/input/remap keys are opaque Host-scoped values carrying owner package, contributor, and local identity. Package importers continue to receive only their local ids.
- The legacy `workspaces.v1`, `installed-package-sessions.v1`, and `configuration.schema.v1` capabilities were removed. Settings schemas use `settings.schema.v1`.
- App-to-Runtime calls now require the authenticated `/api/handshake` protocol negotiation before `/api/v1` use.
- The unreleased Runtime protocol is revision 3 with no revision-2 compatibility shim. Runtime is persistent, always boots installed packages, and accepts App-invocation-owned dev folder/watch sets through fenced, idempotent, memory-only TTL leases.
- Runtime package operations and streams are lease-bound to a package generation. Streams use bounded newline-framed `event`, `completed`, and `error` envelopes; EOF or a partial frame before a terminal envelope is a protocol failure.
- Registry DTOs, Runtime DTOs, `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, and `Sunder.Sdk.Stacks` have new immutable API snapshots under `tests/Sunder.Sdk.PublicApi.Tests/PublicApi`.
- Public developer packages (`Sunder.Sdk*`, `Sunder.Package.Build`, and `Sunder.Package.Templates`) are coordinated at `1.1.0`.
- Generated V1 manifests now require exact `hostRoles` inferred from compiled metadata (`app`, `runtime`, both, or `contract-only`). The unreleased format has no missing-role compatibility default; Format validation checks the entry assembly without reflection loading.
- Runtime and App use separate persistent V1 startup caches. Runtime stores deterministic immutable UI snapshot objects and generation aliases; App stores validated content-addressed extracted content and always activates from generation-owned copies.

## Mixing Rule

Every 1.1-built manifest includes `sdk-baseline-1-1.v1`. A 1.1 Host accepts `sdkPackageVersion >=1.1.0 <1.2.0` and requires that capability before assembly load. Consequently, a 1.0 Host rejects 1.1 packages as an unknown requirement, while a 1.1 Host rejects 1.0 packages with a rebuild diagnostic. A mixed family must never reach assembly loading or fail as `TypeLoadException`.

Future compatible 1.1 patches update the shipped snapshots additively. Any further intentional contract break requires a new coordinated baseline, capability, package range, and breaking inventory; do not re-add 1.0 shims.
