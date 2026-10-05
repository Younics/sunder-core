# create-sunder-package

> **Release/source channel:** The npm tarball README describes that published version. The repository copy tracks current source and may be ahead of npm; use the matching `sdk/v*` tag when auditing a release.

Run `npm create sunder-package@latest` to scaffold a runtime-backed TypeScript Node Worker package. Add `-- --template react-node` for a combined hosted web App and Node Worker package.

The shipped presets are `node` and `react-node`. Both author schema V3 through one configured `src/rpc.ts` router, emit Package Manifest V1, use Worker Protocol V1 and generated RPC descriptor V1, and bundle a thin CommonJS Worker module for the fixed managed Node 24 profile. The React preset adds a Vite web target, a generated same-package browser client resolver, and a separate automatic owned App-use projection.

After `npm install`, run `npm run contracts:accept` **before** `npm test`, generation or development. Review and commit the resulting `sunder-contracts.owned.json` and the scaffolded canonical empty `sunder-contracts.lock.json`. The fresh package-qualified contract defaults to `1.0.0`; contract versions evolve independently of package versions. Run `npm run contracts:check` after review and `npm run contracts:freeze` before release packaging. Normal builds change neither tracked file.

If the new package needs imports, select them before first Accept with
`sunder-package api add|implement --candidate ...` or `platform implement --platform-catalog ...`,
then bind any imported providers. Local unpublished authoring needs no Registry.
The reviewed lock may therefore be nonempty when first Accept runs.

The generated `package` command builds all six exact-RID target records and emits a deterministic universal archive with adjacent ownership evidence. Package-owned native assets may be declared by exact RID; execution-runtime and toolchain files are rejected.

`sunder-package generate` writes typed bindings under `.sunder/generated/rpc/`. Typechecking, development, and production builds refresh bindings before bundling. Keep `.sunder/` untracked. Templates contain no maintained descriptor JSON, duplicated provider/use/version metadata, manual client, or protocol dependency. External package imports remain a separate, currently gated workflow.

Smoke runs through the Sunder Host and requires a trusted `sunder.runtime.node` release for the current RID. Use `sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1` when it is not already installed.
