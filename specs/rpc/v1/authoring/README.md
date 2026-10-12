# Sunder RPC V1 Authoring Artifacts

Status: normative Workstream A structural schemas for implementation-first RPC authoring.

Current C# source authoring uses only `Sunder.Sdk.Services`; RPC-prefixed declaration
aliases are retired. Ordinary methods, generated interfaces and the qualified
constructor/value rules are documented in the [ordinary-service guide](../../../../docs/package-development/ORDINARY-SERVICES.md).
Frozen C# conformance source remains byte-authenticated history, not a second
supported SDK surface. The Package.Build historical reader projects it into current
source only for canonical-byte and original-span oracle checks.

The [generated-service shared handoff](GENERATED-SERVICE-HANDOFF.md) freezes the
2026-10-06 amendment: required reviewed `consumerRoles`, package-only immutable
`retiredPrivateContracts`, and required project-export `packageVersion`. These
schema-1 changes are deliberate pre-release closure changes. The handoff owns exact
member order, migration commands/diagnostics, combined bounds and public-first
classification guards. Format group validation is role-scoped; Package.Build and
Node reader/writer and generated-service cutover remain their owning work.

Earlier implementation checkpoint: `GenerateSunderRpcBindingsTask` integrates package-authority imports into the initial C# semantic pass and final generated-member compilation. Hash-verified receipt consumption projects import groups and canonical reviewed-range/presence dependencies into manifests. Canonical exact-RID Worker leaves retain identical import evidence. Owned project exports preserve the producer lock hash; the amendment requires external App and Runtime grants to derive independently from reviewed lock intent. The historical lifecycle observations below apply to public evolution; they do not require current private contracts to allocate independent versions or pass Accept/Freeze.

The shared internal `SunderRpcContractCompatibility` classifier in `Sunder.Package.Format`, lossless C# owned-baseline lifecycle, and source-based Pack gate are implemented. Registry consumes the same version gate in preflight and under publication family/tuple locks. [Evolution rules and canonical recipes](evolution/README.md) specify the supported directional proofs, conservative `Major` cases, history floors, and allocation guards. No public NuGet package or public classifier API is added. Node now implements its independent production classifier, lossless package-owned baseline lifecycle, and source-verified Pack; see [Node execution status](#node-owned-lifecycle-execution-status). Registry authority records, publication-evidence import, and new prerelease allocation policy remain incomplete.

[Registry-authoritative platform catalogs](platform-catalog/README.md) use bounded Format and Node content validation, source-derived Core Check/Accept/Freeze/Export and explicit Registry publication-evidence import. The user-selected Registry establishes publication authority; catalogs have no signatures, signing keys or trust-generation metadata. Ordinary Stack source reproduces the complete shipped `2.0.0` descriptor/ABI tuple across all 48 definitions and six methods with unchanged hashes. SDK source builds generate the SDK-only Stack handler and required partial factory through a short-lived build-only tool process. Canonical package migration and deployment/release qualification remain distinct from this implemented authoring path.

Source-generated C# provider codecs preserve required `false`/zero/permitted null, optional presence despite initializers, numeric-enum integer lexical forms, exact string-enum names, transparent wrappers, and frozen property naming. Qualified source constructors preserve explicit optional presence through typed construction/population; unsupported bindings are rejected rather than delegated to a serializer fallback. Narrow CLR integer codecs use the SDK safe-integer reader plus checked ranges, accepting fraction/exponent spellings of integral values representable by the target type without changing the descriptor or ABI.

Generated codecs support SDK-profile nullable and tagged record unions, including inferred authored constant tags, concrete variant roots, and recursive models. Whole-value and exactly-one-variant checks reuse the SDK validator; metadata supplies required synthetic tags and a `[ThreadStatic]` depth guard bounds recursive conversion. General scalar/array/map untagged unions remain unsupported by the SDK profile; no new profile is introduced. These results do not establish Registry publication or phase completion.

The obsolete blanket C# nonempty-import generation gate is removed, not the downstream admission boundaries. Registry and Host still deny nonempty import groups, imported bundles, and optional dependencies pending Phases 2/3. Node tooling emits only explicit owned package-authority bundles, required dependencies, and empty import groups and denies import/optional inputs pending Phase 4. Generating or structurally validating an artifact is not publishing, installing, or activating it. The corpus and implementation checkpoint do not establish Phase 1 completion or final acceptance.

The key words MUST, MUST NOT, REQUIRED, SHALL, SHALL NOT, SHOULD, SHOULD NOT, and MAY are to be interpreted as described by RFC 2119 and RFC 8174 when they appear in uppercase.

These Draft 2020-12 schemas resolve locally. Exports and receipts reference the sibling `rpc-model-projection.schema.json` fragments `$defs/modelProjection` and `$defs/methodPolicies`; other references are internal `#/$defs/...` references. Schema identifiers are names, not permission to retrieve or substitute remote schemas. Every fixed-shape object is closed. Keyed maps admit only values and key grammars declared by their local schema.

## Artifact Ownership

| Artifact | Owner and lifetime | Tracked or distributed |
| --- | --- | --- |
| `contract-lock.schema.json` | One canonical package root owns `sunder-contracts.lock.json`; an associated App shares it and owns no copy. Package tooling alone mutates it through explicit add, update, remove, implement, require, and unrequire operations. Normal build is read-only and offline. | The lock is source-tracked. It is not package content. |
| `owned-contracts.schema.json` | Package tooling owns one reviewed `sunder-contracts.owned.json` per canonical package root. Core catalog tooling owns the sole platform-authority instance under `eng/platform-contracts`. | The baseline is source-tracked. Publication evidence is committed after publication without moving the artifact-producing tag. It is not package content. |
| `cache-entry-receipt.schema.json` | Core package tooling owns `<descriptorSha256>.receipt.json` beside each immutable `<descriptorSha256>.json` descriptor in the shared `~/.sunder/contracts/v1/sha256/` cache. | Marker-owned and untracked. It is never authority by itself and is never archived. |
| `package-public-inventory.schema.json` | Registry derives the immutable complete public inventory from one validated package artifact. A local candidate must derive byte-identical payload data. | Registry evidence and lock-mutation input. It is not author-maintained package content. |
| `package-project-export.schema.json` | The canonical C# package project generates the RID-neutral export for its associated App. | Marker-owned intermediate output, untracked, and never archived. |
| `authoring-receipt.schema.json` | The C# or Node build pipeline generates the validated join used before manifest creation. | Marker-owned intermediate output, untracked, never archived, and never accepted from an installed package as authority. |
| `rpc-model-projection.schema.json` | The compiler derives package-wide source-model and service-reference identities; pack aggregates the complete public-only projection. | Required indexed `payload/shared/contracts/rpc-model-projection.v1.json` in every current artifact, including an explicit empty document when there are no public bundles. |
| `platform-catalog.schema.json` | Core tooling exports `sunder-platform-contracts.v1.json`; the selected Registry's administrator validates and publishes the original canonical catalog. | Immutable Core release asset and Registry artifact. It is not a NuGet/npm package, a `.sunderpkg` member, or a Runtime protocol input. |

The schema files in this directory are tracked normative source.

## Member Models

### Model Projection (current unreleased V1 cut)

