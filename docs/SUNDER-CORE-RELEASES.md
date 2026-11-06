# Sunder Core Releases

> This describes the current branch. Audit historical releases against their own
> source, workflow and retained artifacts.

## Version streams

| Tag | Published output |
| --- | --- |
| `app/vX.Y.Z` | Bundled desktop App and Full-only Velopack update feeds |
| `cli/vX.Y.Z` | Standalone self-contained CLI |
| `host/vX.Y.Z` | Current-user Supervisor with its nested Runtime worker |
| `nuget-<package>/vX.Y.Z` | One public NuGet project |
| `npm-<package>/vX.Y.Z` | One public npm project |

Each developer package owns its version: `<Version>` in its `.csproj`, or `version`
in its npm `package.json`. The public inventory lists identities/paths, not a second
version ledger. Examples are `nuget-sunder-sdk/v1.2.0`,
`nuget-sunder-sdk-worker/v1.1.1` and `npm-younics-sunder-package-tool/v2.0.0`.
Historical combined `sdk/v*` releases remain recognizable by their package assets;
new publication does not create coordinated SDK tags.

Only the newest stable App release becomes GitHub `latest`. Package, CLI, Host and App
prerelease announcements use `make_latest=false`. npm consumer channels are
`latest` for stable versions and `next` for prereleases.

## Existing workflow checks

`sunder-sdk-release.yml` (**Sunder Core — Main CI and Releases**) owns
protected-main pushes and manual dispatch on `main`. It selects versions, calls
the reusable `sunder-ci.yml` with `scope: all`, stages tested archives, publishes
selected npm packages before NuGet packages, and finalizes their project releases
in the same run. SDK publication retains its main-workflow provenance identity.
Independent App selection starts its exact-tag release workflow and links that run
in the main summary. PRs, merge queues, weekly schedules and default manual
validation also use `all`.
Desktop build, test and installer failures therefore belong to App validation, while
SDK validation retains package-side Avalonia/bridge contracts and Host/CLI consumers.

### Configuration ownership

Routine source, package-version and toolchain changes do not require workflow edits:

| Owner | Configuration |
| --- | --- |
| Project files and public package inventory | Package identities, independent versions and dependency constraints |
| `eng/toolchains.json`, `global.json` | Development .NET/Node/npm versions; the setup action reads these inputs |
| `eng/ci/platforms.json` | Native RIDs, runner labels, signing runner exceptions and packaging-smoke platforms |
| `Directory.Packages.props`, `eng/release/tooling.json` | Velopack version and release-tool SDK/signing-client pins |
| `eng/test-oracles/ci-scenarios.json`, `template-smoke.json` | Required test scenarios and generated-template variants |
| `eng/test-oracles/core-test-policy.json` | Component scopes, test/lane ownership and expected skips |

`scripts/ci/core_ci.py` prepares source, templates and interop inputs using project
metadata and generated output identities. `scripts/release/` owns component packaging,
signing setup, DMG verification and Full-only feeds. Workflows retain job order,
permissions, protected environments, credential scopes, artifact transport and action
SHA pins. Test additions under a scenario's declared owner are discovered automatically.
Build roots, prerequisites and report audits use the same scope. Ordinary setup reads
only development toolchains; App packaging explicitly requests Velopack/signing metadata.

Validation retains:

- locked restore, ordinary parallel Release build, portable .NET/public API tests;
- Node SDK/tooling/artifact tests, then rebuilt outputs for .NET interop;
- exact selected NuGet/npm archive identity, contents and digest checks;
- generated .NET and Node/React template builds and Host lifecycle tests;
- universal six-RID modules and cross-language Host integration;
- supported-surface comparisons against the protected PR/pre-push Git base;
- release-script fixtures and actual TRX/JUnit report auditing.

Template tests combine the selected archives with published dependencies. Different
package versions and exact older SDK dependencies are not rewritten into one version.
An older authored version that was never published is not backfilled for testing.
The test feed is separate from the archives eligible for publication.

`sunder-host-validation.yml` provides native platform/process and App packaging
checks, including actual official .NET/Node installation and Worker launch on all
six RIDs for scopes containing Host consumers. App tags require App and Host
validation; Host tags use the Host scope, and CLI tags use the CLI scope.
CLI tags execute every produced self-contained binary.
Host tags exercise authenticated Supervisor/Runtime readiness and shutdown.

