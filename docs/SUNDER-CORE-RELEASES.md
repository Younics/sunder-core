# Sunder Core Releases

> **Source channel:** This document describes release automation on the current branch. Use the same file from the tag being released when auditing a historical workflow.

Sunder Core component bytes are published to private GitHub drafts and, for SDK, immutable registries before they are separately promoted from the public `Younics/sunder-core` repository with GitHub Actions. Release validation differs by tag stream; this document describes the checks that currently exist rather than promising platform CI that is not configured.

## Version streams

The Sunder Core repository has separate version streams:

- `app/vX.Y.Z` publishes the bundled Sunder desktop app.
- `cli/vX.Y.Z` publishes the standalone CLI.
- `host/vX.Y.Z` publishes the standalone current-user Host: the `Sunder.Host.Supervisor` public gateway and its nested `Sunder.Runtime.Host` Runtime worker.
- `sdk/vX.Y.Z` publishes six coordinated NuGet packages and three coordinated npm packages for the public SDKs, build tooling, and templates. Stable tags also export and retain the canonical Core catalog and its closed numeric-asset verification receipt from that exact approved source.
- `runtime/vX.Y.Z` builds and retains a reviewed signed six-RID runtime set on a private numeric draft. A separate protected dispatch publishes those unchanged archives through Registry.

Prerelease tags such as `app/v0.1.0-beta.1`, `cli/v0.1.0-beta.1`, `host/v0.1.0-beta.1`, `sdk/v1.1.1-beta.1`, and `runtime/v1.0.0-beta.1` are supported. Component tags become GitHub prereleases only when explicitly promoted; runtime tags remain dedicated evidence identities. An SDK `X.Y.0-*` tag is rejected because each coordinated minor dependency range starts at stable `X.Y.0`; use a later patch prerelease within an established minor line.

## Existing Workflow Checks

The PR and `main` SDK CI workflow restores, builds, and runs `dotnet test Sunder.Core.slnx` on Ubuntu. It also runs a CLI help smoke test and creates, restores, builds, and packs template variants on Ubuntu. This is not a cross-platform test matrix.

The App packaging CI validates bundled output plus launcher and local-state behavior on pinned Ubuntu, Windows, and macOS images. It also syntax-checks the release/install scripts and creates and inspects an unsigned fixture DMG on macOS. It does not sign or release the App.

