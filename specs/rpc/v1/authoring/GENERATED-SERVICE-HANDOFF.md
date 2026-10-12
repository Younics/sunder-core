# Generated-Service Shared Authoring Handoff

Normative amendment, 2026-10-06. These are the shared model and validation contracts
for the C#, TypeScript and JavaScript authoring implementations. The Format import
validator and the schemas implement the shared foundation. Package.Build and Node
own their corresponding readers, writers, migrations, generation and command tests.

## C# Source Surface Finalization

C# has one source-authoring pipeline: the neutral service/value/semantic declarations
in `Sunder.Sdk.Services`. RPC-prefixed authoring declarations and their exposure/
audience enums are retired; the compiler/editor MUST NOT accept an alternate legacy
marker or use an explicit-method-only fallback. Declared public async instance
methods are discovered normally, with optional semantic metadata. Recognized local
lifecycle methods remain excluded. Low-level SDK wire/runtime RPC types and generated
provider-registration factories retain their existing identities.

Private versions, constructor defaults, presence/nullability, exact offset-date-time
projection and imported range selection retain their qualified semantics. A
constructor may accept the same reference type with wider outer nullability than
the serialized property; the property still determines required wire presence and
nonnullable type. No narrowing or arbitrary conversions are inferred. Imported
ordinary clients use reviewed requested ranges/prerelease policy and exact contract
tuples, never restore-selected package version/content as routing authority. Owned
clients retain exact owning version and generation/activation binding.

Frozen source, JSON/text conformance and historical baselines keep their original
bytes. A test-only historical reader may explicitly project old declaration tokens
to current source and map diagnostic positions back; this MUST NOT become a product
authoring compatibility mode. Current samples and author guidance use Services.

## Closed Shapes And Member Order

Keep `lockVersion: 1` and `schemaVersion: 1`. This deliberately tightens the
unreleased, closed authoring models; it does not introduce a permissive schema-1
reader or change RPC V1 descriptors, manifests, inventory hashes or runtime framing.

| Model | Exact addition and order |
| --- | --- |
| Lock `packageImports[packageId]` | Required `consumerRoles`, immediately after `requiredContracts`. Members are exactly `localName`, `presence`, `requiredContracts`, `consumerRoles`. |
| Package owned baseline | Optional root `retiredPrivateContracts`, immediately after `contracts`. Root order is `schemaVersion`, `authorityKind`, `authorityId`, `packageId`, `contracts`, `retiredPrivateContracts`. |
| Platform owned baseline | `retiredPrivateContracts` is forbidden, even when empty. |
| Package project export | Required root `packageVersion`, immediately after `packageId` and before `contracts`. All ten root members are required. |
| Authoring receipt / manifest import groups | No new member. Existing `useRefs[].role` carries each group's actual App/Runtime grants. Receipt root already has `packageVersion`. |

All objects remain closed, with the existing strict bytes, duplicate/case-collision,
canonical ordering, hash, authority and snapshot checks. Generated exports/receipts
are regenerated at cutover; they are not legacy migration authority by themselves.

## Consumer Role Intent

`consumerRoles` is exactly one of `['app']`, `['runtime']`, `['app','runtime']`
(JSON uses double quotes). No empty, null, reversed, repeated, case-folded or unknown
value is accepted. New **Add** defaults to both roles in the stored intent, even if
only Runtime is currently present. Targeted Update preserves existing roles unless
new roles are explicitly reviewed; adding an App later does not widen a runtime-only
selection. Presence and required-contract readiness keep their separate meanings.

For each package import, compute:

```text
consumer subset = selected inventory's complete public package-authority subset
effective roles = reviewed consumerRoles intersect present package roles
bundleRefs       = exact consumer subset
useRefs          = effective roles x bundleRefs
```

Generate each role independently from the same lock. Within one role, coalesce exact
contract tuples and apply required-wins only to imports granting **that role**.
An App-only required contract cannot make a Runtime use required through a different
runtime-only reexport selection. Authority dependencies grant no uses. Retained
narrowed action policy must survive; if an old action policy cannot be reconciled
losslessly with the existing source/role policy model, fail explicit migration rather
than expanding it. This member adds role intent, not a second action-policy store.