Supported RIDs are `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`
and `osx-arm64`. Executable tests use actual native architectures. Windows ARM64
App signing uses the x64 Azure signing client on Windows x64.

These checks do not fabricate deployed Registry recovery, whole-product performance
or production installer acceptance. Those retain their owning requirements. See
[the official runtime contract](OFFICIAL-RUNTIME-HANDOFF.md) and
[SDK compatibility](SUNDER-SDK-COMPATIBILITY.md).

## Publish developer packages

Change only the relevant project versions, review their real dependency constraints,
run `node scripts/release/check-sdk-version.mjs`, then commit and push `main`.
Dependencies and template dependency ranges are authored independently; increasing
a template's own version does not replace its dependency versions. Stable assembly
identity is reviewed separately from NuGet package versions.

| Project state | Publication action |
| --- | --- |
| Never published | Pack/test and publish |
| Authored version strictly exceeds the highest published SemVer | Pack/test and publish |
| Equal to or below the highest published SemVer | Skip publication for that project |

History comes from NuGet's version index, npm's complete version history and
published GitHub package archives, not mutable dist tags or exact-version absence.
For example, unpublished `1.9.0` is skipped after `1.10.0`. Prereleases participate
in SemVer precedence; build metadata cannot make a version newer. Source changes
without a version increase do not publish. No selected SDK versions means no SDK
release packing, tags, publishing login or registry writes; normal source validation
and independent App selection remain.
Only HTTP 404 means absent. Authentication, rate-limit and service failures stop.

