# @sunder/sdk

> **Release/source channel:** The npm tarball README describes that published version. The copy on the repository's default branch tracks current source and may be ahead of npm; use the matching `sdk/v*` tag when auditing a release.

`@sunder/sdk` contains RPC descriptors and generated bindings, the native `sunder.worker.v3` Runtime worker API, and the web App bridge. Worker code receives the common package identity, content and temporary roots, persistent role-local workspace, Host-mediated settings/state/files/secrets, structured logging, RPC clients, callback/auth handlers, exact-generation fault reporting, and staged lifecycle signals. It does not expose managed module/DI contracts, background services, Stack contributor adapters, Avalonia, Registry clients, or package installation APIs.

Use `@sunder/sdk/browser` from any framework-agnostic web App target. That export targets only the injected `sunder.bridge.v2` API and contains browser-safe RPC, navigation, and external-link bridge types; it does not import Node built-ins, worker framing, or package tooling. React is one scaffold choice, not a web target requirement.

```ts
import { getSunder } from "@sunder/sdk/browser";

const catalog = await getSunder().rpc.discover("example.rpc");
```

The main export is dependency-free and intended for Node worker Runtime code and build-time descriptor generation. Keep browser code on the `/browser` export.

Configure a worker synchronously. Sunder snapshots and freezes the returned composition before advertising it to the Host; lifecycle callbacks may perform asynchronous work and receive cancellation through `AbortSignal`:

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
