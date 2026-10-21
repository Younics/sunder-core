# Local public source projection review (unreleased V1)

This amendment closes the local owned-baseline gap between wire compatibility and
the exported source API. It applies to package-owned public lifecycle records and
Core-owned platform source records. Package-only baseline-free services retain
their package-version lifecycle.

## Exact identity and compatibility

Build/Check, release Freeze, and source-backed Pack MUST match the complete current
descriptor, ABI, exposure, `modelProjection` and `methodPolicies` against reviewed
evidence. This includes projected model names, service-reference contract/service
identities, optional service-reference member policies, and method timeout policies.
An explicit empty model projection is evidence; missing evidence is **unreviewed**.
Qualified prebuilt role-input validation applies the same comparison to its retained
role receipts before qualification/Pack, without compiling or replacing role bytes.

The existing descriptor classifier continues to classify **wire** changes. Wire
compatibility is directional value-set containment; it does not establish that
either callers or providers still compile. A separate exported-source classification
protects **both consumer and provider source** and is combined with the wire result
by taking the stricter result for acceptance/version allocation:

| Exported source result | Rule |
| --- | --- |
| Unchanged | Descriptor-derived normalized source shapes, model projection and method policies all match. Annotation/version text alone does not change source shape. |
| Minor | Only new definition/error declarations and their projections remain; every existing service/method, value definition and error declaration retains its exact normalized shape, and existing mappings/policies retain their meanings. |
| Major | A service or method is added/removed/changed inside an existing contract; an existing normalized value/error shape changes; a prior model/reference mapping changes/disappears; an existing definition gains a model/reference identity; optional-reference or existing method policies change; or historical projection evidence is unavailable. |

Adding a method is often wire-Minor for existing callers, but requires another member
on a generated provider interface. Adding a service similarly changes the existing
contract's provider obligations. Both require explicit Major acknowledgement even
when model maps and method policies are equal. A distinct new contract does not alter
an existing contract's provider interface and retains the ordinary new-contract path.

Removing an optional output member can be wire-compatible because the smaller output
is still accepted, while existing consumer code still accesses that member. The source
check therefore includes stored descriptor definitions, not just projected names.
It compares normalized canonical-ABI service maps and all existing definition/error
entries exactly. This covers signatures, member presence, enum members, aliases,
inline/anonymous types, and indirect references. It is deliberately conservative:
even a bound/presence change that a particular language could tolerate requires Major
until a safe two-sided generated-source proof is supported. Annotation-only changes
and unchanged recursive shapes remain source-Unchanged.

These comparisons use parser-bounded descriptor bytes/depth and map counts (256
definitions, 64 services, 512 methods), with no recursive reference expansion.
The immutable descriptor already bound by the companion supplies historical source
shape; no companion bytes or historical snapshots are rewritten. C# and Node execute
the shared [source-shape cases](evolution/source-shape-cases.json); emitted-interface
and projected-member compiler regressions also demonstrate unchanged provider and
consumer source failing after wire-compatible changes. The wire classifier and its
historical oracles retain their original results.

Normal source drift reports wire and exported-source classifications independently.
Wire-identical source changes are not silently accepted. Major source changes use
the existing exact key/current/next version and breaking acknowledgement rules;
issued tuples cannot be overwritten. Unissued drafts remain mutable through
explicit review, subject to every applicable immutable history floor. History
checks compare the highest retained version and every version in the candidate's
major, as with wire evolution.

An unchanged public API MUST reuse its exact reviewed contract tuple across package
versions; package version changes alone do not allocate contract versions or replace
original release evidence. Neither classifier permits automatic runtime schema,
ABI-only, version or source-projection substitution. Runtime/import pins remain exact.

## Data amendment: companion reviews, not rewritten snapshots

`owned-contracts.schema.json` remains schema version 1 (V1 is unreleased). Contract
entry order is now `contractId`, optional `current`, `history`, optional
`projectionReviews`. The optional array has 1..257 records, ordered by strictly
increasing SemVer precedence. Omit it when there is no review. Each record MUST
reference exactly one retained current/history version and contains, in order:

1. `contractVersion`: the exact version, including build metadata;
2. `descriptorSha256`: the exact retained descriptor hash;
3. `abiSha256`: the exact retained ABI hash;
4. `reviewKind`: `accepted-source` or `initial-source`;
5. `canonicalProjection`: a string containing deterministic two-space JSON, strict
   UTF-8, no BOM, LF endings and exactly one terminal LF;
6. `projectionSha256`: lowercase SHA-256 of those UTF-8 projection bytes (no domain
   prefix).