An import with no effective role fails authoring with
`package-api.consumer-roles-unavailable`; it must not silently grant another role
or emit a group with empty `useRefs`. Role-leaf generation must validate the complete
canonical project's role intent before projecting that leaf; a Runtime-only leaf
does not invalidate an App-only intent belonging to the same canonical project.

Format validates exactly the roles actually named by each manifest group:
`useRefs == distinct(useRefs.role) x bundleRefs`, with exact matching local uses and
present targets. Overlapping owner/reexport groups need not have equal role sets.
Every imported App/Runtime use still needs reverse coverage from some API group;
every imported bundle/provider still needs its proper group. No group may borrow a
missing bundle-use reference from another group. App providers and platform consumers
remain forbidden. Lock-to-receipt validation additionally checks the exact reviewed
role projection; Format cannot reconstruct an omitted lock role from a manifest.

### Explicit Consumer Migration

Final Build, Check, Pack and Restore readers reject missing `consumerRoles`. A
separate bounded internal migration reader alone may read the old closed entry shape.
It may not be a permissive flag on the normal reader. Preserve all original lock
selection/provenance/readiness fields and verify their normal semantics before any
write. Known old missing intent is Runtime-only, never the new Add default. Retained
role/use evidence must agree; conflicting or unrepresentable narrow intent fails.
Only an explicitly supplied reviewed role set may broaden it.

The owner command handoff is `MigrateSunderPackageApiConsumerRoles` (.NET) and
`sunder-package api migrate-consumer-roles --project <root>` (Node). Without a package
selection these migrate missing fields conservatively across the root. A reviewed
override is targeted with existing `SunderPackageId` / positional package ID and
`SunderPackageApiConsumerRoles` / `--consumer-roles`; accepted option values are
exactly `app`, `runtime`, `app,runtime`. The same role option applies to new Add and
targeted Update. Global operations reject a per-selection override. Missing-field
migration is explicit, never a Restore side effect. A partially migrated input may be
read only by that migration path; all output entries must satisfy the final schema.

The operation reports each old/new role set, reparses the complete final proposal,
checks aggregate role-scoped limits, and atomically replaces only the lock under its
existing lease with original input/source/policy fingerprints rechecked. It does not
refresh selected artifacts or acquire new authority. Already explicit roles are
preserved unless targeted review changes them. No-op preserves bytes and mtime;
failure leaves the original lock and generated tree intact.

## Private Baseline Retirement

`retiredPrivateContracts` is an ordinally sorted map of **original stable keys to
complete original package-owned records**. Each value is the existing closed
`{ contractId, current?, history }` shape; snapshots and release/publication evidence
have exactly the existing member shapes and order. Every present current/history
snapshot MUST have `exposure: 'package'`. A historical-only record still requires
nonempty immutable history. A captured draft may remain `current.state: 'draft'`;
the entire captured record becomes immutable archive evidence regardless of that
label. It is not a current build contract or a pending Freeze operation.

Absent and `{}` are valid; null is invalid. Preserve absence versus explicit empty
on a no-op roundtrip. A package-only baseline can therefore have empty `contracts`
and a nonempty archive. Historical contract versions, bytes, state and evidence keep
their original values, including versions unequal to their old package release.

### Classification And Transition Guards

1. Parse and validate **all** active and archived records, descriptors, ABI projections,
   release/publication joins and immutable history before classification. Inspect
   every snapshot, not only `current` or the current source's exposure.
2. An entry is retirement-eligible only if its entire current/history record is
   package-exposure. Entries with a public current **or any public history** remain
   in `contracts`. Do not split their history, relabel snapshots, or archive just a
   private current to evade public evidence. History-only private records can be
   retained in place or archived explicitly; they are not current release blockers.
3. `MigratePrivateSunderContracts` (.NET) and
   `sunder-package contracts migrate-private --project <root>` (Node) move eligible
   entries under the same key, preserving the exact complete record. No version
   allocation, Accept, Freeze, publication import, source rewrite or descriptor
   regeneration is part of migration. The source/retained evidence must not identify
   the selected entry as a current public contract. Public exposure transitions stay
   explicit public lifecycle operations.
