# Sunder Core Compatibility Candidate

This document records the intentional Core surface first captured at Phase 2 and declared immutable for the coordinated Sunder 1.0 compatibility freeze at Phase 7 exit. The freeze does not authorize tagging or publication.

The generated source of truth is [`eng/compatibility/core-compatibility-candidate.json`](../eng/compatibility/core-compatibility-candidate.json). It inventories public .NET and npm artifacts, API snapshots, MSBuild inputs and outputs, CLI behavior, Runtime and Host HTTP routes, Registry client calls, errors, configuration, telemetry, schemas, templates, release tags, and artifact identities. Source hashes freeze implementation-defined metadata where Minimal APIs do not declare complete OpenAPI response metadata.

## Regeneration

Restore .NET and npm dependencies, then regenerate or verify the candidate from the repository root:

```shell
dotnet restore Sunder.Core.slnx --locked-mode
npm ci --prefix languages/typescript/node
node scripts/compatibility/generate-core-compatibility-candidate.mjs --write
node scripts/compatibility/generate-core-compatibility-candidate.mjs --check
```

The generator builds the current Release CLI and all TypeScript packages before collecting CLI help and emitted declaration files. `--check` regenerates the complete candidate in memory and fails when the checked-in bytes differ. Core CI runs this check with complete Git tag history so release evidence is deterministic.

## Retained Surfaces

- RPC descriptor V2 is the current deployable schema. `SunderRpcProtocol.DescriptorVersion` selects V2.
- RPC descriptor V1 remains an intentional standalone parsing and generation utility. Manifest V3 packages require descriptor V2 and reject descriptor V1.
- Descriptor schema version and semantic contract version are independent. Current RPC descriptors use schema V2; a contract can retain semantic version `1.0.0` when its contract identity has not changed. The Stack contributor contract intentionally uses exact semantic version `2.0.0` with no version range.
- `SunderStackContributorRpc.CreateWorkerProviderRegistration` is removed; Worker packages now construct registrations at their own Worker boundary and `Sunder.Sdk.Stacks` no longer depends on `Sunder.Sdk.Worker`.
- App Runtime and Registry composite interfaces remain active composition boundaries.
- Archive V1, content-index V1, projection V1, Stack V1, `sunder.rpc.values.v1`, and `.v1` capability names identify current contracts; their suffix does not mark obsolete compatibility.
- Runtime package authoring remains Worker-only: one canonical unsuffixed self-contained Worker V3 executable, with an optional role-isolated App project and package-local Protocol project.
- Current unmanaged packages may use the explicit Registry source-adoption operation. Core does not infer provenance or scan historical layouts.
- C# RPC generation preserves public declarations by default. `SunderRpcCSharpGenerator` accepts explicit `Public` or `Internal` accessibility, and packaged `Sunder.Package.Build` exposes the same exact values through `SunderRpcBinding` `Accessibility` metadata. Internal mode uniformly covers every top-level generated declaration; invalid values fail closed before output replacement. TypeScript generation is unchanged.

The generated `allowedFutureDeltas` ledger retains exactly two approved and implemented cleanup deltas: typed internal Runtime Stack failure classification and removal of the unshipped Stack Worker-registration convenience. Any further Core API, schema, artifact, hash, or behavior delta requires a new explicit compatibility decision and invalidates this freeze.

## Ownership Boundaries

- Core defines neither `Registry:PublicOrigin` nor `Registry:Artifacts:PublicOrigin`; public and artifact origin policy belongs to the Registry repository.
- Explicit Registry endpoint operation metadata belongs to the Registry server. Core Registry clients freeze their called routes, verbs, trust boundary, and source implementations.
- Core has no CloudFront configuration or public surface.
- Agent descriptor/provider/consumer counts, Agent persistence, and Registry database migrations are frozen in their owning repositories rather than duplicated into this ledger.

## Package Policy

The candidate accepts only archive 1, content index 1, package manifest 3, projection 1, Stack schema/index/reader 1, and Node project schema 2. Direct tests reject old and future values, and Stack metadata uses duplicate-property-rejecting JSON parsing.

Authored managed runtime-package payloads include their PDB files. Host-owned framework PDBs are excluded. Generated XML documentation companions are excluded from both the six public developer NuGets and runtime package payloads; explicitly authored XML content remains eligible. `.sunderpkg` has no package-level signature contract; canonical content identity and immutable Registry provenance remain the integrity boundary.

## Persistence Policy

The generated ledger records every durable Core state root and document version plus its read-failure policy. Runtime and App V1 state roots reject incompatible or unmarked roots instead of inferring historical layouts. Installed packages, RPC permissions, package transactions, package state, encrypted secrets, storage migration records, Runtime resource policy, Host lifecycle, Host identity, and Host payload descriptors fail closed on incompatible data.

The local Stack index is schema 1. Malformed, old, and future indexes are preserved and reported as corruption rather than presented as an empty library. Notification and update-setting documents are intentionally unversioned disposable preferences: malformed files are quarantined and recreated. An incompatible shell layout resets layout fields while preserving theme and preferred Runtime URL.
