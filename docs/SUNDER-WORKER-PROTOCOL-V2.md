# Sunder Worker Protocol V2

This document defines the clean-break isolated Runtime protocol selected by the exact Host capability
`worker-protocol.v2`. Its wire identity is `sunder.worker.v2` with protocol version `2`.

Worker V2 is a package-authoring boundary, not a transport for managed package modules. A V2 executable
registers native worker contributions through `Sunder.Sdk.Worker`. Neither the Host nor the child process
loads, discovers, or adapts `ISunderRuntimePackageModule`.

## Evolution

- A `runtime/worker` target opts into V2 by requiring `worker-protocol.v2`.
- A worker target without that exact ordinal capability continues to use `sunder.worker.v1`.
- Existing legacy `runtime/process` archives follow the same selection rule while that kind remains readable.
- Fresh C# target authoring rejects `process` in favor of `worker`; aggregation preserves validated legacy
  `process` leaves rather than rewriting their target identity.
- The Host selects the protocol before process launch and sets `SUNDER_WORKER_PROTOCOL` to the selected wire identity.
- `host.hello` and `worker.ready` must bind the same exact identity and version.
- There is no in-band negotiation, probing, downgrade, or fallback after launch.
- A Host without V2 support rejects the target during compatibility validation before starting its executable.
- Existing Node workers remain on V1 until their tooling explicitly emits `worker-protocol.v2`.

## Transport

V2 retains the V1 standard-stream transport and fail-closed framing rules:

- standard input is Host-to-worker protocol traffic;
- standard output is worker-to-Host protocol traffic;
- standard error is a bounded emergency diagnostic channel only;
- every frame has exactly one canonical `Content-Length` header followed by strict UTF-8 JSON;
- unknown envelope types, unknown properties, duplicate or case-colliding properties, invalid ordering, and reused ids are fatal;
- the Host clears the inherited environment and supplies only explicit package, protocol, path, temporary-directory, and locale values;
- malformed protocol, deadline failure, queue exhaustion, or unexpected process exit faults the exact package activation without automatic restart.

Control frames stay bounded. Data that cannot safely fit after JSON encoding uses a Host-issued ephemeral payload
handle. A transport payload handle only moves bytes for one protocol operation. It is not an RPC content reference
and carries none of the content store's owner, audience, generation, endpoint, expiry, repeatability, or use-count
authority.

## Composition And Lifecycle

Composition must finish while a replacement session is still only a candidate. Candidate work must not begin until
the previous generation has drained. V2 therefore has four distinct barriers.

1. `host.hello` / `worker.ready`
   - The worker validates the exact sanitized process environment.
   - The worker invokes configuration once and returns one immutable contribution catalog.
   - No background, generation-owned, callback, operation, stream, or RPC work may start.
2. `host.start-candidate` / `worker.candidate-started`
   - The worker invokes its optional non-destructive candidate-start callback.
   - Failure prevents publication.
   - A discarded candidate proceeds directly to shutdown without a generation commit.
3. `host.commit-generation` / `worker.generation-committed`
   - The Host supplies the exact activation id and initial committed session generation.
   - The worker invokes its optional generation-commit callback; inbound dispatch remains closed.
   - Retrying that exact commit is idempotent; a different commit is fatal.
4. `host.activate` / `worker.activated`
    - The Host has published and verified all contribution catalogs and opened external lease admission.
    - The worker opens inbound dispatch and ordinary worker-originated Host calls.
    - The acknowledgement is sent only after the worker activation callback completes successfully.

Host-mediated logging has a separate lifecycle lane and may be used by candidate, generation, activation, active,
and shutdown callbacks. The worker drains that lane before acknowledging shutdown.

The initial commit fixes the worker activation's minimum session generation, and `host.activate` must echo that
generation exactly. If the same process activation survives a later session publication, an inbound RPC provider
snapshot may carry that later `sessionGeneration`. A value later than the initial commit is valid; a value lower
than the initial commit is a fatal protocol violation.

