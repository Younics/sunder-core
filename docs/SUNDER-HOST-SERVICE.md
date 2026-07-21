# Sunder Current-User Host

## Scope

The desktop App owns one Host for the current user. Opening Sunder starts the Host when it is not already running. The Host survives the desktop window closing so Runtime and Agent work can continue for the login session, but it is not registered as a machine service and does not start before the user opens Sunder after login.

The Host remains bound to `http://127.0.0.1:5275/`; remote TLS and pairing are separate work. Because loopback TCP ports are not user-namespaced, simultaneous Sunder Hosts in multiple logged-in user sessions are not supported by this first current-user implementation.

No App distribution requires administrator privileges, a dedicated operating-system account, or a companion MSI, DEB, or PKG. Runtime packages execute with the current desktop user's permissions.

## Payload

Each App artifact contains the Supervisor and nested Runtime worker under `RuntimeHost/`. Before launch, the App copies that payload into a versioned current-user directory under the platform local-application-data root:

```text
Sunder/host/payloads/host-<version-hash>/
```

The App rejects links and reparse points while staging, preserves executable modes on Unix, and writes the active payload descriptor atomically. This stable location prevents AppImage unmounts, moved macOS bundles, Velopack version cleanup, or App replacement from invalidating a running Host path.

When the bundled Host version changes, the App:

1. Stages the new payload without changing the active descriptor.
2. Authenticates to and stops the previous Supervisor.
3. Starts the staged Supervisor and waits for a compatible ready Runtime.
4. Commits the new active descriptor and removes old payloads.
5. Restarts the previous payload when activation fails.

Durable Host and Runtime state are not stored with executable payloads and survive payload replacement.

## Session Launch

- Windows launches the staged Supervisor as an independent current-user process.
- Linux uses a transient `systemd-run --user` service with restart-on-failure and falls back to `setsid` when no user systemd manager is available.
- macOS submits a transient `launchctl` job in the current login session. It does not install a LaunchAgent and removes the legacy `dev.sunder.runtime` LaunchAgent when encountered.

These launchers keep the Host independent from the App UI without registering login-start or machine-wide services.

## State And Authentication

Host state defaults to:

```text
<LocalApplicationData>/Sunder/host/v1/
```

Runtime and package state defaults to:

```text
<LocalApplicationData>/Sunder/runtime/v1/
```

The Supervisor creates a stable Host identity and a new external bearer credential when it starts. The private connection document is available only to the current user. Windows protects its token with DPAPI CurrentUser; Unix connection directories and files use current-user-only modes. App and CLI clients resolve this Host connection before the legacy direct-Runtime connection.

The Supervisor owns the Runtime worker, private IPC endpoint, worker credential, durable desired state, crash recovery, and lifecycle operations. Authenticated Host shutdown remains available for App-owned payload replacement.

## Distribution And Removal

- Windows distributes the signed Velopack `Setup.exe`.
- macOS distributes a signed and notarized DMG containing `Sunder.app` and an Applications shortcut.
- Linux distributes the Velopack AppImage.

App updates require no elevation. The updated App stages and activates its matching Host payload on the next start.

Windows uninstall stops the current-user Host and removes staged executable payloads. Deleting a macOS app bundle or Linux AppImage has no operating-system uninstall callback; Host and Runtime state remain user data, and a running Host naturally ends with the login session.
