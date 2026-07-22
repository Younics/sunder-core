# Sunder Core Releases

Sunder Core component releases are published from the public `Younics/sunder-core` repository with GitHub Actions. Release validation differs by tag stream; this document describes the checks that currently exist rather than promising platform CI that is not configured.

## Version streams

The Sunder Core repository has separate version streams:

- `app/vX.Y.Z` publishes the bundled Sunder desktop app.
- `cli/vX.Y.Z` publishes the standalone CLI.
- `host/vX.Y.Z` publishes the standalone current-user Host: the `Sunder.Host.Supervisor` public gateway and its nested `Sunder.Runtime.Host` Runtime worker.
- `sdk/vX.Y.Z` publishes the coordinated public SDK, build-tooling, and template NuGet packages.

Prerelease tags such as `app/v0.1.0-beta.1`, `cli/v0.1.0-beta.1`, `host/v0.1.0-beta.1`, and `sdk/v1.1.1-beta.1` are supported and are published as GitHub prereleases. `sdk/v1.1.0-*` is explicitly rejected because the 1.1 dependency and Host baseline starts at stable `1.1.0`.

## Existing Workflow Checks

The PR and `main` SDK CI workflow restores, builds, and runs `dotnet test Sunder.Core.slnx` on Ubuntu. It also runs a CLI help smoke test and creates, restores, builds, and publishes template variants on Ubuntu. This is not a cross-platform test matrix.

The App packaging CI runs on macOS when its packaging files change. It syntax-checks the release/install scripts and creates and inspects an unsigned fixture DMG. It does not build, sign, or release the App.

The `sdk/v*` release workflow runs `Sunder.Runtime.Host.Tests` on Linux, Windows, and macOS, with external keystore tests enabled only on macOS. A second three-OS gate runs focused `Sunder.Package.Format` tests, consumes the packed `Sunder.Package.Build` and `Sunder.Sdk` NuGet artifacts in a clean sample, and validates the resulting exact archive through the CLI/Format path. Release MSBuild invocations set only the coordinated Sunder package version and computed minor-line range, using MSBuild's `%2C` command-line escape for the range comma, so project-specific fixture versions remain intact and project-reference dependencies pack with bounded ranges. The workflow also runs `Sunder.Package.Build.Tests`, `Sunder.Sdk.PublicApi.Tests`, bounded dependency inspection, and generated-template validation before publishing the five NuGet packages.

The `app/v*` and `host/v*` tag workflows run `Sunder.Host.Supervisor.Tests` and `Sunder.Runtime.Host.Tests` on Linux, Windows, and macOS before packaging their release assets. The `cli/v*` workflow validates its release archives but does not run `dotnet test`. Do not treat a tag release as a replacement for the PR/main SDK CI coverage.

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

Windows CLI artifacts are `.zip` files. Linux and macOS CLI artifacts are `.tar.gz` files.

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

Create and push an SDK SemVer tag:

```powershell
git tag sdk/v1.1.0
git push origin sdk/v1.1.0
```

The `sdk/v*` workflow publishes these coordinated packages to GitHub Releases and NuGet.org:

- `Sunder.Sdk`
- `Sunder.Sdk.Avalonia`
- `Sunder.Sdk.Stacks`
- `Sunder.Package.Build`
- `Sunder.Package.Templates`

NuGet.org publishing requires the `NUGET_API_KEY` repository secret. The GitHub Release is created before the NuGet.org push, so release operators must confirm the package push succeeds.

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
