# Sunder CLI

`Sunder.Cli` provides command-line access to Registry discovery/publishing and local runtime package install/update operations.

## Configuration

Default production settings come from `src/Host/Sunder.Cli/appsettings.json`:

| Setting | Default |
| --- | --- |
| `Registry:ApiUrl` | `https://hub.sunderapp.io/` |
| `Registry:WebUrl` | `https://hub.sunderapp.io/` |
| `Runtime:Url` | `http://127.0.0.1:5275/` |

Debug builds load `appsettings.Development.json` by default, which points Registry API/web URLs at `http://localhost:5288/`.

Environment selection uses:

- `SUNDER_ENVIRONMENT`
- `DOTNET_ENVIRONMENT`
- `ASPNETCORE_ENVIRONMENT`

Global options:

| Option | Meaning |
| --- | --- |
| `--registry-api-url <url>` | Registry API URL |
| `--registry-web-url <url>` | Registry web URL used by browser auth |
| `--runtime-url <url>` | Local runtime host URL |
| `--timeout <duration>` | Command request timeout. Defaults to `15m`; accepts values like `15m`, `900s`, `900`, or `00:15:00` |
| `--json` | Emit one machine-readable JSON result instead of text output |

Environment overrides:

| Variable | Meaning |
| --- | --- |
| `SUNDER_REGISTRY_API_URL` | Registry API URL |
| `SUNDER_REGISTRY_WEB_URL` | Registry web URL |
| `SUNDER_RUNTIME_URL` | Local runtime host URL |

Runtime-bound commands authenticate automatically from the current user's private Host connection file, with the direct-Runtime connection retained as a development fallback. The URL in the selected file must exactly match the configured `Runtime:Url`, `SUNDER_RUNTIME_URL`, or `--runtime-url`; a missing or mismatched entry fails closed instead of sending an unauthenticated request.

Before any `/api/v1` call, the CLI performs the authenticated unversioned `/api/handshake` request. The handshake identifies the Runtime protocol, supported revision range, Runtime instance, and feature set. An absent, malformed, unauthenticated, or incompatible handshake stops the command before a versioned request is sent. Runtime product and informational versions are diagnostics only; product SemVer does not determine protocol compatibility.

The managed Host connection document lives under the current user's Host V1 root. The direct-Runtime fallback remains under the Runtime V1 root. Unversioned connection files and legacy `auth.json` files are not read.

## Output And Exit Codes

Text output is deterministic: package and Stack collections are sorted, dates use UTC ISO-8601, and untrusted server content is sanitized. Tokens, Runtime secrets, stack traces, and raw HTML responses are not printed.

`--json` emits one JSON object with `exitCode`, `success`, `messages`, and `data` properties:

```powershell
sunder search agent --json
```

Stable exit codes are:

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Operation failed |
| `2` | Command usage or argument error |
| `3` | Resource not found |
| `4` | Authentication required |
| `5` | Forbidden or conflicting operation |
| `6` | Runtime or Registry unavailable |
| `124` | Request timed out |
| `130` | Cancelled with Ctrl+C |

## Runtime Status

Show authenticated local Runtime status:

```powershell
sunder system status
```

`sunder runtime status` is an equivalent alias.

## Reset Runtime V1 State

Deliberately remove all local Runtime V1 state:

```powershell
sunder runtime reset --yes
```

The command is rejected without `--yes`. If Runtime is running, the CLI first obtains and consumes a short-lived, one-time authenticated reset challenge, asks Runtime to drain and stop, and then waits for the exclusive Runtime state lease. If Runtime is already stopped, reset proceeds under that same lease.

Reset validates the V1 schema identity before deleting anything and operates only on fixed V1 categories: package catalog/payloads, package state/files/secrets/logs, uploads/snapshots, Registry credentials, Runtime connection data, and the validated V1 root itself. Output reports `reset`, `already-empty`, or `partial` for each category and does not print local paths or secret values. A partial reset retains schema/key metadata and is safe to retry. Legacy roots and legacy credential namespaces are never scanned or deleted.

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

The Runtime owns the saved Registry credential. The CLI only receives the browser launch URL and typed auth status; credentials are never returned to the CLI or accepted on its command line.

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
- Local archives are streamed to the Runtime and mutations use opaque upload handles; the CLI never sends its local path in a Runtime DTO.
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

- The CLI sends one typed update request to the Runtime.
- The Runtime reads installed state, resolves Registry updates, downloads and verifies artifacts, and applies one atomic transaction.
- The CLI does not resolve plans, download package artifacts, or perform package-store mutations.

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

Publish to a development Registry endpoint:

```powershell
sunder publish --file .\MyPackage.1.0.0.sunderpkg --dev-local --registry-api-url http://localhost:5288/
```

Publish a large package with a longer Registry request timeout:

```powershell
sunder publish --file .\MyPackage.1.0.0.sunderpkg --timeout 30m
```

Publish behavior:

- The CLI validates the package archive before upload.
- Authenticated publish requires `sunder auth login`; the Runtime supplies the saved credential internally.
- Publish uses the global Registry request timeout; the default is 15 minutes.
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

The CLI and Runtime may use different temporary roots. Package content crosses `/api/v1` as bounded authenticated streams, not shared local paths.

Runtime-bound commands:

- `sunder system status`
- `sunder auth ...`
- `sunder list`
- `sunder install --file ...`
- `sunder install <package-id> ...`
- `sunder update ...`
- authenticated `sunder publish`, package management, and Stack management commands

Use `--runtime-url` or `SUNDER_RUNTIME_URL` when the runtime is not listening on the default URL. The selected URL must match the authenticated connection information published by that Runtime instance.

```powershell
sunder list --runtime-url http://127.0.0.1:5276/
```

## V1 Client Boundaries

`Sunder.Cli` is a parser, command-handler, and renderer layer. Command handlers receive explicit option records and do not construct Runtime HTTP endpoints.

- `Sunder.Runtime.Client` owns Runtime authentication, endpoint paths, JSON and Problem Details handling, content upload verification, and package stage/commit delegation.
- `Sunder.Runtime.Client` owns protocol negotiation, explicit request/stream deadlines, bounded response and error reads, and fail-closed compatibility checks shared by CLI and App transports.
- The Runtime owns Registry credentials, install/update plans, package downloads, verification, and package mutations.
- The CLI keeps a small anonymous Registry browse client for public search/details and explicit Stack downloads.
- Local package and Stack archive validation remains CLI-owned.
- Development-only local Registry publish remains CLI-owned behind `--dev-local`.
- The CLI test project enforces a less-than-500-line limit across every production CLI source file and rejects direct Runtime credential or package-mutation implementations.
