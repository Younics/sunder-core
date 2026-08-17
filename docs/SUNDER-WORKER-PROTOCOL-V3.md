# Sunder Worker Protocol V3

This document defines the clean-break isolated Runtime protocol selected by the exact Host capability
`worker-protocol.v3`. Its wire identity is `sunder.worker.v3` with protocol version `3`.

Worker V3 is a package-authoring boundary, not a transport for managed package modules. A V3 executable
registers native worker contributions through `Sunder.Sdk.Worker`. Neither the Host nor the child process
loads, discovers, or adapts `ISunderRuntimePackageModule`.

## Evolution

- Every Manifest V3 Runtime target has exact kind `worker` and requires `worker-protocol.v3`.
- `dotnet`, `process`, Worker V1, and a Runtime target without that exact ordinal capability are invalid.
- The Host selects the protocol before process launch and sets `SUNDER_WORKER_PROTOCOL` to the selected wire identity.
- `host.hello` and `worker.ready` must bind the same exact identity and version.
- There is no in-band negotiation, probing, downgrade, or fallback after launch.
- A Host without V3 support rejects the target during compatibility validation before starting its executable.
- Current .NET and Node tooling emit `worker-protocol.v3`.

## Transport

V3 uses a standard-stream transport with fail-closed framing rules:

- standard input is Host-to-worker protocol traffic;
- standard output is worker-to-Host protocol traffic;
- standard error is a bounded emergency diagnostic channel only;
- every frame has exactly one canonical `Content-Length` header followed by strict UTF-8 JSON;
- unknown envelope types, unknown properties, duplicate or ASCII-case-colliding properties, invalid ordering, and reused ids are fatal;
- the Host clears the inherited environment and supplies only explicit package, protocol, path, temporary-directory, and locale values;
- malformed protocol, lifecycle deadline failure, transport-queue exhaustion, or unexpected process exit faults the exact package activation without automatic restart; per-call stream-consumer exhaustion is isolated as described below.
- The current Host allows up to two minutes for generation commit, migration preparation, and activation acknowledgement. The shorter process-handshake deadline does not bound package-owned generation startup work.

Control frames stay bounded. Data that cannot safely fit after JSON encoding uses a Host-issued ephemeral payload
handle. A transport payload handle only moves bytes for one protocol operation. It is not an RPC content reference
and carries none of the content store's owner, audience, generation, endpoint, expiry, repeatability, or use-count
authority.

## Composition And Lifecycle

Composition must finish while a replacement session is still only a candidate. Candidate work must not begin until
the previous generation has drained. V3 therefore has five distinct barriers.

1. `host.hello` / `worker.ready`
   - The worker validates the exact sanitized process environment.
   - The worker invokes configuration once and returns one immutable contribution catalog.
   - No background, generation-owned, callback, auth, or RPC work may start.
2. `host.start-candidate` / `worker.candidate-started`
   - A .NET worker awaits every `ISunderWorkerLifecycleService.PrepareCandidateAsync` in registration order, then invokes its optional non-destructive candidate-start callback.
   - Failure prevents publication.
   - A discarded candidate proceeds directly to reverse-order lifecycle-service shutdown without a generation commit or activation.
3. `host.commit-generation` / `worker.generation-committed`
   - The Host supplies the exact activation id and reserved candidate session generation. This fence is not public yet.
   - A .NET worker awaits every lifecycle service's generation commit in registration order, then invokes its optional generation-commit callback; inbound dispatch remains closed.
   - Retrying that exact commit is idempotent; a different commit is fatal.
4. `host.prepare-storage-migrations` / `worker.storage-migrations-prepared`
   - The Host supplies the exact committed activation id and session generation after every process participant has committed.
   - The worker invokes its optional pre-migration callback with a generation-fenced read-only state surface.
   - Only `worker.state-get`, `worker.state-list`, and required transport-payload cleanup are admitted. Settings,
     state mutation, files, secrets, RPC, callbacks, auth, and provider dispatch remain closed.
   - The Host awaits preparation from every process participant before activating any participant or running any
     declared storage migration.
5. `host.activate` / `worker.activated`
     - The Host has verified a hidden candidate RPC graph while public catalog visibility and external lease admission remain closed.
     - The worker opens ordinary worker-originated Host calls; Host data and dependency RPC resolve only against the exact candidate generation.
     - Declared storage migrations run before lifecycle-service and option-level activation callbacks.
     - A .NET worker awaits every lifecycle service's activation in registration order and then its option-level activation callback.
     - The acknowledgement is sent only after declared migrations and all activation work complete successfully.
    - After every worker acknowledges, the Host atomically publishes package and UI snapshot authority, promotes the
      candidate provider endpoints, emits `SessionGenerationChanged`, and opens external lease admission.

Host-mediated logging has a separate lifecycle lane and may be used by candidate, generation, activation, active,
and shutdown callbacks. The worker drains that lane before acknowledging shutdown.

