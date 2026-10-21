# create-sunder-package

> **Release/source channel:** The npm tarball README describes that published version. The repository copy tracks current source and may be ahead of npm; use the matching `sdk/v*` tag when auditing a release.

Run `npm create sunder-package@latest` for a TypeScript Node service package. Add `-- --preset react-node` for React plus Node, and `--language js` for genuine checked JavaScript (including JSX in the React preset).

Both presets configure one `src/services.ts` or `.js` module exporting `defineService` definitions. Sunder generates Worker bootstrap, registrations, descriptors and `#sunder/api` tokens/clients. React uses `ApiProvider` and `useApi(Self)`. TypeScript types and checked JS JSDoc use the same semantic compiler; JS sources are independently authored rather than transpiled TS samples.

After `npm install`, run `npm test`, `npm run sunder:dev`, or `npm run build`. Private services follow the complete package version and require no Accept/Freeze or owned baseline. Commit the canonical empty contract lock and keep generated `.sunder/` output untracked. Public exposure is explicit and retains reviewed Accept/Freeze history. Normal builds never write tracked locks/baselines.

If the new package needs imports, select them with
`sunder-package api add|implement --candidate ...` or `platform implement --platform-catalog ...`,
then bind any imported providers. Local unpublished authoring needs no Registry.
Add stores App+Runtime consumer intent by default; only present roles receive grants. Existing narrowed roles require a targeted `api update --consumer-roles app,runtime` review before widening. Every input lock must already contain explicit role intent and complete projection pins.

The generated `package` command builds all six exact-RID target records and emits a deterministic universal archive with adjacent ownership evidence. Package-owned native assets may be declared by exact RID; execution-runtime and toolchain files are rejected.

`sunder-package generate` writes runtime JS, browser-safe declarations, codecs and receipts atomically under `.sunder/generated/rpc/`. The scaffolded ordinary package-import mapping makes `#sunder/api` work in tsc/checkJs, editors and Vite. Templates contain no router/schema trees, authored Worker bootstrap, descriptor JSON or manual client construction.

Smoke runs through the Sunder Host and requires a trusted `sunder.runtime.node` release for the current RID. Use `sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1` when it is not already installed.