`SunderRpcModelProjection` contains mandatory ordinal maps `models` and
`serviceReferences`, followed by optional `optionalServiceReferenceMembers`.
The first two maps remain mandatory, including when empty. Keys are bare descriptor `$defs` keys, 1-128 ASCII letters,
digits, dots, underscores or hyphens. Maps are disjoint and contain at most 256
entries combined. Model values are portable identifiers, not source paths, CLR
names, aliases or namespaces. Backend keywords and case-only public-name
collisions are rejected. Repeated exact model identities must have equal normalized
closures, including when referenced through multiple definitions. Service-reference values are closed
`{ contractId, serviceId }` pairs resolving in the complete validated descriptor
set. A public sidecar may reference only public bundles.

Every export/receipt contract requires `modelProjection` immediately after
`abiSha256`, before optional `methodPolicies` and use policies. Both Runtime and App receipts use
`authoring-receipt.schema.json`. Private evidence stays in these compiler
documents. Lock `packageArtifacts[packageId]` and receipt `packageArtifacts[]`
require `modelProjectionBytes` (1-4,194,304) and `modelProjectionSha256` immediately
after `publicInventorySha256`, before `contracts`. Missing metadata is an error;
current V1 does not infer or reconstruct names from descriptor text. Historical
baseline/source fixtures outside the current corpus retain their original bytes.

The sidecar document is ordered `schemaVersion`, `packageId`, `packageVersion`,
`contracts`; each entry is ordered `contractId`, `contractVersion`,
`descriptorSha256`, `abiSha256`, `modelProjection`, then optional `methodPolicies`.
It covers all and only public
manifest bundles once, sorted ordinally with one version per contract ID. Its
exact indexed bytes contribute to package content identity. Model consistency is
keyed by ultimate authority plus model name, comparing normalized complete schema
closures after substituting projected model/service identities. Different
authorities do not merge. Source-symbol grouping and equality with reviewed origin
projections must still be proved by the compiler/selection pipeline.

`SunderRpcServiceReferenceSchema.Create()` supplies the ordinary V1 closed locator
schema for Build: `packageId`, `packageVersion`, `providerId`, `contractId`,
`contractVersion`, `descriptorSha256`, `abiSha256`, `activationId`,
`activationEpoch`, `sessionGeneration`, `endpoint`. IDs use string bounds 1-128,
versions 5-256, hashes 64-64, activation ID the existing UUID format, epoch and
generation integers 0-9,007,199,254,740,991, endpoint string bounds 1-256. Runtime
code additionally validates identity grammar, nonempty activation GUID, nonblank
endpoint and exact equality of every locator field with the visible provider,
including both increased and decreased generation rejection. The locator grants
no authority and contains no serialized service instance.

Shared Format APIs are internal through the existing Build/tool friend assemblies:
`SunderRpcModelProjection`, `SunderRpcServiceReferenceProjection`,
`SunderRpcModelProjectionContract`, `SunderRpcModelProjectionDocument`, and
`SunderRpcModelProjectionGraph.Validate(contracts, descriptorsByContractId,
authoritiesByContractId)`. Projection constructors copy their input collections;
`Parse(JsonElement)`, `ToJson()`, `ToCanonicalUtf8()` and `Validate(descriptor)`
support compiler evidence. Document `Parse(bytes)` requires deterministic bytes;
`Validate(manifest, descriptorsByContractId)` adds artifact/public closure checks.
Its `ModelProjectionBytes` and `ModelProjectionSha256` pin those exact bytes.

Candidate inspection exports the exact public sidecar beside `inspection.json`
and descriptor-hash files; the receipt appends byte count then hash after
`publicInventorySha256`. Maximum exact membership is 130 files. Target-only
projection inspection does not require shared bytes; shared and complete installed
sets do. Source snapshots and role plans use their existing membership/evidence
fields rather than a new role-plan projection field.

### Optional Method Policies

Contract entries in project exports, Runtime/App authoring receipts, and the public
sidecar may carry `methodPolicies` immediately after `modelProjection` (before
`appUse`/`runtimeUse` when present). The shape is:

```json
{
  "example": {
    "invoke": {
      "timeoutSeconds": 2100
    }
  }
}
```

Service and method maps are ordinally ordered and keyed by the descriptor's strict
1-128-character member-ID grammar. Every key must resolve in that entry's exact
descriptor. Limits match RPC V1: at most **64 services and 512 methods total per
contract**. Every leaf is closed and requires exactly one canonical integer
`timeoutSeconds`, **1-2100 inclusive**. Empty maps, null, unknown fields/targets,
duplicate keys, fractional/exponent encodings, zero and out-of-range values are
rejected. An empty policy set is omitted entirely; source `TimeoutSeconds = 0`
means omission, never an explicit zero entry. Existing document byte/depth/value
budgets apply unchanged.

These are ordinary call hints: the host clamps the effective deadline. They do not
modify RPC V1 descriptors, descriptor/ABI hashes, framing, grants or publication
requirements. The compiler owns existing `rpc.extended-deadline.v1` inference.
Public sidecars contain policies only for public bundles; private policies remain
in inline compiler exports/receipts. Policy-only changes alter the existing
sidecar byte/SHA pins. Source, role and reexport agreement must compare the full
entry metadata, rather than just descriptors or `modelProjection` maps. Model
closure consistency does not compare these hints across distinct contracts.

Shared internal APIs are `SunderRpcMethodPolicy(int timeoutSeconds)` with
`TimeoutSeconds`, and `SunderRpcMethodPolicies(services)` with immutable nested
`Services`, `IsEmpty`, `Empty`, `Parse(JsonElement)`, `ToJson()`,
`ToCanonicalUtf8()` and `Validate(SunderRpcContractDescriptor)`.
`SunderRpcModelProjectionContract` accepts an optional final `methodPolicies`
constructor argument and exposes non-null `MethodPolicies`, defaulting to `Empty`.
Its `ToCanonicalUtf8()` includes every nonempty policy and omits an empty set.

### Explicit Optional Service-Reference Occurrences

`modelProjection.optionalServiceReferenceMembers` is an optional map from a bare
owner definition key to an ordinally sorted array of direct wire-property names.
It follows `serviceReferences` and is omitted when empty. Absence always preserves
strict service-reference behavior; nullable schemas do not imply this policy.
The approved Agent usage annotates only `ExecutionTarget` on
`AgentToolSourceContext`, `AgentToolExecutionContext`, and
`AgentPromptContextRequest`; Format validates explicit metadata, not type-name
guesses or a global reference-type switch.

The map admits at most 256 owners, 1-256 members per owner, and 4096 members total.
Owner keys follow the existing bare-definition grammar. Wire-property names follow
the descriptor's 1-128 Unicode-scalar bound. Keys and member arrays are ordinally
ordered, with duplicate and ASCII-case-colliding names rejected. Present empty
maps/lists or null values are invalid. The owner map is independent of `models` and
`serviceReferences`: an owner may also be a projected model, or may be a borrowed
`Imported_*` record without claiming model ownership.

Every owner must be a reachable concrete record definition with a direct property
map. Every named property must exist and remain wire-required. Resolve property
schema aliases, then require exactly a null alternative and a reference resolving
to a known `serviceReferences` definition and selected service target. Nonnullable
references, nullable DTOs, locator lookalikes, transparent values, containers,
wildcards and fabricated paths do not establish optional occurrence policy.

Normalized model closure comparison includes policy presence/absence for each
wire member, including aliases and nested borrowed records. Shared authority/model
identities cannot disagree on this policy. Reexports and role/source snapshots
must retain complete canonical projection bytes, including the optional map;
schema equality alone is not sufficient evidence. Public inspection continues to
export only public-bundle metadata, while private compiler graphs are checked by
the same graph validator. Descriptor bytes, ABI hashes, eleven-field locator
shape, and `methodPolicies` are unchanged by this policy.

