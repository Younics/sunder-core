# Sunder CLI

> **Source channel:** This reference tracks the current source command tree and can be ahead of a released CLI. Use the document from the matching `cli/v*` tag for a released binary. Removed top-level aliases are intentionally not documented or accepted.

`Sunder.Cli` is a thin command-line client for Registry discovery/management and local package operations through the current-user Host gateway. A direct-loopback standalone Runtime remains a development fallback.

## Command Tree

The public roots are deliberately capability-oriented:

```text
sunder
  runtime status|start|stop|restart|reset
  package search|info|list|status|install|update|enable|disable|uninstall
  package source adopt
  package config schema|list|get|set|unset
  package secret status|set|unset
  package auth status|login|logout
  stack search|info|download|open|import|export
  registry auth login|status|logout
  registry package publish|yank|unyank|deprecate|undeprecate|tag
  registry stack publish|delete
  dev package validate
  dev stack inspect
  dev registry package publish-local
  dev registry stack publish-local
  config show
  version
```

Use `sunder --help` or help on any group/leaf for the exact arguments. There are no top-level `search`, `install`, `publish`, `auth`, `validate`, `dist-tag`, or `system` aliases.

## Configuration

Default production settings come from `languages/csharp/dotnet/host/Sunder.Cli/appsettings.json`:

| Setting | Default |
| --- | --- |
| `Registry:ApiUrl` | `https://hub.sunderapp.io/` |
| `Registry:WebUrl` | `https://hub.sunderapp.io/` |
| `Runtime:Url` | `http://127.0.0.1:5275/` current-user Host gateway, or a standalone Runtime during development |

