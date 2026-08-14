# SUNDER_PACKAGE_NAME

TypeScript worker Runtime package `SUNDER_PACKAGE_ID`.

The package uses authoring schema V2, emits Package Manifest V3, and exposes the `example.messages` RPC V2 contract only to its package. Its owner-only Runtime provider is locked to contract version `2.0.0`; this Node-only preset declares no caller uses.

`workerCapabilities` starts empty. Add the exact optional capability before using Worker context settings, storage, storage migrations, workspace, secrets, logging, callback, or auth APIs; package tooling rejects unsupported declarations and stamps the canonical set on every worker target. `storage.key-migration.v1` automatically adds `storage.v1`.

The `rpcBindings` configuration generates typed provider APIs and client code at `.sunder/generated/rpc/example-messages.g.ts`. Run `sunder-package generate` to refresh it directly; `npm run typecheck`, development, and production builds generate it automatically. Keep `.sunder/` untracked.

- `npm run sunder:dev` bundles with the explicitly recorded current developer Node and watches for clean process drain/restart.
- `npm test` runs strict TypeScript checking and Node's built-in test runner.
- `npm run build` downloads and verifies pinned Node 24.18.1, then emits the current exact-RID SEA target leaf.
- `npm run smoke` performs a native `sunder.worker.v3` handshake against that SEA.
- `npm run package` aggregates available exact-RID leaves into a deterministic `.sunderpkg`.

Production package payloads contain SEA executables, not source JavaScript or external-Node launch metadata. Windows unsigned output requires the explicit `--allow-unsigned-windows` tool option; release signing uses `SUNDER_WINDOWS_SIGN_SCRIPT`. macOS uses ad-hoc local signing unless `SUNDER_MACOS_SIGN_IDENTITY` is configured.
