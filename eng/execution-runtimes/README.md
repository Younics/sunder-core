# Curated execution-runtime locks

`execution-runtime-lock.schema.json` defines the closed V1 lock contract. A valid lock contains exactly one .NET 10 and one Node 24 entry for each supported RID:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Every entry binds an immutable HTTPS source and SHA-256, upstream license files and hashes, a closed worker launch profile, an exact deterministic repack recipe, native smoke metadata, source approval evidence, release and attestation key identities, minimum Host trust generation, `lts` support-line metadata, the `supported` post-verification default status, a native runner profile, and platform signing/notarization requirements. Recipes can bind either a named archive root or `.` and must list excluded source paths explicitly; every exclusion must exist in the locked source, and all other links are rejected rather than copied into a runtime archive.

## Lock trust domains

`test-fixtures.lock.json` is synthetic test data. Its `.invalid` URLs, generated source digests, `test-only` scope, fixture runners, and test key IDs have no production trust. Focused tests independently regenerate every fixture source artifact and prove that each checked-in digest describes those exact synthetic bytes.

`production.lock.json` is intentionally not checked in yet. Production validation and the native release workflow require that exact file and reject the fixture lock. Do not create a production lock by copying fixture hashes or by calculating unreviewed vendor downloads locally.

The following gates require external approval and cannot be established by repository code:

1. Select exact supported .NET 10 and Node 24 patch releases and immutable vendor URLs.
2. Independently verify and approve each upstream digest and license file digest.
3. Record durable approval evidence and approver identities in every production lock entry.
4. Approve native runner images for all six RIDs and their platform signing/notarization identities.
5. Provision CI-attestation and release-signing services, publish their trusted public keys through the host trust process, and integrate non-exportable signer adapters.
6. Approve the Registry publication destination and release promotion policy.

No private key belongs in a lock, script argument, workflow input, repository secret consumed as raw key bytes, or `Sunder.ExecutionRuntime.Build` CLI option.

## Platform Catalog Boundary

The existing non-public `languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build`
tool also owns source-based Core platform baseline lifecycle, catalog export and
publication-evidence import, plus a separate credential-free migration-input check.
The user-selected Registry establishes catalog authority without a catalog signature.
The
[platform-catalog specification](../../specs/rpc/v1/authoring/platform-catalog/README.md)
documents source derivation and Format/Node content validation.

The command family is `platform-contracts check|accept|freeze|export|import-publication --core-root <root>`.
See the [Core platform command reference](../platform-contracts/README.md) for exact
invocations. Options are closed per action; every option takes one value, and
unknown or duplicate options fail:

| Action | Options in addition to required `--core-root` |
| --- | --- |
| `check` | None; validates the complete source/baseline set without writes. |
| `accept` | Optional `--key`, `--current-version`, `--next-version`, and `--acknowledge-breaking-change` (`true` or `false`). Version selection requires an exact key; existing-version overrides require the old/new pair, and breaking acceptance also requires `true`. |
| `freeze` | Required `--catalog-release`; optional `--key`. |
| `export` | Required `--catalog-release` and `--output`; emits a complete canonical catalog. |
| `import-publication` | Required `--catalog` and `--registry`; confirms an existing Registry publication. |

Initial acceptance is narrower: only `--key stack-contributor-rpc-service-adapter`
and `--next-version 2.0.0`, with no old version or breaking acknowledgement, can bootstrap
a missing baseline, and actual source must match the fixed migration hashes below.
Catalog releases are exact stable SemVer. Export requires the complete exact source
set to match frozen/published current snapshots and writes canonical catalog bytes.
Administrator Registry import establishes publication authority. The output must be new,
with an existing parent outside Core `languages/`. Cancellable leases, bounded
source/reference/baseline and directory-membership revalidation, atomic baseline/
staged catalog writes, and final byte checks guard writes; no orphan cleanup is added.

`CheckSunderPlatformContracts` is an explicit target on this private tool with
`DependsOnTargets="Build"`; it invokes the source-based baseline check. Ordinary
Build never invokes it, accepts/freezes a baseline, exports a catalog or publishes
to Registry.

Separately, from the **Core repository root**, check the reviewed SDK descriptor oracle:

