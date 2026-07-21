# Sunder Host Architecture

## Status

This document defines the target architecture for a Sunder desktop client that connects to a Sunder Host on the same or a different machine. Implementation is incremental. Existing Runtime API behavior remains supported until each Host-owned concern is migrated.

## Product Model

Sunder has three process roles:

- `Sunder.App` is the desktop client. It owns the shell, App package modules, local browser integration, local file selection, and client-local presentation state.
- `Sunder.Host.Supervisor` is the stable Host process. It owns the public network endpoint, Host identity, paired clients, Runtime worker lifecycle, updates, auditing, and resumable content transport.
- `Sunder.Runtime.Host` is the Runtime worker. It owns installed package state, Runtime package modules, package state/settings/secrets, Registry access, package operations, and user-local execution.

The Supervisor is the only remotely reachable process. The Runtime worker communicates through a private Unix domain socket or Windows named pipe and must not bind a public listener in supervised mode.

## Required Properties

1. The Host endpoint remains reachable while the Runtime worker is stopped, restarting, updating, or failed.
2. Runtime worker replacement does not change the Host URL or Host trust identity.
3. Every paired client sees the same committed Host and package state.
4. Clients have distinct identities only for revocation, auditing, transient ownership, cancellation, and optimistic concurrency.
5. All paired clients initially receive the same `shared-owner` authority.
6. Package operation JSON remains a bounded control plane. Binary content uses a separate streaming data plane.
7. App filesystem paths are never interpreted as Runtime filesystem paths.
8. App-role and Runtime-role package payloads are selected independently for each process target.

## Protocols

Host control and Runtime data protocols are versioned independently.

| Protocol | Identity | Purpose |
| --- | --- | --- |
| Host | `dev.sunder.host` | Stable Host handshake, lifecycle, trust, clients, updates, and Host events |
| Runtime | `dev.sunder.runtime` | Package state, operations, streams, settings, Registry, and Runtime events |

Product versions do not determine protocol compatibility.

The Host exposes `/api/host/handshake` even when no Runtime worker is available. Runtime `/api/handshake` and `/api/v1/**` requests are selectively forwarded to the active worker. If no worker is ready, the Host returns typed `503` Problem Details instead of a connection refusal.

In supervised mode, Runtime does not bind TCP. The Supervisor creates a random per-worker Unix domain socket on macOS/Linux or a current-user-only named pipe on Windows, and Runtime serves HTTP/1.1 over that endpoint. Unix endpoint directories use mode `0700` and socket files use mode `0600`. Runtime publishes the logical origin `http://sunder-runtime/` only as an HTTP routing and credential scope; a custom client connection callback maps that origin to private IPC. Standalone development Runtime mode retains its validated loopback TCP listener.

## Identities

| Identity | Lifetime | Use |
| --- | --- | --- |
| `HostId` | Current-user Host state | Stable trust and profile identity |
| Host SPKI pin | Host key lifetime | TLS server authentication |
| `SupervisorInstanceId` | Supervisor process | Detect Supervisor restarts |
| `RuntimeInstanceId` | Runtime worker process | Fence Runtime snapshots, events, logs, and leases |
| `WorkerEpoch` | Promoted worker | Prevent stale gateway forwarding |
| `ClientId` | Paired client installation | Revocation, audit, transient ownership, and cancellation |
| `MutationId` | User action | Idempotent mutation retry |

The Host derives `ClientId` from authenticated credentials. It never trusts a caller-provided identity header. Before forwarding, the Host removes external authorization, forwarding, and reserved Sunder identity headers. It forwards a Host-generated internal assertion over the private worker channel.

Every worker receives a new IPC endpoint, bearer credential, and monotonically increasing in-process `WorkerEpoch`. The gateway exposes only a `Ready` worker, never rolls its connection pool back to an older epoch, and disposes the prior pool on promotion. Both Supervisor and Runtime attempt endpoint cleanup so graceful shutdown, worker failure, and Supervisor loss do not leave a reusable private endpoint.

## State Boundaries

Host state is current-user-scoped and survives Runtime reset:

```text
host/v1/
  identity/
  clients/
  audit/
  operations/
  deployments/
  versions/
  content/
  connection/
  runtime/v1/
```

The Runtime root remains independently resettable. Resetting Runtime package data must not delete Host identity, paired clients, worker deployment history, or Host control credentials.

## Lifecycle

The desktop App stages the bundled Supervisor and Runtime worker in a stable versioned directory under current-user application data. The staged payload is independent from AppImage mounts, macOS bundle movement, and Velopack version cleanup. It is activated only after authenticated readiness succeeds; failed activation restores the prior payload.

Opening the App starts the Supervisor for the current login session. Windows uses an independent user process, Linux prefers a transient `systemd --user` service and falls back to `setsid`, and macOS submits a transient `launchctl` job. No machine service, dedicated account, user enrollment handoff, or privileged companion package is installed. The fixed loopback origin means simultaneous Hosts in multiple logged-in user sessions are not yet supported.

