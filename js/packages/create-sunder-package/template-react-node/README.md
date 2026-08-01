# SUNDER_PACKAGE_NAME

Combined React App and TypeScript process Runtime package `SUNDER_PACKAGE_ID`.

- `npm run sunder:dev` watches the React/Vite and worker sources and publishes one canonical current-RID dev package.
- `npm test` runs strict TypeScript checking and package metadata tests.
- `npm run build` emits a pinned Node 24.18.1 SEA Runtime leaf plus a Vite web App leaf for the current exact RID.
- `npm run smoke` performs a native `sunder.worker.v1` handshake against the SEA.
- `npm run package` aggregates available exact-RID leaves into a deterministic `.sunderpkg`.

The browser imports only `@sunder/sdk/browser`. It receives opaque provider handles through the injected bridge and never receives Runtime credentials, filesystem paths, or endpoint references. RPC uses remain default-denied until granted in Sunder Settings.
