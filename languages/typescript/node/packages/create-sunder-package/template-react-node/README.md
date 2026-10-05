# SUNDER_PACKAGE_NAME

Combined React App and runtime-backed TypeScript Worker package `SUNDER_PACKAGE_ID`.

The package uses implementation-first authoring schema V3 and emits Package Manifest V1. Its bundled CommonJS Worker at `bin/worker.cjs` is launched by the managed `sunder.runtime.node` 24.x execution runtime through Worker Protocol V1. The Vite App remains a separate role-isolated web target.

`src/rpc.ts` owns the schema-bearing implementation and exports one `rpc.router`. The configured App receives the complete automatic owned-contract use projection: required discovery, unary invocation and streaming subscription for this example. A separate generated App receipt carries these grants; the Runtime receipt carries its provider and no caller uses. No descriptor or use declaration is maintained by the author.

Start a new package in this order:

```sh
npm install
npm run contracts:accept
npm run contracts:check
npm test
```

First Accept precedes generation. Select any local package/catalog imports and bind imported providers before Accept if needed; no Registry is required. Review and commit `sunder-contracts.owned.json` and `sunder-contracts.lock.json`; keep `.sunder/` untracked. The new package-qualified `messages` contract starts at `1.0.0`, independently of package version. Normal generate/dev/build never edit the baseline or lock.

The browser imports `createPackageApi` from `.sunder/generated/rpc/browser.js` and resolves `api.messages` to a typed client. Resolution requires one active same-package provider with the exact contract tuple. Zero matches, ambiguity and catalog reset are explicit failures; no raw contract IDs, hashes or provider handles appear in App code.

- `npm run sunder:dev` watches Worker and web sources and publishes one canonical current-RID development package.
- `npm test` runs strict TypeScript checking and package metadata tests.
- `npm run build` emits thin Worker and Vite web App leaves for the current exact RID.
- `npm run smoke` loads and releases `sunder-dev` through the managed Sunder Runtime.
- `npm run contracts:freeze` freezes the reviewed current contracts before release packing.
- `npm run package` builds all six Runtime and App exact-RID leaves and emits a deterministic universal `.sunderpkg` plus adjacent ownership evidence.

Smoke requires a trusted Node runtime for the current RID. If absent, run `sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1` or import an approved runtime artifact before retrying.

Browser code imports only `@sunder/sdk/browser`. It receives opaque provider handles through the injected bridge and never receives Runtime credentials, filesystem paths, or Runtime endpoint references. Undeclared RPC actions remain default-deny.

Declare package-owned native addons with the exact-RID `nativeAssets` map in `sunder.package.json`. Native assets remain in their Runtime/RID layer; Node/.NET runtime and toolchain files are rejected.
