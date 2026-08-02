# Package Trust And Security

> **Applies to:** Sunder SDK `1.1.x`, package and Stack format V1, .NET 10, and Runtime protocol revision 3.

Installing a Sunder package is a code-trust decision. V1 validates package integrity and structure, but it does not sandbox package code or cryptographically attest a package publisher.

## Execution Trust

Runtime modules execute inside `Sunder.Runtime.Host`; App modules execute inside `Sunder.App`. Both processes run with the current desktop user's operating-system permissions. Package code can use normal .NET and native APIs to access resources available to that user, including files, network, environment variables, processes, and user credentials outside Sunder.

Collectible `AssemblyLoadContext`, separate DI providers, role boundaries, constrained host-service registration, path validation, and package-scoped APIs improve lifecycle and integrity. They are **not** a security boundary. Native libraries are supported and execute with the same permissions.

Only install packages whose code and distribution source you trust. A Registry ownership record controls publication; it does not make the contained code safe.

Installation and update are also the consent boundary for schema-first cross-package RPC. Sunder displays each version's declared contract actions in package details, then grants all declared actions to that exact installed version and manifest. Development packages receive the same effective access only for their active development session. Packages cannot invoke undeclared actions.

RPC content references are not ambient bearer capabilities. Caller-owned call scopes bind request content to one exact target and response content to the originating caller; scope disposal cancels active work and revokes remaining references. Providers receive Host-created invocation contexts whose content authority ends with the handler or stream. Package code may report contract-level domain errors, but it cannot authenticate infrastructure errors.

## Package Integrity Versus Authenticity

The V1 package validator checks the generated manifest, exact content index, SHA-256 hashes and sizes, canonical roots/paths, ZIP entry safety and expansion limits, target kind/role/entry-point shape, declared compatibility, dependencies, RPC descriptors, web view declarations, and icon content. Runtime stages and validates before committing install/update state. Validation does not execute target code or prove that an entry point behaves safely.

Current `.sunderpkg` archives contain no signature file. Content hashes detect corruption or modification relative to the archive's own index, but they do not prove who created the archive. Do not describe V1 package validation as code signing or publisher attestation.

The [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md) is the normative source for archive and extraction validation. Avoid copying those rules into package policy or implementing a second validator.

## Dependency And Supply-Chain Practice

- Keep Sunder SDK/build references in the coordinated `[1.1.0,1.2.0)` range.
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
- Runtime operation DTOs unless the specific flow absolutely requires it;
- logs, structured attributes, exception messages, or notification text;
- command-line arguments, package-process environment variables, or callback launch URLs (the CLI's documented one-shot CI publication variable is a separate automation boundary);
- package assets, manifests, generated output, tests, or source control; or
- Stack fragments and payload files.

Logging's sensitive-key redaction is heuristic and does not sanitize messages or exception details. Stack secret scanning is also defense in depth and cannot classify every binary, encoding, token format, or encrypted value.

## Filesystem Rules

Treat `ContentRootPath` as read-only. Use bounded `Storage.Files` for normal mutable files and `RoleLocalWorkspace` only for libraries that require a local path. File-store paths reject traversal and observed links/reparse points. Workspace paths are lexically contained, but packages are responsible for links they create and for third-party APIs that follow them.

Do not expose host paths through Runtime operation DTOs, Stack records, errors, or UI. App and Runtime may not share a filesystem.

## Network And Callbacks

App/CLI-to-Host and Host-to-Runtime traffic is authenticated. Connection credentials are current-user private state and must never appear in package configuration, logs, process arguments, or package APIs.

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

An App package runs in the shell process. A UI exception is contained as a client-local package fault where possible, but malicious or unsafe App code can still affect the process. Keep App dependencies minimal and perform privileged/destructive work in explicit Runtime operations with validation and confirmation.

## Stacks

Treat imported Stack archives as untrusted data even after format validation. Preview must be side-effect free, show accurate actions/conflicts, and require explicit input for omitted secrets. Classify every required input explicitly as public or secret; secret inputs must not declare portable defaults or appear in logs, diagnostics, telemetry, notifications, or display text. Validate contributor-owned fragment schemas and values before mutation. Contributor-local atomicity means another contributor's committed changes cannot be rolled back by the host.

## Reporting Vulnerabilities

Do not open a public issue containing exploit details, credentials, tokens, or private package data. Follow the private process in the repository root [`SECURITY.md`](../../SECURITY.md).
