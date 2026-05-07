# Sunder CLI

`Sunder.Cli` provides command-line access to Registry discovery/publishing and local runtime package install/update operations.

## Configuration

Default production settings come from `src/Host/Sunder.Cli/appsettings.json`:

| Setting | Default |
| --- | --- |
| `Registry:ApiUrl` | `https://registry.sunder.dev/` |
| `Registry:WebUrl` | `https://registry.sunder.dev/` |
| `Runtime:Url` | `http://127.0.0.1:5275/` |

Debug builds load `appsettings.Development.json` by default, which points Registry API/web URLs at `http://localhost:5288/`.

Environment selection uses:

- `SUNDER_ENVIRONMENT`
- `DOTNET_ENVIRONMENT`
- `ASPNETCORE_ENVIRONMENT`

Global URL options:

| Option | Meaning |
| --- | --- |
| `--registry-api-url <url>` | Registry API URL |
| `--registry-web-url <url>` | Registry web URL used by browser auth |
| `--registry-url <url>` | Back-compatible alias that sets both Registry URLs |
| `--runtime-url <url>` | Local runtime host URL |

Environment overrides:

| Variable | Meaning |
| --- | --- |
| `SUNDER_REGISTRY_API_URL` | Registry API URL |
| `SUNDER_REGISTRY_WEB_URL` | Registry web URL |
| `SUNDER_REGISTRY_URL` | Legacy alias for both Registry URLs |
| `SUNDER_RUNTIME_URL` | Local runtime host URL |
| `SUNDER_REGISTRY_TOKEN` | Bearer token used for authenticated publish/package management |

## Help

Print CLI help:

```powershell
sunder --help
```

## Authentication

Sign in through browser auth:

```powershell
sunder auth login
```

Show current auth state:

```powershell
sunder auth status
```

Remove saved auth for the configured Registry:

```powershell
sunder auth logout
```

Authenticated commands use tokens in this order:

- `--token <token>`
- `SUNDER_REGISTRY_TOKEN`
- saved token from `sunder auth login`

## Search Packages

Search public Registry packages:

```powershell
sunder search
sunder search agent
sunder search openai --skip 20 --take 20
```

Search is anonymous.

## Package Info

Show package details:

```powershell
sunder info sunder.package.agent
```

Show a specific version:

```powershell
sunder info sunder.package.agent --version 1.0.0
```

Info is anonymous.

## List Installed Packages

List packages installed in the local runtime:

```powershell
sunder list
```

The CLI contacts the configured runtime URL. The default runtime URL is `http://127.0.0.1:5275/`.

## Install Packages

Install from the Registry by dist tag, defaulting to `latest`:

```powershell
sunder install sunder.package.agent
sunder install sunder.package.agent --tag latest
```

Install an exact Registry version:

```powershell
sunder install sunder.package.agent --version 1.0.0
```

Install from a local `.sunderpkg` file:

```powershell
sunder install --file .\Sunder.Package.Agent.1.0.0.sunderpkg
```

Allow downgrade or same-version reinstall:

```powershell
sunder install sunder.package.agent --version 1.0.0 --allow-downgrade --reinstall
sunder install --file .\MyPackage.1.0.0.sunderpkg --allow-downgrade --reinstall
```

Install behavior:

- Registry installs resolve a dependency-aware install plan before downloading artifacts.
- Local file installs validate the archive before calling the runtime host.
- Existing packages are upgraded through the runtime update path.
- Local file installs do not accept `--version` or `--tag`.

## Update Packages

Update all installed packages with available updates:

```powershell
sunder update --all
```

Update one installed package:

```powershell
sunder update sunder.package.agent
```

Include prerelease versions in update resolution:

```powershell
sunder update --all --include-prerelease
```

Update behavior:

- The CLI reads installed package state from the runtime.
- The Registry resolves available updates.
- Each update is applied through the same runtime package update path as local installs.

## Validate Packages

Validate a `.sunderpkg` archive:

```powershell
sunder package validate .\MyPackage.1.0.0.sunderpkg
```

The top-level `validate` command currently routes to the same implementation:

```powershell
sunder validate .\MyPackage.1.0.0.sunderpkg
```

Validation checks archive structure, manifest fields, entry assembly, icon file, content index entries, SHA-256 hashes, file sizes, unsafe paths, duplicate paths, and unindexed files.

## Publish Packages

Publish a validated `.sunderpkg` to the configured Registry:

```powershell
sunder publish --file .\MyPackage.1.0.0.sunderpkg
```

Publish without setting or promoting `latest`:

```powershell
sunder publish --file .\MyPackage.1.0.0.sunderpkg --no-latest
```

Publish with an explicit token:

```powershell
sunder publish --file .\MyPackage.1.0.0.sunderpkg --token $env:SUNDER_REGISTRY_TOKEN
```

Publish to a development Registry endpoint:

```powershell
sunder publish --file .\MyPackage.1.0.0.sunderpkg --dev-local --registry-url http://localhost:5288/
```

Publish behavior:

- The CLI validates the package archive before upload.
- Authenticated publish requires sign-in, `SUNDER_REGISTRY_TOKEN`, or `--token`.
- `--dev-local` calls the development-only local publish endpoint and does not require an auth token.
- The Registry rejects duplicate package versions.
- Package ownership is enforced for non-development publish.

## Yank And Unyank Versions

Yank a package version:

```powershell
sunder yank sunder.package.agent 1.0.0
```

Unyank a package version:

```powershell
sunder unyank sunder.package.agent 1.0.0
```

These commands require package management auth.

## Deprecate And Undeprecate Versions

Set a deprecation message:

```powershell
sunder deprecate sunder.package.agent 1.0.0 --message "Use 1.1.0 instead."
```

Clear a deprecation message:

```powershell
sunder undeprecate sunder.package.agent 1.0.0
```

These commands require package management auth.

## Dist Tags

List dist tags:

```powershell
sunder dist-tag list sunder.package.agent
```

Set a dist tag:

```powershell
sunder dist-tag set sunder.package.agent latest 1.0.0
```

Delete a dist tag:

```powershell
sunder dist-tag delete sunder.package.agent beta
```

`dist-tag set` and `dist-tag delete` require package management auth. `dist-tag list` is anonymous.

## Local Runtime Requirement

Commands that install, update, or list local packages require `Sunder.Runtime.Host` to be reachable.

Runtime-bound commands:

- `sunder list`
- `sunder install --file ...`
- `sunder install <package-id> ...`
- `sunder update ...`

Use `--runtime-url` or `SUNDER_RUNTIME_URL` when the runtime is not listening on the default URL.

```powershell
sunder list --runtime-url http://127.0.0.1:5276/
```