Debug builds load `appsettings.Development.json` by default. Environment selection checks `SUNDER_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, and `ASPNETCORE_ENVIRONMENT` in that order.

Global options:

| Option | Meaning |
| --- | --- |
| `--registry-api-url <url>` | Registry API URL. |
| `--registry-web-url <url>` | Registry web URL used by browser sign-in. |
| `--runtime-url <url>` | Current-user Host gateway, or standalone Runtime URL during development. |
| `--timeout <duration>` | Request timeout; default `15m`. Accepts `15m`, `900s`, `900`, or `00:15:00`. |
| `--json` | Emit one versioned machine-readable result. |

Environment URL overrides are `SUNDER_REGISTRY_API_URL`, `SUNDER_REGISTRY_WEB_URL`, and `SUNDER_RUNTIME_URL`. `sunder config show` displays the effective endpoints and timeout without contacting either service.

Runtime-bound commands authenticate through the current user's private Host connection document. Before a versioned Runtime call, the CLI performs the authenticated `/api/handshake` and rejects missing, malformed, unauthenticated, or incompatible protocol data. The configured URL must exactly match that connection document; the CLI does not fall back to an unauthenticated request.

## Output And Exit Codes

Text collections are deterministically sorted, timestamps use UTC ISO-8601, and untrusted server text is sanitized. Tokens, Runtime secrets, stack traces, raw HTML responses, and local Runtime paths are not printed.

```powershell
sunder package search agent --json
```

`--json` emits one object with `schemaVersion`, `command`, `exitCode`, `success`, `messages`, and `data`. Stable exit codes are:

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Operation failed |
| `2` | Usage or argument error |
| `3` | Resource not found |
| `4` | Authentication required |
| `5` | Forbidden or conflicting operation |
| `6` | Runtime or Registry unavailable |
| `124` | Request timed out |
| `130` | Cancelled with Ctrl+C |

## Runtime

Start, stop, or restart the current user's Runtime through the authenticated Host gateway and wait for the generation-bound operation to complete:

```powershell
sunder runtime start
sunder runtime stop
sunder runtime restart
```

Show Host and Runtime status. This remains useful while Runtime is stopped:

```powershell
sunder runtime status
```

Deliberately drain the Runtime and delete validated Runtime V1 state:

```powershell
sunder runtime reset --yes
```

Reset is rejected without `--yes`. It acquires a one-time authenticated reset challenge and exclusive state lease, validates the V1 schema identity, and operates only on fixed V1 categories. It never scans legacy roots. Partial reset results retain schema/key metadata and are safe to retry.

## Packages

Anonymous Registry discovery:

```powershell
sunder package search
sunder package search agent --skip 20 --take 20
sunder package info sunder.package.agent
sunder package info sunder.package.agent --version 1.0.0
```

List installed packages and their source/version policy, or inspect one package's installed and current-session state:

```powershell
sunder package list
sunder package status sunder.package.agent
```

Install from the Registry. A tag install defaults to `latest`; an exact version and a tag are mutually exclusive:

```powershell
sunder package install sunder.package.agent
sunder package install sunder.package.agent --tag beta
sunder package install sunder.package.agent --version 1.0.0
```

Install a local archive, with explicit downgrade/reinstall consent when needed:

```powershell
sunder package install --file .\MyPackage.1.0.0.sunderpkg
sunder package install --file .\MyPackage.1.0.0.sunderpkg --allow-downgrade --reinstall
```

Local archives are validated and streamed into Runtime-owned temporary storage; Runtime DTOs never carry the CLI's local path.

Update one managed package or all eligible managed packages:

```powershell
sunder package update sunder.package.agent
sunder package update --all
sunder package update --all --include-prerelease
sunder package update --all --registry-origin https://hub.sunderapp.io/
```

Updates are provenance-aware and do not require the CLI's configured Registry. Runtime resolves each Registry-managed, follow-tag package from its recorded Registry origin, source package id, tag, range, and prerelease policy. Resolver-added transitive dependencies can move with their parent plan, while versions explicitly selected by the user remain pinned. Local archives, development packages, legacy unknown records, origin mismatches, and incomplete policies are skipped with warnings rather than silently adopted. `--registry-origin` is only an explicit filter. `--include-prerelease` is an additive one-run preference; neither option rewrites provenance or converts a pinned/unmanaged install into a managed one. Multi-origin updates are prepared before one installed-package commit.

Adopt an explicit Registry source and update policy for a migrated, local, or otherwise unmanaged package. Preview and apply are separate operations:

```powershell
sunder package source adopt sunder.package.agent --registry-origin https://hub.sunderapp.io/ --tag latest --dry-run
sunder package source adopt sunder.package.agent --registry-origin https://hub.sunderapp.io/ --tag latest --yes
sunder package source adopt sunder.package.agent --registry-origin https://hub.sunderapp.io/ --version 1.0.0 --yes
```

Exactly one of `--tag` and `--version`, and exactly one of `--dry-run` and `--yes`, is required. `--include-prerelease` applies only to a tag policy. Adoption resolves the proposed replacement before Runtime records the new source; it is never inferred from global configuration.

Enable or disable an installed package:

```powershell
sunder package enable sunder.package.agent
sunder package disable sunder.package.agent
```

Uninstall is always plan-first. Review the Runtime-generated plan without mutation:

```powershell
sunder package uninstall sunder.package.agent --dry-run
```

Apply a freshly generated exact plan:

```powershell
sunder package uninstall sunder.package.agent --yes
sunder package uninstall sunder.package.agent --yes --cascade
```

Exactly one of `--dry-run` and `--yes` is required. If dependents must also be removed, apply is rejected without `--cascade`. Runtime binds apply to a confirmation token for the displayed package set/reload impact, rejects a stale plan, and retains package data in V1.

Inspect and change only configuration fields declared by the package schema:

```powershell
sunder package config schema sunder.package.agent
sunder package config list sunder.package.agent
sunder package config get sunder.package.agent mode
sunder package config set sunder.package.agent mode fast
sunder package config unset sunder.package.agent mode
```

Secret values are never accepted as command arguments and are never readable through the CLI. Status reports presence only; writes must come from one environment variable or redirected standard input:

```powershell
sunder package secret status sunder.package.agent
sunder package secret set sunder.package.agent api-key --from-env SUNDER_AGENT_API_KEY
Get-Content .\api-key.txt | sunder package secret set sunder.package.agent api-key --stdin
sunder package secret unset sunder.package.agent api-key
```

Manage a package's declared browser-auth capability independently from Registry auth:

```powershell
sunder package auth status sunder.package.agent
sunder package auth login sunder.package.agent
sunder package auth logout sunder.package.agent
```

Login launches the Runtime-issued browser URL, waits only until the bounded session reaches a terminal state, and cancels the Runtime session when the CLI is cancelled.

## Stacks

Public Stack browse/open/download operations are anonymous:

```powershell
sunder stack search agent
sunder stack info example.agent-stack
sunder stack open example.agent-stack
sunder stack download example.agent-stack --output .\agent.sunderstack
```

`--force` is required to replace an existing download destination. Downloads are bounded and verified against Registry artifact metadata before replacement.

Preview a local Stack import through Runtime without applying package or contributor changes:

```powershell
sunder stack import --file .\agent.sunderstack --dry-run
```

Noninteractive apply is intentionally unavailable until fragment/action selection and Public/Secret input sources can be supplied without exposing sensitive values. The CLI discards the preview plan after rendering its actions, inputs, and blockers.

Export Runtime-discovered default Stack content, or explicitly include all discovered items and details:

```powershell
sunder stack export example.agent-stack --output .\agent.sunderstack
sunder stack export example.agent-stack --all --output .\agent-full.sunderstack --force
```

Runtime owns discovery and archive creation. The CLI sends item identities rather than projecting discovered values and verifies the bounded download before replacing the destination.

## Local Artifact Validation

Inspect package and Stack archives locally without Runtime or Registry configuration:

```powershell
sunder dev package validate .\MyPackage.1.0.0.sunderpkg
sunder dev stack inspect .\MyStack.sunderstack
```

These commands validate canonical format and integrity. They do not execute package code, prove publisher identity, establish source trust, or make an archive safe to install.

## Human Registry Authentication

```powershell
sunder registry auth login
sunder registry auth status
sunder registry auth logout
```

Browser login is for a human user. Runtime owns, protects, refreshes, supplies, revokes, and deletes the saved Registry credential. The CLI receives only a browser launch URL and typed status; it never receives the credential. Package/Stack management and the default publication path use this Runtime-owned identity.

## Registry Package Management

Publish a validated archive with human Runtime-owned auth:

```powershell
sunder registry package publish --file .\MyPackage.1.0.0.sunderpkg
```

Stable versions set/promote `latest` by default. Prerelease versions do not change `latest` by default. Override either decision explicitly:

```powershell
sunder registry package publish --file .\MyPackage.1.1.0-beta.1.sunderpkg --set-latest
sunder registry package publish --file .\MyPackage.1.1.0.sunderpkg --no-latest
```

`latest` is a movable Registry dist tag, not special installed state. An installed record retains its exact version and provenance; follow-tag update resolution consults the recorded tag later.

Manage immutable versions and tags:

```powershell
sunder registry package yank sunder.package.agent 1.0.0
sunder registry package unyank sunder.package.agent 1.0.0
sunder registry package deprecate sunder.package.agent 1.0.0 --message "Use 1.1.0 instead."
sunder registry package undeprecate sunder.package.agent 1.0.0
sunder registry package tag list sunder.package.agent
sunder registry package tag set sunder.package.agent beta 1.1.0-beta.1
sunder registry package tag delete sunder.package.agent beta
```

Tag listing is anonymous. Mutation commands require Runtime-owned human auth.

## Registry Stack Management

```powershell
sunder registry stack publish --file .\MyStack.sunderstack
sunder registry stack delete example.agent-stack
```

Stack management defaults to Runtime-owned human auth.

## CI Publication Credentials

Protected automation may publish a package or Stack directly with a scoped Registry publish token. Do not put the token in an option, process argument, repository file, or log.

Environment input:

```bash
SUNDER_REGISTRY_PUBLISH_TOKEN="$PUBLISH_TOKEN" \
  sunder registry package publish --file ./MyPackage.1.0.0.sunderpkg --credential-source environment
