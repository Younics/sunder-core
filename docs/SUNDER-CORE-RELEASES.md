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
`nuget-sunder-sdk-worker/v1.1.1` and `npm-sunder-package-tool/v2.0.0`.
Historical combined `sdk/v*` releases remain recognizable by their package assets;
new publication does not create coordinated SDK tags.

Only a stable App release becomes GitHub `latest`. Package, CLI, Host and App
prerelease announcements use `make_latest=false`. npm consumer channels are
`latest` for stable versions and `next` for prereleases.

## Existing workflow checks

`sunder-sdk-release.yml` owns protected-main pushes and manual dispatch on `main`.
It selects versions, calls the reusable `sunder-ci.yml`, stages tested archives,
publishes selected packages and finalizes their project releases in the same run.
There is no tag redispatch, separate approval workflow or per-release configuration.
`sunder-ci.yml` also runs for PRs, merge queues, weekly schedules and manual source validation.

### Configuration ownership

Routine source, package-version and toolchain changes do not require workflow edits:

| Owner | Configuration |
| --- | --- |
| Project files and public package inventory | Package identities, independent versions and dependency constraints |
| `eng/toolchains.json`, `global.json` | Development .NET/Node/npm versions; the setup action reads these inputs |
| `eng/ci/platforms.json` | Native RIDs, runner labels, signing runner exceptions and packaging-smoke platforms |
| `Directory.Packages.props`, `eng/release/tooling.json` | Velopack version and release-tool SDK/signing-client pins |
| `eng/test-oracles/ci-scenarios.json`, `template-smoke.json` | Required test scenarios and generated-template variants |

`scripts/ci/core_ci.py` prepares source, templates and interop inputs using project
metadata and generated output identities. `scripts/release/` owns component packaging,
signing setup, DMG verification and Full-only feeds. Workflows retain job order,
permissions, protected environments, credential scopes, artifact transport and action
SHA pins. Test additions under a scenario's declared owner are discovered automatically.

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
six RIDs. App and Host tags depend on native validation; Host tags omit App
bundle/installer checks. CLI tags execute every produced self-contained binary.
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
without a version increase do not publish. No selected versions means no release
packing, tags, publishing login or registry writes; normal source validation remains.
Only HTTP 404 means absent. Authentication, rate-limit and service failures stop.

The six public NuGets are `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, `Sunder.Sdk.Stacks`,
`Sunder.Sdk.Worker`, `Sunder.Package.Build` and `Sunder.Package.Templates`.
The npm packages are `@sunder/sdk`, `@sunder/package-tool` and `create-sunder-package`.
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
There is no stored-key fallback. Readback permits only valid NuGet repository-signature
changes, using trust learned from pinned NuGet.org endpoints. Revoke any old long-lived
publishing key only after a successful replacement. Local tests do not prove OIDC
exchange or production publication.

#### npm token authentication

Store `NPM_TOKEN` in `sunder-core-release` (or the existing repository secret).
The npm account needs publishing rights to `@sunder` and the unscoped
`create-sunder-package` name. Use a granular token with package write permission
and 2FA bypass for unattended CI. Initial package creation may require broader
package access; restrict it to the three exact names after successful bootstrap.
Do not grant organization-management permissions. Rotate before expiry.

The token is passed as `NODE_AUTH_TOKEN` only to npm publication/channel promotion.
`id-token: write` supplies Sigstore provenance, not npm authentication. No npm
trusted-publisher setup or placeholder publication is required.

For a missing selected version, preflight validates the archive, protected-main
workflow and provenance context, authenticates with `npm whoami`, then performs
`npm publish --dry-run` with scripts disabled. Actual publication uses public access,
provenance and `accept-v<version>`. Exact bytes, metadata, digests, signatures and
attestations must verify before advancing its consumer channel. Existing uploads
are verified, not overwritten. A newer channel is never moved backwards.
Signed provenance must name this repository, main workflow and exact source commit.

### Recovery and historical cleanup

Prefer **Re-run failed jobs** on the original run. Preserve successful build/stage
artifacts. A new dispatch of the same protected-main source can recover a fully
staged draft batch from retained releases, skipping builds. Incomplete staging
requires the original failed job and its tested Actions artifact; missing/conflicting
bytes never permit a rebuild under the same published version.

npm and NuGet are not a cross-registry transaction. A failure can leave a published
prefix and private GitHub drafts. Lost acknowledgements reconcile by exact readback.
Do not move a tag or clobber an asset to recover. Investigate irrecoverable conflicts
and use a new authored version where necessary.

Historical releases/tags are not automatically deleted. Cleanup follows successful
replacement publication and explicit confirmation of the deletion scope.

## Publish App, CLI or standalone Host

After authorization, create and push `app/vX.Y.Z`, `cli/vX.Y.Z` or `host/vX.Y.Z`.
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
