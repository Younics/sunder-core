# @sunder/sdk

> **Release/source channel:** The npm tarball README describes that published version. The copy on the repository's default branch tracks current source and may be ahead of npm; use the matching `sdk/v*` tag when auditing a release.

`@sunder/sdk` is the TypeScript subset for Sunder RPC descriptors, generated bindings, `sunder.worker.v1` Runtime workers, and the web App bridge. It is not a TypeScript port of the managed `Sunder.Sdk` capability surface: it does not expose managed module/DI contracts, package storage/settings/secrets/logging, callbacks, background services, Stack contributor adapters, Avalonia, Registry clients, or package installation APIs.

Use `@sunder/sdk/browser` from any framework-agnostic web App target. That export contains browser-safe RPC, navigation, and external-link bridge types only; it does not import Node built-ins, worker framing, or package tooling. React is one scaffold choice, not a web target requirement.

```ts
import { getSunder } from "@sunder/sdk/browser";

const catalog = await getSunder().rpc.discover("example.rpc");
```

The main export is dependency-free and intended for Node worker Runtime code and build-time descriptor generation. Keep browser code on the `/browser` export.

Worker stdout is reserved for Content-Length framed protocol JSON. Use `safeStderr()` for bounded diagnostic logging.

Provider handlers receive an invocation-bound `context.content` client. Use `registerFile()` to publish package-owned files, `openFile()` to materialize caller content, and `discard()` on the returned file when it is no longer needed; the Host also removes undiscarded files when the invocation ends.
