# Core source-bound release handoffs

These scripts implement release sequencing and immutable byte handoffs. They do
not mint signing trust, approve source/locks, bootstrap Registry authority, or
substitute mocked fixtures for production evidence.

## Coordinates and approvals

Both SDK and runtime tag workflows require `SUNDER_CORE_RELEASE_APPROVED=approved`,
an HTTPS `SUNDER_CORE_RELEASE_APPROVAL_REFERENCE`, and exact
`SUNDER_CORE_ARTIFACT_SOURCE_COMMIT`. The clean checkout, remote tag target,
`GITHUB_SHA`, `GITHUB_WORKFLOW_SHA` and workflow ref must agree. Runtime additionally
requires `SUNDER_CORE_SDK_TAG` at that same commit,
`SUNDER_EXECUTION_RUNTIME_LOCK_SHA256`, and an existing numeric
`SUNDER_EXECUTION_RUNTIME_RELEASE_ID` whose draft tag and `target_commitish` agree.
Existing source/signer/native-runner approvals and pinned drivers remain required.
Use the protected `sunder-core-release` and
`sunder-execution-runtime-native-release` environments with required reviewers;
repository/organization variables supply runner labels before job selection.
Shared coordinates and approval variables used by workflow-level `env` must also
be repository/organization variables. `GITHUB_REF_PROTECTED` must be `true`;
operators must configure the branch/tag rulesets and required-reviewer environments.
Native/coordinator runners must provide Bash, `gh`, `jq`, and Python 3.11+ as
`python3`, in addition to the existing pinned .NET/signing/native-driver prerequisites.

Publication is only `sunder-execution-runtime-publication.yml`, protected
`sunder-execution-runtime-publication`, `workflow_dispatch`, `refs/heads/main`.
Inputs are `runtime_tag`, `release_id`, `native_run_id`, `native_run_attempt`,
`artifact_source_commit`, `publication_evidence_commit`, `runtime_lock_sha256`,
and confirmation `publish-approved-retained-runtimes`. Approvals must still match
those exact coordinates. `SUNDER_CORE_PUBLICATION_EVIDENCE_COMMIT` names the
approved direct descendant; it may change only the Core owned baseline's verified
publication fields and receipt JSON under `eng/release/publication-evidence/`.
The original frozen baseline content/history and both artifact tags remain intact.
The evidence's catalog identity must match the original source-derived catalog.

`SUNDER_CORE_REGISTRY_ORIGIN` is the selected canonical credential-free HTTPS origin
with a terminal slash. Registry administrative credentials are supplied only to the
publication transaction after all retained-input checks. Approved public-key
directories are still required for both pre-publication and downloaded-byte
signature/attestation verification. The retained verifier requires .NET 10 on the
coordinator; publication runs its DLL, never `dotnet run/build/publish` or a native
release driver.

## Native asset receipt

`runtime-native-<release-id>-<native-run-id>-<native-attempt>-receipt.json` is the
completion marker for that attempt's numbered `-file-001`, `-file-002`, ... assets.
It has exactly:

- `schemaVersion: 1`, `kind: https://sunderapp.io/acceptance/runtime-native-assets/v1`;
- `destination: { repository, releaseId, tag }`;
- `artifactSourceCommit`, `sdkTag`, `runtimeLockSha256`;
- `invocation: { workflowPath, workflowRef, workflowSha, runId, runAttempt }`;
- `files`: sorted logical paths with exactly `{ path, sha256, size, assetName, assetId }`.

The 22 files are twelve `.sunderruntime` archives under `execution-runtimes/<rid>/`,
six original `execution-runtime-receipts/<rid>.json`, `production.lock.json`,
`release-drivers.v1.json`, `release-set.v1.json`, and `runtime-verifier.zip`.
The existing engineering release-set validator remains the cryptographic authority;
the handoff checks its exact receipt/lock/driver/archive byte relationships.
Numbered release asset IDs, sizes and SHA-256 digests are rechecked independently
of the receipt. A replaced ID, even for equal bytes, fails. Paths, regular files,
zip extraction, counts and sizes are bounded and links/traversal are rejected.

The producer uploads and byte-verifies files before uploading the receipt. Partial
same-attempt uploads are resumable by existing immutable upload helpers; changed
bytes or a false completion marker fail. Every new native attempt runs the full
matrix and uses new names. A publisher selects an exact completed successful
tag-native attempt, not whichever attempt is newest; a newer failed attempt does
not replace retained successful evidence. Missing assets block publication.

Publication records `artifactSourceCommit`, `publicationEvidenceCommit`, selected
native coordinates/receipt hash and the separate main workflow SHA/run/attempt in
the existing recovery journal. `evidenceDiff` records the verified direct parent,
evidence commit, changed-path allowlist result and publication-only baseline check.
Existing lookup/upload/per-family promotion and
post-publication download/resolution verification are retained. Final Registry
receipts use the existing `execution-runtime-release-...` upload protocol in a
credential-separated contents-write job. A lost journal recovers through exact
Registry lookup; it never authorizes changing the selected native bytes.

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
