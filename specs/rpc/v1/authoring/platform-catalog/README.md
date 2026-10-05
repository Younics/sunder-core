# Registry-Authoritative Platform Catalogs

The user explicitly selects and trusts a Registry. That Registry establishes
publication authority for packages and platform APIs. The official V1 origin is
`https://hub.sunderapp.io/`; another configured origin uses the same protocol.
Platform catalogs require no publisher signature, public-key bundle, trust profile,
minimum trust generation, or package-author signing configuration.

Unpublished local authoring instead selects an explicit canonical Core-exported
catalog without a Registry. The lock omits `registryOrigin` and retains all exact
release/content/descriptor pins. Present null or malformed origins remain invalid.

## Ownership And Implementation

Paths below are relative to the Core repository.

| Owner | Responsibility |
| --- | --- |
| `languages/csharp/dotnet/packages/Sunder.Sdk.Stacks` | Implemented Stack adapter and value source; source-derived SDK-neutral transport. |
| `eng/platform-contracts/sunder-contracts.owned.json` | Reviewed descriptor/ABI history and explicit acceptance/release state. |
| `languages/csharp/dotnet/libraries/Sunder.Package.Format/SunderPlatformContractCatalog.cs` | Bounded canonical catalog creation, content validation and exact pin checks. |
| `languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build/PlatformContractSourceOperations.cs` | Core check/accept/freeze/export and Registry publication-evidence import. |
| `languages/typescript/node/packages/package-tool/src/platform-catalog.ts` | Independent Node canonical catalog and exact pin validation. |

Parsing a local catalog establishes content integrity, not Registry publication.
The selected Registry's authenticated administrator introduces platform releases;
ordinary package publishers cannot claim or advance platform-owned families.
The V1 platform authority is `sunder.core`, with mandatory contract family
`sunder.stack.contributor`. All other package RPC retains package authority.

## Source And Release Preparation

Ordinary `Sunder.Sdk.Stacks` source derives the complete Stack `2.0.0` descriptor:
48 definitions and six methods. Its existing descriptor and ABI hashes remain
unchanged. Source and generated transport must reach a two-pass fixed point before
the complete baseline is accepted. SDK builds use the actual evaluated source,
reference, define, nullable, configuration and target-framework context.

The Core engineering tool owns explicit `platform-contracts check`, `accept`,
`freeze`, `export`, and `import-publication` commands. Export validates all current
source against release-frozen/published history and emits a complete catalog ready
for local selection and administrator import. No additional signing step exists. Commands preserve
immutable versions, input fences, cancellation and atomic output behavior; ordinary
Build/Rebuild does not accept, freeze, export, publish or acquire Registry evidence.
See the [command reference](../../../../../eng/platform-contracts/README.md).

## Closed Canonical Catalog

The [schema](../platform-catalog.schema.json) requires these members, in order:

1. `schemaVersion`: `1`.
2. `authorityKind`: `platform`.
3. `authorityId`: `sunder.core`.
4. `releaseVersion`: stable strict SemVer for the coordinated Core release.
5. `contractCount`: the complete inventory count.
6. `contracts`: 1–128 records, strictly ordered by unique contract ID.

Every contract record contains, in order, `contractId`, `contractVersion`,
`descriptorBytes`, `descriptorSha256`, `abiSha256`, and `canonicalDescriptor`.
Exactly one record names `sunder.stack.contributor`. Each record reproduces the
descriptor's exact canonical UTF-8 bytes, byte count, identity and both hashes.
Descriptor versions are independent of the catalog release version.

The complete catalog is canonical compact UTF-8 without BOM or terminal newline.
Outer members use schema order and JCS string escaping. Embedded descriptors retain
their independent RPC V1 canonicalization. Duplicate, unknown, missing, null,
case-colliding or reordered members are rejected.

Both implementations bound raw input to 64 MiB, outer depth to 16 and preflight
values/property names to 262,144. A descriptor is bounded to 4 MiB. Bounds apply
before allocating/parsing complete untrusted documents.

```text
selectedAuthorityContentIdentity = lowercase-hex(SHA-256(canonicalCatalogBytes))
```

The digest is external metadata, not a catalog member. There is no nested signing
envelope or separate payload digest. Validated snapshots defensively copy bytes and
freeze contract collections. Pin validation compares release, whole-catalog content,
contract ID/version and descriptor/ABI hashes.

## Local/Registry Selection And Offline Restore

Local platform intent selects the explicit catalog within the coordinated SDK minor
line, before first owned Accept if needed. Published intent resolves a Supported catalog through the explicitly chosen
Registry. The tool's coordinated SDK minor line supplies the stable version range.
The returned same-origin catalog URL, exact byte count, strong hash ETag, complete
descriptor inventory and release identity must agree. The tracked lock retains
that origin and the selected immutable catalog/descriptor identities.

Restore hydrates an existing selection. It never selects a new release, changes an
origin or rewrites the lock. A local catalog input must match the existing pins.
Cached catalogs and ownership receipts are immutable content-addressed pairs;
generation and Pack validate them offline alongside descriptor cache entries. Local
package candidates and catalog inputs can be restored together; missing local evidence
never triggers a Registry fallback.
Interrupted publication is completed by explicit restore, and GC removes only
ownership-proven unreferenced pairs after its normal grace/fencing checks.

Registry revocation prevents new compatible selection and admission. Exact retained
catalog content remains readable for historical restore and recovery; successful
content validation alone cannot grant current publication or execution permission.
The Host retains Registry lifecycle policy and its existing bounded offline lease.

See [platform imports](../../../../../docs/package-development/PLATFORM-CONTRACT-IMPORTS.md)
and the [Registry read/publication contract](../../../../../docs/REGISTRY-PLATFORM-CONTRACT-CATALOGS.md).

## Scope

This catalog model replaces the former catalog-specific Ed25519/trust-generation
design. It does not change shared execution-runtime artifact signing or OS application
signing. Package source/behavior migration, deployed Registry publication, supported-RID
acceptance and performance qualification retain their separate implementation/release
checkpoints.