The shared constructor is
`SunderRpcModelProjection(models, serviceReferences, optionalServiceReferenceMembers = null)`;
its public property has type
`IReadOnlyDictionary<string, IReadOnlyList<string>> OptionalServiceReferenceMembers`.
Construction snapshots and sorts inputs, `Empty` and existing two-argument callers
remain strict, and `Parse`, `ToJson`, `ToCanonicalUtf8`, and `Validate(descriptor)`
retain their signatures. `SunderRpcModelProjectionGraph.Validate(contracts,
descriptorsByContractId, authoritiesByContractId)` retains its existing signature
and validates complete owned/imported sets before role filtering.

### Contract Lock

`lockVersion` is `1`. `packageArtifacts` is keyed by selected package ID and stores requested range/prerelease intent, exact selected package release/content, an optional canonical Registry origin, complete public-inventory count/hash, and every public contract metadata record. Local selection omits `registryOrigin`; present null or malformed origins are invalid. `packageImports` is keyed by the same package ID and stores, in order, `localName`, mandatory `presence` (`required` or `optional`), `requiredContracts`, and mandatory `consumerRoles`. Package presence controls dependency installation; `requiredContracts` controls provider readiness independently. `consumerRoles` is exactly `["app"]`, `["runtime"]`, or `["app","runtime"]`. New Add defaults to both; missing old intent is migration input only and final Build/Restore readers reject it with explicit migration guidance. Targeted updates preserve narrowed intent unless reviewed otherwise. `providerImports` is keyed by a local provider-import name and points to one package artifact and public package-authority contract. `platformImports` is keyed by a local name and stores one provider-only contract selection from an exact local or Registry catalog.

Package-artifact contract records retain `artifactSource` as observed in the selected artifact. It is artifact-local, not a cross-artifact coalescing key: the owner may record `owned` and a reexport `imported` for the identical descriptor and ultimate provenance. Each inventory retains its own value. That value is not copied to a generated bundle: every cross-package generated bundle has `source` equal to `imported`. Both authority kinds retain exact selected authority release/content; catalog signing/trust members are outside the closed schema.

### Owned Contracts

`schemaVersion` is `1`. The root selects exactly one authority mode. Package mode requires `packageId` and semantically requires `authorityId` to equal it; platform mode forbids `packageId` and fixes `authorityId` to `sunder.core`. `contracts` is keyed by stable local contract key. Each value records contract identity, an optional active `current` snapshot, and append-only immutable `history`. Exposure is part of every snapshot so a later policy change cannot relabel frozen history. Absence of `current` represents an acknowledged whole-contract removal and requires nonempty history.

