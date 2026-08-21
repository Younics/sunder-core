# @sunder/package-tool

> **Release/source channel:** The npm tarball README describes that published version. The repository copy tracks current source and may be ahead of npm; use the matching `sdk/v*` tag when auditing a release.

Builds canonical development trees and exact-RID Node 24.18.1 Single Executable Application target leaves for Sunder `worker` Runtime packages. It also builds framework-agnostic static `web` App targets; Vite is the current adapter and React is only a scaffold preset.

This tool is not the canonical package format itself and is not a general Python, Rust, Go, or arbitrary-worker packager. Other toolchains may emit the same canonical manifest, content index, payload layers, and Host-supported `worker` protocol without depending on this npm package.

Production builds always use a SHA-256-verified pinned upstream Node distribution and revalidate cached downloads before reuse. The developer's current Node is used only by `sunder-package dev`, whose external launch metadata is adjacent to, and never inside, the canonical package tree.

Upstream Node SEA CI skips macOS x64 coverage. Sunder therefore requires its own native `osx-x64` protocol-handshake smoke test and does not infer compatibility from the arm64 build.

`fetchRegistryContract` is the read-only integration for immutable public Registry RPC V2 descriptors. It accepts a Registry origin plus an exact contract ID/version, rejects redirects and any download URL other than the exact same-origin descriptor endpoint, bounds reads to 4 MiB, and verifies exposure, size, descriptor SHA-256, ABI SHA-256, ETag when present, identity, and canonical bytes. `fetchRegistryContractBindings` performs the same checks and passes both verified hashes with the reparsed descriptor to the TypeScript binding generator. Those checks establish descriptor integrity relative to Registry metadata; they do not attest that provider code is safe.

Target-leaf producers and recursive aggregators coordinate through an explicit discovery root. The built-in `build` and `package` commands use `dist/targets`; custom `package --leaf` selections must also pass `--discovery-root <path>`, and programmatic `LeafBuildInput` and `LeafDiscoverySource` values must name the same containing root.

## Package Manifest V3 RPC authoring

`sunder.package.json` must use `schemaVersion: 2`. Schema V1 is rejected with a migration diagnostic rather than reinterpreted. A package contract must be an RPC descriptor V2; V1 descriptors remain available only to standalone binding generation.

RPC authoring entries are closed objects. Hashes are never authored:

```json
{
  "schemaVersion": 2,
  "id": "example.package",
  "name": "Example Package",
  "version": "1.0.0",
  "entry": "src/worker.ts",
  "workerCapabilities": ["logging.v1", "storage.v1"],
  "contracts": [
    {
      "path": "contracts/example.rpc.json",
      "descriptorPath": "contracts/example.rpc.json",
      "exposure": "public"
    }
  ],
  "providers": [
    {
      "providerId": "example.rpc.default",
      "role": "runtime",
      "contractId": "example.rpc",
      "contractVersion": "2.0.0+release.7",
      "audience": "owner"
    }
  ],
  "usesContracts": [
    {
      "role": "runtime",
      "contractId": "example.rpc",
      "contractVersion": "2.0.0+release.7",
      "required": true,
      "actions": ["discover", "invoke"]
    }
  ],
  "dependencies": [
    {
      "packageId": "example.dependency",
      "versionRange": ">=1.0.0 <2.0.0"
    }
  ]
}
```

Each contract entry is exactly `{ path, descriptorPath?, exposure }`, where `exposure` is `package` or `public`. Each provider is exactly `{ providerId, role, contractId, contractVersion, audience }`, where `role` is `app` or `runtime` and `audience` is `owner` or `public`. Each use is exactly `{ role, contractId, contractVersion, required, actions }`. Contract versions are strict, exact SemVer values, including build metadata; `versionRange` is accepted only for package dependencies. Uses are unique by `(role, contractId)`, and providers by `(role, providerId)`.