4. Every unselected active record and every existing archived record is identical in
   the proposal. Existing archive keys cannot be overwritten, merged, trimmed or
   enriched; conflicting destinations fail. An already completed migration is a no-op.
   Archive additions equal the removed active before-images, including every decoded
   canonical descriptor/ABI byte and evidence value. Root identity cannot change.
5. Normal Build/Check/Pack never rewrites a tracked baseline. A legacy active private
   current that requires retirement produces migration guidance. Current public
   contracts cannot disappear merely because the new source is private, renamed,
   removed or baseline-free. Match existing keys **and contract IDs** before the
   private fast path; use the existing public drift/removal/exposure review and
   `SunderRpcContractCompatibility` rules for any active record with public evidence.
   Preserve prior public-baseline loss detection. A private-to-public successor needs
   explicit public acceptance/authority review, not adoption of the archived version.
6. Each map retains its own key/contract-ID uniqueness and snapshot-version checks.
   Archived identities may describe the current source-derived private services;
   they are not a second current map. A later explicitly accepted public successor
   may reuse an archived private key/ID without moving or mutating the archive; such
   an overlap must never make the public entry disappear from active validation.
7. Migrate under the existing root baseline lease, rechecking original exact baseline,
   source/reference/lock inputs after acquisition and immediately before atomic
   replacement. Reparse and compare the complete proposed model, including the archive.
   Stale input, invalid public state or failure publishes nothing. Normal baseline
   writers must also preserve all preexisting archived records losslessly.

### Combined Bounds

The archive adds no second capacity budget. Across `contracts` plus
`retiredPrivateContracts`, there are at most **128 entries**, counted as the sum even
when a reviewed successor reuses an archived key/ID. Each record has at most **256
history snapshots** and at most one current snapshot; the combined maximum is
**32,896 snapshots**. Apply the existing **268,435,456 raw-byte**, **depth 16** and
**2,097,152 structural value/property** baseline budgets to the whole document before
DOM allocation, not separately per map. Sum limits and immutable transition checks
are semantic requirements that JSON Schema alone cannot express. Overflow fails;
tools never drop history to fit. Each map also keeps the schema's 128-entry ceiling.

## Current Private Version And Project Export

For current package-exposure source, copy the **full effective manifest package
SemVer** into every descriptor, receipt, export and generated role/RID tuple. Validate
the existing strict SemVer grammar and 256-character bound before generation. Compare
ordinal strings, including prerelease, metadata and their case; SemVer precedence is
not exact identity. No assembly/SDK version, truncation or fallback is allowed.
`2.0.0-beta.1+build.7` must remain exactly that string. An unchanged package version
may identify a new unissued development snapshot only with complete exact owner,
artifact/generation and descriptor/ABI validation; immutable released artifacts stay
immutable. No independent private Accept/Freeze or prerelease allocation is performed.

The export root `packageVersion` binds its generation independently of a baseline.
Each exported private descriptor version must equal it. The consumer also compares
the root with its expected effective package version and binds the existing input
fingerprint, exact lock hash, owned policies and descriptor tuples. Public exported
versions remain baseline-governed and may differ from root `packageVersion`. Keep
normalized `appUse`/`runtimeUse` policies outside descriptor/ABI identity. External
App and Runtime uses are derived separately from the lock, never inherited from the
other role's export or providers.

This equality rule belongs to the new source-authoring/export/receipt boundary.
Do not unconditionally apply it to retained archives, publication history or general
archive inspection: existing private artifact versions remain valid historical inputs.
Public inventory remains public-only and the public compatibility classifier keeps
its version/history guarantees.

## Diagnostic Handoff

These stable authoring diagnostic IDs are required at the owning entry points; the
message includes the path, package/contract key and relevant exact values. Existing
generic closed-shape and public compatibility diagnostics still apply elsewhere.