```sh
dotnet run --project languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build -- check-platform-catalog-migration \
  --descriptor languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json

dotnet msbuild languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build/Sunder.ExecutionRuntime.Build.csproj \
  -t:CheckSunderPlatformCatalogMigrationInput
```

The older target builds the tool and invokes that same check against the real SDK file;
ordinary Build does not invoke the check. It verifies RPC descriptor V1,
`sunder.rpc.values.v1`, contract `sunder.stack.contributor` version `2.0.0`, service
`stack-contributor`, and all six methods: `export`, `import`, `import-applied`,
`list-export-items`, `metadata`, and `preview-import`. Canonical descriptor and ABI
hashes must respectively equal
`38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898` and
`6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8`.
This check is migration-oracle evidence, not the synthetic ping catalog fixture,
source-derived Stack parity, a published catalog, or release authorization. It emits no
catalog. Separately, ordinary source from the implemented adapter and internal typed
wire models reproduces those full canonical descriptor/ABI bytes, all 48 definitions,
and all six methods. The source helper does not read the descriptor oracle or load
Runtime Host or compiled Stack assemblies; it uses recursive ordinary SDK C# layout,
curated runtime/SDK references, and a fixed RID-neutral `net10.0` Release context.

Catalog export validates source and exact canonical content, reopens/rechecks staged
bytes and publishes without overwriting existing output. Registry administrator import
establishes publication authority; catalog selection needs no signing adapter, key,
trust bundle or crypto dependency. Execution-runtime signing and Development key
operations below remain separate.

Actual SDK Stack source builds invoke this tool as a build-only producer of an SDK-neutral handler and required partial
factory in immutable configuration/SHA-keyed `obj` output, without Worker/Build/tool
runtime assembly dependencies. The tiny routing wrapper preserves errors;
handwritten handler JSON conversion is removed. Source union codecs are complete
within the existing SDK profile, including nullable/tagged closed records, inferred
constant tags, concrete variants, and guarded nested recursion. General untagged
scalar/array/map unions remain profile-unsupported, not unfinished codec work.
Embedded descriptor/props distribution and public DTO/client bridge retirement
remain open; all 48 definitions, six methods, and shipped `2.0.0` hashes/baseline are
unchanged. C# and Node retain exact catalog content in their offline caches and validate
selected Registry origins/pins. Content-validation types establish integrity; deployment,
catalog publication and final migration/release acceptance require their own evidence.

## Isolated Development signing

The non-public Core tool owns a separate local Ed25519 release key and native-attestation key. First use creates both keys automatically in the current user's local application-data directory under `Sunder/development/execution-runtime-keys/v1`. An explicit `SUNDER_EXECUTION_RUNTIME_DEVELOPMENT_STATE_ROOT` or `--state-root` may instead select an absolute ignored local directory such as `<workspace>/.sunder/execution-runtime-keys/v1`; specifying both is rejected.

From the workspace root, create or inspect the current public projection without changing an existing generation:

```bash
dotnet run --project repos/sunder-core/languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build -- development-keys ensure
```

The state is fail-closed. Unix directories and files must remain exactly `0700` and `0600`; Windows state receives a protected current-user-only ACL. Links/reparse points, non-canonical or malformed state, missing generations, unexpected inventory, mismatched public/private material, and relaxed permissions are rejected rather than repaired or regenerated. Key IDs are derived from each public-key SHA-256 fingerprint, and command output contains only key IDs, public keys, fingerprints, and the trust generation. Private key bytes and private-key file paths are never CLI or Registry/Host configuration values.

`development-build` consumes one validated production-lock entry, verified source archive, and native attestation produced after the required native checks. It replaces only the attestation signing identity/signature with the isolated Development attestation key, projects the isolated release key and generation in memory, then uses the same deterministic archive builder and final inspector as `build`:

```bash
dotnet run --project repos/sunder-core/languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build -- development-build \
  --lock repos/sunder-core/eng/execution-runtimes/production.lock.json \
  --id sunder.runtime.dotnet --version <version> --rid <rid> \
  --source <verified-source-archive> --native-attestation <native-attestation.json> \
  --output <output.sunderruntime>
```

After all six RID artifacts for one exact runtime release are present in an otherwise empty directory, publish them to the local loopback Registry:

