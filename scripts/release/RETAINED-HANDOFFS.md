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
repository/organization variables supply runner labels before job selection.
Shared coordinates and approval variables used by workflow-level `env` must also
be repository/organization variables. `GITHUB_REF_PROTECTED` must be `true`;
operators must configure the branch/tag rulesets and required-reviewer environments.
Native/coordinator runners must provide Bash, `gh`, `jq`, and Python 3.11+ as
`python3`, in addition to the pinned .NET and component acceptance prerequisites.

`SUNDER_CORE_PUBLICATION_EVIDENCE_COMMIT` names the
approved direct descendant; it may change only the Core owned baseline's verified
publication fields and receipt JSON under `eng/release/publication-evidence/`.
The original frozen baseline content/history and SDK artifact tag remain intact.
The evidence's catalog identity must match the original source-derived catalog.

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

The existing production-acceptance decision binds six `core-production-<rid>.json`
files through `nativeReceipts` (RID, file SHA-256, acceptance-driver SHA-256). Each
native record contains `runtimeInstallations`, using Host's current
`InstalledExecutionRuntimeSummary` DTOs, and passed installer/launcher checks with
durable evidence references. This is test evidence, not a new runtime archive,
signature or publication family. Source/tag/run/asset and immutable retry checks
continue to protect actual component and package releases.

## Stable SDK catalog receipt

The tagged source-built engineering tool performs `platform-contracts check` then
`export`. No Accept, Freeze or Import is performed during artifact production.
The export must reproduce the exact release-frozen baseline. Its bounded export
metadata is transported in an exact-attempt Actions artifact to the draft writer.
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

Consumers must pin the selected numeric release and receipt asset ID/hash, then
verify the closed receipt, source/run evidence and catalog numeric asset ID/bytes.
Local export and mock tests grant no Registry publication or first-party ownership.
Registry bootstrap/import and the source-to-publication evidence commit require
their separate real, approved acceptance records.
