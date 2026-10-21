# Sunder RPC V1 Contract Evolution

Status: implemented shared internal classifier and immutable-version gate in
`Sunder.Package.Format.SunderRpcContractCompatibility`, consumed by C# package
build tooling, Core platform baseline lifecycle, and Registry publication. This adds
no public NuGet package or package-author API. The classifier operates on descriptors already accepted by
the strict RPC V1 parser; it is not another schema parser, authority validator,
or Registry client.

The rules and recipes here are language-neutral. Node now implements an independent
production TypeScript classifier, lossless package-owned baseline lifecycle,
Check/Accept/Freeze, and source-verified release Pack, including prebuilt leaves.
Its actual classifier executes all 199 shared cases: 79 proof/inverse, 32
presence/nullability directions, 16 minimum versions, and 72 forward/reversed history
cases. C# oracles and frozen expectations are unchanged. This establishes bounded
owned classification/lifecycle execution, not import/platform/optional admission,
publication-evidence import, new prerelease allocation, template cutover, or phase closure.

## Classification

`Classify(previous, candidate)` returns `Unchanged`, `Patch`, `Minor`, or `Major`
and bounded reasons. It compares canonical descriptor bytes first, then canonical
ABI projections, then attempts a directional proof:

| Result | Rule |
| --- | --- |
| `Unchanged` | Exact canonical descriptor bytes match, including version. Raw object-member ordering differences that canonicalize identically are not changes. |
| `Patch` | Descriptor bytes differ but canonical ABI bytes match, including annotation, version-only, or normalized set-order changes. |
| `Minor` | ABI differs and every affected existing surface passes the supported proof; new services, methods, definitions, and error declarations may be added. |
| `Major` | A breaking change is found, or the supported proof cannot establish compatibility. Unknown is never treated as compatible. |

For every existing method and error-details schema, the accepted-value sets obey:

```text
request:                 previous subset candidate
response/event/details:  candidate subset previous
```

The provider must accept every old request. Every new response, stream event,
and error-details value must remain valid for the old consumer. A definition
used in both directions must satisfy both proofs. Exact descriptor and ABI
identities remain mandatory for runtime use; classification never authorizes
substituting a different locked descriptor.

### Supported Proofs

- Closed records require the target's required properties to be guaranteed by
  the source, every source property to be accepted by the target, and property
  values and cardinalities to be contained. Presence and nullability are
  independent, including all four required/optional and nullable/non-null states.
- Ordinary strings support length containment. Arrays support cardinality and
  item containment; maps support cardinality, value, and ordinary string-key
  containment. Missing minima mean zero and missing maxima mean unbounded.
  Closed-record required/property counts also bound source cardinality.
- Ordinary string/boolean/null literal sets support inclusion without remapping;
  a constant must remain equal. Adding or removing the enum/constant form is
  conservatively `Major`. Empty collections do not waive generated item/key API
  checks.
- Named references must keep their names. Equality includes all reachable named
  definitions, not merely unchanged local `$ref` text. Exact recursive graphs
  are accepted, including annotation changes handled by ABI equality.
- Removing an allowed error from an existing method may be `Minor`; adding an
  allowed error to an existing method is `Major`. Adding an error declaration is
  distinct from allowing that error on an existing method.

### Conservative Major

Removal or rename of a service, method, definition, or error declaration, a
contract-ID change, method-kind or idempotency change, and error-details presence
changes are `Major`. Existing definitions not covered by a directional proof,
including unused generated value APIs, must remain graph-equal.

Changed numeric bounds/literals, exact formats/logical types/content encodings,
dynamic/any or object-form transformations, changed tagged unions or discriminator
meaning, inline/reference transformations, and renamed aliases are unclassified
`Major` unless exact ABI-graph equality already proves no change. An unequal
compared graph reaching a cycle is `recursive-change-unclassified`; there is no
coinductive subset guess. Even apparently widening a numeric request bound is
not a supported compatibility proof.

The proof budget is 65,536 steps, traversal depth 128, and 1,024 UTF-16 code units
per JSON Pointer. Exhaustion yields `Major` with `analysis-limit-exceeded`.
Reasons are distinct `(Path, Code)` pairs sorted ordinally by path then code,
bounded to 64 entries including `changes-truncated` when needed. Reason truncation
does not stop classification or hide a later `Major` result. Reasons contain
paths/codes, never descriptor bodies, annotations, or schema literal values.

## Immutable Versions

`ValidateVersion(candidate, immutableHistory)` is separate from classification:

