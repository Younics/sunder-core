# @sunder/sdk

> **Release/source channel:** The npm tarball README describes that published version. The copy on the repository's default branch tracks current source and may be ahead of npm; use the matching `sdk/v*` tag when auditing a release.

`@sunder/sdk` contains RPC V1 descriptors and generated bindings, the `sunder.worker.v1` Runtime worker API, and the web App bridge. Worker code receives the common package identity, content and temporary roots, persistent role-local workspace, Host-mediated settings/state/files/secrets, structured logging, RPC clients, callback/auth handlers, exact-generation fault reporting, and staged lifecycle signals. It does not expose managed module/DI contracts, background services, Stack contributor adapters, Avalonia, Registry clients, or package installation APIs.

Use `@sunder/sdk/browser` from any framework-agnostic web App target. That export targets only the injected `sunder.bridge.v1` API and contains browser-safe RPC, navigation, and external-link bridge types; it does not import Node built-ins, worker framing, or package tooling. React is one scaffold choice, not a web target requirement.

```ts
import { getSunder } from "@sunder/sdk/browser";

const catalog = await getSunder().rpc.discover("example.rpc");
```

The main export is dependency-free and intended for Node worker Runtime code and build-time descriptor generation. Keep browser code on the `/browser` export.

## Implementation-First RPC

The main export includes the closed `rpc` DSL for owned TS/JS development through package-tool schema-version-3 configuration. It provides `router`, `contract`, `provider`, `unary`, `serverStream`, and the frozen `schema` constructors. `RpcSchemaValue` and `RpcHandlers` retain schema-derived types, including `bigint`, `Uint8Array`, optional versus nullable properties, maps and unions. The package tool supplies contextual source checking against generated backend types without executing handlers.

```ts
import { runWorker } from "@sunder/sdk";
import { packageRpc } from "./rpc.js";

void runWorker(() => ({ providers: packageRpc.providerRegistrations }));
```