`worker.generation-fault` is reserved and unimplemented. The SDK exposes no generation-fault reporting API and the
Host accepts no such envelope in this protocol slice. Protocol failures still fault the process activation through
the supervised process boundary.

`host.shutdown` may retire an active generation or discard a composed candidate. The worker cancels inbound work,
drains handlers and worker-originated Host calls, releases Host-issued handles and call scopes, runs its shutdown
callback, drains logging, sends `worker.shutdown-ack`, and exits. The Host kills the process tree if the shared
shutdown deadline expires.

## Immutable Contributions

`worker.ready` contains one closed contribution catalog:

- `settingsSchema`: an optional canonical settings schema;
- `runtimeOperations`: reserved and required to be an empty array;
- `runtimeStreams`: reserved and required to be an empty array;
- `callbackHandlers`: reserved and required to be an empty array;
- `authHandler`: reserved and required to be `false`;
- `rpcProviders`: the implemented schema-first RPC provider identities;
- `candidateLifecycle` and `generationLifecycle`: whether the implemented lifecycle callbacks exist.

Registration closes when `worker.ready` is sent. No contribution can appear, disappear, or change for that process
activation. The Host validates manifest declarations, local RPC descriptor hashes, settings schema, RPC providers,
and the required reserved values before constructing `ActiveLoadedPackage` registrations.

The Host creates direct remote handler registrations. It does not create a fake contribution registry or package
module. One aggregate worker lifecycle participant represents the child process to the existing session publisher.

## Invocation Envelopes

The only implemented Host-to-worker work envelope is schema-first RPC `host.invoke`. Its root property set is exactly
`type`, `id`, `kind`, `providerId`, `serviceId`, `methodId`, `request`, and `context`. Its `context` property set is
exactly `callerPackageId`, `callerPackageVersion`, `deadlineUtc`, `callDepth`, and `provider`. Its `provider` property
set is exactly `packageId`, `packageVersion`, `providerId`, `contractId`, `contractVersion`, `contractSha256`,
`activationId`, `activationEpoch`, `sessionGeneration`, `endpointReference`, `catalogRevision`, `state`, and
`faultCode`. Required nullable properties such as `faultCode` are present with JSON `null`; properties are not omitted
and no additional properties are accepted.

```json
{
  "type": "host.invoke",
  "id": "hostCall1",
  "kind": "unary",
  "providerId": "example.provider",
  "serviceId": "messages",
  "methodId": "send",
  "request": { "message": "hello" },
  "context": {
    "callerPackageId": "caller.package",
    "callerPackageVersion": "1.0.0",
    "deadlineUtc": "2026-08-04T12:00:00.0000000Z",
    "callDepth": 1,
    "provider": {
      "packageId": "example.package",
      "packageVersion": "1.0.0",
      "providerId": "example.provider",
      "contractId": "example.rpc",
      "contractVersion": "1.0.0",
      "contractSha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
      "activationId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "activationEpoch": 1,
      "sessionGeneration": 8,
      "endpointReference": "rpc1_example",
      "catalogRevision": 1,
      "state": "active",
      "faultCode": null
    }
  }
}
```

`kind` is exactly `unary` or `server-stream`. A unary call terminates with `worker.result` or `worker.error`. A
server-stream call emits ordered `worker.event` frames and then `worker.complete`, or terminates with `worker.error`.
The provider snapshot must identify the registered package, process activation, provider, and contract, be active and
unfaulted, and meet the generation floor established by the initial commit. `host.cancel` is advisory but must be
drained to a terminal frame within the cancellation deadline.

The `server-stream` kind is an RPC provider stream, not the package-scoped Runtime stream capability. Runtime
operations and Runtime streams, callbacks, auth, and dynamic storage-key migration are reserved and unimplemented;
there are no V2 invocation envelopes for them. A V2 `worker` or legacy `process` target that requires
`runtime-operations.v1`, `runtime-invocation-errors.v1`, `callbacks.v1`, `auth.v1`, or
`storage.key-migration.v1` is rejected during compatibility validation before its executable is launched.

