# SUNDER_PACKAGE_NAME

TypeScript process Runtime package `SUNDER_PACKAGE_ID`.

- `npm run sunder:dev` bundles with the explicitly recorded current developer Node and watches for clean process drain/restart.
- `npm test` runs strict TypeScript checking and Node's built-in test runner.
- `npm run build` downloads and verifies pinned Node 24.18.1, then emits the current exact-RID SEA target leaf.
- `npm run smoke` performs a native `sunder.worker.v1` handshake against that SEA.
- `npm run package` aggregates available exact-RID leaves into a deterministic `.sunderpkg`.

Production package payloads contain SEA executables, not source JavaScript or external-Node launch metadata. Windows unsigned output requires the explicit `--allow-unsigned-windows` tool option; release signing uses `SUNDER_WINDOWS_SIGN_SCRIPT`. macOS uses ad-hoc local signing unless `SUNDER_MACOS_SIGN_IDENTITY` is configured.
