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

SDK version selection runs automatically on `main`; it creates the SDK tag and
dispatches the existing integrated tag workflow. Other component tags still start
their workflows directly. Prereleases are supported. Only a stable App release becomes GitHub
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

CI compares each authored package version before release artifact work and rechecks
immediately before packing. Only packages newer than their own registry history get
`.nupkg`/`.tgz` output; an unchanged/older version creates no release archives or
candidate-feed upload. Source compilation/tests (including their temporary test
fixtures), native validation and cross-language tests still run. Archive-dependent
template/Host smoke checks run for a complete newly packed candidate; a partial
package selection does not satisfy or bypass the existing full release qualification.

CI also runs on the weekly schedule, manual dispatch and SDK tags. On those runs,
the final report audit consumes the successful owning jobs' TRX/JUnit reports without
rebuilding or rerunning suites; the App web-bridge VM tests are required in the
main job. Superseded PR runs are cancelled without cancelling release runs.
`sunder-host-validation.yml` is a reusable native lane: platform/process
and App bundle tests on Ubuntu, Windows and macOS; actual official .NET/Node
installation and Worker launch on all six RIDs; exact native Worker archive
installation; Linux AppImage, macOS DMG layout and Windows script checks.

App and Host tags depend on native validation. Host tags disable the reusable
lane's App bundle/installer checks; Core CI and App tags keep them enabled.
CLI tags run the CLI suite and
execute every produced binary for version/text-help/JSON-help checks. Host tags
launch the produced Supervisor and nested Runtime with isolated state, require
authenticated readiness, reject unauthenticated access, and stop the Runtime.

The supported RIDs are `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`
and `osx-arm64`. Native ARM64 and Intel runners are used for executable tests;
Windows ARM64 App signing uses the x64 Azure signing client on Windows x64.

## Publication, Acceptance, And Promotion

Verification and promotion are stages in the component tag workflow. There is no
separate acceptance/promotion dispatch or externally supplied acceptance executable.
Protect all four tag streams against updates/deletion. App/CLI/Host creation remains
restricted; SDK tag creation must permit the selector's `GITHUB_TOKEN`. SDK helpers
independently require the protected tag's exact workflow/source to belong to `main`.
Configure the referenced protected environments before release.

`scripts/release/github_release.py` owns draft discovery, numeric-ID transport,
exact component inventories, checksum/provenance/SPDX verification and finalization.
It downloads actual remote bytes, requires uploaded SHA-256 metadata, and rejects
duplicate names/IDs, changed source, unexpected assets and mismatched retries.
SDK tag runs share `sunder-release-sdk` to serialize publication and channel updates;
other components use `sunder-release-<tag>`. SDK branch selectors have their own slot.
No release asset is clobbered and failed drafts remain available for recovery.
Partial evidence uploads preserve retained bytes and the original invocation.
Finalization checks the remote tag and asset identities again before its numeric PATCH.

Build/test jobs receive no publication credentials. SDK credentials remain separated:

- `sunder-core-release`: the one-time Registry origin, `NUGET_USER` (a variable or
  existing environment secret), and `NPM_TOKEN`. SDK upload/promotion/finalization
  jobs use this environment, with `sdk/v*` allowed and no per-release review. The
  NuGet job uses `id-token: write` to obtain a short-lived NuGet
  credential, with no stored NuGet publish key, GitHub contents-write or npm credential.
- npm publication: the granular `NPM_TOKEN`, supplied as
  `NODE_AUTH_TOKEN` only to the preflight/publish step; `id-token: write` supplies
  provenance, not npm authentication. No NuGet key or GitHub contents-write permission.
- npm promotion: the same `NPM_TOKEN`, supplied only to the
  consumer-channel step. The token can publish and move channels; the protected job
  controls workflow ordering, not the token's registry capabilities.
- SDK finalization: GitHub contents-write permission, with no
  npm, NuGet, signing or deployment credential.
- Other components retain `sunder-github-release-promotion`; `sunder-app-signing`
  retains its separate Windows/Apple signing configuration and approvals.

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
The workflow-pinned Velopack CLI uses separate channels such as `app-win-x64-stable` and
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

Push the coordinated authored version to `main`. `sdk_release.py` compares each
package's authored version at that commit with its **highest published SemVer**:

| Package state | Release artifact action |
| --- | --- |
| Never published | Build/pack, then qualify and publish |
| Authored version greater than every published version | Build/pack, then qualify and publish |
| Authored version equal to or older than the highest published version | Skip build/pack/release/publish work for that package |

The comparison uses the full NuGet version index (including unlisted versions) and
npm's version history, not `latest`/`next` aliases or exact-version absence. Prereleases
participate in SemVer ordering; build metadata does not make a version newer.
For example, an unpublished `1.9.0` is skipped if `1.10.0` is already published.
Source changes without a version increase do not trigger package publication.
When no packages are newer, no new SDK tag, release build or publishing login is
started; this does not depend on an old GitHub release retaining all its assets.
Ordinary source CI continues to run.