Worker-to-Host requests use worker-issued ids. They never carry caller identity. The Host stamps identity from the
authenticated process activation and applies the active session lease. The implemented capabilities are:

| Capability | Methods |
| --- | --- |
| Settings | `worker.settings-get`, `worker.settings-set`, `worker.settings-delete` |
| State | `worker.state-get`, `worker.state-set`, `worker.state-delete`, `worker.state-list` |
| Files | `worker.files-read`, `worker.files-write`, `worker.files-delete` |
| Secrets | `worker.secrets-get`, `worker.secrets-set`, `worker.secrets-delete` |
| Logging | `worker.logging-write`, `worker.provider-fault-diagnostic` |
| RPC | `worker.scope-open`, `worker.scope-close`, `worker.get-provider`, `worker.report-invariant-violation`, `worker.discover`, `worker.watch`, `worker.invoke`, `worker.subscribe`, `worker.content-register`, `worker.content-open`, `worker.content-release` |
| Transport payload | `worker.payload-allocate`, `worker.payload-release` |

Worker requests terminate with `host.result`, ordered `host.event` plus `host.complete`, or `host.error`.
`worker.cancel` is subject to the same terminal-drain rule.

Caller-owned RPC scope envelopes have these exact authority fields:

- `worker.scope-open` carries `id` and nullable `deadlineUtc`. Its `host.result.value` contains the opaque
  Host-issued `scopeId` and Host-clamped `deadlineUtc`.
- `worker.scope-close` carries `id` and `scopeId`. A successful result is JSON `null`. Closing an unknown or
  already closed scope is a protocol violation; closing is otherwise idempotent in the SDK.
- Scoped `worker.get-provider`, `worker.report-invariant-violation`, `worker.discover`, `worker.watch`, `worker.invoke`, and `worker.subscribe`
  retain their unscoped fields and add `scopeId`.
- `worker.report-invariant-violation` carries the exact `endpointReference` and a sanitized `exceptionMessage` of at
  most 512 characters. The Host accepts it only from the designated Agent orchestrator and only while that endpoint
  remains visible and belongs to the current activation; stale or unauthorized reports return `false`.
- Scoped `worker.content-register` carries `id`, `scopeId`, the exact target `endpointReference`, a contained
  worker `filePath`, and registration `options`.
- Scoped `worker.content-open` carries `id`, `scopeId`, and the content `reference`. Its result is a transport-only
  `handleId` and contained Host-materialized `filePath`.
- V2 `worker.content-release` carries `id`, either `scopeId` or `invocationId`, and `handleId`. A successful result
  is JSON `null`. V1 invocation content retains `worker.content-discard`; it does not gain caller-owned scopes.

The Host maps `scopeId` only to an internally created scope for the authenticated process activation. It bounds
open scopes and materialized handles, rejects reused or fabricated identifiers, and revokes scope calls, references,
leases, and files on close, deadline, caller retirement, shutdown, or process failure. A worker never supplies a
caller package, activation, session generation, content owner, or content audience.

`scope-close`, `content-release`, and legacy V1 `content-discard` use a separately bounded control-call lane reserved
from ordinary RPC call capacity. Scope and handle capacity remains reserved until cleanup receives a terminal Host
response, and materialized-handle capacity is reserved before content is acquired. The configured control bound must
cover every permitted open scope, materialized content handle, and transport payload handle, so ordinary call
saturation or resource churn cannot prevent authority cleanup. Cancellation that crosses a successful
resource-producing result still releases the returned scope or handle before normal work continues.

### Settings, State, Files, Secrets, And Transport Payloads

V2 settings and state calls expose the existing `IPackageSettings` and `IPackageKeyValueStore` contracts. They are
valid only during activation and active operation, never carry package or generation identity, and use these exact
envelopes:

- `worker.settings-get` carries `id`, `key`, and `mode` (`effective` or `stored`).
- `worker.settings-set` carries `id`, `key`, and a non-null data `value`.
- `worker.settings-delete` carries `id` and `key`.
- `worker.state-get` carries `id`, `key`, and `mode` (`value` or `contains`).
- `worker.state-set` carries `id`, `key`, and a non-null data `value`.
- `worker.state-delete` carries `id` and `key`.
- `worker.state-list` carries `id` and nullable `prefix`.
- `worker.secrets-get` carries `id` and `key`.
- `worker.secrets-set` carries `id`, `key`, and a non-null data `value`.
- `worker.secrets-delete` carries `id` and `key`.
- `worker.files-read` carries `id` and `relativePath`.
- `worker.files-write` carries `id`, `relativePath`, and upload payload `contents`.
- `worker.files-delete` carries `id` and `relativePath`.

String results and set values use either an inline form or a transport payload. Missing get results use an inline
JSON `null`; empty strings remain valid values. Inline strings and the complete serialized inline key array are
limited to 65,536 UTF-8 bytes inclusive; larger values use a transport payload:

```json
{ "kind": "inline", "value": "text" }
{ "kind": "inline", "value": null }
{ "kind": "payload", "handleId": "...", "filePath": "...", "length": 1048576, "sha256": "..." }
```

`state-get` with `mode: "contains"` returns a JSON Boolean. `state-list` returns either
`{ "kind": "inline", "keys": [...] }` or a payload whose strict UTF-8 bytes are the complete JSON key array. Keys
must be unique and ordinally sorted. Secret values use the same data forms but remain non-enumerable. File reads
return JSON `null` for a missing file or a Host-issued payload descriptor; file writes always use an upload payload.
Settings, state, and secret values are limited to 1,048,576 UTF-8 bytes, while file payloads are limited to
16,777,216 bytes. A complete serialized state key list that exceeds the 1,048,576-byte data-payload bound returns
`resource-exhausted`; callers should use a narrower prefix when possible.

For worker-to-Host payloads, `worker.payload-allocate` carries `id` and the exact byte `length`. Its result contains
the Host-issued `handleId`, contained `filePath`, and echoed `length`. After writing the file, the worker references
it as `{ "kind": "payload", "handleId": "...", "length": ..., "sha256": "..." }`; a structurally valid set call
consumes and deletes that upload handle whether the storage mutation succeeds, is rejected, or is cancelled.

For Host-to-worker payloads, the result descriptor includes the contained file path and SHA-256. The worker verifies
the path, length, hash, strict UTF-8, and capability-specific bounds before use, then sends
`worker.payload-release` with `id` and `handleId`. Explicit payload release uses the control lane and returns JSON
`null`. Unknown, reused, wrong-direction, malformed, or hash-mismatched handles are protocol violations. Handles and
files are activation-scoped, separately bounded, and revoked on shutdown or process failure; they do not carry RPC
content authority.

Settings, state, file, and secret argument failures return an authenticated `validation` error without exposing
stored values or canonical filesystem paths. Storage corruption and I/O failures return sanitized `unavailable`
errors. Mutation cancellation is terminal-authoritative: `worker.cancel` remains advisory, a crossed successful
Host result reports success after commit, and a Host cancellation error reports cancellation before commit.

### Logging

`worker.logging-write` carries `id`, `channel`, integer `level`, nullable `category`, integer `eventId`, nullable
`eventName`, `message`, scalar `attributes`, and bounded `exceptions`. The `event` channel requires an event name and
routes to `IPackageEventLogger`; the `logger` channel requires a category and routes through the package-scoped
`ILoggerFactory`. A successful write returns JSON `null`.

Messages are limited to 16,384 characters. Categories and event names are limited to 256 characters. Each entry may
contain at most 64 unique attributes with 128-character keys and scalar values limited to 4,096 characters, plus an
exception chain of at most five entries. The Host owns package identity, sink selection, retention, formatting, and
sensitive-key redaction. Attributes cannot replace Host-owned provenance fields. Message and exception text are not
secret-safe and must never contain credentials.

