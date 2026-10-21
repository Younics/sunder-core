# Sunder Core Compatibility Candidate

## Current observation and protected history

The current generator writes only
`eng/compatibility/core-current-compatibility-candidate.json`, with schema 2,
candidate ID `sunder-core-current`, status **UNFROZEN** and release state
**UNRELEASED**. This is a current-source observation, not a release qualification
or SDK freeze. No current candidate is claimed until its exact clean source and
fresh build inputs are available.

The following archives are inert, byte-preserved history:

- [`core-compatibility-candidate.json`](../eng/compatibility/core-compatibility-candidate.json),
  the pre-implementation-first migration ledger;
- [`stack-contributor-rpc-migration-baseline.json`](../eng/compatibility/stack-contributor-rpc-migration-baseline.json),
  its Stack descriptor/ABI, packaging and declaration capture.

Neither archive supplies current runtime/build expectations, a historical-hash
join or a pending Phase 0 prerequisite. The archived Stack reconstruction test
is excluded from compilation. Current Stack domain behavior, source parity,
platform lifecycle, contract semantics and real artifact tests retain ownership.
Frozen wire conformance corpora and supported released SDK baselines keep their
separate genuine compatibility purposes.

## Collecting a new current candidate

After the source owner completes clean commits, select the exact reviewed Core
source commit. The generator rejects dirty/untracked source and a checkout that
differs from that commit, allowing only its new current candidate output to differ.
It never infers a pin from a dirty checkout and has no historical-output override.
Once dependencies are prepared, use:

```shell
node scripts/compatibility/generate-core-compatibility-candidate.mjs --write --source-commit <exact-clean-commit>
node scripts/compatibility/generate-core-compatibility-candidate.mjs --check --source-commit <exact-clean-commit>
```

Both modes build the selected Release CLI and TypeScript packages before collecting
CLI help and emitted declarations. They must not run while another owner holds the
build outputs. `--check` compares the new current file byte-for-byte; it does not
validate or rewrite a migration ledger. Offline source-selection fixtures run with
`node --test scripts/compatibility/current-candidate.test.mjs` and do not build.

The observation inventories public .NET/npm artifacts, API snapshots, MSBuild,
CLI/HTTP behavior, errors, configuration, persistence, telemetry, schemas, templates
and release/artifact identities. Source fingerprints describe the selected input;
they do not make an output its own independent expected baseline. SDK freeze,
native/cold/source-artifact qualification and independently retained supported
release evidence remain separate required gates.

## Retained Surfaces

- RPC descriptor V1 is the current and only canonical C# deployable schema. `SunderRpcProtocol.DescriptorVersion` selects V1, and Manifest V1 packages reject other descriptor versions.
- Descriptor schema version and semantic contract version are independent. A contract can retain semantic version `1.0.0` when its contract identity has not changed. The Stack contributor contract intentionally uses exact semantic version `2.0.0` with no version range.
- `SunderStackContributorRpc.CreateWorkerProviderRegistration` is removed; Worker packages now construct registrations at their own Worker boundary and `Sunder.Sdk.Stacks` no longer depends on `Sunder.Sdk.Worker`.
- App Runtime and Registry composite interfaces remain active composition boundaries.
- Archive V1, content-index V1, projection V1, Stack V1, `sunder.rpc.values.v1`, and `.v1` capability names identify current contracts; their suffix does not mark obsolete compatibility.
- Runtime package authoring uses one canonical unsuffixed Library project. Core
  generates isolated exact-RID Worker V1 and optional App roles. Current topology,
  dependency and artifact checks reject authored split roles and Protocol helpers.
- Current unmanaged packages may use the explicit Registry source-adoption operation. Core does not infer provenance or scan historical layouts.
- C# RPC generation preserves public declarations by default. `SunderRpcCSharpGenerator` accepts explicit `Public` or `Internal` accessibility, and packaged `Sunder.Package.Build` exposes the same exact values through `SunderRpcBinding` `Accessibility` metadata. Internal mode uniformly covers every top-level generated declaration; invalid values fail closed before output replacement.

## Completed Cutovers

- TypeScript package tooling and SDK consumers emit Manifest V1, Worker Protocol V1, RPC descriptor V1, and runtime-backed CommonJS modules without Node executable or SEA tooling bytes.
- .NET package builds emit framework-dependent managed modules without AppHost, runtime-pack, symbol, or generated XML documentation bytes.

The new observation carries no pending migration cutovers or historical allowed
deltas. This does not waive qualification for the first supported release.

## Ownership Boundaries

- Core defines neither `Registry:PublicOrigin` nor `Registry:Artifacts:PublicOrigin`; public and artifact origin policy belongs to the Registry repository.
- Explicit Registry endpoint operation metadata belongs to the Registry server. Core Registry clients freeze their called routes, verbs, trust boundary, and source implementations.
- Core has no CloudFront configuration or public surface.
- Agent expectations come from its independent current inventory and separately
  reviewed current descriptor records. Agent persistence and Registry database
  migrations remain owned by their repositories.

## Package Policy

The canonical candidate accepts only archive 1, content index 1, package manifest 1, projection 1, Worker Protocol V1, RPC descriptor V1, and Stack schema/index/reader 1. Node authoring project schema 2 emits only Manifest V1 runtime-backed modules. Direct tests reject old and future values, and Stack metadata uses duplicate-property-rejecting JSON parsing.

Runtime-package payloads exclude symbol files, apphosts, language-runtime/framework-pack bytes, and generated XML documentation companions; explicitly authored XML content remains eligible. `.sunderpkg` has no package-level signature contract; canonical content identity and immutable Registry provenance remain the integrity boundary.

## Persistence Policy

The generated ledger records every durable Core state root and document version plus its read-failure policy. Runtime and App V1 state roots reject incompatible or unmarked roots instead of inferring historical layouts. Installed packages, RPC permissions, package transactions, package state, encrypted secrets, storage migration records, Runtime resource policy, Host lifecycle, Host identity, and Host payload descriptors fail closed on incompatible data.

The local Stack index is schema 1. Malformed, old, and future indexes are preserved and reported as corruption rather than presented as an empty library. Notification and update-setting documents are intentionally unversioned disposable preferences: malformed files are quarantined and recreated. An incompatible shell layout resets layout fields while preserving theme and preferred Runtime URL.
