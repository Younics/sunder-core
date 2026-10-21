# Sunder Core Releases

> **Source channel:** This describes the current branch. Audit historical releases
> against the workflow and scripts from their exact source tag.

## Version streams

| Tag | Published output |
| --- | --- |
| `app/vX.Y.Z` | Bundled desktop App and Full-only Velopack update feeds |
| `cli/vX.Y.Z` | Standalone self-contained CLI |
| `host/vX.Y.Z` | Current-user Supervisor with its nested Runtime worker |
| `sdk/vX.Y.Z` | Six NuGets, three npm packages, notices, and the stable Core catalog |

Each tag starts one integrated build, verification, publication and finalization
workflow. Prereleases are supported. Only a stable App release becomes GitHub
`latest`; SDK, CLI, Host and App prereleases explicitly use `make_latest=false`.
SDK npm consumer channels are `latest` for stable versions and `next` for prereleases.

## Existing Workflow Checks

`sunder-ci.yml` is the PR/main entry point and is reused by SDK releases. It owns:

- locked restore, ordinary parallel Release build, portable .NET tests and public API checks;
- Node SDK/tooling/artifact tests, then rebuilt Node outputs for .NET interop;
- the six-package NuGet inventory and exact npm archive/digest inspection;
- source catalog export and generated headless/Avalonia/no-view/Stack template builds;
- generated Node/React package lifecycle and universal six-RID module validation;
- required generated-template and cross-language Host tests with explicit result identities;
- release-script fixtures and compact TRX/JUnit result reports;
- supported-release history comparison against the protected Git base, retained
  input hashes and additive-compatible public API comparison (currently an
  explicit `UNFROZEN` / `UNRELEASED` ledger).

SDK tags additionally require a release-specific qualification bundle before
publication. The empty-ledger PR/dev success cannot satisfy that gate.

Its complete-report audit also runs on the weekly schedule, manual dispatch and
SDK tags. `sunder-host-validation.yml` is a reusable native lane: platform/process
and App bundle tests on Ubuntu, Windows and macOS; actual official .NET/Node
installation and Worker launch on all six RIDs; exact native Worker archive
installation; Linux AppImage, macOS DMG layout and Windows script checks.

App and Host tags depend on native validation. CLI tags run the CLI suite and
execute every produced binary for version/text-help/JSON-help checks. Host tags
launch the produced Supervisor and nested Runtime with isolated state, require
authenticated readiness, reject unauthenticated access, and stop the Runtime.

The supported RIDs are `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`
and `osx-arm64`. Native ARM64 and Intel runners are used for executable tests;
Windows ARM64 App signing uses the x64 Azure signing client on Windows x64.

## Publication, Acceptance, And Promotion

Verification and promotion are stages in the component tag workflow. There is no
separate acceptance/promotion dispatch or externally supplied acceptance executable.
Protect the four tag streams with rulesets restricting creation and blocking
updates/deletion. Configure the referenced protected environments before release.

`scripts/release/github_release.py` owns draft discovery, numeric-ID transport,
exact component inventories, checksum/provenance/SPDX verification and finalization.
It downloads actual remote bytes, requires uploaded SHA-256 metadata, and rejects
duplicate names/IDs, changed source, unexpected assets and mismatched retries.
The same `sunder-release-<tag>` concurrency group covers the whole workflow.
No release asset is clobbered and failed drafts remain available for recovery.
Partial evidence uploads preserve retained bytes and the original invocation.
Finalization checks the remote tag and asset identities again before its numeric PATCH.

Build/test jobs receive no publication credentials. SDK credentials remain separated:

- `sunder-core-release`: approved catalog/source coordinates and the NuGet publish key;
  the NuGet job has no GitHub contents-write or npm OIDC permission.
- npm publication: `id-token: write`, no NuGet key or GitHub contents-write permission;
  `.github/workflows/sunder-sdk-release.yml` remains its trusted-publisher identity.
