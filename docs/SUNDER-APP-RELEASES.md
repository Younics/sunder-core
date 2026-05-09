# Sunder App Releases

Sunder.App releases are published from the public `Younics/sunder-core` repository with GitHub Actions and Velopack.
The release workflow currently builds and packages the app only; automated tests will be added separately.

## Publish a release

Create and push a SemVer tag:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Prerelease tags are supported and are published as GitHub prereleases:

```powershell
git tag v0.1.0-beta.1
git push origin v0.1.0-beta.1
```

## Build outputs

The release workflow packages these runtimes:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

Each runtime uses its own Velopack channel to avoid release-feed collisions, for example `win-x64-stable` and `osx-arm64-stable`.

## Signing

Release packages are currently unsigned. Windows code signing and macOS signing/notarization can be added later without changing the release tag flow.