The initial commit fixes the worker activation's minimum session generation, and `host.activate` must echo that
generation exactly. If the same process activation survives a later session publication, an inbound RPC provider
snapshot may carry that later `sessionGeneration`. A value later than the initial commit is valid; a value lower
than the initial commit is a fatal protocol violation.

After successful activation, a .NET worker monitors each lifecycle service's stable `Completion` task. An unexpected
fault or cancellation reports a worker failure for the exact active generation. `worker.generation-fault` reports a bounded failure for the exact active generation. The Host fences the report by
activation and generation, withdraws only that generation, and terminates its worker. Protocol failures also fault
the process activation through the supervised process boundary.

`host.shutdown` may retire an active generation or discard a prepared candidate. The worker cancels inbound work,
drains handlers and worker-originated Host calls, releases Host-issued handles and call scopes, runs its option-level
shutdown callback, shuts prepared lifecycle services down in reverse registration order, disposes package-owned
composition, drains logging, sends `worker.shutdown-ack`, and exits. The Host kills the process tree if the shared
shutdown deadline expires.

## Immutable Contributions

`worker.ready` contains one closed contribution catalog:

- `settingsSchema`: an optional canonical settings schema;
- `storageMigrations`: immutable state and secret startup migration plans;
- `callbackHandlers`: the immutable package-local callback-handler ids;
- `authHandler`: whether the reserved authentication handler is present;
- `rpcProviders`: the implemented schema-first RPC provider identities;
- `candidateLifecycle` and `generationLifecycle`: whether the implemented option callbacks or composition lifecycle services exist;
- `migrationPreparationLifecycle`: required `true`, confirming support for the exact-generation preparation barrier.

Registration closes when `worker.ready` is sent. No contribution can appear, disappear, or change for that process
activation. The Host validates manifest declarations, local RPC descriptor hashes, settings schema, RPC providers,
and the required reserved values before constructing `ActiveLoadedPackage` registrations.

The Host creates direct remote handler registrations. It does not create a fake contribution registry, package
module, or managed package lifecycle participant. The supervised child process is the Host-side lifecycle boundary.

## Invocation Envelopes

The only implemented Host-to-worker work envelope is schema-first RPC `host.invoke`. Its root property set is exactly
`type`, `id`, `kind`, `providerId`, `serviceId`, `methodId`, `request`, and `context`. Its `context` property set is
exactly `callerPackageId`, `callerPackageVersion`, `deadlineUtc`, `callDepth`, and `provider`. Its `provider` property
set is exactly `packageId`, `packageVersion`, `providerId`, `contractId`, `contractVersion`, `descriptorSha256`,
`abiSha256`, `activationId`, `activationEpoch`, `sessionGeneration`, `endpointReference`, `catalogRevision`, `state`, and
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
      "descriptorSha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
      "abiSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
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

The `server-stream` kind is an RPC provider stream; Manifest V3 has no parallel package-scoped Runtime operation or
stream transport. Callback and auth work use their dedicated Host-stamped lifecycle envelopes, and dynamic storage
key migration uses bounded Host-mediated migration requests.

Worker-to-Host requests use worker-issued ids. They never carry caller identity. The Host stamps identity from the
authenticated process activation and applies the active session lease. The implemented capabilities are:

| Capability | Methods |
| --- | --- |
| Settings | `worker.settings-get`, `worker.settings-set`, `worker.settings-delete` |
| State | `worker.state-get`, `worker.state-set`, `worker.state-delete`, `worker.state-list` |
| Files | `worker.files-read`, `worker.files-write`, `worker.files-delete` |
| Secrets | `worker.secrets-get`, `worker.secrets-set`, `worker.secrets-delete` |
| Storage migration | `worker.migration-keys`, `worker.migration-apply` |
| Logging | `worker.logging-write`, `worker.provider-fault-diagnostic` |
| RPC | `worker.scope-open`, `worker.scope-close`, `worker.get-provider`, `worker.report-invariant-violation`, `worker.discover`, `worker.watch`, `worker.invoke`, `worker.subscribe`, `worker.content-register`, `worker.content-open`, `worker.content-release` |
| Transport payload | `worker.payload-allocate`, `worker.payload-release` |

Worker requests terminate with `host.result`, ordered `host.event` plus `host.complete`, or `host.error`.
`worker.cancel` is subject to the same terminal-drain rule.

Each received RPC stream has a nonblocking per-call queue bounded independently by message count and encoded JSON
bytes. If a producer outruns its consumer, the receiver atomically classifies the rejection as `count`, `bytes`, or
`count+bytes`, cancels only that call, and surfaces `rpc.worker.stream-queue-limit` as `resource-exhausted` with
`possibly-dispatched` / `unsafe`. Healthy sibling calls and worker activations remain available. A bounded local
message suffix may identify the limit, queue accounting, operation, and sanitized provider/service/method; it never
includes request data, secrets, paths, call-scope ids, or raw endpoint references. This diagnostic is not
`SunderRpcError.Details` and does not add a wire property.