1. Every historical descriptor must belong to the candidate's contract family.
   Exact reuse of any immutable version requires identical canonical bytes for
   every matching entry. Version identity includes build metadata. Exact reuse
   succeeds before allocation/backfill/prerelease checks, including an old major
   or an already immutable prerelease; a conflicting duplicate still fails.
2. A new allocation must have greater SemVer precedence than the highest
   immutable version. Lower/backfilled versions and build-metadata-only
   allocations fail with `contract-version.bump-required`.
3. Compare against the highest version and every historical version in the
   candidate's major. Compute each minimum from that historical version, then
   take the strictest minimum. Do not take the strictest class and repeatedly
   rebase it onto the highest version: an old minor addition must not force every
   later patch to become another minor.
4. A higher major establishes a new compatibility line. Earlier-major history
   below the highest version does not constrain the new line. Major zero has no
   special exemption from breaking-change rules.
5. New prerelease allocations and transitions involving a relevant historical
   prerelease fail with `contract-version.prerelease-policy-required` until an
   explicit allocation policy exists. An earlier-major prerelease below a stable
   highest version is not relevant to the new line. Package-selection
   `includePrerelease` does not supply contract allocation policy.

`GetMinimumVersion` retains exact input for `Unchanged`; otherwise it increments
the required stable patch/minor/major component, resets lower components, and
discards build metadata. Decimal identifier arithmetic has no machine-integer
limit, but descriptor versions remain bounded to 256 characters. An overlength
minimum reports `contract-version.version-limit-exceeded`; validation can still
accept a higher fitting stable bump. C# baseline acceptance automatically chooses
the smallest fitting bump, escalating patch to minor to major when necessary,
and fails without writing if none fits.

The shared validator does not infer first-publication history from an empty
list. Local new contracts default to `1.0.0`. Registry currently rejects an
otherwise admissible initial public version other than exact `1.0.0` with
`contract-version.history-unavailable`. In particular, a version above `1.0.0`
may represent legitimate prior private or migration history. Missing public
history is not proof that the contract should be renumbered; frozen identities
must not be rewritten to satisfy this temporary gate.

## Lifecycle Integration

The [local public source projection review amendment](../REVIEWED-PROJECTIONS.md)
adds a separate exported-source classification and version-bound companion reviews.
Descriptor/ABI compatibility alone does not establish source API compatibility.
Missing historical projection data remains unknown; initial adoption is explicit
and current-source-only, and normal Build/Freeze/Pack fail on missing/drifted reviews.

C# baseline models retain complete `SnapshotJson` and read-only `History`,
including every release/publication evidence field. The deterministic writer
reparses its output and rejects model/JSON disagreement instead of discarding or
repairing evidence. Exposure is outside descriptor ABI classification and is
conservatively `Major` in either direction at the owned-baseline layer.