- `sunder-sdk-npm-promotion`: only the package-scoped `NPM_DIST_TAG_TOKEN` for consumer channels.
- `sunder-github-release-promotion`: final GitHub contents-write permission, with no
  npm, NuGet, signing or deployment credential.
- `sunder-app-signing`: Windows and Apple signing configuration.

### Official installer/launcher qualification

Host obtains original Microsoft .NET 10 Runtime and Node 24 LTS archives through
compiled official-source policy and vendor checksums. Native tests require both
actual installer/launch cases to execute; zero matches or skipped required cases fail.
See [the official runtime contract](OFFICIAL-RUNTIME-HANDOFF.md) for installed
identity/provenance, private-root launch, offline reuse and rebind semantics.

CI results describe their actual scope. Deployed Registry policy/WORM recovery,
whole-product performance and production installer acceptance retain their owning
qualification requirements. They are not fabricated by a release receipt or by
running a source test suite. Agent native role capture and no-rebuild fan-in remain
owned by the Agent release pipeline.

## Publish App

After authorization, create and push `app/vX.Y.Z`. The workflow builds, signs and
notarizes, validates all six payloads and Full-only feeds, then publishes them.
Velopack `0.0.1298` uses separate channels such as `app-win-x64-stable` and
`app-osx-arm64-stable`. Windows produces signed Setup executables, Linux unsigned
AppImages, and macOS signed/notarized drag-to-install DMGs.

## Publish CLI

After authorization, create and push `cli/vX.Y.Z`. Native tests run each actual
self-contained binary before Windows ZIP or Unix tar.gz archive creation.

## Publish Standalone Host

After authorization, create and push `host/vX.Y.Z`. Each ZIP/tar.gz contains
`Sunder.Host.Supervisor` and `RuntimeHost/Sunder.Runtime.Host`. It is the public
current-user gateway distribution, distinct from the development Runtime fallback.

## Publish SDK

The `sunder-core-release` gate requires `SUNDER_CORE_RELEASE_APPROVED=approved`,
an HTTPS `SUNDER_CORE_RELEASE_APPROVAL_REFERENCE`, exact
`SUNDER_CORE_ARTIFACT_SOURCE_COMMIT`, and canonical `SUNDER_CORE_REGISTRY_ORIGIN`.
The clean source, protected tag, workflow SHA and remote tag must agree.
Release qualification also requires the protected environment's
`SUNDER_SDK_COMPATIBILITY_BASE_COMMIT`: an independent approved history ancestor,
with an actual ledger even for the first release.

The release consumes the tested `sunder-tested-sdk` artifact from the same run;
publication jobs never rebuild or repack it. Stable releases use the source-built
engineering tool's `platform-contracts check` and `export`, retaining
`sunder-platform-contracts.v1.json` and `sunder-platform-contracts.receipt.v1.json`
on the same numeric release. The closed receipt binds the catalog asset ID,
hash/size, source, baseline, inventory, selected Registry origin and run attempt.
Catalog retention does not grant Registry import or bootstrap authority.
See [source-bound handoffs](../scripts/release/RETAINED-HANDOFFS.md).

Before any draft creation or package publication, the SDK stage requires the
same-run artifact's `compatibility/qualification.json`, exact source/base manifests,
new candidate reference and retained inputs, plus independently collected owning-lane
execution artifacts. The integrated `compatibility-corpus` matrix runs reviewed
recipes on portable Linux and all six native RIDs. Its separate same-run artifacts
bind actual OS/RID, source/run/attempt, exact corpus/candidate bytes, recipe/command
and reports; required tests come from immutable reviewed recipes. This applies
to prereleases as well. `sunder-sdk-compatibility.tar` retains the validated bundle
through the existing numeric-release transport and evidence inventory. Every
publish/promote/finalize path rechecks the stage-pinned bundle and package bytes.
Real retained inputs and recipes with owning tests that consume the collector's
input context are still missing, so SDK tags fail with a missing-input diagnostic.
The collector/transport is implemented; no production execution evidence has been
fabricated. PR/dev CI still accepts the honest empty ledger.

### Prepare the version