For a new version the selector creates `sdk/v<version>` at that commit and explicitly
dispatches `sunder-sdk-release.yml` at the tag. No manual SDK tag is needed. This
explicit dispatch is necessary because tag pushes using `GITHUB_TOKEN` do not start
another workflow. The dispatched pipeline records the real tag/event context; it
does not impersonate a tag-push event.

An active version is left running. A failed version resumes **only failed jobs in
its original run**, preserving successful build/stage outputs and the original
source/run identity. An existing tag is never advanced to a later `main` commit.
Recovery of a partially published version requires its complete retained candidate;
it never starts another build. The same applies if all versions have uploaded but
the original draft still needs finalization. That is recovery of the original
release, not a new equal-version release. Superseded versions are not resumed.
Only HTTP 404 means absent; authentication, rate-limit and other registry failures
stop before publication. Npm channels never move to an older version.

The SDK source gate no longer reads `SUNDER_CORE_RELEASE_APPROVED`, its approval
reference, or a manually maintained artifact-source SHA. The clean checkout,
protected tag, remote tag and workflow SHA must agree, and the source must belong
to `main`. `SUNDER_CORE_REGISTRY_ORIGIN` remains one-time canonical configuration.
Release qualification also requires the protected environment's
`SUNDER_SDK_COMPATIBILITY_BASE_COMMIT`: an independent approved history ancestor,
with an actual ledger even for the first release.

**Readiness:** automatic selection is implemented, but compatibility-input
production and automatic supported-history confirmation/loading are not complete.
The selector rejects a new version before creating a tag when reviewed compatibility
recipes are absent. The qualification gate below is not bypassed; pushing the
current source is not yet an end-to-end publication guarantee.

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
Run `node scripts/release/check-sdk-version.mjs`, then commit and push `main`; the
automatically created SDK tag must equal that authored version.
Dependencies use NuGet `[X.Y.0,X.(Y+1).0)` and npm `>=X.Y.0 <X.(Y+1).0`.
Use a later-patch prerelease within an established minor line, not `X.Y.0-*`.
The current tools are npm `11.19.1` and Vite `8.2.0`. Exact SDK and Node/npm pins
are owned by [`eng/toolchains.json`](../eng/toolchains.json); see the
[toolchain policy](package-toolchains.md).

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

### Registry authentication

#### NuGet trusted publishing

