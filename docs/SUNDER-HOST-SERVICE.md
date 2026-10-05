# Sunder Current-User Host Service

## Scope

This is the canonical documentation for implemented current-user Host behavior. The Host is the public current-user gateway implemented by `Sunder.Host.Supervisor`; its nested Runtime worker is `Sunder.Runtime.Host`. A direct-loopback standalone Runtime is a development fallback and is not the managed Host.

The desktop App owns one Host for the current user. Opening Sunder starts the Host when it is not already running. The Host survives the desktop window closing so Runtime-worker and Agent work can continue for the login session, but it is not registered as a machine service and does not start before the user opens Sunder after login.

The Host remains bound to `http://127.0.0.1:5275/`; remote TLS and pairing are separate work. Because loopback TCP ports are not user-namespaced, simultaneous Sunder Hosts in multiple logged-in user sessions are not supported by this first current-user implementation.

No App distribution requires administrator privileges, a dedicated operating-system account, or a companion MSI, DEB, or PKG. Runtime packages execute with the current desktop user's permissions.

## Payload

Each App artifact contains the Host Supervisor and nested Runtime worker under `RuntimeHost/`. Before launch, the App copies that payload into a versioned current-user directory under the platform local-application-data root:

```text
Sunder/host/payloads/host-<version-hash>/
```

The App rejects links and reparse points while staging, preserves executable modes on Unix, and writes the active payload descriptor atomically. This stable location prevents AppImage unmounts, moved macOS bundles, Velopack version cleanup, or App replacement from invalidating a running Host path.

When the bundled Host version changes, the App:

1. Stages the new payload without changing the active descriptor.
2. Authenticates to and stops the previous Host Supervisor.
3. Starts the staged Host Supervisor and waits for a compatible ready Runtime worker.
4. Commits the new active descriptor and removes old payloads.
5. Restores the previous payload only when typed pre-launch evidence proves that the candidate was not submitted. After candidate submission, activation is forward-only: the App preserves the candidate and failure evidence for a later retry instead of rolling back.

Durable Host and Runtime state are not stored with executable payloads and survive payload replacement.

## Session Launch

- Windows launches the staged Host Supervisor as an independent current-user process.
- Linux uses a transient `systemd-run --user` service with restart-on-failure and falls back to `setsid` when no user systemd manager is available.
- macOS submits a `launchctl` job in the current login session from an app-private definition. It does not install a login-start LaunchAgent.

These launchers keep the Host independent from the App UI without registering login-start or machine-wide services.

## State And Authentication

Host state defaults to:

```text
<LocalApplicationData>/Sunder/host/v1/
```

Runtime-worker and package state defaults to:

```text
<LocalApplicationData>/Sunder/runtime/v1/
```

The Host Supervisor creates a stable Host identity and a new external bearer credential when it starts. The private connection document is available only to the current user. Windows protects its token with DPAPI CurrentUser; Unix connection directories and files use current-user-only modes. App and CLI clients resolve this Host connection before the direct standalone Runtime connection.

`RuntimeWorkerCoordinator` is the sole mutable owner of the Runtime worker process, private IPC and pipe leases, worker connection publication, durable lifecycle state, termination, and crash recovery. The stateless `RuntimeWorkerLaunchProbe` creates launch metadata, returns a launched process for immediate coordinator adoption, and borrows process and diagnostic handles only while probing readiness; it never retains or disposes those resources or mutates lifecycle state. Authenticated Host shutdown remains available for App-owned payload replacement.

## Package Session Startup

The Runtime worker validates the complete package dependency graph before activation. It prepares independent packages concurrently, partitions valid packages into stable dependency waves, and starts Workers concurrently only within the current wave. A dependent wave cannot start until every Worker in the preceding wave is candidate-ready. Publication remains sequential, and Runtime commits the candidate generation only after the complete graph is ready. A candidate failure cancels pending starts, strictly stops started candidate Workers in reverse plan order, and leaves the previous generation recoverable.

Package preparation and Worker startup have separate conservative concurrency limits. Both default to `2`, accept integers from `1` through `8`, and can be configured for the Runtime process with:

| Command-line option | Environment variable |
| --- | --- |
| `--package-preparation-concurrency <1-8>` | `SUNDER_PACKAGE_PREPARATION_CONCURRENCY` |
| `--package-worker-startup-concurrency <1-8>` | `SUNDER_PACKAGE_WORKER_STARTUP_CONCURRENCY` |

An explicit command-line value takes precedence over its environment value. These controls bound work; they do not alter dependency ordering or generation commit semantics.

For startup diagnostics and benchmark evidence, `SUNDER_RUNTIME_TRACE_PATH` enables append-only NDJSON from the Runtime process. Trace schema `2` preserves the canonical `package.session.load.started` to `package.generation.committed` interval and records source discovery, graph planning, projection-cache lookup/build, projection preparation, exact execution-runtime binding, Worker wave boundaries, process start, protocol readiness, candidate readiness, and a terminal commit or failure. Package events include monotonic elapsed time, queue/active duration, byte counters, cache result, activation identity, exact runtime identity, process ID/birth identity, outcome, and generation correlation. The trace contains no package file paths, process arguments, credentials, or environment values.

## Distribution And Removal

- The Windows App release workflow requires Azure Trusted Signing for the Velopack `Setup.exe`.
- The macOS App release workflow requires Developer ID signing and notarization for the DMG containing `Sunder.app` and an Applications shortcut.
- Linux distributes an unsigned Velopack AppImage.

App updates require no elevation. The updated App stages and activates its matching Host payload on the next start.

Windows uninstall stops the current-user Host and removes staged executable payloads. Deleting a macOS app bundle or Linux AppImage has no operating-system uninstall callback; Host and Runtime state remain user data, and a running Host naturally ends with the login session.