| ID | Trigger / actionable message requirement |
| --- | --- |
| `package-api.consumer-roles-migration-required` | Missing final lock member. Name the owning migration command; say that omitted review preserves Runtime-only intent and Restore/Build does not migrate. |
| `package-api.consumer-roles-invalid` | Present invalid role value. Show the three exact allowed arrays; do not reinterpret it as legacy absence. |
| `package-api.consumer-roles-migration-ambiguous` | Conflicting retained role/action evidence. Identify the selection and require explicit reviewed reconciliation. |
| `package-api.consumer-roles-unavailable` | Canonical project has no role in the selected set. Require a matching role or explicit intent change. |
| `rpc-authoring.private-baseline-migration-required` | Active legacy private current. Point to `MigratePrivateSunderContracts` / `contracts migrate-private`. |
| `rpc-authoring.private-retirement-public-evidence` | Attempt to retire any public current/history or a current public source match. Require the public lifecycle; preserve the full record. |
| `rpc-authoring.private-retirement-conflict` | Destination key/identity collision prevents an exact before-image move. No overwrite or key-renaming repair. |
| `rpc-authoring.private-retirement-mutated` | A proposed writer removes, changes or enriches an existing archived record. Preserve its original evidence. |
| `rpc-authoring.baseline-combined-limit` | Combined active/retired entry or snapshot count exceeds the stated budget. No eviction. |
| `rpc-authoring.private-package-version-invalid` | Effective owning version missing, invalid or over 256 characters. Correct manifest package-version evaluation. |
| `rpc-authoring.private-version-mismatch` | Generated private version differs from owning package/export/receipt version. Show both full strings and regenerate coherently. |

## Implementation Owner Checklist

- **Package.Build:** append nonoptional `ConsumerRoles` to
  `SunderRpcLockedPackageImport` as `IReadOnlyList<string>`; preserve it in every immutable copy, parser, writer,
  selection and receipt/role projection. Add a lossless `RetiredPrivateContracts` map
  (`IReadOnlyDictionary<string, SunderRpcOwnedContractBaseline>`) to
  `SunderRpcOwnedPackageBaseline` and retain each `SnapshotJson`/`History` value.
  Retain absent/empty archive representation for lossless no-op writes. Add nonoptional
  `string PackageVersion` after `PackageId` to `SunderRpcPackageProjectExport` and
  nonoptional `string expectedPackageVersion` after `expectedPackageId` to
  `ReadPackageProjectExport`, updating every role consumer. Implement the
  guarded explicit migrations, private source identity and public-first classification.
- **Node:** implement the same closed fields, migration commands, classification,
  bounds and atomic fences in the existing tooling pipeline for both TS and genuine
  JS. Update its owned authoring-schema harness for this revised index and semantics;
  its prior hard-coded case count/index digest is intentionally stale. Do not change
  frozen RPC, ABI, evolution, parity or historical migration oracle bytes to compensate.
- **Both:** extend owning tests for lossless mixed public/private migration, archived
  draft/frozen/published/history-only states, public-history demotion, unchanged public
  evidence, concurrent drift/failure/no-op behavior, combined 128/129 limits, role-scoped
  required-wins, absent intent, and exact metadata-sensitive private identity. Preserve
  all existing public inventory and runtime authorization checks.

The shared conformance index updates only current authoring vectors and dependent
lock digests. Full owner suites and integrated generated-service acceptance follow
the reader/writer cutover. Website publication remains deferred.

### Conformance Pins For Owner Harness Updates

The current authoring index has **69 cases**. Its exact file SHA-256 is
`cc27c7375fc9df1ff9f36317b64d83ea66d1140437758728cc91748ab7adcb08`.
The three revised lock fixtures retain explicit Runtime-only intent:

| `conformance/valid/` file | Exact new SHA-256 |
| --- | --- |
| `mixed-contract-lock.json` | `3cc9c5dd3212c7e6c3d4be510ce913e4b00112abde0ff495229291c673437731` |
| `package-api-contract-lock.json` | `2bc60eedf068f12de2496d235b8961e85f192c53c804ceab4a2fea82540ec313` |
| `package-api-mixed-authority-contract-lock.json` | `671c562cf2d0b1039caefa1b6b8867fb748400497187f84bc04252f8673708f8` |

Their dependent current authoring receipts/export use these new lock hashes.
`parity/expectations.json` retains its historical presence/readiness projection pin
`4d2c20ba9591c04449ee4fe1e48fbbd1664642807e039a6984245317a9735c1d`.
Owner harnesses must explicitly distinguish the old pre-role-field byte image from
the revised current authoring fixture; do not rewrite that frozen parity oracle or
weaken exact hashing of current inputs. New App-only/both-role lock fixtures and the
metadata-sensitive private-export pair are current amendment conformance vectors.