The six public NuGets are `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, `Sunder.Sdk.Stacks`,
`Sunder.Sdk.Worker`, `Sunder.Package.Build` and `Sunder.Package.Templates`.
The npm packages are `@younics/sunder-sdk`, `@younics/sunder-package-tool` and `@younics/create-sunder-package`.
Pins remain owned by [the toolchain policy](package-toolchains.md).

### Tested-byte handoff

The generated plan captures the workflow source and selected project metadata.
CI packs selected projects once and consumes those archives in its artifact tests.
Release documentation is bound to the same committed source before packing, then
source inputs are restored. Publication never rebuilds, repacks or edits an archive.

After validation, staging creates immutable project tags and numeric GitHub drafts.
Each draft retains its tested archive, plan, checksums, provenance, SBOM and toolchain
record. `Sunder.Sdk.Stacks` also retains the source-exported
`sunder-platform-contracts.v1.json`. Catalog retention neither imports it into a
Registry nor grants Registry authority; no manually configured origin/catalog
receipt is needed to publish NuGet/npm packages.

Before every publication/finalization, the helpers verify actual remote bytes,
uploaded SHA-256 metadata, exact inventories, original source and remote tags.
Duplicate/case-colliding names, changed source and conflicting retries fail.
Source SHAs, asset IDs and hashes are derived provenance, not operator inputs.
See [retained handoffs](../scripts/release/RETAINED-HANDOFFS.md).

### Registry authentication

Use a main-only `sunder-core-release` environment. No per-release reviewer, manual
approval reference, compatibility-base commit, source SHA or asset locator is
required. Protect `main`, and protect `nuget-*/v*` and `npm-*/v*` against updates and
deletion while permitting the workflow token to create them. App/CLI/Host keep their
own restricted creation/signing policies. Keep release jobs serialized.

Package jobs need `contents: write` because GitHub hides drafts from read-only
tokens. Build/test and metadata evaluation receive no publication secrets. Only
the registry's publishing step receives its credential; finalization receives
neither registry credential.

#### NuGet Trusted Publishing

Create a [NuGet.org trusted publishing policy](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing):

| Field | Value |
| --- | --- |
| Repository Owner | `Younics` |
| Repository | `sunder-core` |
| Workflow File | `sunder-sdk-release.yml` (filename only) |
| Environment | `sunder-core-release` |
| Package Owner | The actual NuGet owner of the six public packages |

Permit pushing new packages and versions for the six exact names above, not
unlisting/relisting. Store `NUGET_USER` as an environment variable or existing
environment secret: it is the policy creator's individual NuGet username, not an
email address or organization name. The workflow prefers the variable.

After downloading retained bytes, the pinned official `NuGet/login` action exchanges
GitHub OIDC (`id-token: write`) for a short-lived key passed only to the publish step.
There is no stored-key fallback. A successful `dotnet nuget push` completes the
publication step; NuGet validation, signing and indexing continue asynchronously.
The pipeline does not wait for registry downloads or perform post-publish readback.
Revoke any old long-lived publishing key only after a successful replacement.
Local tests do not prove OIDC exchange or production publication.

#### npm token authentication

Store `NPM_TOKEN` in `sunder-core-release` (or the existing repository secret).
The npm account needs publishing rights to all three packages in the `@younics`
organization scope. Use a granular token with package write permission
and 2FA bypass for unattended CI. Initial package creation may require broader
package access; restrict it to the three exact names after successful bootstrap.
Do not grant organization-management permissions. Rotate before expiry.

The token is passed as `NODE_AUTH_TOKEN` only to npm publication.
`id-token: write` supplies Sigstore provenance, not npm authentication. No npm
trusted-publisher setup or placeholder publication is required.

For a missing selected version, preflight validates the archive, protected-main
workflow and provenance context, authenticates with `npm whoami`, then performs
`npm publish --dry-run` with scripts disabled. Actual publication uses public access,
provenance and the final consumer channel: `latest` for stable versions, `next` for
prereleases. A successful publish completes the step without registry polling,
reinstallation, signature/attestation readback or a separate channel-promotion phase.
A pre-publish version-history lookup skips existing versions and refuses to backfill
below a newer publication. Publishing failures propagate immediately.
Automatic npm provenance names this repository, main workflow and its source commit.

The finalization job requires successful publishing jobs (or no selected packages
for that registry), then publishes the retained GitHub drafts. It checks their
original artifact evidence and tags, without querying npm or NuGet again. A green
release means the registries accepted the uploads; package availability can follow
later as each registry finishes processing.

### Recovery and historical cleanup

Prefer **Re-run failed jobs** on the original run. Preserve successful build/stage
artifacts. A new dispatch of the same protected-main source can recover a fully
staged draft batch from retained releases, skipping builds. Incomplete staging
requires the original failed job and its tested Actions artifact; missing/conflicting
bytes never permit a rebuild under the same published version.

A later protected-main commit can also recover a complete staged batch when its
package identities/versions still match the retained plan and the original source
is an ancestor of the workflow commit. This lets a publishing-tool fix resume an
interrupted release. The workflow fetches full history, reuses the original plan,
archives, tags and evidence, and reports both artifact and workflow source commits.
It does not pack or stage replacement artifacts. Versions already in the registry
are skipped; finalized GitHub releases are also skipped even if registry indexing
has not caught up. Remaining NuGets are uploaded from the retained archives.

Unpublished npm archives still require their original source workflow: npm's
automatic provenance binds the publishing workflow commit. Later-source recovery
can skip already-published npm versions, but cannot sign an older tarball as if it
were built from the new workflow source. For a batch whose npm publication already
succeeded, the corrected workflow can finish the outstanding NuGets and drafts.

npm and NuGet are not a cross-registry transaction. A failure can leave a published
prefix and private GitHub drafts. On retry, a pre-publish metadata lookup skips
versions already present; NuGet also accepts an exact-version duplicate push via
`--skip-duplicate`. An unsuccessful command fails the run, including an ambiguous
lost acknowledgement; retry after the registry has processed the upload. There is
no registry-availability waiting loop. Do not move a tag or clobber an asset to
recover. Investigate irrecoverable conflicts and use a new authored version where
necessary.

Historical releases/tags are not automatically deleted. Cleanup follows successful
replacement publication and explicit confirmation of the deletion scope.

If an initial publication fails because its npm scope is unavailable, a rename
requires freshly built and tested archives. Retained plans, archives and provenance
must not be relabeled. The unchanged NuGet versions can still collide with the old
batch's drafts/tags: recovery requires matching package metadata and a complete
retained batch from an ancestor of the workflow source.
Before restarting those unpublished versions, retain the failed batch's assets and
digests, verify absence in both registries, and explicitly approve removal of the
conflicting unpublished drafts/tags. Any already-published version keeps its original
bytes and source; use a new authored version for changed payloads.

## Publish App, CLI or standalone Host

The App authors its independent `<Version>` in
`languages/csharp/dotnet/host/Sunder.App/Sunder.App.csproj`. The selector requires
one unconditional literal version and checks its evaluated Release value; the
.NET SDK's implicit `1.0.0` default is not a release version owner.

On protected-main pushes or dispatches, App selection compares that version with
published `app/v*` releases using SemVer. Stable releases ignore prereleases as
their baseline; prereleases also respect already published stable versions.
An established major may receive a newer maintenance release alongside a newer
major, but it cannot replace that newer stable App as GitHub `latest`. A previously
unpublished older major is not backfilled. Drafts do not count as publications.

App selection is independent of SDK selection/publication and SDK artifact recovery.
Main creates the exact App tag at its source commit, then explicitly dispatches
`sunder-app-release.yml` on that tag. A `GITHUB_TOKEN`-created tag push alone does
not trigger another workflow. Dispatch needs `actions: write`; tag creation and
draft discovery need `contents: write`. Tag creation policies must permit this
workflow token to create `app/v*`, while preventing tag updates/deletion.
The workflow file path stays stable for existing npm provenance.

The main summary reports **App release started** and links the App run; it does
not report completed App publication. The App run independently validates its
authored version, exact tag/checkout/workflow source, and (for dispatch) main
ancestry, then validates App/Host, packages six RIDs and publishes verified assets.
Stable versions use the `stable` Velopack channel; prereleases use `beta`.
The App summary reports the final publication result and installer download link.

Already-published App versions are skipped. An existing matching ancestor tag
keeps its original source; an active run is linked, and a failed run is resumed
with **Re-run failed jobs**. A draft's retained provenance identifies its original
run. Recovery requires unexpired original platform artifacts (retained for 90
days); source, inventory and invocation conflicts fail rather than replacing bytes.
A package-job retry after successful artifact upload reuses that verified artifact,
including failures in later cleanup/reporting. Unacknowledged dispatches reconcile
against exact-tag/source runs before another dispatch. App release promotion is
serialized so maintenance releases cannot race a newer stable `latest` selection.

Manual exact-tag App pushes remain supported. After authorization, create and push
`app/vX.Y.Z` (matching its authored version), `cli/vX.Y.Z` or `host/vX.Y.Z`.
Their tag workflows validate the actual native outputs before announcement, using
`sunder-github-release-promotion`. The App additionally uses `sunder-app-signing`.
CLI/Host archives have no OS signing step and must not be described as signed.

The App workflow produces Full-only Velopack channels such as `app-win-x64-stable`:
signed Windows Setup executables, unsigned Linux AppImages and signed/notarized
macOS drag-to-install DMGs. The Host archive contains `Sunder.Host.Supervisor` and
`RuntimeHost/Sunder.Runtime.Host`, distinct from the development Runtime fallback.

## App Bundle Versions

The App contains CLI, Supervisor and Runtime from its own source commit. On first
use it copies the Host bundle into a stable, versioned current-user payload directory.
Standalone CLI/Host versions remain independent.

Strict packaging imports no mutable Velopack history and rejects Delta packages.
The release contains six Full nupkgs, six channel manifests, two Windows Setups,
two Linux AppImages and two macOS DMGs. Portable ZIPs are local macOS intermediates
only. Install scripts check checksums and platform signatures. Updates install no
privileged Host companion; protected App directories can still require OS authorization.

## App Signing And Notarization

Configure `Younics/sunder-core` → Settings → Environments → `sunder-app-signing`.
Windows values may be variables or secrets (`vars` wins); Apple material must be
environment secrets. Workspace signing Terraform provisions Azure account/OIDC
trust separately and is not run during release.

Windows: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
`AZURE_TRUSTED_SIGNING_ENDPOINT`, `AZURE_TRUSTED_SIGNING_ACCOUNT_NAME` and
`AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME`. Azure OIDC supplies signing access;
no private key/PFX is stored in GitHub. Optional `WINDOWS_PUBLISHER_SUBJECT` adds an
exact certificate-subject check; omitting it does not disable signature validation.
Protect the signing environment with reviewers and restricted release tags.

Apple: `APPLE_BUILD_CERTIFICATE_BASE64`, `APPLE_CERTIFICATE_PASSWORD`, `APPLE_ID`,
`APPLE_APP_SPECIFIC_PASSWORD`, `APPLE_TEAM_ID`, `APPLE_SIGNING_KEYCHAIN_PASSWORD` and
`APPLE_SIGN_APP_IDENTITY`. Use Developer ID Application with `com.younics.sunder`.
Packaging signs/notarizes/staples the App, then packages those unchanged bytes in
a signed/notarized/stapled DMG. No Installer certificate is required;
`APPLE_INSTALLER_CERTIFICATE_BASE64` and `APPLE_SIGN_INSTALL_IDENTITY` are not prerequisites.
