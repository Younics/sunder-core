# Sunder Core Releases

> **Source channel:** This document describes release automation on the current branch. Use the same file from the tag being released when auditing a historical workflow.

Sunder Core component releases are published from the public `Younics/sunder-core` repository with GitHub Actions. Release validation differs by tag stream; this document describes the checks that currently exist rather than promising platform CI that is not configured.

## Version streams

The Sunder Core repository has separate version streams:

- `app/vX.Y.Z` publishes the bundled Sunder desktop app.
- `cli/vX.Y.Z` publishes the standalone CLI.
- `host/vX.Y.Z` publishes the standalone current-user Host: the `Sunder.Host.Supervisor` public gateway and its nested `Sunder.Runtime.Host` Runtime worker.
- `sdk/vX.Y.Z` publishes six coordinated NuGet packages and three coordinated npm packages for the public SDKs, build tooling, and templates.

Prerelease tags such as `app/v0.1.0-beta.1`, `cli/v0.1.0-beta.1`, `host/v0.1.0-beta.1`, and `sdk/v1.1.1-beta.1` are supported and are published as GitHub prereleases. An SDK `X.Y.0-*` tag is rejected because each coordinated minor dependency range starts at stable `X.Y.0`; use a later patch prerelease within an established minor line.

## Existing Workflow Checks

The PR and `main` SDK CI workflow restores, builds, and runs `dotnet test Sunder.Core.slnx` on Ubuntu. It also runs a CLI help smoke test and creates, restores, builds, and packs template variants on Ubuntu. This is not a cross-platform test matrix.

The App packaging CI runs on macOS when its packaging files change. It syntax-checks the release/install scripts and creates and inspects an unsigned fixture DMG. It does not build, sign, or release the App.

The Node SEA CI installs the exact npm version declared by `languages/typescript/node/package.json` before every `npm ci`, runs `npm ls --all` as a lockfile/graph integrity gate, and tests the TypeScript SDK, tooling, and templates. Its native matrix builds and protocol-smokes `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64` on matching runners; only those six native leaves can enter the universal-package fan-in gate.

The `sdk/v*` release workflow runs `Sunder.Runtime.Host.Tests` on Linux, Windows, and macOS, with external keystore tests enabled only on macOS. A second three-OS gate runs focused `Sunder.Package.Format` tests, consumes the packed `Sunder.Package.Build` and `Sunder.Sdk` NuGet artifacts in a clean package with a real Runtime target, and validates the exact archive through `sunder dev package validate`. `scripts/release/check-sdk-version.mjs` derives the NuGet range `[X.Y.0,X.(Y+1).0)` and npm range `>=X.Y.0 <X.(Y+1).0` from the production version, verifies that all six packable projects consume the coordinated metadata, and checks npm workspace manifests, templates, Vite, and the lockfile. The tag must match that metadata. Release MSBuild invocations then pass the verified version/range, using MSBuild's `%2C` command-line escape for the range comma, so project-specific fixture versions remain intact and project-reference dependencies pack with bounded ranges. The workflow also runs `Sunder.Package.Build.Tests`, `Sunder.Sdk.Worker.Tests`, `Sunder.Sdk.PublicApi.Tests`, dependency inspection, and only the template switches present in the released template.

The same SDK workflow installs the checker-reported exact `packageManager` npm before `npm ci`, runs `npm ls --all`, then tests, builds, and packs exactly three npm tarballs. It requires MIT `LICENSE`/`NOTICE` files, preserves non-publishing `npm publish --dry-run` checks, and verifies packed metadata and template dependencies against the derived npm range. A separate OIDC-only preflight proves that GitHub can exchange package-scoped npm credentials and repeats the local dry-runs; it does not write the registry and cannot prove that a real publication will succeed. The six NuGets, three npm tarballs, combined npm notices, and lockfile-derived Node dependency inventory are uploaded together and included in checksums/provenance/SBOM evidence.