Create a [NuGet.org trusted publishing policy](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
with these fields:

| NuGet field | Value |
| --- | --- |
| Policy Name | `sunder-core-sdk` (descriptive only) |
| Package Owner | `younics` |
| CI/CD Provider | `GitHub Actions` |
| Repository Owner | `Younics` |
| Repository | `sunder-core` |
| Workflow File | `sunder-sdk-release.yml` (filename only) |
| Environment | `sunder-core-release` |

Enable **Push**, select **Push new packages and package versions**, and leave
**Unlist or relist package versions** disabled. New-package permission is needed
for the first publication of SDK packages that do not yet exist. Use these exact
package names, one per line, rather than a broad wildcard:

```text
Sunder.Sdk
Sunder.Sdk.Avalonia
Sunder.Sdk.Stacks
Sunder.Sdk.Worker
Sunder.Package.Build
Sunder.Package.Templates
```

In **Younics/sunder-core → Settings → Environments**, create/protect
`sunder-core-release`, allow the `sdk/v*` release tags, and set its environment
variable `NUGET_USER` to the individual NuGet.org username that creates the policy
(not an email address or GitHub username). Use `younics` if that is the individual
login; if it is an organization, use the policy creator's individual member account.
An existing `NUGET_USER` environment secret is also supported, so it need not be
copied merely to change its storage type. The workflow prefers the variable when
both exist. Keep the Registry origin and compatibility configuration described
above; trusted publishing does not replace qualification.

The NuGet publication job downloads the retained release assets first, then uses
the commit-pinned official `NuGet/login` action immediately before publishing.
GitHub OIDC is exchanged for a masked temporary `NUGET_API_KEY`, valid for one hour,
passed only to the publish step. No stored `NUGET_API_KEY` secret or fallback is
used. Authentication failure stops publication; existing-version byte/signature
verification and immutable recovery are unchanged.

After a successful trusted-publishing release, revoke the old long-lived key on
NuGet.org and remove the obsolete GitHub `NUGET_API_KEY` secret. Creating the policy
or passing local workflow checks does not prove a successful OIDC exchange or
production publication.

#### npm access-token publishing

The three npm packages use a granular access token for both their first publication
and later versions; no npm trusted-publisher configuration or prior placeholder
publication is required. The npm account must have publishing rights to the
`@sunder` scope and be able to create the unscoped `create-sunder-package` name.
GitHub organization ownership does not confer npm scope ownership.

On **npmjs.com → Access Tokens → Generate New Token**, configure:

| Token setting | Value |
| --- | --- |
| Name | `sunder-core-publish` |
| Packages and scopes permission | **Read and write (publish and stage)** |
| Bypass two-factor authentication | Enabled for unattended CI publication |
| Packages | **All Packages** for initial creation, including the new unscoped name |
| Organization-management permissions | **No access** (not needed to publish) |
| Expiration | A short operational lifetime; rotate before expiry |

Enable account 2FA. Package publishing access must permit granular tokens with
2FA bypass; **Require two-factor authentication and disallow tokens** is incompatible
with this publication mode. Do not grant organization/team governance permissions.
After the first release, replace the bootstrap token with one restricted to exactly
`@sunder/sdk`, `@sunder/package-tool`, and `create-sunder-package`, then revoke the
broader token. Never paste a token into source, workflow YAML, or release evidence.

Store `NPM_TOKEN` in **Younics/sunder-core → Settings → Environments →
sunder-core-release**, or as a repository Actions secret. Both npm jobs use that
environment, so the existing environment secret works without duplication. If both
locations define it, the environment value wins. `NPM_DIST_TAG_TOKEN` and the
separate `sunder-sdk-npm-promotion` environment are no longer needed by SDK jobs.
Keep the SDK tag/source/compatibility gates.
The token is absent from build/test, toolchain-installation, artifact-download,
NuGet, and GitHub-finalization steps. `actions/setup-node` configures npm to read
`NODE_AUTH_TOKEN`; no literal token is written into the repository.

`preflight-npm-publishing.sh` validates all local archive identities, the SDK
tag/workflow/provenance context, existing public package identities, and token
authentication using `npm whoami`. HTTP 404 allows a first publication; other HTTP
failures stop before dry-runs. Missing versions get `npm publish --dry-run` with
public access, lifecycle scripts disabled, provenance, and the version-specific
`accept-v<version>` tag. Existing versions skip the conflicting dry-run but still
require exact byte/signature/provenance readback in `publish-sdk.sh`.

`id-token: write` remains enabled only on the npm upload job for GitHub/Sigstore
provenance. A valid token and local dry-run do not prove package write permission,
2FA bypass, or successful provenance generation; actual publication/readback is the
acceptance evidence. Retained candidate bytes and all release qualification gates
remain mandatory for first publications as well as later releases.

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

The next `main` push, or **Run workflow** on `main`, performs the same failed-job
resume automatically. Do not dispatch a fresh run at the existing SDK tag to recover
a partial publication; the prepare check rejects another run's retained candidate.

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

Configure **Younics/sunder-core → Settings → Environments → `sunder-app-signing`**.
Windows settings may be environment variables or secrets; the workflow reads `vars`
first and then `secrets`. Apple signing material must be environment secrets.
The private workspace's signing Terraform provisions the Azure account/OIDC trust;
it is not run during App release and does not replace these GitHub settings.

Windows environment values: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`, `AZURE_TRUSTED_SIGNING_ENDPOINT`,
`AZURE_TRUSTED_SIGNING_ACCOUNT_NAME`, and `AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME`.
Azure OIDC supplies signing access; no Windows private key/PFX is stored in GitHub.

`WINDOWS_PUBLISHER_SUBJECT` is optional. When supplied, the packaging script also
requires the signed installer's certificate subject to match it exactly. Leaving it
unset does not disable Azure signing or Authenticode signature validation. Existing
Azure settings are sufficient; no additional GitHub setting is needed. Protect the
environment with reviewers and restrict release tags.

Apple secrets: `APPLE_BUILD_CERTIFICATE_BASE64`, `APPLE_CERTIFICATE_PASSWORD`,
`APPLE_ID`, `APPLE_APP_SPECIFIC_PASSWORD`, `APPLE_TEAM_ID`,
`APPLE_SIGNING_KEYCHAIN_PASSWORD`, and `APPLE_SIGN_APP_IDENTITY`.
Use the Developer ID Application identity with bundle ID `com.younics.sunder`.
Packaging signs/notarizes/staples the App, then places those unchanged bytes into
the themed DMG and signs/notarizes/staples it. No Installer certificate is required.
Signing material belongs in environment secrets, never source control.

The current DMG flow does not use `APPLE_INSTALLER_CERTIFICATE_BASE64` or
`APPLE_SIGN_INSTALL_IDENTITY`; those settings are not prerequisites for this workflow.

Standalone CLI and Host workflows have no OS signing step; their checksum-verified
archives must not be described as signed releases.