```bash
dotnet run --project repos/sunder-core/languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build -- development-publish \
  --registry http://127.0.0.1:5288/ --directory <six-rid-release-directory>
```

Publication independently reinspects all six artifacts with the current Development public keys. It sends the exact release signing key and per-RID native-attestation key expectations, performs exact lookup before create, uploads only missing RIDs with digest-derived idempotency keys and local-path Development endpoints, rechecks completeness, and uses the normal Registry promotion service. A matching staged or Supported publication resumes; partial, mixed, stale-generation, differently keyed, or conflicting state fails closed.

Rotate both roles together and advance the Development trust/policy generation explicitly:

```bash
dotnet run --project repos/sunder-core/languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build -- development-keys rotate
```

Stop and restart the local AppHost after rotation. Previously signed Development artifacts are intentionally outside the new current trust set. `development-keys reset` deletes all isolated signing generations and does not create replacements; the next ensure/AppHost start creates a new generation 1. Because reset changes the fingerprint while rolling the local generation back, also discard the local Registry database/object volumes and Runtime state before restarting. Registry policy synchronization otherwise rejects the rollback or same-generation fingerprint change. Neither rotate nor reset modifies Production trust, locks, signer adapters, workflows, or artifacts.

## Native workflow configuration

The native workflow is triggered by `runtime/v*` tag pushes. It fails closed until
the production lock and exact Core artifact source are approved. Both that tag and
`SUNDER_CORE_SDK_TAG` must resolve to `SUNDER_CORE_ARTIFACT_SOURCE_COMMIT`;
`GITHUB_SHA` and `GITHUB_WORKFLOW_SHA` must name that same clean source.
`SUNDER_EXECUTION_RUNTIME_LOCK_SHA256` binds the tagged production lock and
`SUNDER_EXECUTION_RUNTIME_RELEASE_ID` selects an existing numeric source-bound
draft. The native environment is `sunder-execution-runtime-native-release`.
Repository administrators must configure each approval status as exactly
`approved` and provide a durable HTTPS evidence URL in the matching
`*_APPROVAL_REFERENCE` variable:

| Gate | Status variable |
| --- | --- |
| Shared Core artifact source | `SUNDER_CORE_RELEASE_APPROVED` |
| Sources | `SUNDER_EXECUTION_RUNTIME_SOURCES_APPROVED` |
| Signers | `SUNDER_EXECUTION_RUNTIME_SIGNERS_APPROVED` |
| Native runners | `SUNDER_EXECUTION_RUNTIME_RUNNERS_APPROVED` |
| Publication | `SUNDER_EXECUTION_RUNTIME_PUBLICATION_APPROVED` |

The workflow also requires approved non-secret adapter identities in `SUNDER_EXECUTION_RUNTIME_RELEASE_SIGNER_ADAPTER` and `SUNDER_EXECUTION_RUNTIME_ATTESTATION_SIGNER_ADAPTER`. For each `WIN_X64`, `WIN_ARM64`, `LINUX_X64`, `LINUX_ARM64`, `OSX_X64`, and `OSX_ARM64` suffix, it requires a runner label, a lock-matching runner profile, a self-contained non-exportable-signing driver path, and that driver's reviewed lower-case digest through `SUNDER_EXECUTION_RUNTIME_RUNNER_<suffix>`, `SUNDER_EXECUTION_RUNTIME_RUNNER_PROFILE_<suffix>`, `SUNDER_EXECUTION_RUNTIME_RELEASE_DRIVER_<suffix>`, and `SUNDER_EXECUTION_RUNTIME_RELEASE_DRIVER_SHA256_<suffix>`.

Trusted raw 32-byte public keys are supplied from non-link runner directories named by `SUNDER_EXECUTION_RUNTIME_RELEASE_PUBLIC_KEYS` and `SUNDER_EXECUTION_RUNTIME_ATTESTATION_PUBLIC_KEYS`. Each file is `<locked-key-id>.ed25519.pub`; private keys are never present. `SUNDER_EXECUTION_RUNTIME_COORDINATOR_RUNNER` selects the centralized fan-in and publication runner. `SUNDER_CORE_REGISTRY_ORIGIN` is the shared canonical HTTPS origin with a terminal slash used for the Core catalog and runtime publication. The protected `sunder-execution-runtime-publication` environment supplies `SUNDER_EXECUTION_RUNTIME_REGISTRY_ADMIN_TOKEN` only after retained-input verification.