The Node CI installs the exact npm version declared by `languages/typescript/node/package.json` before every `npm ci`, runs `npm ls --all` as a lockfile/graph integrity gate, and tests the TypeScript SDK, tooling, and templates. It builds RID-neutral CommonJS Worker modules for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`, aggregates those six exact target records into one universal package, and validates the archive through the .NET CLI inspector. Managed launch smoke remains gated on a separately approved, signed Node execution-runtime release and is never replaced by invoking the CI runner's local Node.

The `sdk/v*` release workflow runs `Sunder.Runtime.Host.Tests` on Linux, Windows, and macOS, with external keystore tests enabled only on macOS. A second three-OS gate runs focused `Sunder.Package.Format` tests, consumes the packed `Sunder.Package.Build` and `Sunder.Sdk` NuGet artifacts in a clean package with a real Runtime target, and validates the exact archive through `sunder dev package validate`. `scripts/release/check-sdk-version.mjs` derives the NuGet range `[X.Y.0,X.(Y+1).0)` and npm range `>=X.Y.0 <X.(Y+1).0` from the production version, verifies that all six packable projects consume the coordinated metadata, and checks npm workspace manifests, templates, Vite, and the lockfile. The tag must match that metadata. Release MSBuild invocations then pass the verified version/range, using MSBuild's `%2C` command-line escape for the range comma, so project-specific fixture versions remain intact and project-reference dependencies pack with bounded ranges. The workflow also runs `Sunder.Package.Build.Tests`, `Sunder.Sdk.Worker.Tests`, `Sunder.Sdk.PublicApi.Tests`, dependency inspection, and only the template switches present in the released template.

The same SDK workflow installs the checker-reported exact `packageManager` npm before `npm ci`, runs `npm ls --all`, then tests, builds, and packs exactly three npm tarballs. It requires exact archive paths, package names, versions, npm-reported SHA-1/SHA-512 digests, MIT `LICENSE`/`NOTICE` files, public/provenance metadata, and coordinated template dependencies. A separate preflight first proves that all three real npm coordinates exist with the expected public repository identity, then proves that GitHub can exchange package-scoped npm credentials and repeats non-publishing local dry-runs. It does not write the registry and cannot prove that a real publication will succeed. The six NuGets, three npm tarballs, combined npm notices, and lockfile-derived Node dependency inventory are uploaded together and included in checksums/provenance/SBOM evidence.

The `app/v*` and `host/v*` tag workflows run `Sunder.Host.Supervisor.Tests` and `Sunder.Runtime.Host.Tests` on Linux, Windows, and macOS before packaging their release assets. The `cli/v*` workflow runs `Sunder.Cli.Tests` before packaging and executes each self-contained produced binary on its matching native architecture for version, text-help, and JSON-envelope smoke gates before archive creation. CLI and Host first validate committed non-RID lock files, then explicitly evaluate the transient per-RID restore graph with locked mode disabled only for that packaging restore; a RID cannot be added to the committed non-RID lock graph under NuGet locked mode. Do not treat a tag release as a replacement for the PR/main SDK CI coverage.

Release workflows pin third-party actions to full commit SHAs and disable persisted checkout credentials. Privileged SDK publication work is separated so the draft contents writer has no npm OIDC or NuGet key, the NuGet publisher has no contents write or OIDC permission, and npm OIDC jobs have no contents write or NuGet key. Promotion likewise separates the npm dist-tag credential from the job whose GitHub token can finalize a draft.

Protect `app/v*`, `cli/v*`, `host/v*`, `sdk/v*`, and `runtime/v*` with repository tag rulesets that restrict creation to release operators and block tag updates or deletion. Tag protection is repository configuration, not something a workflow file can establish. Before creating an SDK tag, require the tagged commit to be on protected `main` with successful Sunder Core CI and Sunder Node CI results, including the six-RID universal-package and .NET inspector gate; the tag-triggered SDK workflow does not replace that gate. Require separate approved execution-runtime release evidence before claiming managed Node launch coverage.

## Publication, Acceptance, And Promotion

Tag pushes perform immutable publication, not production acceptance or promotion. The `app/v*`, `cli/v*`, and `host/v*` workflows build and test once, create a private GitHub draft, verify every draft asset against the local bytes and release evidence, and archive those exact draft bytes as `sunder-<component>-publication-evidence-<numeric-release-id>-<originating-run-attempt>`. The `sdk/v*` workflow does the same, publishes and verifies the six immutable NuGet versions, and publishes the three immutable npm versions under the unique `accept-v<version>` dist-tag. It does not move npm `latest` or `next`. Primary package bytes and publication handoffs are retained for at least 90 days; an expired or missing handoff is a promotion blocker, not permission to rebuild an already-used version.

After production acceptance evidence is reviewed, explicitly dispatch `sunder-release-promotion.yml` with nine coordinates: component, numeric GitHub release ID, exact tag, exact 40-character publication source SHA, publication run ID and originating run attempt, production-acceptance run ID and run attempt, plus the exact 40-character production-acceptance workflow SHA. The publication source SHA identifies the released bytes; the workflow SHA independently identifies the trusted acceptance logic and may differ. There is no `push`, tag, `workflow_call`, or other automatic route into this workflow. The publication verifier checks the remote tag target, checked-out publication source, successful tag-push run and selected attempt, run-specific publication Actions artifact, numeric draft identity, exact asset inventory and bytes, and provenance fields for repository, release ID, workflow, tag, source, run, and attempt. Production acceptance is downloaded from the exact `production-acceptance-<release-id>-<run-id>-<run-attempt>-receipt.json` and its receipt-bound immutable files on that same numeric draft; the 90-day Actions artifact is only a redundant operational copy. A separate verifier requires an accepted decision and complete hash manifest that record `.github/workflows/sunder-production-acceptance.yml`, `refs/heads/main`, and the supplied workflow SHA. Both the successful run and selected attempt must report `head_branch=main` and that exact `head_sha`; reruns cannot silently switch workflow revisions. The decision must also contain distinct passed checks for released-byte provenance, production install, update, recovery, migration, and the component's native or browser production path. SDK promotion re-verifies npm bytes, signatures, provenance, and NuGet canonical entries and repository signatures before mutation. Promotion never rebuilds, packs, signs, or republishes an immutable version.

The dispatch-only `sunder-production-acceptance.yml` workflow is the sole producer for this evidence. It runs from protected `main`, re-verifies the exact publication before and after its six-RID native matrix, requires the approved production runtime lock and signer/runner gates, reproduces both locked runtime families, and accepts only hash-bound receipts from external native scenario drivers. Each reproducibility receipt binds the canonical native-output receipt and reviewed release-driver digest for its RID; the protected external coordinator must preserve both receipts in the final manifested archive and bind its own reviewed digest into the decision. The fan-in job alone receives GitHub release write credentials. It uploads attempt-specific immutable files and then uploads the receipt last as the completion marker; an equal same-attempt retry converges, while a new attempt uses new names and leaves incomplete older-attempt files harmless. It downloads and byte-compares the selected receipt-bound assets, then checks the complete publication-plus-acceptance draft inventory. Promotion selects one exact successful run attempt, downloads only that receipt-bound evidence, verifies GitHub digest metadata and bytes, and applies the same acceptance verifier while tolerating well-formed assets from prior attempts.

The workflow remains fail-closed until the real `eng/execution-runtimes/production.lock.json`, approved source/signing/runner/publication references, six native runner labels and matching profiles, non-exportable signer adapters, native runtime release drivers, six Core acceptance drivers, production Registry origin, and acceptance coordinator are configured. Every native release driver, Core acceptance driver, and acceptance coordinator must be one self-contained executable with its reviewed lower-case SHA-256 configured alongside its path. The workflow copies it into a non-writable temporary directory, rechecks the snapshot immediately before and after execution, passes the digest to the executable, and requires the resulting receipt to bind that digest. Native lanes receive no GitHub release, npm, NuGet, or Registry publication/promotion credential. The protected `sunder-core-production-acceptance` environment must require reviewers; runner labels used by `runs-on` must be repository or organization variables because environment variables are not available while GitHub selects a runner. External drivers obtain any required production access through approved workload identity, not repository-stored private keys. Missing infrastructure cannot be replaced by operator assertions, fixture locks, fabricated hashes, or tag-workflow checks.

### Execution-runtime publication evidence

The tag-triggered `sunder-execution-runtime-native-release.yml` verifies the exact approved `artifactSourceCommit`, shared with the Core SDK tag, plus the production-lock hash and numeric draft ID. Its six native lanes each emit exactly two locked `.sunderruntime` archives and one canonical native-output receipt. That receipt binds the production-lock hash, RID, archive identities, and the lower-case SHA-256 of the reviewed self-contained release driver. Fan-in preserves those original six receipts, independently reinspects every archive, byte-compares the regenerated receipts, and rejects digest substitution, omission, mixed drivers, partial RID sets, or any extra input. It retains all twelve archives and the source-built verifier under `runtime-native-<release-id>-<run-id>-<run-attempt>-*`; its receipt binds numeric asset IDs, sizes and hashes and is uploaded last. This workflow has no Registry credential or publication step.

`sunder-execution-runtime-publication.yml` is a separate protected `main` dispatch.
Its explicit coordinates select the runtime tag, numeric draft, successful native
run/attempt, artifact source, approved evidence-only direct descendant and exact
lock hash. It downloads and reverifies those retained bytes, uses the retained
verifier without rebuilding, and records the publication workflow's independent
`GITHUB_SHA`. Advancing `main` does not change the selected runtime bytes. Both
artifact tags remain at the artifact source rather than moving to the later
`publicationEvidenceCommit`. The [retained handoff contract](../scripts/release/RETAINED-HANDOFFS.md)
lists the exact new inputs and metadata.

Registry publication consumes one ordered six-RID release-driver document and the six original native receipts. The release-set receipt binds their hashes and all 12 archive identities. The publication journal additionally binds repository, source SHA, workflow path/ref/SHA, run and attempt, production lock, release-set hash, and every driver digest while retaining separate stage, validate, and promote progress for each runtime family. A retry verifies an existing family stage before skipping it and never treats one family's successful promotion as evidence for the other.

After Registry verification, a credential-separated job with `contents: write` and no Registry administration token archives the lock, driver document, six lane receipts, release-set receipt, Registry publication receipt, and deterministic evidence manifest on the source-bound numeric draft. Assets use the attempt-specific `execution-runtime-release-<release-id>-<run-id>-<run-attempt>-*` prefix. The `*-receipt.json` asset is uploaded last and is the only completion marker. Each asset is accepted only when GitHub reports the expected uploaded state, size, and SHA-256 and a downloaded copy compares byte-for-byte with the local snapshot. The Actions artifacts used between jobs expire after one day and are not release evidence.

For recovery, repeat publication against the same retained native coordinates, not a rebuild from current `main`. Equal existing assets are verified and skipped, missing assets are uploaded, an upload whose response was lost is reconciled from remote bytes, and a differing asset is never deleted or replaced. Runner loss can therefore leave only an incomplete prefix without a completion receipt; a same-attempt upload retry converges, while a later workflow attempt uses a new prefix and leaves the older incomplete prefix non-authoritative. A finalized or source-mismatched runtime destination is a hard failure. If the exact native lock, receipts, archives or source-bound draft cannot be recovered, stop rather than regenerating evidence or reusing Registry versions under different bytes.

Publication and promotion use the same `sunder-release-draft-<tag>` concurrency key. Draft discovery lists authenticated releases because GitHub's release-by-tag endpoint does not resolve drafts, then every read, upload, verification, and final PATCH uses the numeric release ID. A retry reuses only a complete byte-identical draft; it never replaces an asset. Cleanup deletes only a draft newly created by the failing run before verification, never a pre-existing or verified draft. Immediately before finalization, promotion re-fetches and re-verifies the numeric release and complete asset set.

Repository administrators must configure both referenced environments with required reviewers:

- `sunder-sdk-npm-promotion` contains only `NPM_DIST_TAG_TOKEN`, a short-lived granular token scoped to the three coordinated npm packages. npm trusted-publisher OIDC authorizes publication but not `npm dist-tag`, so this separate credential is required to move `latest` or `next`.
- `sunder-github-release-promotion` contains required-reviewer protection and the environment-only `SUNDER_GITHUB_PROMOTION_GUARD` secret so an accidentally auto-created empty environment fails closed. It contains no npm, NuGet, signing, or deployment credential. Its job receives only `contents: write` plus read access needed to retrieve the exact publication and production-acceptance evidence.

For SDK, the first protected job moves `latest` for a stable version or `next` for a prerelease and verifies each coordinate before advancing. A second protected job, which has no npm credential, verifies the aggregate channel again and finalizes the SDK draft. App promotion finalizes its draft and makes a stable App release GitHub `latest`; App prereleases do not move `latest`. CLI and Host promotion only finalize their exact drafts. Finalization sends an explicit `make_latest=false` for SDK, CLI, Host, and App prereleases; only stable App sends `make_latest=true`.

## Publish App

After explicit authorization, create and push an App SemVer tag to start private publication:

```powershell
git tag app/v0.1.0
git push origin app/v0.1.0
```

The App release workflow packages these runtimes:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

The App release uses Velopack. Each runtime uses its own App Velopack channel to avoid release-feed collisions, for example `app-win-x64-stable` and `app-osx-arm64-stable`. The workflow requires Windows signing configuration for `Setup.exe` and macOS signing/notarization configuration for drag-to-install DMGs; Linux publishes unsigned AppImages. The DMG Finder window uses the Sunder graphite-and-amber theme and contains `Sunder.app` plus an Applications shortcut.

When the required GitHub environment values and secrets are configured, the workflow signs Windows App assets with Azure Trusted Signing. It signs and notarizes macOS assets with an Apple Developer ID certificate using bundle id `com.younics.sunder`. The workflow fails rather than publishing unsigned Windows or macOS App assets when that configuration is missing. Linux AppImages are intentionally unsigned.

## Publish CLI

After explicit authorization, create and push a CLI SemVer tag to start private publication:

```powershell
git tag cli/v0.1.0
git push origin cli/v0.1.0
```

The CLI workflow publishes self-contained artifacts for these runtimes:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Windows CLI artifacts are `.zip` files. Linux and macOS CLI artifacts are `.tar.gz` files. The produced-binary gate uses native Windows ARM and macOS Intel runners rather than assuming an x64/ARM64 artifact can be validated by a different architecture.

## Publish Standalone Host

After explicit authorization, create and push a standalone current-user Host SemVer tag to start private publication:

```powershell
git tag host/v0.1.0
git push origin host/v0.1.0
```

The standalone Host workflow publishes self-contained archives for these runtimes:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Each archive contains the public current-user Host gateway (`Sunder.Host.Supervisor`) and its nested Runtime worker (`RuntimeHost/Sunder.Runtime.Host`). Windows Host artifacts are `.zip` files. Linux and macOS Host artifacts are `.tar.gz` files. This distribution is not the standalone Runtime development fallback.

## Publish SDK

The protected `sunder-core-release` source gate requires
`SUNDER_CORE_RELEASE_APPROVED=approved`, an HTTPS
`SUNDER_CORE_RELEASE_APPROVAL_REFERENCE`, the exact
`SUNDER_CORE_ARTIFACT_SOURCE_COMMIT`, and `SUNDER_CORE_REGISTRY_ORIGIN`.
Stable SDK releases run the existing engineering `platform-contracts check` and
`export` commands against that clean tag source. They retain
`sunder-platform-contracts.v1.json` and
`sunder-platform-contracts.receipt.v1.json` on the same numbered SDK draft,
including them in the existing checksums, provenance and SBOM. The receipt binds
the catalog's numeric asset ID, size/hash, selected Registry origin, frozen
baseline hash, workflow/run attempt and complete catalog inventory hash. These
local-content/release identities authorize no Registry mutation by themselves;
administrator bootstrap/import and later publication-evidence verification remain
separate. SDK prereleases retain their existing package flow and do not issue a
stable platform catalog.

### Prepare the version

Set the coordinated production version in `languages/csharp/dotnet/Directory.Build.props`, update its bounded minor range and SDK assembly version, and stamp the npm workspace/package/template metadata and `languages/typescript/node/package-lock.json` in the same source change. Internal npm and generated-template dependencies use `>=X.Y.0 <X.(Y+1).0`; the current toolchain pins `packageManager` to npm `11.19.0` and Vite to `8.2.0`. Do not edit only the workflow or rely on an old `1.1.0` constant. Verify the complete source stamp before tagging:

```powershell
node scripts/release/check-sdk-version.mjs
```

The SDK tag must exactly match the reported `version`. The checker also reports the derived NuGet/npm ranges and exact npm/Vite versions consumed by CI. The release workflow runs the same check against the tag and fails before package construction when any production metadata is inconsistent.

After explicit authorization, create and push an SDK SemVer tag to start immutable publication:

```powershell
git tag sdk/v1.1.0
git push origin sdk/v1.1.0
```

The `sdk/v*` workflow places these coordinated packages in the private GitHub draft and publishes their immutable versions to NuGet.org:

- `Sunder.Sdk`
- `Sunder.Sdk.Avalonia`
- `Sunder.Sdk.Stacks`
- `Sunder.Sdk.Worker`
- `Sunder.Package.Build`
- `Sunder.Package.Templates`

It also publishes these coordinated npm packages:

- `@sunder/sdk`
- `@sunder/package-tool`
- `create-sunder-package`

NuGet.org immutable publication remains in the tag workflow and requires the `NUGET_API_KEY` repository secret. Before use, CI validates the exact NuGet.org V3 package/publish/repository-signature endpoints, downloads every advertised repository certificate, verifies its SHA-256 fingerprint, and later requires the published package to validate against signer trust learned from that source. NuGet.org has no non-mutating API-key introspection endpoint, so the credential itself is proven only by a real missing-version publication. npm immutable publication also remains in `sunder-sdk-release.yml` because that filename is part of the trusted-publisher identity. Publication uses the version-unique `accept-v<version>` staging tag; only protected promotion later moves stable `latest` or prerelease `next`.

### Trusted Publishing

Each package name must already exist under the expected npm owner before its package settings can carry a trusted-publisher relationship. Coordinate bootstrap and trusted-publisher preflight are deliberately separate. If a coordinate is absent, `preflight-npm-trusted-publishing.sh` reports all missing names and exits with status `3` before requesting a GitHub OIDC token or running a publish dry-run. The coordinated workflow contains no placeholder, fake, or bootstrap publication path. Do not push the SDK tag until an npm owner has completed the initial legitimate publication through npm's supported authenticated process and reviewed the resulting package identity.

After all three names exist, configure each package's GitHub Actions trusted publisher with these exact values:

| npm field | Value |
| --- | --- |
| Organization or user | `Younics` |
| Repository | `sunder-core` |
| Workflow filename | `sunder-sdk-release.yml` |
| Environment | Leave empty unless the workflow is deliberately changed to use one |
| Allowed actions | Select `npm publish` |

The workflow filename field accepts only the filename, not `.github/workflows/sunder-sdk-release.yml`. npm does not validate these values when they are saved. npm CLI 11.15 or newer can configure the same relationship after interactive login and 2FA:

```powershell
npm trust github @sunder/sdk --repo Younics/sunder-core --file sunder-sdk-release.yml --allow-publish
npm trust github @sunder/package-tool --repo Younics/sunder-core --file sunder-sdk-release.yml --allow-publish
npm trust github create-sunder-package --repo Younics/sunder-core --file sunder-sdk-release.yml --allow-publish
```

Set each package's publication path to require 2FA and trusted publishing; do not retain a reusable npm publication token. The SDK publication workflow accepts no long-lived npm token. The separate promotion token is package-scoped, stored only in its reviewed environment, and used only for `npm dist-tag`. If any package is absent, has unexpected public identity, has incorrect repository/workflow trust, or cannot exchange a package-scoped OIDC token, `npm-preflight` fails before the draft or either registry publication. A successful preflight confirms established coordinate identity and OIDC exchange only; its `npm publish --dry-run` calls are local simulations and cannot prove that npm will accept, attest, or expose the real publish.

### Transaction and verification

The workflow creates one artifact set for all six NuGet and three npm packages. It performs npm OIDC preflight, creates and byte-verifies a draft GitHub Release, and processes the three npm packages in dependency order under `accept-v<version>`. Before advancing to the next coordinate, every existing or newly published package must pass registry-byte, digest, staging-tag, signature, provenance, workflow, ref, commit, and signing-certificate verification. An aggregate three-package verification repeats those checks as defense in depth before NuGet publication can begin. NuGet package-set verification also rejects generated DLL-matched XML documentation companions while permitting required package-control XML. Only after that real npm verification succeeds does it publish and verify the six NuGet packages. The terminal publication job depends explicitly on both registry jobs and leaves the GitHub Release private. No job combines contents write, npm OIDC, and the NuGet credential.

GitHub release verification downloads every draft asset and compares its actual bytes, size, API-reported SHA-256 digest, complete `SHA256SUMS` and provenance inventories, and SPDX file checksums with the local artifact set. NuGet package-set verification requires the six exact paths, identities, versions, repository URL and commit, and valid ZIP contents; publication verification then permits only repository-signature differences from those packed canonical entries and trusts signers advertised by NuGet.org rather than by the candidate package. npm verification requires exact tarball bytes, SHA-1/SHA-512 registry digests, metadata, dependency versions, and dist tags. `npm audit signatures --include-attestations` cryptographically verifies the provenance bundles; the release check then requires the signing certificate and signed SLSA statement to identify `Younics/sunder-core`, `.github/workflows/sunder-sdk-release.yml`, the exact `sdk/v*` ref, and the release commit.

### Partial-publish recovery

NuGet.org and npm do not provide a cross-registry transaction, and versions are immutable. A failure during npm publication can leave only a prefix of the three npm versions; NuGet has not started in that case. A failure after npm verification, during NuGet publication, leaves the complete verified npm set and possibly only a prefix of the six NuGet versions. This npm-first ordering avoids publishing NuGet when trusted npm publication fails, but it cannot make the two registries atomic. Once real npm publication starts, the workflow deliberately leaves the GitHub Release as a draft rather than deleting the recovery evidence.

Use GitHub's **Re-run failed jobs** on the same workflow run, tag, and commit; do not rerun successful packaging jobs after registry publication has started. Existing npm versions are downloaded and verified against the original bytes, staging tag, and required provenance before they are skipped; missing npm versions continue in dependency order. NuGet then applies the same verify-before-skip rule and continues missing versions in dependency order. The draft and handoff retain the originating run attempt in provenance even when a later attempt recovers those exact numeric-release bytes. Never rebuild a different artifact under an already-used version, move the tag, overwrite verified draft assets, or use promotion to repair failed publication. If an existing registry version fails byte/content/provenance verification, stop the release and investigate rather than continuing.

If the exact artifact set cannot be recovered, keep the GitHub draft private, unlist or deprecate the partial packages where registry policy permits, publish a new coordinated patch version, and record which immutable versions escaped. If immutable publication succeeded but promotion failed, rerun the failed protected promotion jobs with the same nine coordinates; every mutation is preceded by exact evidence verification and npm dist-tag movement is idempotent.

The release remains one `sdk/v*` version transaction as far as the external registries permit: exactly six coordinated NuGet versions, three coordinated npm versions, and one public GitHub Release from one commit.

## App Bundle Versions

The App bundle contains CLI and current-user Host outputs from the same commit as the App release. On first use of a Host version, the App copies the Host Supervisor and nested Runtime worker from `RuntimeHost/` into a stable versioned current-user payload directory. The App tag controls the Velopack app version. Standalone CLI and Host releases keep their own tags and GitHub Releases.

Release packaging validates the complete bundled App, Supervisor, Runtime worker, and CLI payload before platform packaging.

The workflow requires exactly two Windows Setup executables, two Linux AppImages, and two macOS DMGs. The macOS Portable ZIP is retained only as a local intermediate used to extract the Velopack-created App bundle; neither it nor a Setup PKG is published. Tag publication invokes `StrictRelease`/`--strict-release`, skips all mutable GitHub Velopack history, and rejects any generated Delta package. The six exact Full packages and channel manifests remain in the draft because application updates use them independently of the bootstrap artifacts.

The App workflow never replaces a stale draft. It resolves the authenticated numeric draft, reuses it only when its complete byte set and provenance match, otherwise fails closed, and creates a new draft only when none exists. A failed unverified draft is deleted only when the failing run created it; a pre-existing or verified draft is never deleted. Only `sunder-release-promotion.yml` can make the verified draft public and move GitHub `latest`. The publication workflow requires `WINDOWS_PUBLISHER_SUBJECT` and `APPLE_TEAM_ID` so it can pin the platform-validated signing identities. Public install scripts verify release checksums and platform signatures without installing a privileged Host companion. When a Windows publisher pin is not supplied to the install script, it displays the platform-validated publisher and requires interactive confirmation before execution. Without an Apple pin, the install script requires matching notarized Developer ID signatures and downloads the DMG without opening it. App updates do not install an elevated Host component; the operating system may still request authorization when the App's installation directory is not user-writable. The next App start activates the matching current-user Host payload.

## App Signing And Notarization

Do not commit certificates, private keys, provisioning material, app-specific passwords, or signing passwords to this public repository. Store signing material only in GitHub Actions secrets.

Required Windows App signing environment values:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_TRUSTED_SIGNING_ENDPOINT`
- `AZURE_TRUSTED_SIGNING_ACCOUNT_NAME`
- `AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME`
- `WINDOWS_PUBLISHER_SUBJECT`

