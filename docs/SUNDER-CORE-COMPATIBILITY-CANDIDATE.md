# Sunder Core Compatibility Candidate

This document records the pre-implementation-first Core surface used as a
migration oracle. Phase 0 was reopened on 2026-09-04, so this candidate is not a
final Phase 7 compatibility freeze and does not authorize tagging or publication.

The generated source of truth is [`eng/compatibility/core-compatibility-candidate.json`](../eng/compatibility/core-compatibility-candidate.json). It inventories public .NET and npm artifacts, API snapshots, MSBuild inputs and outputs, CLI behavior, Runtime and Host HTTP routes, Registry client calls, errors, configuration, telemetry, schemas, templates, release tags, and artifact identities. Source hashes freeze implementation-defined metadata where Minimal APIs do not declare complete OpenAPI response metadata.

[`eng/compatibility/stack-contributor-rpc-migration-baseline.json`](../eng/compatibility/stack-contributor-rpc-migration-baseline.json)
separately freezes the canonical descriptor and ABI bytes, hashes, services,
methods, and errors for the platform-owned `sunder.stack.contributor` contract.
It also inventories the current descriptor source, embedded and packed assets,
packed `buildTransitive` file, and every direct Stack-package RPC MSBuild
declaration, including empty binding/provider/use sets. Its scope names template,
sample, test, and generated-output exclusions and rejects unclassified Core .NET
RPC declarations. The recorded migration policy moves identity distribution to
the Registry-published Core platform catalog while retaining only reviewed Stack domain and
semantic-adapter behavior in `Sunder.Sdk.Stacks`. It is migration evidence only
and is never a package build input.

## Regeneration

The checked-in candidate is the historical migration oracle. Do not regenerate it
to hide implementation-first or Registry-authority changes during migration. The
2026-10-03 catalog decision supersedes its signed-catalog pending-cutover wording;
current public API snapshots, schemas and owning tests validate the new surface.
Phase 7 must review/freeze the final surface after canonical project retirement.

For an approved candidate refresh, restore .NET and npm dependencies, then use the
owning generators from the repository root:

```shell
dotnet restore Sunder.Core.slnx --locked-mode
npm ci --prefix languages/typescript/node
node scripts/compatibility/generate-core-compatibility-candidate.mjs --write
node scripts/compatibility/generate-core-compatibility-candidate.mjs --check
SUNDER_UPDATE_CORE_RPC_MIGRATION_BASELINE=1 dotnet test languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/Sunder.Sdk.PublicApi.Tests.csproj --no-restore --filter FullyQualifiedName~StackContributorRpcMigrationBaselineTests
dotnet test languages/csharp/dotnet/tests/Sunder.Sdk.PublicApi.Tests/Sunder.Sdk.PublicApi.Tests.csproj --no-restore --filter FullyQualifiedName~StackContributorRpcMigrationBaselineTests
```

The generator builds the current Release CLI and all TypeScript packages before collecting CLI help and emitted declaration files. `--check` regenerates the complete candidate in memory and fails when the checked-in bytes differ. Core CI runs this check with complete Git tag history so release evidence is deterministic.

## Retained Surfaces

- RPC descriptor V1 is the current and only canonical C# deployable schema. `SunderRpcProtocol.DescriptorVersion` selects V1, and Manifest V1 packages reject other descriptor versions.
- Descriptor schema version and semantic contract version are independent. A contract can retain semantic version `1.0.0` when its contract identity has not changed. The Stack contributor contract intentionally uses exact semantic version `2.0.0` with no version range.
- `SunderStackContributorRpc.CreateWorkerProviderRegistration` is removed; Worker packages now construct registrations at their own Worker boundary and `Sunder.Sdk.Stacks` no longer depends on `Sunder.Sdk.Worker`.
- App Runtime and Registry composite interfaces remain active composition boundaries.
- Archive V1, content-index V1, projection V1, Stack V1, `sunder.rpc.values.v1`, and `.v1` capability names identify current contracts; their suffix does not mark obsolete compatibility.
- Runtime package authoring remains Worker-only: one canonical unsuffixed Worker
  V1 project with a managed module entry point plus an optional role-isolated App
  project. Current package-local Protocol projects are migration inputs scheduled
  for deletion, not final authoring boundaries.
- Current unmanaged packages may use the explicit Registry source-adoption operation. Core does not infer provenance or scan historical layouts.
- C# RPC generation preserves public declarations by default. `SunderRpcCSharpGenerator` accepts explicit `Public` or `Internal` accessibility, and packaged `Sunder.Package.Build` exposes the same exact values through `SunderRpcBinding` `Accessibility` metadata. Internal mode uniformly covers every top-level generated declaration; invalid values fail closed before output replacement.

## Completed Cutovers

- TypeScript package tooling and SDK consumers emit Manifest V1, Worker Protocol V1, RPC descriptor V1, and runtime-backed CommonJS modules without Node executable or SEA tooling bytes.
- .NET package builds emit framework-dependent managed modules without AppHost, runtime-pack, symbol, or generated XML documentation bytes.

The generated candidate's `pendingCutovers` and `allowedFutureDeltas` ledgers
deliberately carry the same three-item implementation-first inventory while
Phase 0 is open. They are migration controls, not approved final compatibility
deltas. Phase 7 replaces them with the first supported V1 baseline after
source-derived authoring, project cleanup, and cross-language parity are
complete.

## Ownership Boundaries

- Core defines neither `Registry:PublicOrigin` nor `Registry:Artifacts:PublicOrigin`; public and artifact origin policy belongs to the Registry repository.
- Explicit Registry endpoint operation metadata belongs to the Registry server. Core Registry clients freeze their called routes, verbs, trust boundary, and source implementations.
- Core has no CloudFront configuration or public surface.
- Agent descriptor/provider/consumer counts, Agent persistence, and Registry database migrations are frozen in their owning repositories rather than duplicated into this ledger.

## Package Policy

The canonical candidate accepts only archive 1, content index 1, package manifest 1, projection 1, Worker Protocol V1, RPC descriptor V1, and Stack schema/index/reader 1. Node authoring project schema 2 emits only Manifest V1 runtime-backed modules. Direct tests reject old and future values, and Stack metadata uses duplicate-property-rejecting JSON parsing.

Runtime-package payloads exclude symbol files, apphosts, language-runtime/framework-pack bytes, and generated XML documentation companions; explicitly authored XML content remains eligible. `.sunderpkg` has no package-level signature contract; canonical content identity and immutable Registry provenance remain the integrity boundary.

## Persistence Policy

The generated ledger records every durable Core state root and document version plus its read-failure policy. Runtime and App V1 state roots reject incompatible or unmarked roots instead of inferring historical layouts. Installed packages, RPC permissions, package transactions, package state, encrypted secrets, storage migration records, Runtime resource policy, Host lifecycle, Host identity, and Host payload descriptors fail closed on incompatible data.

The local Stack index is schema 1. Malformed, old, and future indexes are preserved and reported as corruption rather than presented as an empty library. Notification and update-setting documents are intentionally unversioned disposable preferences: malformed files are quarantined and recreated. An incompatible shell layout resets layout fields while preserving theme and preferred Runtime URL.