Each native lane builds twice, snapshots and SHA-256-binds its driver, cryptographically verifies both release and native-attestation signatures, and emits a receipt. Central fan-in independently reinspects the exact 12-artifact/six-RID set. It retains the archives, six native receipts, production lock, driver document, release-set receipt and source-built verifier as numeric draft-release assets with an exact run/attempt completion receipt. Native production receives no Registry credential and never publishes to Registry. One-day Actions artifacts are transport between jobs, not the durable handoff. A new native attempt reruns all lanes; fan-in never mixes outputs from different attempts.

After the approved catalog import/bootstrap, select the approved Core
`publicationEvidenceCommit` in `SUNDER_CORE_PUBLICATION_EVIDENCE_COMMIT`. It must
be a direct descendant of the artifact source, limited to verified baseline
publication fields and `eng/release/publication-evidence/*.json` receipt manifests.
The baseline's descriptor/history/freeze data must remain identical and its
publication content identity must match the source-derived canonical catalog.
Neither SDK nor runtime tag moves to this evidence commit.

Dispatch `sunder-execution-runtime-publication.yml` from protected `main` with
the exact runtime tag, numeric release ID, successful native run/attempt, artifact
source commit, publication-evidence commit, approved lock hash and
`publish-approved-retained-runtimes` confirmation. The workflow authenticates the
selected successful attempt, tag/source ancestry and every retained numeric asset
ID/size/hash. It uses the retained verifier to recheck signatures/attestations and
never rebuilds or resigns artifacts or the verifier, even when `main` advanced.
The publication receipt records artifact source, evidence descendant, native
attempt, and the separate publication workflow SHA/run/attempt.

`publish-release-set.sh` then uses the authenticated exact-publication lookup to create or resume each family. The deterministic stage ID commits the immutable release metadata plus the expected release-signing identity and per-RID native-attestation identities, including while no artifacts are present. The coordinator independently retries idempotent RID uploads and promotes each complete six-RID family in its own atomic Registry transaction. There is intentionally no 12-artifact transaction: interruption between the .NET and Node promotions is recovered by verifying and skipping an already-Supported exact family before continuing the other.

The `--output` path is both the durable coordinator journal and final receipt. The script writes it atomically with mode `0600` before the first Registry mutation and after every reconciled phase. A rerun may reuse that path, or recover after the file is lost, because the journal is never authoritative: Registry lookup must match the exact runtime ID, version, packaging revision, ABI, protocol, Host trust generation, all six deterministic idempotency keys, archive/manifest/content/attestation digests, and release/attestation key identities. Unknown create, upload, or promotion outcomes are resolved through that protected lookup; absent, mixed, deprecated, retired, revoked, or otherwise mismatched state fails closed. The administrator token remains only in `SUNDER_REGISTRY_ADMIN_TOKEN` and is never written to the journal.

`publication-dry-run.fixture.sh` exercises the closed credential-free plan and invokes `publication-resume.fixture.sh`. The resume fixture uses a deterministic mock Registry to lose successful create/upload responses, force a retryable RID failure, terminate immediately before and after each family promotion, recover with and without the local journal, verify exact Supported skips, and reject mixed state.

The shared native preflight defaults to `--phase acceptance`, preserving the
existing production-acceptance publication-approval requirement. The tag-native
workflow explicitly selects `--phase native`, requiring exact tag/source/lock
approval while leaving Registry approval to the later protected publisher.
See the [closed handoff contracts](../../scripts/release/RETAINED-HANDOFFS.md) for
receipt fields and recovery rules. Local mocked fixtures do not approve a real
production lock, signing operation, native runner, source/evidence commit or
Registry publication.

The repository build CLI accepts only a SHA-256-pinned signer adapter. It writes the signature input to adapter stdin and accepts exactly 64 raw signature bytes on stdout; the adapter receives the locked key ID but no private-key argument. Final output is exposed only after an independent archive snapshot verifies the release key, attestation key, identity, inventory, and hashes.

Those variables are configuration assertions, not proof generated by this repository. The approval references, runner images, OIDC/service policies, trusted public keys, platform certificates, notarization account, and publication authority all require review outside this codebase.
