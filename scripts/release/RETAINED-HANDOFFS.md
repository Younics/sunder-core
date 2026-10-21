# Core source-bound release handoffs

These scripts implement release sequencing and immutable byte handoffs. They do
not mint signing trust, approve source/locks, bootstrap Registry authority, or
substitute mocked fixtures for production evidence.

## Coordinates and approvals

The SDK tag workflow requires `SUNDER_CORE_RELEASE_APPROVED=approved`,
an HTTPS `SUNDER_CORE_RELEASE_APPROVAL_REFERENCE`, and exact
`SUNDER_CORE_ARTIFACT_SOURCE_COMMIT`. The clean checkout, remote tag target,
`GITHUB_SHA`, `GITHUB_WORKFLOW_SHA` and workflow ref must agree.
Use the protected `sunder-core-release` environment with required reviewers;
`GITHUB_REF_PROTECTED` must be `true`;
operators must configure the branch/tag rulesets and required-reviewer environments.
Runners provide Bash, `gh`, `jq`, Python as `python3`, and the workflow's pinned
.NET/Node toolchains.

`SUNDER_CORE_PUBLICATION_EVIDENCE_COMMIT` names the
approved direct descendant; it may change only the Core owned baseline's verified
publication fields, the exactly confirmed SDK compatibility ledger append, and
receipt JSON under `eng/release/publication-evidence/`.
The original frozen baseline content/history and SDK artifact tag remain intact.
The evidence's catalog identity must match the original source-derived catalog.
The compatibility append must match the exact freeze-qualified reference retained
on the numeric published SDK release. A compatibility-only confirmation is allowed
without inventing a catalog publication (including SDK patch prereleases).

`SUNDER_CORE_REGISTRY_ORIGIN` is the selected canonical credential-free HTTPS origin
with a terminal slash. Catalog import and its later evidence commit require their
existing independent Registry authority approval. No catalog signer is introduced.

## Native component and package evidence

App/Host/CLI native builds and Agent's six-RID generated Worker/App role capture and
qualified package fan-in remain required. Original official .NET 10 Runtime and
Node 24 archives instead flow directly through Host; they are not Sunder release
assets. Native acceptance records Host's exact installed identity and provenance,
including upstream version, source, RID, checksum and original archive hash/size.
See [the official runtime contract](../../docs/OFFICIAL-RUNTIME-HANDOFF.md).

The integrated tag workflows run the checked-in native tests and retain their
TRX/Worker results. `github_release.py` stages and verifies numeric release assets,
then finalizes them after the component's required jobs. The former separate
acceptance-driver/coordinator and promotion dispatch framework is retired.
Deployed policy/WORM, production installer and performance qualifications retain
their owning requirements and are not implied by these source/native test results.

## Stable SDK catalog receipt

The tagged source-built engineering tool performs `platform-contracts check` then
`export`. No Accept, Freeze or Import is performed during artifact production.
The export must reproduce the exact release-frozen baseline. The draft-writing
stage exports and retains it in the same job and attempt.
The canonical catalog remains `sunder-platform-contracts.v1.json`.

`sunder-platform-contracts.receipt.v1.json` has exactly:

- `schemaVersion: 1`, `kind: https://sunderapp.io/acceptance/platform-catalog-release/v1`;
- `repository`, numeric `releaseId`, exact SDK `tag`, `artifactSourceCommit`;
- `registryOrigin`, `baselineSha256`, `inventorySha256`;
- `invocation: { workflowPath, workflowRef, workflowSha, runId, runAttempt }`;
- `catalog: { assetName, assetId, sha256, size }`.

`inventorySha256` hashes the full `contracts` array as compact UTF-8 JSON with
ordinally sorted object keys, no ASCII escaping and no terminal newline. Catalog
content identity continues to be SHA-256 of the existing exporter's exact bytes;
descriptor and domain-separated ABI hashes remain the existing RPC V1 identities.
This metadata adds no signature, key, envelope or trust-generation protocol.