`workerCapabilities` is required even when empty. It explicitly declares the optional TypeScript Worker V3 surfaces used by worker code. The closed supported set is `settings.schema.v1`, `settings.v1`, `storage.v1`, `storage.key-migration.v1`, `role-local-workspace.v1`, `secrets.v1`, `logging.v1`, `callbacks.v1`, `auth.v1`, and `rpc.extended-deadline.v1`. `auth.v1` closes over `callbacks.v1`, and `storage.key-migration.v1` closes over `storage.v1`. Duplicate, unknown, protocol-selection, and managed-only capabilities are rejected. The tool sorts declarations and adds `rpc.v2`, the matching SDK baseline, and `worker-protocol.v3`; every generated worker leaf and every aggregated external leaf must match the current project declaration exactly.

`rpc.extended-deadline.v1` opts the Runtime target into Host-clamped deadlines beyond the default and the associated Worker V3 lineage field. The Host rejects the required capability before launch when it does not implement that extension. On a supporting Host, the selected target declaration enables lineage emission in the worker process; packages that omit the capability retain the original strict V3 request envelopes for compatibility with older Hosts.

Every use and provider must bind one exact local bundle. App declarations require an App target. A package-only contract can be provided only to its owner; a public contract can have owner or public provider audience. Exposure controls contract distribution and audience controls provider visibility; neither grants an RPC action.

Generated package manifests retain `archiveFormatVersion: 1` and emit `manifestVersion: 3`. Contract bundles contain `descriptorSha256`, `abiSha256`, and `exposure`; uses contain their exact version and ABI hash; providers contain both hashes and audience. All hashes and packaged canonical descriptor bytes come from the same immutable descriptor snapshot. Runtime targets use only kind `worker` and declare `rpc.v2`, `worker-protocol.v3`, the SDK baseline, and the exact configured optional worker capabilities. Legacy `dotnet`, `process`, `rpc.v1`, and `worker-protocol.v1` Runtime shapes are rejected during aggregation.

Runtime and App target leaves contain only declarations for their own role. Aggregation requires identical package-wide bundles, keeps descriptor payloads shared, unions identical role declarations across RIDs, rejects conflicting declarations, and validates the final union against current project authoring.

## Build-integrated RPC bindings

Projects may configure up to 128 local TypeScript RPC binding outputs in the optional top-level `rpcBindings` array in `sunder.package.json`:

```json
{
  "rpcBindings": [
    {
      "path": "contracts/example.rpc.json",
      "outputFile": "example.rpc.ts"
    }
  ]
}
```

Each entry has exactly two properties. `path` is a portable project-relative descriptor path, and `outputFile` is a portable relative `.ts` path. Declaration outputs ending in `.d.ts`, absolute paths, traversal, duplicate inputs, and output paths that collide under ASCII case folding are rejected. Descriptor inputs must be regular files inside the project and cannot be symbolic links, reparse points, or pass through either.

Generated files are rooted at `.sunder/generated/rpc`, so the example is available at `.sunder/generated/rpc/example.rpc.ts`. Standalone `rpcBindings` inputs may be RPC descriptor V1 or V2. A descriptor listed under `contracts` must be V2; when the same path appears in both sections, manifest emission, packaged bytes, and generated identity constants reuse one loaded snapshot.

Run generation directly with either the CLI or the package API:

```sh
sunder-package generate --project .
```

```ts
import { generateRpcBindings } from "@sunder/package-tool";

await generateRpcBindings(process.cwd());
```

`dev`, production worker bundling, and Vite web builds generate bindings before bundling. Watch mode treats configured descriptor paths as structural inputs and recreates its build context after a descriptor change. `smoke` and `package` consume existing build outputs and do not generate bindings.

The generated root is marker-owned and replaced as one locked, staged set. Failed parsing or generation leaves the previous valid set untouched, removed configuration entries remove stale files, and an unchanged set is not rewritten. An existing unmarked `.sunder/generated/rpc` directory is never adopted or deleted. Generation reads only configured local files and does not contact the Registry.
