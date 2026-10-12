# SUNDER_PACKAGE_NAME

Sunder Node service package `SUNDER_PACKAGE_ID`.

The configured `src/services` module exports ordinary `defineService` functions.
TypeScript types or checked JavaScript JSDoc supply the value model; Sunder generates
validated clients, dispatch and Worker bootstrap. Generated files stay under ignored
`.sunder/`. Commit the reviewed `sunder-contracts.lock.json`.

```sh
npm install
npm test
npm run sunder:dev
npm run build
npm run package
```

Private services follow the complete package version and need no Accept/Freeze or
owned baseline. If you deliberately expose public services, use `contracts:accept`,
review/commit the generated owned baseline, and `contracts:freeze` before release.
Normal builds never modify a lock or baseline.

`npm test` checks source and generated types and runs ordinary service behavior tests.
`sunder:dev` watches and publishes `dist/sunder-dev`; `build` emits the current exact-RID
leaf. `package` emits all six thin Node Runtime leaves and a deterministic archive plus
ownership evidence. No Node executable is embedded.

`smoke` uses the managed Sunder Host. Install/select an approved runtime first with
`sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1`.
This headless preset declares no caller grants. Providing a service does not authorize
broker calls; explicit Runtime service use and selected dependency roles remain separate.

Add optional `workerCapabilities` before using package state, files, settings, secrets,
workspace, logging, callbacks/auth or migrations. Export `configure(context)` for real
lifecycle hooks. Declare package-owned native addons in the exact-RID `nativeAssets`
map; runtime/toolchain payloads remain forbidden.
