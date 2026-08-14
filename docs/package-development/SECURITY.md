# Package Trust And Security

> **Applies to:** Sunder SDK `1.1.x`, package archive V1, package manifest V3, Stack format V1, .NET 10, and Runtime protocol revision 8.

Installing a Sunder package is a code-trust decision. V1 validates package integrity and structure, but it does not sandbox package code or cryptographically attest a package publisher.

## Execution Trust

Runtime targets execute only as supervised Worker V3 child processes; App modules execute inside `Sunder.App`. Every process runs with the current desktop user's operating-system permissions. Worker isolation contains lifecycle and failures but is not an operating-system sandbox. Package code can use normal platform APIs to access resources available to that user, including files, network, processes, and user credentials outside Sunder.

Process supervision, App load contexts, separate role lifetimes, path validation, and package-scoped APIs improve lifecycle and integrity. They are **not** a security boundary. Native libraries are supported and execute with the same permissions.

Workers receive an explicit environment rather than the Runtime process environment. The baseline is limited to OS process lookup, home, locale, package-local temp, and documented activation variables. It excludes Runtime/Registry tokens, cloud credentials, managed/native profiler injection, startup hooks, and arbitrary `SUNDER_*` bootstrap values. `docker-environment.v1` is an install/update consent requirement that admits only the bounded Docker connection allowlist; it never admits Docker auth JSON or passphrases. This reduces accidental ambient leakage but is not a sandbox: package code still runs as the user and can read resources that user can access.

Only install packages whose code and distribution source you trust. A Registry ownership record controls publication; it does not make the contained code safe.

Installation and update are also the consent boundary for schema-first cross-package RPC. Sunder displays each version's declared contract actions in package details, then grants all declared actions to that exact installed version and manifest. Development packages receive the same effective access only for their active development session. Packages cannot invoke undeclared actions.

RPC content references are not ambient bearer capabilities. Caller-owned call scopes bind request content to one exact target and response content to the originating caller; scope disposal cancels active work and revokes remaining references. Providers receive Host-created invocation contexts whose content authority ends with the handler or stream. Package code may report contract-level domain errors, but it cannot authenticate infrastructure errors.

## Package Integrity Versus Authenticity

The V1 package validator checks the generated manifest, exact content index, SHA-256 hashes and sizes, canonical roots/paths, ZIP entry safety and expansion limits, target kind/role/entry-point shape, declared compatibility, dependencies, RPC descriptors, web view declarations, and icon content. Runtime stages and validates before committing install/update state. Validation does not execute target code or prove that an entry point behaves safely.

Current `.sunderpkg` archives contain no signature file. A C# build may platform-sign its apphost and native closure through the pre-index signing hook; those embedded signature bytes are then hash-bound like all other content, and post-index tampering is rejected. Content hashes and embedded platform signatures still do not establish a Sunder publisher identity. Do not describe V1 package validation as package signing or Registry publisher attestation.

The [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md) is the normative source for archive and extraction validation. Avoid copying those rules into package policy or implementing a second validator.

## Dependency And Supply-Chain Practice

- Keep normal Sunder SDK/build references in the coordinated `[1.1.0,1.2.0)` range. `Sunder.Sdk.Worker` intentionally requires the exact matching `Sunder.Sdk` patch version.
- Bound other NuGet dependencies intentionally and commit `packages.lock.json` for reproducible CI restore.
- Review transitive and native assets included under package `lib`/`runtimes`.
- Put language-neutral RPC descriptors and optional typed helpers in a minimal protocol package; never expose implementation or Host types.
- Declare Sunder runtime dependencies separately from NuGet protocol-helper dependencies.
- Validate the exact Release artifact that will be uploaded.
- Never overwrite a published version; publish a new SemVer and deprecate/yank a bad immutable version.
- Treat development-package folders as trusted local code. `--watch` rebuilds can replace active code without a new install prompt.

## Data And Secrets

Use `IPackageSecrets` for credentials. Runtime encrypts package secrets at rest, but the package receives plaintext and can exfiltrate it. Encryption does not protect against malicious package code or compromise of the current user account.

Never place secrets in:

- package state/files/settings defaults;
- RPC DTOs unless the specific flow absolutely requires it;
- logs, structured attributes, exception messages, or notification text;
- command-line arguments, package-process environment variables, or callback launch URLs (the CLI's documented one-shot CI publication variable is a separate automation boundary);
- package assets, manifests, generated output, tests, or source control; or
- Stack fragments and payload files.

Logging's sensitive-key redaction is heuristic and does not sanitize messages or exception details. Stack secret scanning is also defense in depth and cannot classify every binary, encoding, token format, or encrypted value.

## Filesystem Rules

Treat `ContentRootPath` as read-only. Use bounded `Storage.Files` for normal mutable files and `RoleLocalWorkspace` only for libraries that require a local path. File-store paths reject traversal and observed links/reparse points. Workspace paths are lexically contained, but packages are responsible for links they create and for third-party APIs that follow them.

Do not expose host paths through RPC DTOs, Stack records, errors, or UI. App and Runtime may not share a filesystem.

## Network And Callbacks

App/CLI-to-Host and Host-to-worker traffic is authenticated. Connection credentials are current-user private state and must never appear in package configuration, logs, process arguments, or package APIs.

Human Registry credentials are Runtime-owned and never returned to CLI commands. Protected publication automation may use the CLI's bounded environment/stdin credential sources, but must use a scoped Registry publish token and keep it out of arguments and logs.

Use `IPackageCallbackHandler`/`IPackageAuthHandler` for browser callbacks. Runtime owns the bounded loopback listener, session routing, expiry, and duplicate rejection. Packages must not start their own listener. The package must still validate provider state, nonce, PKCE, origin, account identity, and callback values.

For outbound HTTP:

- require HTTPS outside loopback development;
- set explicit deadlines and response-size limits;
- validate redirect and final origins;
- avoid automatic credential forwarding across origins; and
- honor cancellation during Runtime retirement and shutdown.

## UI And Untrusted Content

Do not render untrusted HTML or execute package/Registry text as markup or script. Encode text, bound images/downloads, and keep user confirmation around destructive operations. Package icon validation allows only bounded recognized raster formats; SVG is not accepted in V1 because Sunder has no complete sanitizer for scripts, external resources, and compressed expansion.

An App package runs in the shell process. A UI exception is contained as a client-local package fault where possible, but malicious or unsafe App code can still affect the process. Keep App dependencies minimal and perform privileged/destructive work through explicit owner RPC methods with validation and confirmation.

## Stacks

Treat imported Stack archives as untrusted data even after format validation. Preview must be side-effect free, show accurate actions/conflicts, and require explicit input for omitted secrets. Classify every required input explicitly as public or secret; secret inputs must not declare portable defaults or appear in logs, diagnostics, telemetry, notifications, or display text. Validate contributor-owned fragment schemas and values before mutation. Contributor-local atomicity means another contributor's committed changes cannot be rolled back by the host.

## Reporting Vulnerabilities

Do not open a public issue containing exploit details, credentials, tokens, or private package data. Follow the private process in the repository root [`SECURITY.md`](../../SECURITY.md).