```

Redirected standard input:

```bash
printf '%s' "$PUBLISH_TOKEN" | \
  sunder registry stack publish --file ./MyStack.sunderstack --credential-source stdin
```

`--credential-source` accepts only `runtime`, `environment`, or `stdin` and defaults to `runtime`. Environment mode reads `SUNDER_REGISTRY_PUBLISH_TOKEN` and clears it from the CLI process after capture. Stdin mode requires redirected input and accepts one bounded credential with an optional trailing newline. Automation credentials apply only to package/Stack publication; other management commands remain Runtime-owned human operations.

## Development Registry Publication

Loopback-only development endpoints are explicit and do not accept production auth options:

```powershell
sunder dev registry package publish-local --file .\MyPackage.1.0.0.sunderpkg --registry-api-url http://localhost:5288/
sunder dev registry stack publish-local --file .\MyStack.sunderstack --registry-api-url http://localhost:5288/
```

Package `publish-local` uses the same stable/prerelease `latest` default and accepts `--set-latest` or `--no-latest`. These commands call development-only Registry endpoints and must remain loopback-only.

## Client Boundaries

`Sunder.Cli` owns parsing, rendering, local archive inspection, anonymous Registry browse/download, direct scoped-token publication, and loopback development publication. `Sunder.Runtime.Client` owns Runtime authentication, handshake negotiation, endpoint paths, bounded transport, uploads, and Problem Details. Runtime owns human Registry credentials, provenance-aware install/update plans, artifact downloads, package mutations, uninstall plans, and Registry management delegation.
