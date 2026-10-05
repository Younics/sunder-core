# SUNDER_PACKAGE_NAME

Runtime-backed TypeScript Worker package `SUNDER_PACKAGE_ID`.

The package uses implementation-first authoring schema V3 and emits Package Manifest V1. Its Worker is a bundled CommonJS module at `bin/worker.cjs`, launched by the managed `sunder.runtime.node` 24.x execution runtime through Worker Protocol V1. The package does not embed Node or an executable.

`src/rpc.ts` owns the schema-bearing implementation and exports one `rpc.router`. The tool derives the package-qualified `messages` contract, exact identities, dispatch and typed clients under `.sunder/generated/rpc/`. `RpcHandlers<typeof messages>` checks the ordinary implementation in the editor and in standalone TypeScript builds. This Node-only preset has one owner-only Runtime provider and no caller uses.

Start a new package in this order:

```sh
npm install
npm run contracts:accept
npm run contracts:check
npm test
```

First Accept precedes generation. Select any local package/catalog imports and bind imported providers before Accept if needed; no Registry is required. Review and commit `sunder-contracts.owned.json` and `sunder-contracts.lock.json`; keep `.sunder/` untracked. The new package-qualified contract starts at `1.0.0`, independently of the package version. Later implementation changes require explicit Accept; normal generate/dev/build never edit either tracked file.

- `npm run sunder:dev` watches sources and publishes `dist/sunder-dev` in the canonical runtime-backed shape.
- `npm test` runs strict TypeScript checking and Node's test runner.
- `npm run build` emits the current exact-RID thin Worker target leaf.
- `npm run smoke` loads and releases `sunder-dev` through the managed Sunder Runtime.
- `npm run contracts:freeze` freezes the reviewed current contracts before release packing.
- `npm run package` builds all six exact-RID leaves and emits a deterministic universal `.sunderpkg` plus adjacent ownership evidence.

Smoke requires a trusted Node runtime for the current RID. If absent, run `sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1` or import an approved runtime artifact before retrying.

`workerCapabilities` starts empty. Add an exact optional capability before using settings, storage, storage migrations, workspace, secrets, logging, callbacks, or auth. Package tooling rejects unsupported declarations and stamps the canonical capability set on every Worker target.

Declare package-owned native addons with the exact-RID `nativeAssets` map in `sunder.package.json`. Native assets remain in their RID layer; Node/.NET runtime and toolchain files are rejected.