The Azure values are produced by the private workspace Terraform setup in `infra/windows-signing`. All required values can be stored as GitHub environment variables for `sunder-app-signing`; no Windows private key, PFX file, or signing password is stored in GitHub. `WINDOWS_PUBLISHER_SUBJECT` must be the exact expected Authenticode publisher subject and is required even though Azure Trusted Signing itself does not require that separate verification pin.

Required macOS App signing and notarization secrets:

- `APPLE_BUILD_CERTIFICATE_BASE64`
- `APPLE_CERTIFICATE_PASSWORD`
- `APPLE_ID`
- `APPLE_APP_SPECIFIC_PASSWORD`
- `APPLE_TEAM_ID`
- `APPLE_SIGNING_KEYCHAIN_PASSWORD`
- `APPLE_SIGN_APP_IDENTITY`

The macOS identity secret should match the Developer ID Application certificate subject available after import, for example `Developer ID Application: Company Name (TEAMID)`. When those secrets are configured, Velopack signs, notarizes, and staples the App bundle. Release packaging then places that unchanged App in the themed DMG before signing, notarizing, and stapling the outer disk image. A Developer ID Installer certificate is not required because the workflow no longer creates a Setup PKG.

The CLI and standalone current-user Host workflows have no signing step. Their release archives are not signed by these workflows. Do not represent standalone CLI or Host artifacts as signed releases.