The embedded document is closed: required `modelProjection`, followed by optional
nonempty `methodPolicies`. Their grammars and semantic validation are exactly the
existing public sidecar projection/policy grammars, including descriptor linkage.
The embedded document is limited to 4,194,304 bytes, depth 16 and 262,144 structural
values/properties. Keeping it as an opaque outer string preserves the baseline's
existing 268,435,456-byte, depth-16 and 2,097,152-value/property limits even with the
maximum 128 families and 257 snapshots/reviews per family. Review metadata is local
authoring evidence; it is not a new Registry DTO, package sidecar, or wire field.

Models retain the full companion JSON alongside the full `SnapshotJson`. Writers
reparse both and reject typed-model/JSON disagreement. Reviews move with their
snapshots into history; frozen reviews cannot be removed, rebound or replaced.
Snapshot/release/publication evidence is retained, with only the existing verified
release-frozen-to-published confirmation transition admitted. Descriptor-only
historical baselines remain readable and round-trip byte-for-byte. Readers MUST NOT
infer empty metadata or derive old source names/policies from the current compiler.

## Current baseline validation

Normal Accept never enriches an unchanged descriptor-only tuple from current source.
The .NET package and Core platform initial-adoption operations are removed. Existing
`initial-source` companions remain readable and immutable, including the current
Core Stack baseline; all new allocations use `accepted-source` reviews.

Build/Check validate source directly against the reviewed current baseline. A missing
review requires restoration of actual evidence or explicit acceptance of a new Major
tuple. Frozen or published tuples cannot acquire a review from current source, even
if a proposed companion is labeled `initial-source`. Unknown historical projections
retain conservative Major version floors. Registry same-published-tuple protection
remains independently enforced.

The Quickstart owned file contains only retained private evidence and validates
without an adoption or retirement step. Frozen conformance and migration oracles
remain exact historical evidence; they are not regenerated or enriched by current
validation. Draft public tuples still require Freeze before Pack.

## Batch review and publication

C# batch review proposals additionally bind `expectedCurrentProjectionSha256`
(the exact companion hash, or the explicit marker `unreviewed`) and
`expectedNextProjectionSha256`. They follow the current ABI hash and next ABI hash,
respectively, in each review entry. Old descriptor-only review documents remain
parseable evidence, but cannot authorize changed-source acceptance: regenerate the
proposal. The existing exact baseline/input/lock fingerprint fence still applies.

Package publication confirmation reuses Core's complete projection-entry comparison
(and Node's equivalent) against the authenticated retained artifact whenever a
source review exists. Legacy publication confirmation retains its original scope
and never manufactures projection reviews. Registry's independent same-published-
tuple full `modelProjection`/`methodPolicies` rejection remains authoritative and is
neither duplicated nor weakened by this local gate. No Registry implementation
change is required.

## Diagnostics and validation scope

- `rpc-authoring.source-projection-unreviewed`: no current source review;
- `rpc-authoring.source-projection-drift`: current metadata differs;
- `rpc-authoring.source-projection-history-unavailable`: published/historical source
  evidence cannot be backfilled from current source;
- `rpc-authoring.source-projection-bump-required`: insufficient exported-source
  version floor;
- `rpc-authoring.immutable-projection-review-mutated` /
  `rpc-authoring.immutable-evidence-mutated`: lost or altered immutable evidence.

Focused regression coverage lives in the owned-baseline evolution/command tests,
`owned-projection-review.test.ts`, `owned-baseline.test.ts`, and
`owned-rpc-review.test.ts`. The C# follow-up validation built the engineering tool
standalone with zero warnings/errors, exercised 359 owned evolution/command/platform,
model public-name and imported shadowing cases (358 passed initially; the one failing
synthetic prerelease fixture was corrected and passed its targeted retry), and passed
all 140 `PlatformContractSourceOperationsTests`. Historical fixture round-trip
oracles were included. The corrections were limited to test setup and a missing test
namespace import; no production gate was weakened. No .NET-to-Node interop,
PublicApi snapshot refresh, full Core/workspace, or Agent run was part of this scope.
Node validation and broader integration remain separately coordinated; lanes sharing
Node outputs must run sequentially.

The subsequent source-shape hardening ran the 355 focused C# owned evolution,
command, platform and emitted provider/consumer cases. Four command-fixture failures
were corrected to explicitly acknowledge method additions before attempting their
baseline lease; all ten affected command/lease cases then passed their targeted
retry. The C#/Node shared source-shape matrix has eleven cases. Node's owning
baseline/projection and unchanged wire-oracle tests passed 306/306, and owning
source/App/review integration passed 51 with the opt-in packed-artifact test skipped.
Node used 24.18.1 with npm 11.19.0; TypeScript was emitted in place without artifact
cleanup. The refreshed standalone engineering tool built without warnings/errors and
its actual Core platform check passed. All eighteen captured Core/Node/Agent owned
baseline files retained their exact hashes, byte lengths and current versions,
including initial companions and immutable history. This scope did not rerun the
full workspace/Agent/artifact lanes or change the wire classifiers/frozen wire oracles.
