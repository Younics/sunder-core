# Sunder App Releases

Sunder.App releases are published from the public `Younics/sunder-core` repository with GitHub Actions and Velopack.
The release workflow currently builds and packages the app only; automated tests will be added separately.

## Publish a release

Create and push an App SemVer tag:

```powershell
git tag app/v0.1.0
git push origin app/v0.1.0
```

Prerelease tags are supported and are published as GitHub prereleases:

```powershell
git tag app/v0.1.0-beta.1
git push origin app/v0.1.0-beta.1
```

The Sunder core repository has separate version streams for releasable host components:

- `app/vX.Y.Z` publishes the bundled Sunder desktop app.
- `cli/vX.Y.Z` is reserved for standalone CLI releases.
- `host/vX.Y.Z` is reserved for standalone Runtime Host releases.

## Build outputs

The release workflow packages these runtimes:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Each runtime uses its own App Velopack channel to avoid release-feed collisions, for example `app-win-x64-stable` and `app-osx-arm64-stable`.

## Signing

Release packages are currently unsigned. Windows code signing and macOS signing/notarization can be added later without changing the release tag flow.