The bundler privately binds generated registrations; an unbundled router throws rather than inventing identity or hashes. There is no public `rpc.bind`, SDK runtime compiler or runtime dependency. The [owned source and release workflow](../package-tool/README.md#owned-v3-source-and-releases) documents explicit Accept/Check/Freeze, generated output and source-verified packing.

An owned `rpc.contract` may set `includeSchemaDialect: true` to preserve an existing
descriptor's optional, fixed JSON Schema Draft 2020-12 annotation during migration.
Omission/`false` keeps the default descriptor unchanged. This boolean does not select
another dialect or protocol, and descriptor-hash changes still require the normal
baseline workflow. C# exposes the same option as `SunderRpcServiceAttribute.IncludeSchemaDialect`.

Associated App policy is declared on the owning contract with
`appUse: { required: false, actions: ["discover", "invoke"] }`. Public contracts
require this opt-in; private contracts default to required use of all applicable
actions. Explicit policy defaults to optional use and omitted actions to all
applicable actions. Arrays must contain a distinct, nonempty subset of
`discover`/`invoke`/`subscribe` supported by the methods. Tooling carries policy in
receipts, leaving descriptor/ABI bytes unchanged and granting nothing to the Worker
from App policy.

Use `runtimeUse: { required: true, actions: ["discover", "invoke"] }` on an owned
contract to declare Runtime consumption. Both public and private contracts have
no Runtime use by default, regardless of provider registration. Explicit policy
defaults to optional readiness and all method-applicable actions, with the same
action validation as App policy. Worker receipts combine these exact owned uses
with locked imports; App receipts omit Runtime policy and grants. Required
readiness does not select an invocation endpoint.

The same router accepts lock-generated `rpcImports.<key>.provider(...)` bindings from
the reserved `#sunder/rpc` module. Those builders return typed `RpcImportedProvider`
declarations and use restored descriptor types, with no authored identity/hash/schema
copy. The tool checks all handlers and generates exact Runtime registrations; provider
intent grants no consumer use. See [implement an imported extension](../package-tool/README.md#implement-an-imported-extension).

Use `rpc.schema.json({ objectOnly: true })` for an open raw JSON object without a
property-count bound. It infers `JsonObject` and rejects arrays/scalars; ordinary
`rpc.schema.json()` retains the full `JsonValue` shape. Explicit `minProperties` or
`maxProperties` bounds can be combined with the object-only form.

## Worker Composition

Configure a worker synchronously. Sunder snapshots and freezes the returned composition before advertising it to the Host; lifecycle callbacks may perform asynchronous work and receive cancellation through `AbortSignal`:

This descriptor-first registration example remains the migration form used by existing templates. V3 source uses `packageRpc.providerRegistrations` instead.

```ts
import { PackageStorageKeyMigration, runWorker } from "@sunder/sdk";
import { createExampleMessagesRpcProviderRegistration } from "../.sunder/generated/rpc/example-messages.g";

void runWorker((context) => ({
  providers: [createExampleMessagesRpcProviderRegistration("example.provider", messagesService)],
  stateMigrations: [
    PackageStorageKeyMigration.exact("example.legacy.v1", "example.current.v1"),
  ],
  async onBeforeStorageMigrations(preparation, signal) {
    signal.throwIfAborted();
    const legacyValue = await preparation.state.get("example.legacy.v1", { signal });
    context.logging.createLogger("Example.Worker").debug(`legacy state present: ${legacyValue !== null}`);
  },
  async onActivated(_context, signal) {
    signal.throwIfAborted();
    context.logging.createLogger("Example.Worker").information("activated");
  },
}));
```

`context.settings` separates effective and stored settings. `context.state` and `context.files` provide Host-mediated opaque state and portable files, while `context.workspace.resolve()` addresses persistent role-local files such as SQLite databases. `context.secrets` keeps secret persistence behind Host mediation. `stateMigrations` and `secretMigrations` accept immutable `PackageStorageKeyMigration` exact, opaque-id, JSON-identity, case-insensitive JSON-identity, and dynamic-cleanup rules. The Host matches and applies the advertised plan atomically before `onActivated`; dynamic cleanup receives only a bounded Host-returned physical-key snapshot and never secret values. The pre-migration callback receives the exact generation and a separate read-only state surface; normal package data and RPC remain unavailable until activation. Conventional logger calls are detached and failure-isolated; `context.logging.events.write()` is awaitable.

`context.rpc.createCallScope()` creates caller-owned Host authority with one deadline, cancellation signal, and content client. Always close a scope in `finally`. Unscoped discovery/invocation remains available through `context.rpc`.

Worker stdout is reserved for Content-Length framed protocol JSON. Use structured Host logging for package events and `safeStderr()` only for bounded process diagnostics.

Provider handlers receive an invocation-bound `context.content` client. Use `register()` for an `AsyncIterable<Uint8Array>`, `registerFile()` for a package-owned file, and `open()` to obtain a verified readable stream. Call `discard()` when finished; the Host also revokes undiscarded handles when their invocation or caller scope ends.

Only `RpcDomainError` codes declared by the exact RPC descriptor method can cross a provider boundary. Generated registrations carry those declarations. Undeclared domain errors, infrastructure-shaped `RpcError` values, and unexpected exceptions become one generic provider fault; bounded exception-type diagnostics go to the Host separately.

`callbackHandlers` and `authHandler` in the worker composition expose Host-owned callback and authorization sessions. The runtime validates and freezes Host-stamped session IDs, absolute callback URIs, bounded parameter maps, and cancellation signals, then stamps package/session identity onto sanitized results. `context.reportGenerationFault()` reports at most one bounded fault for the exact active generation.

Worker limits are fixed protocol constants exported as `SUNDER_WORKER_LIMITS`. Package code cannot raise them. Declare every optional worker API used by the package in `sunder.package.json` through `workerCapabilities`; `@sunder/package-tool` rejects unknown or unsupported capabilities. Declaring `storage.key-migration.v1` automatically adds its required `storage.v1` capability.