Update `languages/csharp/dotnet/Directory.Build.props`, the bounded minor range and
the coordinated npm workspace/template/lockfile metadata. Review assembly identity
separately: compatible SDK patches preserve the stable contract assembly version;
do not mechanically change it with the package version.
Run `node scripts/release/check-sdk-version.mjs`; the SDK tag must equal its version.
Dependencies use NuGet `[X.Y.0,X.(Y+1).0)` and npm `>=X.Y.0 <X.(Y+1).0`.
Use a later-patch prerelease within an established minor line, not `X.Y.0-*`.
The current tools are npm `11.19.0` and Vite `8.2.0`.

The six NuGets are `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, `Sunder.Sdk.Stacks`,
`Sunder.Sdk.Worker`, `Sunder.Package.Build`, and `Sunder.Package.Templates`.
The three npm packages are `@sunder/sdk`, `@sunder/package-tool`, and `create-sunder-package`.

### Concrete V1 freeze inputs still required

V1 is independent of the SDK `1.1.x` and package-owned domain contract versions.
There is no version reset to `1.0.0`. Current snapshots and migration ledgers do
not prove release or retained-consumer compatibility. Before establishing the
first supported release reference:

1. Finish the coordinated API edits, regenerate only editable candidate snapshots
   in `sunder-public-api-v2` format, and run their owning tests. Capture the reviewed
   inventory: the nine current .NET API snapshots (SDK, Avalonia, Stacks, Worker,
   Build, Host/Runtime/Registry contracts and Package Format), Build fidelity,
   shipped Node declarations/exports/tooling, and versioned protocol/schema
   boundaries. The new semantic comparator covers .NET v2 declarations; other
   surfaces need their existing semantic tests or conservative exact-byte records.
2. Retain genuine tested **candidate** NuGets, npm archives, package archives,
   source/lockfiles, dependency closure and Host/App binaries, with real source
   commits, immutable retention identities, SHA-256 and sizes. Generate retained
   API references from those exact artifacts. A draft or same-run retained artifact
   is sufficient before publication; a published version is not a prerequisite.
   The new freeze-qualified reference lives in the qualification bundle, separate
   from the published ledger and without a fabricated publication status.
3. Capture reproducible consumer inputs/recipes for old-package/new-Host,
   old-source/new-SDK and newer-feature/older-Host. Extend and execute the owning
   source, process, App and native RID suites. For the first seed, use the genuine
   candidate baseline and unsupported-feature controls without claiming an earlier
    V1 release. Review immutable JSON recipes under
    `eng/compatibility/recipes/<reference-id>/<corpus-id>.json`, including required
    tests and owning source commands. The owned collector executes those recipes
    and retains actual passing TRX/JUnit results for the candidate and all supported
    history. Candidate manifests cannot choose passing selectors/reports. Current
    synthetic collector fixtures are not release evidence.
4. Provision the retained bundle for ordinary CI without publication credentials
   (checked-in small fixtures or immutable public/protected read-only transport),
   and retain an independent protected base manifest. The gate does not download
   artifacts or execute arbitrary recipes. Missing released inputs fail closed.
5. Run the mandatory release qualification using the closed receipt schema. It
   requires this version's own new retained reference, not just passing comparisons
   to earlier releases. After actual publication, `sdk_release_compatibility.py
   confirm` verifies the numeric public release and prepares an exact append to
   `eng/compatibility/supported-releases.json` plus separate publication evidence.
   Only this confirmed append justifies `FROZEN` / `RELEASED`. Review it as the
   direct untagged publication-evidence descendant; preserve the artifact tag and
   immutable qualified reference. Do not synthesize publication identities or
   rewrite the historical Core candidate, Stack or Agent migration oracles.

The [SDK compatibility policy](SUNDER-SDK-COMPATIBILITY.md) specifies the command,
trust anchor, comparison limits and corpus owners. Subsequent compatible Host/SDK
releases must retain the supported V1 baseline and pass all retained comparisons;
updating the current snapshot cannot erase that obligation. Registering corpus
recipes is input retention, not a passing behavioral result or release promotion.

### Trusted Publishing

All three real npm coordinates must already exist under the expected owner.
`preflight-npm-trusted-publishing.sh` reports missing coordinates with exit3 before
OIDC exchange; it never publishes placeholders. Configure each package with:

| npm field | Value |
| --- | --- |
| Organization/user | `Younics` |
| Repository | `sunder-core` |
| Workflow filename | `sunder-sdk-release.yml` |
| Environment | Empty (the npm publication job has no environment) |
| Allowed action | `npm publish` |

Require 2FA and trusted publishing for publication. The distinct granular
`NPM_DIST_TAG_TOKEN` is used only for dist-tag mutation in its protected job.
OIDC preflight and local publish dry-runs do not prove successful real publication.

### Transaction and verification

The workflow qualifies and retains the candidate compatibility reference, stages
a byte-verified GitHub draft, performs npm preflight, then
publishes npm packages in dependency order under `accept-v<version>`. Each existing
or new version must pass byte/digest, metadata, signature and provenance checks
before the next package proceeds. Aggregate verification precedes NuGet publishing.
NuGet verification allows only repository-signature differences from candidate
ZIP entries, using repository certificates learned from pinned NuGet.org endpoints.
The package inventory includes all seven Build task DLLs and rejects generated
DLL-matched XML documentation companions.

After both registries verify, the protected npm stage advances and verifies each
consumer channel. The final stage checks aggregate channels and the same source-bound
GitHub bytes before making the draft public. The npm signed provenance must identify
the exact repository, SDK workflow, tag and source commit.

### Partial-publish recovery

Use **Re-run failed jobs** on the same run/tag/commit. Keep successful candidate
builds and their artifacts. Existing registry versions are verified before being
skipped; missing versions resume in dependency order. Lost acknowledgements are
reconciled by immutable readback, not overwrite. npm and NuGet cannot form a
cross-registry transaction: failures can leave a published prefix with a private draft.

A channel/finalization failure resumes its failed jobs in the same workflow. If
original bytes cannot be recovered or an existing version differs, retain the draft
and use a new coordinated patch after investigating/unlisting/deprecating as appropriate.
Never move a tag or rebuild different bytes under an already-published version.

## App Bundle Versions

The App contains CLI, Supervisor and Runtime outputs from its own source commit.
On first use it copies the Host bundle into a stable, versioned current-user payload
directory. Standalone CLI/Host versions remain independent.

Strict packaging imports no mutable Velopack history and rejects Delta packages.
The release contains six Full nupkgs, six channel manifests, two Windows Setup
executables, two Linux AppImages and two macOS DMGs. Portable ZIPs are local macOS
intermediates only. Install scripts check checksums and platform signatures.
Updates install no privileged Host companion; OS authorization may still be needed
for a protected App directory. Next App start activates the matching Host payload.

## App Signing And Notarization

Windows environment values: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`, `AZURE_TRUSTED_SIGNING_ENDPOINT`,
`AZURE_TRUSTED_SIGNING_ACCOUNT_NAME`, `AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME`,
and exact `WINDOWS_PUBLISHER_SUBJECT`. Azure OIDC supplies signing access; no Windows
private key/PFX is stored in GitHub.

Apple secrets: `APPLE_BUILD_CERTIFICATE_BASE64`, `APPLE_CERTIFICATE_PASSWORD`,
`APPLE_ID`, `APPLE_APP_SPECIFIC_PASSWORD`, `APPLE_TEAM_ID`,
`APPLE_SIGNING_KEYCHAIN_PASSWORD`, and `APPLE_SIGN_APP_IDENTITY`.
Use the Developer ID Application identity with bundle ID `com.younics.sunder`.
Packaging signs/notarizes/staples the App, then places those unchanged bytes into
the themed DMG and signs/notarizes/staples it. No Installer certificate is required.
Signing material belongs in environment secrets, never source control.

Standalone CLI and Host workflows have no OS signing step; their checksum-verified
archives must not be described as signed releases.
