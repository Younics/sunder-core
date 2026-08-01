# @sunder/sdk

Use `@sunder/sdk/browser` from App web targets. That export contains only browser types and bridge accessors; it does not import Node built-ins, worker framing, or package tooling.

```ts
import { getSunder } from "@sunder/sdk/browser";

const catalog = await getSunder().rpc.discover("example.rpc");
```

Dependency-free TypeScript types, descriptor binding generation, and the `sunder.worker.v1` Node process Runtime implementation.

Worker stdout is reserved for Content-Length framed protocol JSON. Use `safeStderr()` for bounded diagnostic logging.

Provider handlers receive an invocation-bound `context.content` client. Use `registerFile()` to publish package-owned files, `openFile()` to materialize caller content, and `discard()` on the returned file when it is no longer needed; the Host also removes undiscarded files when the invocation ends.
