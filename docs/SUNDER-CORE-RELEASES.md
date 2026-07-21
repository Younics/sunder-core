# Sunder Core Releases

Sunder core component releases are published from the public `Younics/sunder-core` repository with GitHub Actions.
The release workflows currently build and package release artifacts only; automated tests will be added separately.

## Version streams

The Sunder core repository has separate version streams for releasable host components:

- `app/vX.Y.Z` publishes the bundled Sunder desktop app.
- `cli/vX.Y.Z` publishes the standalone CLI.
- `host/vX.Y.Z` publishes the standalone Sunder Host Supervisor and nested Runtime worker.

Prerelease tags such as `app/v0.1.0-beta.1`, `cli/v0.1.0-beta.1`, and `host/v0.1.0-beta.1` are supported and are published as GitHub prereleases.

## Publish App

Create and push an App SemVer tag:

```powershell
git tag app/v0.1.0
git push origin app/v0.1.0
```

The App workflow packages these runtimes:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

The App release uses Velopack. Each runtime uses its own App Velopack channel to avoid release-feed collisions, for example `app-win-x64-stable` and `app-osx-arm64-stable`. Windows publishes signed `Setup.exe` files, Linux publishes AppImages, and macOS publishes signed/notarized drag-to-install DMGs. The DMG Finder window uses the Sunder graphite-and-amber theme and contains Velopack's signed/notarized `Sunder.app` plus an Applications shortcut.

Windows App builds are signed with Azure Trusted Signing. macOS App builds use bundle id `com.younics.sunder` and are signed/notarized with Apple Developer ID certificates. Linux AppImage builds are currently unsigned.

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

## Publish Runtime Host

Create and push a Runtime Host SemVer tag:

```powershell
git tag host/v0.1.0
git push origin host/v0.1.0
```

The Runtime Host workflow publishes self-contained artifacts for these runtimes:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Windows Runtime Host artifacts are `.zip` files. Linux and macOS Runtime Host artifacts are `.tar.gz` files.

## App Bundle Versions

The App bundle contains CLI and Sunder Host outputs from the same commit as the App release. On first use of a Host version, the App copies the Supervisor and nested Runtime worker from `RuntimeHost/` into a stable versioned current-user payload directory. The App tag controls the Velopack app version. Standalone CLI and Host releases keep their own tags and GitHub Releases.

Release packaging validates the complete bundled App, Supervisor, Runtime worker, and CLI payload before platform packaging.

The workflow requires exactly two Windows Setup executables, two Linux AppImages, and two macOS DMGs. The macOS Portable ZIP is retained only as a local intermediate used to extract the Velopack-created App bundle; neither it nor a Setup PKG is published. Velopack Full/Delta packages and channel manifests remain public because application updates use them independently of the DMG bootstrap artifact. All distribution assets plus release evidence are assembled in a draft and made public only after exact-asset validation succeeds. Public install scripts verify release checksums and platform signatures without installing a privileged Host companion. An expected Authenticode publisher subject or Apple Team ID can be supplied to add explicit signer pinning. Without a Windows pin, the script displays the platform-validated publisher and requires interactive confirmation before execution. Without an Apple pin, the script requires matching notarized Developer ID signatures and downloads the DMG without opening it. App updates do not install an elevated Host component; the operating system may still request authorization when the App's installation directory is not user-writable. The next App start activates the matching current-user Host payload.

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

The macOS identity secret should match the Developer ID Application certificate subject available after import, for example `Developer ID Application: Company Name (TEAMID)`. Velopack signs, notarizes, and staples the App bundle. Release packaging then places that unchanged App in the themed DMG before signing, notarizing, and stapling the outer disk image. A Developer ID Installer certificate is not required because the workflow no longer creates a Setup PKG.

CLI and Runtime Host standalone releases are not signed yet.