Logging uses a separately bounded lane from ordinary worker calls and cleanup control calls. Structured event writes
are awaitable. Conventional `ILogger` calls are detached and failure-isolated, but remain bounded and are drained
before lifecycle shutdown acknowledgement. Logging is accepted from the completed `worker.ready` barrier through the
shutdown callback; malformed logging envelopes remain protocol violations.

Before a provider-fault `worker.error`, the .NET V2 worker attempts a `worker.provider-fault-diagnostic` call carrying
only `id`, `invocationId`, `providerId`, `serviceId`, `methodId`, a 256-character exception `exceptionType`, and the
lowercase SHA-256 `exceptionFingerprint` of that bounded type. The Host verifies the invocation and provider/method
identity against its active call, recomputes the fingerprint, and durably writes a Host-authored package event before
returning JSON `null`. Exception messages, stacks, payloads, stderr, settings, and secrets are never diagnostic fields;
V2 stderr is drained without forwarding its contents to Host logging. Diagnostic sink failure is isolated and the
original generic provider-fault error remains terminal. A diagnostic cannot authenticate another error kind, and a
non-provider terminal error after an accepted diagnostic is a protocol violation.

## Host Authority

The Host remains authoritative for all cross-process effects:

- package identity, activation identity, session generation, caller identity, and target registration;
- archive, target, SDK capability, RPC descriptor, request, response, and event validation;
- session leases, admission, retirement, deadlines, concurrency, nesting, stream rate, and resource limits;
- settings schema validation and effective values;
- atomic state/settings persistence and storage-key migration;
- path-contained files and activation-owned role-local workspace;
- secret encryption, key protection, persistence, and non-enumerability;
- package log routing, retention, and structured-attribute redaction;
- loopback callback listener, callback session ids, expiry, cancellation, and one-time completion;
- auth projection over the reserved `auth` callback handler;
- RPC permissions, discovery, catalog revisions, endpoint references, call scopes, and content authority;
- classification of operation failures, provider faults, generation faults, and protocol faults.

A worker-supplied identity or generation is only an echoed fence. It never grants authority. Settings, state, files,
and secrets use Host-mediated requests even for local workers so validation, atomicity, recovery, and App/worker
consistency remain centralized. Host ownership of callbacks, auth, storage migration, or generation-fault policy does
not imply that those reserved surfaces are exposed by Worker V2.

## Local Paths

Package content, an activation-owned temporary directory, and a role-local workspace may be exposed as explicit
local capabilities because V2 currently supervises a local process. Canonical settings, state, and secret document
paths are not capability APIs and are not exposed to a V2 worker. A future non-local transport may omit local path
capabilities without changing the mediated APIs.

## Package Authoring

`Sunder.Sdk.Worker` exposes a V2 entry point that creates a worker context and an immutable options object. The
context supplies Host-mediated services, including RPC. Options currently contain RPC provider registrations, an
optional settings schema, and lifecycle callbacks. This allows package code to retain ordinary service and handler
classes while replacing its Runtime module and contribution-registry entry point with an executable `Program`.

The SDK invokes configuration before `worker.ready`, candidate startup during `host.start-candidate`, generation
participants during `host.commit-generation`, activation callbacks before `worker.activated`, and shutdown callbacks
before `worker.shutdown-ack`.

## Implementation Status

Implemented: exact V2 selection, immutable RPC providers and optional settings schema, staged lifecycle callbacks,
Host-to-worker unary and server-stream RPC, worker-originated RPC scopes and content authority, settings, state,
files, secrets, logging, and transport payloads.

Reserved and unimplemented: dynamic storage-key migration callbacks, package-scoped Runtime operations and streams,
callbacks, auth, background-service orchestration, and generation-fault reporting. Their capability requirements must
not be used on V2 targets until the protocol and both endpoints implement them. Managed Runtime removal and complete
retirement of the legacy `process` archive alias remain separate future compatibility decisions.