For V3, every authenticated `host.error.error` object contains exactly `kind`, `code`, `message`,
`dispatchState`, and `retrySafety`. `dispatchState` is `not-dispatched` or `possibly-dispatched`, and
`retrySafety` is `safe` or `unsafe`.

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
- `worker.content-release` carries `id`, either `scopeId` or `invocationId`, and `handleId`. A successful result
  is JSON `null`.

The Host maps `scopeId` only to an internally created scope for the authenticated process activation. It bounds
open scopes and materialized handles, rejects reused or fabricated identifiers, and revokes scope calls, references,
leases, and files on close, deadline, caller retirement, shutdown, or process failure. A worker never supplies a
caller package, activation, session generation, content owner, or content audience.

`scope-close` and `content-release` use a separately bounded control-call lane reserved
from ordinary RPC call capacity. Scope and handle capacity remains reserved until cleanup receives a terminal Host
response, and materialized-handle capacity is reserved before content is acquired. The configured control bound must
cover every permitted open scope, materialized content handle, and transport payload handle, so ordinary call
saturation or resource churn cannot prevent authority cleanup. Cancellation that crosses a successful
resource-producing result still releases the returned scope or handle before normal work continues.

### Settings, State, Files, Secrets, And Transport Payloads

V3 settings and state calls expose the existing `IPackageSettings` and `IPackageKeyValueStore` contracts. The normal
surfaces are valid only during activation and active operation. During migration preparation, the dedicated read-only
context may issue only `worker.state-get` and `worker.state-list`; the Host binds those requests to the exact generation
from the enclosing barrier. Requests never carry package or generation identity and use these exact envelopes:

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
- `worker.migration-keys` carries `id` and `store` (`state` or `secrets`) and returns a unique, ordinally sorted physical-key snapshot for a declared dynamic-cleanup rule.
- `worker.migration-apply` carries `id`, `store`, and 1 to 32 rules exactly matching that store's immutable declaration. Dynamic rules carry key-only actions resolved from the returned snapshot; secret values never cross the worker boundary.
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

Declared migrations run automatically after every worker completes migration preparation and before the activation
callback. The Host accepts them only while the exact generation is activating, rejects undeclared, changed, duplicate,
or incomplete plans, and revalidates a dynamic physical-key snapshot inside the storage transaction before mutation.
The Host derives a stable migration id from the exact package version and store, hashes the canonical declared plan,
and records completion only after every package activates. A completed exact id/hash is skipped on retry, while a later
package version runs independently even when intermediate versions were skipped. Before migration starts, the Host
backs up state, secrets, and completion journals for every package in the candidate; any migration or activation
failure fences and stops all candidate workers before restoring every package and store. `worker.activated` is invalid
until every declared store migration completes.

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

Before a provider-fault `worker.error`, the .NET V3 worker attempts a `worker.provider-fault-diagnostic` call carrying
only `id`, `invocationId`, `providerId`, `serviceId`, `methodId`, a 256-character exception `exceptionType`, and the
lowercase SHA-256 `exceptionFingerprint` of that bounded type. The Host verifies the invocation and provider/method
identity against its active call, recomputes the fingerprint, and durably writes a Host-authored package event before
returning JSON `null`. Exception messages, stacks, payloads, stderr, settings, and secrets are never diagnostic fields;
V3 stderr is drained without forwarding its contents to Host logging. Diagnostic sink failure is isolated and the
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
- classification of provider faults, generation faults, and protocol faults.

A worker-supplied identity or generation is only an echoed fence. It never grants authority. Settings, state, files,
and secrets use Host-mediated requests even for local workers so validation, atomicity, recovery, and App/worker
consistency remain centralized. Host ownership of callbacks, auth, storage migration, or generation-fault policy does
not imply that those reserved surfaces are exposed by Worker V3.

## Local Paths

Package content, an activation-owned temporary directory, and a role-local workspace may be exposed as explicit
local capabilities because V3 currently supervises a local process. Canonical settings, state, and secret document
paths are not capability APIs and are not exposed to a V3 worker. A future non-local transport may omit local path
capabilities without changing the mediated APIs.

## Package Authoring

`Sunder.Sdk.Worker` exposes a V3 entry point that creates a worker context and an immutable options object. The
context supplies Host-mediated services, including RPC. Options contain RPC provider registrations, an optional
settings schema, callback/auth handlers, and lifecycle callbacks. Package code retains ordinary service and handler
classes behind an executable `Program`.

The SDK invokes configuration before `worker.ready`, candidate startup during `host.start-candidate`, generation
participants during `host.commit-generation`, activation callbacks before `worker.activated`, and shutdown callbacks
before `worker.shutdown-ack`.

## Implementation Status

Implemented: exact V3 selection, immutable RPC providers and optional settings schema, staged lifecycle callbacks,
Host-to-worker unary and server-stream RPC, worker-originated RPC scopes and content authority, settings, state,
files, secrets, logging, callbacks/auth, dynamic storage-key migration, generation-fault reporting, and transport
payloads. Package-scoped Runtime operations and streams are intentionally absent; owner RPC replaces them.
