# Sunder Core Releases

Sunder core component releases are published from the public `Younics/sunder-core` repository with GitHub Actions.
The release workflows currently build and package release artifacts only; automated tests will be added separately.

## Version streams

The Sunder core repository has separate version streams for releasable host components:

- `app/vX.Y.Z` publishes the bundled Sunder desktop app.
- `cli/vX.Y.Z` publishes the standalone CLI.
- `host/vX.Y.Z` publishes the standalone Runtime Host.

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

The App release uses Velopack. Each runtime uses its own App Velopack channel to avoid release-feed collisions, for example `app-win-x64-stable` and `app-osx-arm64-stable`.

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

The App installer bundles CLI and Runtime Host outputs from the same commit as the App release. The App tag controls the Velopack app version. Standalone CLI and Runtime Host releases keep their own tags and GitHub Releases.

## Signing And Notarization

Release packages are currently unsigned. Windows code signing and macOS signing/notarization can be added later without changing the release tag flow.