The `app/v*` and `host/v*` tag workflows run `Sunder.Host.Supervisor.Tests` and `Sunder.Runtime.Host.Tests` on Linux, Windows, and macOS before packaging their release assets. The `cli/v*` workflow runs `Sunder.Cli.Tests` before packaging and executes each self-contained produced binary on its matching native architecture for version, text-help, and JSON-envelope smoke gates before archive creation. CLI and Host first validate committed non-RID lock files, then explicitly evaluate the transient per-RID restore graph with locked mode disabled only for that packaging restore; a RID cannot be added to the committed non-RID lock graph under NuGet locked mode. Do not treat a tag release as a replacement for the PR/main SDK CI coverage.

Release workflows pin third-party actions to full commit SHAs and disable persisted checkout credentials. Privileged SDK work is separated so the contents writer has no npm OIDC or NuGet key, the NuGet publisher has no contents write or OIDC permission, and npm OIDC jobs have no contents write or NuGet key.

Protect `app/v*`, `cli/v*`, `host/v*`, and `sdk/v*` with repository tag rulesets that restrict creation to release operators and block tag updates or deletion. Tag protection is repository configuration, not something a workflow file can establish. Before creating an SDK tag, require the tagged commit to be on protected `main` with successful Sunder Core CI and Sunder Node SEA CI results, including all six native RID smokes and the six-RID universal-package fan-in; the tag-triggered SDK workflow does not replace that native gate.

## Publish App

Create and push an App SemVer tag:

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

Create and push a CLI SemVer tag:

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

Create and push a standalone current-user Host SemVer tag:

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

### Prepare the version

Set the coordinated production version in `languages/csharp/dotnet/Directory.Build.props`, update its bounded minor range and SDK assembly version, and stamp the npm workspace/package/template metadata and `languages/typescript/node/package-lock.json` in the same source change. Internal npm and generated-template dependencies use `>=X.Y.0 <X.(Y+1).0`; the current toolchain pins `packageManager` to npm `11.19.0` and Vite to `8.2.0`. Do not edit only the workflow or rely on an old `1.1.0` constant. Verify the complete source stamp before tagging:

```powershell
node scripts/release/check-sdk-version.mjs
```

The SDK tag must exactly match the reported `version`. The checker also reports the derived NuGet/npm ranges and exact npm/Vite versions consumed by CI. The release workflow runs the same check against the tag and fails before package construction when any production metadata is inconsistent.

Create and push an SDK SemVer tag:

```powershell
git tag sdk/v1.1.0
git push origin sdk/v1.1.0
```

The `sdk/v*` workflow publishes these coordinated packages to GitHub Releases and NuGet.org:

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

NuGet.org publishing requires the `NUGET_API_KEY` repository secret. Stable npm releases use `latest`; prereleases use `next` and do not displace `latest`.

### Trusted Publishing

Each package name must already exist under the expected npm owner before its package settings can carry a trusted-publisher relationship. The coordinated workflow intentionally contains no placeholder, fake, or bootstrap publication path. If a real package does not yet exist, do not push the SDK tag: package ownership and the initial legitimate publication are external release prerequisites that must be resolved through npm's supported owner-authenticated process before this trusted-publishing workflow can run.

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

Set each package's publishing access to require 2FA and disallow tokens after trusted publishing is operational. The normal SDK workflow accepts no long-lived npm token. If any package is absent, its repository/workflow trust is wrong, or its trust cannot exchange a package-scoped OIDC token, `npm-preflight` fails before the draft or either registry publication. A successful preflight confirms OIDC exchange only; its `npm publish --dry-run` calls are local simulations and cannot prove that npm will accept, attest, or expose the real publish.

### Transaction and verification

The workflow creates one artifact set for all six NuGet and three npm packages. It performs npm OIDC preflight, creates and byte-verifies a draft GitHub Release, publishes the three npm packages in dependency order, and requires registry-byte, digest, dist-tag, signature, provenance, workflow, ref, and commit verification. Only after that real npm verification succeeds does it publish and verify the six NuGet packages. The final job depends explicitly on both registry jobs before making the GitHub Release public. No job combines contents write, npm OIDC, and the NuGet credential.

GitHub release verification downloads every draft asset and compares its actual bytes, size, API-reported SHA-256 digest, and `SHA256SUMS` entries with the local artifact set. NuGet verification permits only repository-signature differences from the packed canonical content. npm verification requires exact tarball bytes, SHA-1/SHA-512 registry digests, metadata, dependency versions, and dist tags. `npm audit signatures --include-attestations` cryptographically verifies the provenance bundles; the release check then requires the signing certificate and signed SLSA statement to identify `Younics/sunder-core`, `.github/workflows/sunder-sdk-release.yml`, the exact `sdk/v*` ref, and the release commit.

