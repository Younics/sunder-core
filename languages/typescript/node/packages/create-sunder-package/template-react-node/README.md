# SUNDER_PACKAGE_NAME

Combined React App and TypeScript worker Runtime package `SUNDER_PACKAGE_ID`.

The package uses authoring schema V2, emits Package Manifest V3, and exposes the `example.messages` RPC V2 contract only to its package. Its owner-only Runtime provider and required App use are locked to contract version `2.0.0`; the App requests only `discover` and `invoke`, with no RPC version range.

`workerCapabilities` starts empty. Add the exact optional capability before using Worker context settings, storage, storage migrations, workspace, secrets, logging, callback, or auth APIs; package tooling rejects unsupported declarations and stamps the canonical set on every worker target. `storage.key-migration.v1` automatically adds `storage.v1`.

The `rpcBindings` configuration generates typed Runtime provider APIs and the browser client at `.sunder/generated/rpc/example-messages.g.ts`. Run `sunder-package generate` to refresh it directly; `npm run typecheck`, development, and production builds generate it automatically. Keep `.sunder/` untracked.

- `npm run sunder:dev` watches the React/Vite and worker sources and publishes one canonical current-RID dev package.
- `npm test` runs strict TypeScript checking and package metadata tests.
- `npm run build` emits a pinned Node 24.18.1 SEA Runtime leaf plus a Vite web App leaf for the current exact RID.
- `npm run smoke` performs a native `sunder.worker.v3` handshake against the SEA.
- `npm run package` aggregates available exact-RID leaves into a deterministic `.sunderpkg`.

The browser imports only `@sunder/sdk/browser`. It receives opaque provider handles through the injected bridge and never receives Runtime credentials, filesystem paths, or Runtime endpoint references. Installing or updating the package consents to the RPC actions declared by that exact manifest; undeclared actions remain default-deny.