The catalog is uploaded/read back by exact numeric asset ID before its receipt is
completed. The receipt's own numeric ID/hash are emitted as handoff coordinates
and read from the exact numeric release metadata; a receipt does not self-hash.
Both files join the SDK's existing checksum/provenance/SBOM inventory. Equal retries
preserve original receipt bytes/creation attempt, and another run/source/origin or
changed catalog/inventory fails. Partial catalog uploads remain on their numeric
draft for exact-byte recovery. Stable SDK promotion revalidates this handoff.
Prerelease SDK packages retain their prior flow without issuing a stable catalog.

Catalog consumers must pin the selected numeric release and receipt asset ID/hash,
then verify the closed receipt, source/run evidence and catalog numeric asset
ID/bytes. Local export and mock tests grant no Registry publication or first-party
ownership. Registry bootstrap/import and the source-to-publication evidence commit
require their separate real, approved acceptance records.

## SDK compatibility qualification and confirmation

Stable and prerelease SDK publication requires the independent release gate in
`scripts/compatibility/sdk_release_compatibility.py`. Protected
`SUNDER_SDK_COMPATIBILITY_BASE_COMMIT` pins the pre-release supported history; the
same-run `sunder-tested-sdk` artifact supplies the real qualification receipt,
new candidate reference and original retained bytes. The tag workflow's owned
collector supplies execution evidence through separate same-run
`sunder-sdk-compatibility-lane-<lane>` artifacts. Candidate envelopes cannot author
test selections or execution results. Required tests and commands come from the
immutable reviewed recipes; actual OS/RID and pre/post input hashes are collected
while those commands execute. XML attributes are parsed by the actual XML parser,
including outcomes, rather than rescanned with regexes.
The full [input contract](../../docs/SUNDER-SDK-COMPATIBILITY.md#mandatory-sdk-release-qualification)
describes the closed schema, result coverage and bounds. Missing inputs block SDK
release; the empty unreleased ledger remains valid in ordinary CI.

`prepare --candidate candidate --directory release-assets --executions compatibility-executions`
authenticates the
trusted Git base and tagged ledger, validates the package and compatibility bytes,
checks all seven independently downloaded lane artifacts, and creates
`sunder-sdk-compatibility.tar`. It never creates an acceptance receipt
from source-test success. The archive joins the existing numeric draft's
checksum/provenance/SPDX inventory. Stage outputs pin its hash and the trusted
base commit/hash for every publication/promotion/finalization recheck. Numeric
asset readback preserves the first retained bytes on equal retries.

Qualification is **pre-publication** and keeps `status: freeze-qualified` outside
the published ledger. The first seed can be tested and retained from candidate
artifacts without publishing them first. No release identities, commits or results
are prepopulated; genuine retained inputs, reviewed recipes and their owning tests
must still be supplied before the collector can produce real execution evidence.

After actual finalization, with the original artifact-source checkout and its real
run/qualification coordinates, confirmation can prepare evidence-only files:

```sh
python3 -B scripts/compatibility/sdk_release_compatibility.py confirm \
  --directory "$DOWNLOADED_SDK_ASSETS" --release-id "$SDK_RELEASE_ID" \
  --output "$NEW_EXTERNAL_EVIDENCE_DIRECTORY"
```

This requires the verified numeric release to be non-draft, checks its exact asset
bytes and provenance, and reads the qualified reference back by numeric asset ID.
It writes a proposed ledger append and
`eng/release/publication-evidence/sdk-compatibility.json` **outside** the source
checkout. The latter binds the source/tag, numeric release/asset, asset hash/size,
qualified-reference hash and original ledger hash. It is not the pre-publication
qualification receipt. Review these files in the existing approved direct untagged
publication-evidence descendant; that verifier rejects any different reference,
history rewrite, moved tag or unconfirmed numeric asset. It neither commits nor
publishes. Adopt the confirmed ledger as trusted history before the next SDK gate.