### Partial-publish recovery

NuGet.org and npm do not provide a cross-registry transaction, and versions are immutable. A failure during npm publication can leave only a prefix of the three npm versions; NuGet has not started in that case. A failure after npm verification, during NuGet publication, leaves the complete verified npm set and possibly only a prefix of the six NuGet versions. This npm-first ordering avoids publishing NuGet when trusted npm publication fails, but it cannot make the two registries atomic. Once real npm publication starts, the workflow deliberately leaves the GitHub Release as a draft rather than deleting the recovery evidence.

Use GitHub's **Re-run failed jobs** on the same workflow attempt, tag, and commit; do not rerun successful packaging jobs after registry publication has started. Existing npm versions are downloaded and verified against the original bytes and required provenance before they are skipped; missing npm versions continue in dependency order. NuGet then applies the same verify-before-skip rule and continues missing versions in dependency order. The final publication step is idempotent if the draft-to-public edit succeeded before a transient follow-up failure. Never rebuild a different artifact under an already-used version, move the tag, or overwrite the draft assets. If an existing registry version fails byte/content/provenance verification, stop the release and investigate rather than continuing.

If the exact artifact set cannot be recovered, keep the GitHub draft private, unlist or deprecate the partial packages where registry policy permits, publish a new coordinated patch version, and record which immutable versions escaped. If all registry packages verified but final GitHub publication failed, rerun only through the normal workflow so the inventory/digest gate executes before the draft is made public.

The release remains one `sdk/v*` version transaction as far as the external registries permit: exactly six coordinated NuGet versions, three coordinated npm versions, and one public GitHub Release from one commit.

## App Bundle Versions

The App bundle contains CLI and current-user Host outputs from the same commit as the App release. On first use of a Host version, the App copies the Host Supervisor and nested Runtime worker from `RuntimeHost/` into a stable versioned current-user payload directory. The App tag controls the Velopack app version. Standalone CLI and Host releases keep their own tags and GitHub Releases.

Release packaging validates the complete bundled App, Supervisor, Runtime worker, and CLI payload before platform packaging.

The workflow requires exactly two Windows Setup executables, two Linux AppImages, and two macOS DMGs. The macOS Portable ZIP is retained only as a local intermediate used to extract the Velopack-created App bundle; neither it nor a Setup PKG is published. Velopack Full/Delta packages and channel manifests remain public because application updates use them independently of the DMG bootstrap artifact.

The App workflow deletes a stale draft for the tag before packaging. It assembles release assets and evidence in a draft, validates the exact asset inventory in that draft, then invokes `gh release edit --draft=false` to make the release public. A failed draft is deleted by the workflow cleanup path. Public install scripts verify release checksums and platform signatures without installing a privileged Host companion. An expected Authenticode publisher subject or Apple Team ID can be supplied to add explicit signer pinning. Without a Windows pin, the script displays the platform-validated publisher and requires interactive confirmation before execution. Without an Apple pin, the script requires matching notarized Developer ID signatures and downloads the DMG without opening it. App updates do not install an elevated Host component; the operating system may still request authorization when the App's installation directory is not user-writable. The next App start activates the matching current-user Host payload.

## App Signing And Notarization

Do not commit certificates, private keys, provisioning material, app-specific passwords, or signing passwords to this public repository. Store signing material only in GitHub Actions secrets.

Required Windows App signing environment values:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_TRUSTED_SIGNING_ENDPOINT`
- `AZURE_TRUSTED_SIGNING_ACCOUNT_NAME`
- `AZURE_TRUSTED_SIGNING_CERTIFICATE_PROFILE_NAME`

These values are produced by the private workspace Terraform setup in `infra/windows-signing`. They can be stored as GitHub environment variables for `sunder-app-signing`; no Windows private key, PFX file, or signing password is stored in GitHub. `WINDOWS_PUBLISHER_SUBJECT` is an optional verification pin and is not required for Azure Trusted Signing.

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