Package mode additionally permits `retiredPrivateContracts` immediately after
`contracts`. It maps original keys to complete original package-owned records and
requires every current/history snapshot to have package exposure. Empty or absent is
valid; platform mode forbids the member. This append-only archive retains exact
before-images, including draft state, snapshots and release/publication evidence;
no public evidence may be moved into it. Combined active/retired counts and guarded
atomic explicit migration are specified in the [handoff](GENERATED-SERVICE-HANDOFF.md#private-baseline-retirement).
Normal Build never writes the tracked baseline. Current private descriptors use the
full effective owning manifest package SemVer and need no independent Accept/Freeze.
Legacy private records remain structurally readable for migration/history; their
old versions are not reinterpreted as current private identity.

A snapshot contains exact canonical descriptor and ABI-projection text, byte counts, hashes, contract version, exposure, and state. `draft` forbids release and publication evidence. `release-frozen` requires package-release or Core catalog-release evidence and forbids publication evidence. `published` requires both release and Registry publication evidence. Historical entries cannot be drafts. Platform snapshots are public and use catalog provenance; package snapshots use package provenance. Exposure changes require explicit baseline acceptance and a new contract version after release freeze; they never rewrite historical exposure.

The C# package and Core platform baseline models retain each complete `SnapshotJson` and read-only `History`, sharing immutable-history/evidence handling and the compatibility classifier. Their deterministic writers preserve all canonical bytes, exposure, release, and publication fields, reparse the proposal, and reject model/JSON disagreement instead of dropping or repairing evidence. Core platform read/write/Check/Accept/Freeze requires public snapshots and a mandatory current Stack contract. Root modes remain separate: ordinary package-baseline readers reject platform authority, and package commands cannot create it.

### Local Lifecycle And Pack

Implemented `CheckSunderContracts`, `AcceptSunderContracts`, and `FreezeSunderContracts` run only on the canonical package source root, never App, inner-RID, project-export/import, design-time, or cross-targeting invocations. Their [exact operation/property grammar](../../../../docs/package-development/IMPLEMENTATION-FIRST-RPC.md#local-lifecycle-commands) is derived from the task/targets. Check validates the complete set with no key/version/acknowledgement selection. Accept permits an unqualified new/compatible batch or one exact `SunderContractKey`; existing-version overrides require `SunderContractCurrentVersion` and `SunderContractNextVersion` together. Breaking, unclassified, exposure, and removal changes also require `SunderAcknowledgeBreakingChange=true`. Freeze permits an optional key and takes package release identity from `$(Version)`, not the contract-version properties.

First Accept can bootstrap a missing baseline from ordinary source and an existing valid `sunder-contracts.lock.json`; `[assembly: SunderPackage]` supplies identity without explicit `SunderPackageId`. Missing baseline with prior receipt/export evidence is not bootstrap. Normal Build/Check is offline and read-only for baseline/lock data, reporting classified drift, minimum next version, affected paths/codes, and executable acceptance guidance. Compatible acceptance automatically chooses the minimum fitting stable version against immutable history, including batches; a sufficient unissued draft version is retained. New prerelease allocation remains denied. Freeze records release evidence without inventing Registry publication. Later package releases may reuse unchanged frozen/published API tuples without changing their original version/evidence.

Accept/Freeze reuse the generation pipeline's in-memory plan and final generated-member validation but atomically publish only the baseline; Build subsequently regenerates RPC outputs. Baseline/source/resolved-reference/lock inputs are verified after acquiring the baseline lease and again immediately before publication. Stale plans or failed writes preserve the previous baseline and RPC tree, with no parallel RPC-tree publisher. Removal keeps immutable nonempty history without creating a descriptor at the acknowledged next version; removing a never-frozen draft with no history removes its entry.

Pack rechecks current public source against the frozen/published baseline and complete lock/cache, including prebuilt leaves, and requires exact complete bundle/provider/use/import-group projection. The amendment makes current private contracts source/package-version-bound and projects reviewed owned/external App and Runtime grants independently. Artifact-only RPC packing is denied, and deleting public baseline evidence or relying on authoring-mode fallback cannot prove freeze. Earlier descriptor-first migration inputs used the same `PackageContractBuilder`, not a new allow flag; extracted/generated snapshots are not authored descriptors. Retained historical evidence does not make that old authoring path current.

The separate Core engineering `platform-contracts check|accept|freeze|export|import-publication --core-root <root>` commands have [closed action-specific options](../../../../eng/platform-contracts/README.md). Bootstrap accepts only the explicit Stack adapter key at `2.0.0` with exact source-derived migration hashes. Freeze requires `--catalog-release`; export requires complete exact source matching frozen/published current snapshots, the catalog release, and a new output in an existing parent outside Core `languages/`. Cancellable leases, bounded inputs, source/reference/baseline and directory-membership revalidation, atomic staged writes and final byte checks protect writes. Administrator Registry import establishes publication. Explicit publication-evidence import confirms its exact origin/catalog metadata; normal Build performs no platform baseline/catalog writes or network acquisition.

### Node Owned Lifecycle Execution Status

This is the earlier independent-baseline implementation checkpoint. The
[generated-service handoff](GENERATED-SERVICE-HANDOFF.md) supersedes its private
version/lifecycle rules and Runtime-only consumer defaults; it does not rewrite the
historical packed artifacts or their evidence.

The actual V3 Node pipeline now reads/writes deterministic package-owned baselines
losslessly, preserving canonical descriptor/ABI text, state, immutable history, and
every admitted release/publication field. It enforces the existing 268,435,456-byte,
depth-16 ceiling, 128-family/256-history limits, and a 2,097,152-value/property
preparse budget; platform baselines remain rejected. The production classifier
executes all 199 shared evolution cases with C# oracles and frozen expectations unchanged.

Closed Node Check/Accept/Freeze commands validate the complete source set. Existing
current/history overrides require an exact key/current/next pair and explicit
acknowledgement for breaking/unclassified/exposure/removal changes. A genuinely new
selected key allows next-only stable first acceptance. First Accept precedes generation
and requires a valid exact lock and no prior baseline/receipt/export. Package and
platform imports can be configured first, including from local archives/catalogs
without a Registry. This fresh-only phase validates exact import materialization and
stable source/lock inputs while deferring complete semantic/provider validation to
first Accept. Existing accepted projects keep their full source-validation pipeline.
Prior generated receipt/export/marker evidence cannot bootstrap a missing baseline. Earlier
baseline-free development cannot adopt receipts; mode fallback is not freeze proof.

Accepted and proposed versions use one source/contextual-types/bindings/runtime/
manifest pipeline. Accept recompiles at proposed versions against the original
physical baseline/absence fingerprint. Baseline-only leased writes perform no
generated-output recovery/orphan cleanup and preserve no-op mtimes. Pack requires
all current entries frozen/published and exact complete owned projection, including
fresh source/Worker-byte verification for prebuilt leaves and post-archive input
revalidation under the baseline lease. Own-member/null-prototype maps and all-context,
provider-order-independent shared-handler checks preserve removal/type safety.

The [Node source/release boundary](../../../../languages/typescript/node/packages/package-tool/README.md#owned-v3-source-and-releases)
owns exact commands and reported packed omit-dev TS/JS lifecycle/Pack execution,
including first contract `2.0.0` independently of package `7.6.5`. SDK runtime dependencies
remain zero. Imports/restores, platform/optional admission, new prerelease allocation,
publication-evidence import, and V2-template cutover remain pending; this execution
status changes no schema, permission, or final acceptance gate.

### Cache Entry Receipt

`schemaVersion` is `1`. The remaining members bind one cached descriptor's contract ID/version, canonical byte count, descriptor hash, and ABI hash. The cache path digest, descriptor bytes, and the receipt's `descriptorSha256` MUST agree. Authority, Registry origin, and selection intent deliberately remain in the lock so a cache entry cannot confer authority.

### Package Public Inventory

This schema validates the exact hashed payload, not the Registry response envelope. The root contains `schemaVersion`, package identity/version/content identity, `publicContractCount`, and complete public `contracts`; both count and array may be zero for a package with no public RPC API. It deliberately has no `publicInventorySha256` member. Each contract contains identity/version, byte count, both hashes, its selected artifact source, and ultimate authority release/content provenance. An `owned` selected-artifact member must have package authority.

The shared internal `SunderPackagePublicApiInventory` in Format now parses and hashes this payload with the frozen domain/member order and strict closure checks. Before DOM parsing it enforces 1,048,576 raw bytes, depth 16, and 4,096 structural values/property names. The Build lock reader reuses this helper instead of duplicate inventory hashing. Registry's `RegistryPackagePublicApiInventoryV1`, contract DTO, and `RegistryPackagePublicApiInventoryResponseV1` have compiled closed-shape fixtures; the response is exactly `{ inventory, publicInventorySha256 }`, with CLR property `PublicInventorySha256` on the envelope only. This is structural-integrity implementation, not persistence, a read backend, an API route, or authority/publication business rules.

### Package Project Export

`schemaVersion` is `1`. Producer, path-free project identity, configuration, target framework, input fingerprint, required `lockSha256`, package ID, and required `packageVersion` key one export. `packageVersion` follows `packageId` and is the exact effective manifest SemVer, including prerelease/build metadata. Private exported versions equal it ordinally; public versions remain baseline-governed. `lockSha256` identifies the producer's exact deterministic lock bytes. `contracts` contains only package-owned local contract snapshots and their optional normalized `appUse`/`runtimeUse` policies; it contains no providers, external Registry imports, path, RID or package-content identity. Each role independently generates its bindings and use projection. External role grants derive separately from the reviewed lock, not from the other role's permissions.

### Authoring Receipt

`schemaVersion`, producer, deterministic `inputFingerprint`, exact `lockSha256`, and package identity/version identify the generation. `schemaVersion` is `1`. `contracts` contains validated canonical snapshots. `packageArtifacts` carries the complete lock-selected public-inventory evidence. `bundles` joins receipt-local contracts to generated manifest `source` and ultimate authority provenance.

`packageArtifactImports`, `packageApiImports`, `providerImports`, and `platformImports` use the exact closed Manifest V1 group member models. `providers` and `uses` point to receipt-local `contractKey` values. `generatedPackageFacades` and `generatedBindings` retain path-free logical names. The receipt contains no source text, handler, absolute path, credential, timestamp, environment value, mutable URL, or generated source body.

The C# manifest builder consumes the receipt with the generation task's expected SHA-256 plus lock/package identity, validates canonical descriptor snapshots and complete inventory/group/task-item agreement, and projects the validated records rather than reconstructing authority from authored items. Generated source, owned/imported descriptor snapshots, and applicable receipt/export outputs publish atomically under the existing marker-owned output lock/transaction. First and final compilation passes must agree on registration and complete receipt bytes before publication.

Manifest bundle members `source` (`owned|imported`), `authorityKind` (`package|platform`), and `authorityId` are mandatory. Owned bundles require the enclosing package's authority and forbid selected-authority fields. Imported bundles require `selectedAuthorityRelease` and `selectedAuthorityContentIdentity`; platform authority names `sunder.core`. Dependencies require canonical nonempty ranges and explicit `presence: required|optional`, with no reader defaults. Unlike the receipt's required arrays, the four root manifest import groups may be absent or empty; explicit JSON `null` is rejected, including for forbidden conditional provenance members.

These strict manifest models are a supported public type surface in the existing internally distributed `Sunder.Package.Format` library for Core/Registry/tooling consumers, not a new public NuGet package. Structural format validation does not itself establish Registry publication or Runtime authorization.

Imported `contractKey` MUST equal the canonical `contractId`, automatically, with no per-contract alias. Its generated binding namespace is `Sunder.Generated.Rpc.Imports.C<encodedContractId>`: uppercase only the first character of each canonical lowercase dot-separated segment, join segments with `_`, and prefix `C`. Thus `example.rpc.v1.minimal` maps to `Sunder.Generated.Rpc.Imports.CExample_Rpc_V1_Minimal` and `sunder.stack.contributor` to `Sunder.Generated.Rpc.Imports.CSunder_Stack_Contributor`. The prefix supports digit-leading IDs; the encoding is injective on canonical lowercase IDs and ignores importing-package local name, version, and hash. Owned keys/names are unchanged. All key-sorted receipt arrays MUST be reordered after this projection.

Package `localName=weather` still generates the namespace-level class `Sunder.Generated.Rpc.WeatherPackageApi`; provider intent key `weatherForecast` still generates `IWeatherForecastService`. Those are package/provider intent names, NOT per-contract binding aliases. The aggregate imported provider interface inherits every descriptor service provider interface. Each facade property `C<encodedContractId>` returns `Sunder.Generated.Rpc.ImportApis.C<encodedContractId>.Contract`.

The per-contract API namespace contains immutable `Provider` with no public `Endpoint`; `CatalogSnapshot` with `Revision`, `Sequence`, `ResetRequired`, and `IReadOnlyList<Provider> Providers`; `CatalogEvent` with `Revision`, `Sequence`, `Kind`, and `Provider? Provider`; `Client`; and `Contract`. `Client` has one property returning each descriptor service client. Descriptor service IDs use the lowercase member-ID grammar, which permits dots, hyphens, underscores, digit-leading IDs, and repeated separators, not just source-authoring lower-kebab keys. Encode the property as `S` plus `UpperInitial(serviceId)`, uppercasing only the ID's first character, then escaping each original `_` as `_5f`, `.` as `_2e`, and `-` as `_2d`. Escape original underscores first so inserted escape prefixes are not escaped again. This is injective on valid descriptor service IDs: `example` becomes `SExample`, `weather-forecast` becomes `SWeather_2dforecast`, and `1.foo` becomes `S1_2efoo`.

```csharp
ValueTask<CatalogSnapshot> ListAsync(CancellationToken cancellationToken = default);
ValueTask<Provider?> GetAsync(Provider provider, CancellationToken cancellationToken = default);
IAsyncEnumerable<CatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default);
Client CreateClient(ISunderRpcCallScope scope, Provider provider);
ValueTask<Provider> ResolveSelectedPackageAsync(CancellationToken cancellationToken = default);
```

List/get/watch allow all authorized exact-contract providers, without selected-package/range/Active filtering. Get revalidates the supplied provider's exact activation endpoint and never substitutes a replacement. Watch preserves revision/sequence cursors, resets, and lifecycle events. Client construction MUST NOT create or dispose the supplied call scope.

Only the selected-package resolver additionally requires exact package ID, package version satisfying `requestedVersionRange` by SemVer precedence and explicit `includePrerelease` policy, and `Active` state. Contract ID/version/`descriptorSha256`/`abiSha256` remain exact for every operation. Prereleases are excluded unless explicitly included; build metadata does not affect package-version precedence. The resolver MUST NOT filter by package content hash or equality to the restore-selected package version. Zero matches report `rpc.provider-not-found`; multiple matches report `rpc.provider-ambiguous`, with no ranking, retry, or endpoint replacement.

### Compatible Installation

The user explicitly selected Compatible Installation on 2026-09-11. Package dependencies MAY install an in-requested-range package version only with the exact locked imported contract ID/version/`descriptorSha256`/`abiSha256`. Every applicable reviewed range and prerelease policy MUST hold. ABI equality alone is insufficient when descriptor bytes/hash differ. A package patch retaining all exact imported RPC identities may satisfy the dependency without matching the restore-selected release/content. No compatible-schema substitution is permitted.

Selected package and ultimate package-authority release/content are exact restore/build/inventory provenance, NOT install or routing hard pins. Different restore-selected artifact bytes across consumers MUST NOT by themselves cause installed release conflicts. Restore provenance and full public-inventory hash consistency remain mandatory; restore MUST NOT substitute a newer artifact or rewrite a lock. Installed artifacts remain individually validated and exactly recorded. Execution-runtime `.sunderruntime` exact pins and platform-catalog release/content constraints are independent.

Dependency contributors coalesce by package ID with `required` winning over `optional`, preserving the source `packageApiImports[].presence`. The current C# manifest projection preserves each selected artifact's canonical reviewed `requestedVersionRange` and rejects authored non-RPC dependency IDs that duplicate lock-derived IDs. Across consumers, range strings need not be identical, but one installed version MUST satisfy every applicable range/prerelease policy and all exact imported RPC identities for each required contract MUST agree. Content identity is not an install-coalescing equality requirement. A distinct ultimate package authority without an explicit selection and reviewed range is an unreviewed-range fail-closed/migration gate: implementations MUST NOT infer either a permissive range or an exact-equality range/content pin from the provenance snapshot. Dependency review MUST NOT implicitly grant uses/actions.

An optional branch may be omitted as a unit; its omission MUST NOT hide or disable an otherwise-authorized provider. The selected resolver still accepts any authorized active matching package ID/version within the requested range and exact RPC identity, including an independently installed package. Required-contract readiness accepts any authorized exact matching provider regardless of provider package. No install disposition grants authorization. Platform imports require an explicit Registry selection and exact restored catalog. The generated resolver performs no restore/install operation or Registry fallback.

### Platform Catalog Envelope

`schemaVersion` is `1`; `authorityKind`, `authorityId`, `releaseVersion`, `contractCount`, and `contracts` are required. Authority is `platform` / `sunder.core`. The catalog records the stable coordinated Core release and complete sorted descriptor inventory with canonical descriptor text and both hashes. Every V1 platform baseline and catalog is nonempty and contains exactly one active `sunder.stack.contributor` record. The whole-catalog SHA-256 is computed externally and is never embedded in the catalog.

Current C# `Create`/`Verify` and independent Node validation enforce those byte/inventory checks. Their objects are defensive immutable content snapshots with exact release/content and descriptor/version/ABI pins. Registry selection establishes authority. Core source operations derive the full Stack descriptor from the implemented adapter and internal typed wire models. Source and neutral generated transport must reach a two-pass fixed point. SDK builds use evaluated `@(Compile)` including `AutoGen` except their own transport output, `@(ReferencePathWithRefAssemblies)`, defines, nullable settings, configuration and target framework; explicit lifecycle commands retain their fixed Release context. [Transport publication rules](../../../../eng/platform-contracts/README.md#input-and-transaction-rules) require complete baseline agreement, input/membership revalidation and immutable marker-owned `obj` output. The older [migration check](../../../../eng/execution-runtimes/README.md#platform-catalog-boundary) verifies the SDK descriptor oracle's unchanged hashes; explicit `export` produces a catalog for administrator publication.

## Raw Bytes And Canonical Text

JSON Schema validates a parsed data model. A conforming reader MUST additionally enforce all of the following before trusting an artifact:

| Instance | Maximum raw bytes before parsing | Maximum outer JSON nesting depth |
| --- | ---: | ---: |
| `sunder-contracts.lock.json` | 33,554,432 | 16 |
| `sunder-contracts.owned.json` | 268,435,456 | 16 |
| Cache entry receipt | 16,384 | 16 |
| Package public inventory payload | 1,048,576 | 16 |
| Public model projection sidecar | 4,194,304 | 16 |
| Package project export | 67,108,864 | 16 |
| Internal authoring receipt | 67,108,864 | 16 |
| Platform catalog | 67,108,864 | 16 |

The raw ceiling includes all whitespace and the terminal LF where one is required. It is checked while reading, before JSON tokenization. Nesting depth counts the root object as depth 1 and each entered object or array as one additional level. These outer byte/depth values and schema collection maxima are format safety ceilings for bounded processing of untrusted input. They do not increase lower package/manifest product limits or prove that a structurally valid lock can be materialized; aggregate product gates are specified below. A canonical descriptor or ABI projection subsequently parsed from a string is a separate RPC V1 document and uses the RPC V1 descriptor depth and graph limits, not the outer depth in this table.

Platform C# and Node readers additionally enforce a maximum of 262,144 structural values and property names before DOM parsing (`JsonDocument.Parse` / `JSON.parse`). Each object/array start, scalar value, and property name counts once; end tokens do not count. Depth 16 and this structural budget are preflight limits, not merely post-allocation schema validation. These checks do not waive strict UTF-8, closed-shape, duplicate/case-collision, canonical-byte, or descriptor validation.

The model-projection sidecar uses the same pre-DOM counting rule with a 524,288
keys/values/containers ceiling. It has no separate allowance beyond the existing
descriptor, generated-document, lock and aggregate materializability limits.

- Input is strict UTF-8 without a BOM. Strings contain only Unicode scalar values. Object members do not duplicate or collide under the V1 ASCII `A` through `Z` to `a` through `z` fold. A parser that silently keeps one duplicate member is insufficient.
- Reads are bounded before parsing. Every canonical RPC descriptor is nonempty and at most 4,194,304 bytes. A schema `maxLength` counts Unicode scalar values, not UTF-8 bytes, so every `descriptorBytes` and `abiProjectionBytes` value MUST be checked against the corresponding exact cached, downloaded, or embedded UTF-8 bytes.
- `canonicalDescriptor` and `canonicalAbiProjection` values are the decoded text of the exact canonical UTF-8 JSON bytes, not pretty JSON, base64, a path, or parser-specific reserialization. They have no BOM, insignificant whitespace, or terminal newline. Descriptors still pass the full RPC V1 raw-token, schema-depth, graph, format, collision, and semantic validator.
- A canonical Registry origin is parsed and reserialized byte-for-byte. HTTPS requires a lowercase ASCII DNS host, optional nondefault port in `1..65535`, and `/` only. User information, extra path, query, fragment, Unicode/IDNA input, IP literals, default ports, and leading-zero ports are invalid. Explicit Development mode alone may use `http://localhost/` or a nondefault port. Schema patterns enforce the structural and length forms they can express; URI parser equivalence and environment mode remain semantic checks.

No receipt or export may use a mutable or absolute filesystem path to stand in for snapshot bytes. Package tools may store snapshots in the same marker-owned transaction internally, but the schema value remains the exact canonical text.

### Deterministic Outer JSON Writer

All eight artifact models use deterministic outer writing. Arbitrary serializer output is not conforming. The writer operates on the already validated data model and applies these rules recursively:

1. Fixed-shape object members are emitted in the exact order of the applicable schema's `properties` object. A conditional branch only requires, forbids, or narrows members already present in that order. Keyed-map members are emitted in ascending ordinal order. Array order is the semantic order specified below.
2. Strings use the RPC V1 RFC 8785 escaping rule: escape quotation mark, reverse solidus, and controls; use `\b`, `\t`, `\n`, `\f`, and `\r` where applicable and lowercase `\u00xx` for other controls; emit `/`, `<`, `>`, `&`, and every other Unicode scalar value without optional escaping. Emit the resulting scalar values as UTF-8.
3. Outer numbers are safe integers and are emitted as canonical base-10 ASCII with no leading zero, plus sign, exponent, fraction, or negative zero. Literals are lowercase `true` and `false`. These schemas admit no outer `null` value.
4. The deterministic document form used for tracked locks/baselines, cache and authoring receipts, and project exports emits a nonempty object or array with one member or element per line, two spaces per nesting level, one ASCII space after `:`, no trailing comma or trailing spaces, LF line endings, and exactly one terminal LF. Empty objects and arrays are `{}` and `[]` on the current line.
5. The canonical compact form used in hash preimages uses the same member, map, array, string, integer, and literal rules but emits no insignificant whitespace and no terminal LF. Canonical RPC descriptor and ABI-projection strings retain their independently defined RFC 8785 bytes; the outer writer escapes those bytes as a JSON string and never reparses them through a generic serializer.

The hashed package inventory root member order is `schemaVersion`, `packageId`, `packageVersion`, `packageContentIdentity`, `publicContractCount`, `contracts`. Each inventory contract uses `contractId`, `contractVersion`, `descriptorBytes`, `descriptorSha256`, `abiSha256`, `artifactSource`, `authorityKind`, `authorityId`, `selectedAuthorityRelease`, `selectedAuthorityContentIdentity`.

The platform catalog member order is `schemaVersion`, `authorityKind`, `authorityId`, `releaseVersion`, `contractCount`, `contracts`. Each catalog contract uses `contractId`, `contractVersion`, `descriptorBytes`, `descriptorSha256`, `abiSha256`, `canonicalDescriptor`. These explicit lists, not a JSON library's dictionary order, define inventory and catalog hash bytes.

Platform catalogs MUST be exact canonical compact UTF-8, with no terminal LF. The outer form uses schema member order with RFC 8785/JCS string escaping, not lexical sorting of outer members. Human-readable conformance fixtures are compacted before use as catalog input; both implementations require the same actual canonical bytes.

## Ordering And Closure

JSON Schema cannot express keyed joins, equality between a count and collection length, uniqueness by a projected tuple, or lexical ordering. Implementations MUST enforce:

- Public inventory and platform catalog contracts are ordinally sorted by `(contractId, contractVersion)` and contain at most one version of each contract ID. Their count member equals array length.
- Lock maps are ordinally key-sorted. Each package artifact's contract map is complete, public-only, keyed by the represented contract ID, and contains at most one version of that ID. `publicContractCount` equals map size. `requiredContracts` is an ordinally sorted set. Required `consumerRoles` is one of the three exact canonical arrays; readers do not sort, repair or default it.
- Lock `providerImports` keys and `(packageId, contractId)` tuples are each unique. Lock `platformImports` keys and `(authorityId, contractId)` tuples are each unique. Every `packageImports` key and package-based `providerImports.packageId` resolves one shared `packageArtifacts` key, and every artifact is referenced by consumer or provider intent.
- Within each baseline map, keys and represented `contractId` values are each unique. History is append-only and ordered by accepted SemVer progression. Within one record, a contract version and its canonical descriptor tuple occur once across `current` and `history`; publication enriches an active frozen tuple rather than creating a second version entry. Retired records are complete immutable before-images and never receive publication enrichment. Their presence does not suppress active public-successor validation. Both maps share the 128-entry and whole-document safety budgets.
- Project-export contracts sort by `contractKey`; both `contractKey` and exact `(contractId, contractVersion)` are unique.
- Receipt top-level identity keys are exhaustive: `contracts` by both `contractKey` and `(contractId, contractVersion)`; `packageArtifacts`, `packageArtifactImports`, `packageApiImports`, and generated package facades by `packageId`; bundles and generated bindings by `contractKey`; provider and platform imports by `key`; providers by `(role, providerId)`; and uses by `(role, contractKey)`. Each such key occurs at most once. Nested artifact contracts are unique by `contractId`; authority constraints are unique by `(authorityKind, authorityId)`; bundle, use, and provider references are unique by their complete tuple.
- Receipt contracts, package artifacts, bundles, providers, uses, generated package facades, and generated bindings sort respectively by `contractKey`, `packageId`, `contractKey`, `(role, providerId)`, `(role, contractKey)`, `packageId`, and `contractKey`. Package artifact/API groups sort by `packageId`; provider/platform groups sort by `key`; nested artifact contracts sort by `(contractId, resolvedContractVersion)`. Authority constraints and all nested reference arrays sort by their complete schema-member tuple. `actions` is the applicable subsequence of `discover`, `invoke`, `subscribe` in that order.
- Equal contract tuples reached through multiple selected artifacts coalesce to one receipt contract, bundle, binding, and role use only when canonical bytes, both hashes, exposure, ultimate authority, and selected authority release/content are byte-identical. Any disagreement in those fields fails this restore/build join. `artifactSource` MUST NOT participate in cross-artifact agreement: `owned` in the owner and `imported` in a reexport are valid together and remain independently authenticated inventory fields. A coalesced lock-generated use has `required=true` when any referring package import granting that same role lists that contract in `requiredContracts`; otherwise it has `required=false`. Package presence and grants to a different role do not participate in use coalescing.
- Missing, extra, duplicate, conflicting, or dangling records and references fail. A reference being locally resolvable is not sufficient: the exact projection and reverse-provenance rules below also apply.

## Semantic Validation

Schema validation is necessary but not sufficient. Validators MUST also perform these checks:

- Parse every semantic version and range with the shared strict implementation. Selected versions satisfy reviewed ranges and prerelease policy. Ranges have a nonempty permitted solution and platform ranges stay within the coordinated SDK/tooling release line.
- Publication timestamps are real proleptic-Gregorian UTC instants in canonical `YYYY-MM-DDTHH:mm:ss[.fraction]Z` form; a present 1-through-9-digit fraction has no trailing zero. Regex matching alone does not validate calendar dates.
- Reparse every canonical descriptor with the strict RPC V1 parser and recompute its canonical ABI projection. The root contract ID/version equals every enclosing copy; exact UTF-8 byte counts, canonical text, descriptor hash, ABI-projection text/count, and ABI hash agree wherever present. An ETag is exactly the strong quoted descriptor hash.
- Recompute every package-public-inventory payload from the validated immutable artifact. Its package identity/version/content equals the artifact; its count, order, fields, and domain-separated digest are exact. Missing, extra, private, duplicate, reordered, or mixed-artifact records fail. A zero-count payload is valid evidence for a non-RPC package, but it cannot create a lock artifact or package API/provider intent.
- For `artifactSource=owned`, package authority ID/release/content equals the selected enclosing package artifact. An imported record retains the exact distinct ultimate authority from the selected artifact manifest. References coalesced within one lock/receipt agree on ultimate release/content provenance, not artifact-local `artifactSource`; this does not require independently restored consumers to select the same artifact for installation. Platform authority is `sunder.core` with an exact selected catalog release/content identity. Neither authority kind carries catalog signing/trust fields.
- A lock `packageImports` target contains at least one public `authorityKind=package` member and has mandatory `presence` equal to `required` or `optional` and mandatory canonical `consumerRoles`; readers never default missing values. An empty inventory fails API add with `package-api.public-api-empty`; a nonempty inventory whose package-authority projection is empty fails with `package-api.consumer-api-empty`. `requiredContracts` selects only that nonempty package-authority subset and never changes package presence or consumer roles. A package `providerImports` entry selects exactly one public package-authority member; direct platform implementation exists only in `platformImports`.
- The lock contains no unreferenced package artifact, duplicate provider-import tuple, duplicate source of a lock-derived package dependency, mixed selected-artifact snapshot, inconsistent ultimate provenance, private contract, descriptor body, credential, header, local path, signed URL, mutable tag, or generated source. Selected packages and explicitly selected distinct ultimate package authorities contribute reviewed-range plus exact-imported-RPC dependencies carrying the originating package API presence; provider imports contribute required dependencies. Unreviewed transitive authority ranges fail closed rather than being inferred from snapshots. Compatible contributors coalesce by package ID with `required` winning over `optional`; restore content identity is not an installed-release equality requirement. Platform authority produces a catalog policy constraint and never a package dependency.
- In a package owned-contract baseline, root `authorityId` equals root `packageId`. Every `packageRelease.packageId` and `publication.packageId` in either map equals that root; a published snapshot's `publication.packageVersion` equals its `packageRelease.packageVersion`. Retired snapshots still pass complete descriptor/hash/evidence validation and may not contain public exposure. In a platform baseline, every catalog-release/publication authority is `sunder.core`, and `publication.catalogRelease` equals `catalogRelease.releaseVersion`. Repeated release, content, and Registry evidence is byte-identical wherever it denotes the same event.
- Baseline state changes follow the [directional compatibility and history rules](evolution/README.md): requests prove previous-subset-candidate, outputs/details prove candidate-subset-previous, and unproved recursive/numeric/format/union transformations remain conservative `Major`. Each historical minimum is anchored to that snapshot rather than rebased on the highest version. Draft replacement, exposure change, release freeze, whole-contract removal, and publication import require their explicit operations; breaking, unclassified, exposure, and removal acceptance also requires exact key/old/new acknowledgement. Every snapshot preserves the exposure frozen with it. Frozen and published tuples, including exposure, are immutable; the planned publication import changes only `state` and adds independently verified evidence to that same tuple. Retained baseline evidence alone is not Registry authority.
- A platform baseline contains exactly one `contractId=sunder.stack.contributor` entry with a non-absent current public snapshot. Every emitted V1 platform catalog contains exactly one matching active record. Other historical baseline entries may have no current snapshot, but the mandatory V1 contract may not be removed.
- A project export's `contracts` contains exactly the package-owned current snapshots selected for the project. Each exported `authorityId` equals root `packageId` and `source` is `owned`. Public exposure/identity equals the reviewed baseline; current private versions equal root `packageVersion` with full exact-string equality and source-derived bytes. Optional normalized `appUse`/`runtimeUse` are validated independently of descriptor identity; no imported descriptor, path or RID is present. Optional `roleUses` retains explicit typed Runtime source intent as exact imported contract/service tuples and policy, ordered by role/contract/service. It participates in the input fingerprint and neutral/exact-RID comparison and never transfers Runtime intent to an App. Consumers compare root package version to the expected effective manifest version.
- A project export requires `lockSha256` immediately after `inputFingerprint`, equal to the SHA-256 of the producer's exact deterministic lock bytes. Exact-RID implementations validate this hash as well as the owned snapshots; associated App receipts copy it unchanged. The hash does not export external contracts or authorize App external uses.
- Receipt `lockSha256` equals the SHA-256 of the exact current deterministic lock bytes. Receipt `packageArtifacts` is a byte-for-byte field projection of the validated lock artifact records, with maps converted only to the specified sorted arrays. There is exactly one `packageArtifactImport` for every receipt package artifact and no other one; its package/range/prerelease/selected release/content/count/inventory hash fields equal that artifact.
- Each `packageArtifactImport.authorityConstraints` array is exactly the sorted unique projection of the artifact's distinct `(authorityKind, authorityId)` values. Each projected record includes the one agreed selected authority release/content tuple. No authority may be omitted or added. Package projections preserve provenance and require explicitly reviewed ranges before generating install dependencies; their release/content fields do not supply an implicit range or hard pin. Platform projections generate only catalog policy constraints.
- Receipt import groups are exact projections of lock intent for the represented roles. Canonical aggregate output has one `packageApiImports` and one generated package facade for every lock `packageImports` entry and no others; role leaves project only their applicable grants from that validated whole-project plan. There is one provider or platform group for every corresponding lock intent and no others. Their package IDs, package presence, local keys, selected identities, role grants, contract tuples, generated names, required flags, and authority provenance equal the lock, canonical descriptors, and normalized generated output. `packageApiImports[].presence` is copied unchanged even when another dependency contributor makes the effective normalized dependency required.
- Every receipt contract has exactly one bundle and every bundle has exactly one receipt contract. An owned bundle has `authorityKind=package`, `authorityId` equal to the package being built, and no selected-authority fields. Every imported bundle is rooted in a package API, provider, or platform import group, including a provider's recursively validated public service-reference metadata closure within the same pinned artifact. It is backed by the exact selected artifact contract or Registry catalog record that supplied its descriptor and ultimate authority. Metadata closure alone grants no consumer actions. A locally resolvable but unselected imported bundle is an orphan and fails.
- A package API's `bundleRefs` equals the complete public package-authority subset of its selected artifact, and `useRefs` equals that subset crossed with that import's reviewed present consumer roles. Overlapping groups may grant different roles; a group need not reference another group's local role use. Completeness within each granted role and global reverse coverage of every imported use/bundle/provider remain mandatory. Imported package API receipt contracts and bundles have `exposure=public`. Platform-authority inventory members remain only in artifact evidence/authority constraints and produce no package facade, consumer bundle, binding, or use.
- Package provider imports and direct platform imports each reference exactly one public imported root bundle and one or more matching runtime provider records, and infer no consumer use. A package provider may separately declare typed Runtime intent for a canonical materialized imported service. Receipt `roleUses` authenticates its exact descriptor/ABI/service tuple, requiredness and action subset; normal manifest `usesContracts` represents the declaration without adding a package API group or lock field. Receipt actions and requiredness must match this source intent exactly, and the target bundle retains the selected artifact's exact public authority. App and platform grants are not inferred. A provider with `audience=public` references only a public contract; a package-exposure contract has only owner-audience providers. Provider role is `runtime` in V1.
- For each ordinary package API and role, derive actions from the validated descriptor: include `discover`; include `invoke` exactly when at least one unary method exists; include `subscribe` exactly when at least one server-stream method exists. Other local uses may narrow author-authorized actions but cannot include `invoke` or `subscribe` when the descriptor has no matching method kind. Every actions array is the applicable subsequence of `discover`, `invoke`, `subscribe`.
- After all exact-tuple and required-wins dependency coalescing, the complete generated manifest has at most 128 contract bundles, 128 role-scoped uses, 128 providers, and 128 normalized package dependencies. Receipt contract/bundle/binding sets and every import-group set fit their schema ceilings. Add, update, implement, restore, `contracts check`, and normal generation evaluate these aggregate limits with all local contracts and roles before committing a lock or publishing output; independent per-artifact maxima do not waive the aggregate gate.
- Every local key and generated logical name passes the frozen ASCII naming, keyword escaping, case-fold, normalization, archive-path, and generated-language collision checks. Schema patterns validate only the portable lexical subset.
- Platform catalog validation checks exact canonical bytes, content identity, authority/release, the mandatory V1 contract, complete inventory, and each descriptor's canonical bytes, size and descriptor/ABI hashes. Selection additionally verifies the chosen Registry's same-origin metadata and publication; local content validation alone grants no publication authority.
- `inputFingerprint` is deterministic over the producer's complete normalized input set. The architecture source does not assign it a cross-tool signature domain, so it is reproducibility/cache identity only, never contract, package, Registry, or signing authority.

## Exact Hashes And Domains

All SHA-256 text is lowercase hexadecimal. `raw32(...)` below means the 32 digest bytes, not their 64-character hexadecimal representation.

Canonical descriptor identity has no prefix:

```text
descriptorSha256 = lowercase-hex(SHA-256(canonicalDescriptorBytes))
```

ABI identity uses the exact 18-byte ASCII domain separator, including its final NUL:

```text
text: sunder.rpc.abi.v1\0
hex:  73756e6465722e7270632e6162692e763100

abiSha256 = lowercase-hex(
  SHA-256(domainSeparatorBytes || canonicalAbiProjectionBytes)
)
```

The package public API inventory digest covers exactly the canonical payload validated by `package-public-inventory.schema.json`. It does not cover an enclosing digest member:

```text
text: sunder.package-public-api-inventory.v1\0
hex:  73756e6465722e7061636b6167652d7075626c69632d6170692d696e76656e746f72792e763100

publicInventorySha256 = lowercase-hex(
  SHA-256(domainSeparatorBytes || canonicalInventoryPayloadBytes)
)
```

The Registry catalog's external immutable content identity is:

```text
selectedAuthorityContentIdentity = lowercase-hex(
  SHA-256(canonicalCatalogBytes)
)
```

It is not a catalog member. No signature preimage, public-key profile or catalog
trust-generation record exists in this model. New selection authenticates its
Registry connection and records the exact origin/release/content. Offline consumers
verify those existing pins; they cannot create new publication authority from bytes.

`lockSha256` is the lowercase SHA-256 of the exact deterministic tracked lock bytes,
including its terminal LF. Lock and input fingerprints have no domain separator;
they MUST NOT substitute for descriptor, inventory, package-content or catalog identities.

## Closed Shape Decisions

The architecture fixes the required information but leaves a few storage spellings implicit. This schema set resolves them for V1 as follows:

- Exact descriptor and ABI-projection snapshots are bounded decoded canonical JSON text. They are not nested permissive JSON values and do not require an external descriptor-schema reference.
- Every artifact whose normative prose names only a version uses the common `schemaVersion: 1` member. The lock retains its explicitly specified `lockVersion: 1` member.
- The shared cache receipt contains integrity only. Lock authority is not duplicated into hash-addressed cache state.
- The deterministic outer writer, explicit hashed member orders, and terminal-LF rule close the cross-language byte-representation requirement; generic serializer defaults are never identity input.
- Final Manifest V1 product gates remain 128 contract bundles, 128 role-scoped uses, 128 providers, and 128 package dependencies after coalescing. Schema collection maxima, baseline history 256, name/range maxima not fixed by the architecture, and the raw-byte/depth table are format safety ceilings only. They bound processing but do not grant product capacity or replace aggregate materializability checks.
- Because JSON Schema operates after parsing, the per-document raw-byte and outer-depth safety ceilings in this README are additional mandatory checks. Changing a safety ceiling is a reviewed format revision even when it does not change a product gate.
- Platform authority is the V1 Core authority `sunder.core`, published by the selected Registry's administrator. Both authority kinds retain immutable release/content provenance without catalog signing fields.
- The platform payload has explicit `contractCount`; like `publicContractCount`, equality to collection length is a semantic check.
- A package-public-inventory payload may be empty for a package with no public RPC API. Lock package artifacts are nonempty, package API add requires a nonempty package-authority projection, and the V1 platform baseline/catalog is nonempty and retains `sunder.stack.contributor`.
- An owned-baseline entry may omit `current` only after acknowledged whole-contract removal; immutable nonempty history preserves issued identity and exposure.
- Producer records use `Sunder.Package.Build` with `csharp`, or `@sunder/package-tool` with `typescript` or `javascript`. Logical names are path-free generated identifiers, not source or output paths.