The Supervisor retains the Runtime worker process handle and persists desired state and lifecycle operations. Runtime lifecycle states are `Stopped`, `Starting`, `Ready`, `Draining`, `Stopping`, `Restarting`, `Updating`, `RollingBack`, `Failed`, and `CrashLoop`.

Lifecycle mutations are durable and idempotent. They return an operation ID that can be queried after disconnect or timeout. A restart completes only after the old worker exits, the Runtime root lease is released, the replacement worker authenticates over private IPC, and Runtime package bootstrap reaches `Ready`.

The initial durable implementation stores desired state, deployment generation, and operation records in one atomically replaced `runtime/v1/lifecycle.json` document under the Host state root. The temporary payload is flushed to durable storage before replacement, and the containing directory is synchronized where the platform/filesystem supports directory `fsync`. The atomic replacement is the acceptance point; post-replacement best-effort synchronization cannot turn a committed mutation into an in-memory rejection. An exclusive lifecycle lock prevents two Supervisor processes from owning the same Host state root.

Persisting that document is the mutation acceptance boundary. Before it, request cancellation produces no operation or desired-state change. After it, client disconnect or polling cancellation does not cancel worker orchestration. The operation remains queryable at `/api/host/v1/operations/{operationId}`. Reusing a `MutationId` with the same kind and expected generation returns the original operation before current-generation validation; reusing it with different input is a conflict. A different mutation conflicts while another lifecycle operation is active.

Accepted and running operations are resumed with the same operation ID after Supervisor recreation. When no operation is active, startup reconciles the persisted desired state: `Running` launches and verifies a worker, while `Stopped` and `Maintenance` do not. Transient process state, Runtime instance identity, and worker connection details are never restored from disk. Stopping the Supervisor closes its owned worker but preserves desired Runtime state so the App can recreate the Supervisor without turning an intended running Host into a durable stop.

Deployment generation advances when a lifecycle mutation is accepted and when Supervisor recovery promotes a worker without a client mutation. Initial operation records are retained indefinitely so retry idempotency is not weakened by premature pruning. A future bounded retention design must preserve a durable mutation outcome index.

The App updates the versioned Supervisor and Runtime worker payload together. It stages the new payload, stops the prior Supervisor, health-checks the replacement, and commits or rolls back without elevation. Stopping the Supervisor cannot be made remotely recoverable through the same Supervisor.

## Client Model

Multiple Apps may connect concurrently. Shared package and Agent state is not partitioned by client. These resources remain client-owned until committed or adopted:

- Content uploads and downloads.
- Package lifecycle stages.
- Stack import plans.
- Browser callback sessions.
- Registry authentication sessions.
- Development package owner leases.
- Reset challenges.

Shared replacement-style mutations require an expected revision. Retryable mutations require a stable `MutationId`. An exact retry returns the prior outcome; reusing an ID with different content is a conflict.

App selection, drafts, viewport state, and App-side package faults remain client-local. An App presentation failure must not disable a Runtime package for other clients.

## Network Trust

Production remote mode requires HTTPS and explicit pairing. The Host provisions a stable ECDSA identity, publishes a self-signed certificate, and clients pin the SHA-256 SubjectPublicKeyInfo hash. Certificate renewal reuses the same key. A changed pin fails closed and requires explicit recovery.

Each paired client owns a protected asymmetric key. It signs a one-time Host challenge to obtain a short-lived in-memory access token. The Host stores the public key and token hashes, never the private key or raw token. Revoking one client cancels its requests and transients without changing shared committed state.

Remote mode initially targets private LAN and VPN deployments. It is not an internet-facing multi-tenant service.

## Package Targets

Cross-machine operation requires package format v2 with independent App and Runtime target declarations. One package artifact may contain multiple exact role/RID projections. The Host stores the complete artifact, activates only its Runtime projection, and resolves App snapshots for each connecting App target.

The initial exact RIDs are:

- `win-x64`
- `win-arm64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

There is no RID fallback in the first implementation. Native files must declare and match an exact role/RID target.

## Migration Sequence

1. Make Runtime streams and clients safe across worker instance changes.
2. Introduce Host control contracts and a loopback compatibility gateway.
3. Move Runtime launching and lifecycle into the Supervisor.
4. Move the worker to private IPC.
5. Add durable current-user Host lifecycle and versioned App-owned payload activation.
6. Add TLS identity, pairing, client profiles, and revocation.
7. Add multi-client ownership, idempotency, and optimistic concurrency.
8. Add the resumable content channel.
9. Add App-local browser callback relay and remote workspace semantics.
10. Add target-aware package format, Registry resolution, and first-party package migration before declaring cross-platform remote support complete.

## Non-Goals For V1

- Public internet or multi-tenant hosting.
- Different authorization roles for paired clients.
- Zero-downtime Runtime worker updates.
- Parallel out-of-order upload chunks.
- Permanent general-purpose blob storage.
- Arbitrary unrestricted Host filesystem browsing.
- Remote Supervisor self-update.