Normal Build and `CheckSunderContracts` validate the complete source/baseline set
offline and report classified drift, minimum next version, affected members,
and an executable acceptance command. Root-scoped `AcceptSunderContracts` and
`FreezeSunderContracts` are implemented through the same in-memory generation
plan, not a second RPC-tree publisher. Exact operation/property grammar is in
[the authoring ADR](../../../../../docs/package-development/IMPLEMENTATION-FIRST-RPC.md#local-lifecycle-commands).

Acceptance supports new contracts and compatible batches without version
overrides, preserves a sufficient unissued draft version, and requires one exact
key, old/new versions, and explicit acknowledgement for breaking, unclassified,
exposure, or removal changes. Frozen/published snapshots are retained intact.
Removal preserves nonempty immutable history without inventing a descriptor for
the acknowledged next version; a never-frozen draft with no history may disappear.
Baseline commits recheck baseline, source, resolved reference, and lock inputs
under the baseline lease and immediately before publication.

Freeze records package release evidence only. Later package versions may reuse an
unchanged API without changing its original contract version or evidence. Pack
requires a frozen/published source baseline and the exact complete current RPC
projection, including prebuilt leaves; artifact-only RPC packing is denied.
Explicit descriptor-first migration inputs are validated by the existing builder,
not a new allow flag, and are scheduled for Phase 4/5 retirement. Mode detection
or fallback does not prove a source contract was frozen.

Node's implemented package-owned path preserves deterministic snapshot/history and
release/publication evidence with the frozen outer-byte/depth/collection bounds and
bounded preparse/proof budgets. Closed Check/Accept/Freeze grammar requires exact
existing key/current/next selection and acknowledgement for breaking, unclassified,
exposure, or removal changes. A genuinely new selected key permits a stable next-only
first override. First Accept requires fresh source and a valid exact lock before
generation; selected package/platform imports may already be present. It cannot adopt
prior receipt/export evidence from baseline-free development.

Accept recompiles source, generated contextual types, and runtime bindings at proposed
versions while preserving the original physical baseline/absence fingerprint. Its
baseline-only lease has no generated-output recovery/orphan cleanup; no-ops preserve
mtimes. Node Pack requires every current snapshot frozen/published and exact complete
owned projection, fresh ordinary-source/Worker-byte checks for prebuilt leaves, and
post-archive input revalidation under the baseline lease. Own-property membership across
all ABI maps/reference traversal correctly reports declared `constructor` error removal
as `/errors/constructor` / `error-removed`; source maps use null prototypes. Every shared
handler is checked in all generated contexts independent of provider order without type
widening. The [Node lifecycle reference](../../../../../languages/typescript/node/packages/package-tool/README.md#private-and-public-versions)
documents private package-version contracts and reviewed public evolution. Explicit
publication import verifies external evidence; mode fallback cannot prove freeze.

Core platform baseline read/write/Check/Accept/Freeze shares these immutable-history,
evidence-preserving snapshot, and classifier paths, with public snapshots and a
mandatory current `sunder.stack.contributor`. Ordinary package-baseline readers still
reject platform authority. The separate engineering
[`platform-contracts` commands](../../../../../eng/platform-contracts/README.md) validate
a two-pass source/generated-transport fixed point in a fixed `net10.0` Release
context; SDK builds instead supply their actual evaluated compiler inputs, including
`AutoGen` sources except their own transport output. Initial bootstrap requires
the exact adapter key, `--next-version 2.0.0`, and the fixed shipped migration hashes.
That exception preserves the existing Stack identity, not a new Registry allocation
policy. Freeze records the required catalog release, and export requires the complete
exact source set to match frozen/published snapshots. Baseline and catalog writes use
cancellable leases, input/directory-membership revalidation, atomic staging and final
byte checks. Registry publication-evidence import is explicit; ordinary Build writes
neither baseline nor exported catalog.

Registry invokes the shared version gate before storage intents and again under
family/tuple locks in the publication transaction. It validates bounded stored
canonical history and includes earlier allocations in the same transaction.
The selected Registry establishes package/platform publication authority, including
owner-only version publication, administrator catalog import and terminal revocation.
Format and Node catalog validation establish exact content and immutable pins without
catalog signing/trust inputs. See the [catalog model](../platform-catalog/README.md).
Canonical package migration and deployment/release qualification remain separate work.
Generated Stack transport and SDK-profile nullable/tagged record union codecs do not
expand the classifier's conservative proofs or add a schema profile.

The explicit migration target `CheckSunderPlatformCatalogMigrationInput` checks the SDK
Stack `2.0.0` descriptor oracle and six methods. Separately, the implemented adapter
and typed wire models reproduce all 48 definitions and the complete shipped canonical
descriptor/ABI tuple from source. `CheckSunderPlatformContracts` checks source against
the Core baseline. These checks establish source consistency, not Registry publication.

## Canonical Recipes

Both fixtures have `formatVersion: 1` and are executable language-neutral recipes,
consumed by C# and the independent production Node classifier. They are not authored
package descriptors or an alternative classifier implementation:

| Fixture | Execution and expected result |
| --- | --- |
| [proof-cases.json](proof-cases.json) | Clone `baseline`; apply `setup`, then `previous`; clone the result and apply `candidate`. Operations use JSON Pointer `path` with `value`, `schema`, `copy`, or `remove`. Reparse both descriptors with the strict V1 parser. Optional `inverse` swaps the parsed descriptors. Assert exact `kind` and sorted `[Path, Code]` reasons. |
| [history-cases.json](history-cases.json) | Start from the proof baseline, apply the named `variant`, then assign `version`. Assert `minimumCases` and each history case's required diagnostic substrings, or success for an empty diagnostic array. Repeat with reversed history to prove order independence. |

The proof fixture's `fourStateCases` additionally runs every presence/nullability
transition in request and output directions. `SunderRpcContractCompatibilityTests`
executes these recipes plus bounds, immutability, determinism, sampled directional
validation, and diagnostic-safety tests. Node now executes every shared recipe,
inverse, and four-state direction against its actual classifier, totaling 199 cases;
the expected classifications/reasons and C# oracles are unchanged. This bounded
execution does not establish remaining import/publication/admission parity, retained
release evidence, or Phase 1 completion.
