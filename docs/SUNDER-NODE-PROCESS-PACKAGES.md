# Sunder TypeScript Worker Runtime Packages

> **Source channel:** This page tracks current source and may be ahead of npm. For a release, use the page from the matching `sdk/v*` tag and the README embedded in each npm tarball.

`npm create sunder-package@latest` scaffolds a runtime-backed TypeScript Worker V1 package. The Worker is one bundled CommonJS module at `bin/worker.cjs`; the package does not contain Node executables or runtime distributions. The Host launches that module through an exact, separately trusted Node 24 execution runtime. The `react-node` template adds a hosted React/Vite web App target to the same universal package.

These are authoring presets, not a closed package model. The canonical format permits future Python, Rust, Go, and other Worker toolchains, while `web` App targets are framework-agnostic. Every toolchain must emit the canonical Manifest V1, content index, payload layers, exact-RID target records, and a module ABI supported by an approved execution runtime.

## npm Package Boundaries

| Package | Included subset |
| --- | --- |
| `@sunder/sdk` | Strict RPC descriptor V1 parse/validation/generation, Worker V1 data/logging/storage-migration/callback/auth/RPC APIs, and browser bridge types. |
| `@sunder/package-tool` | Runtime-backed Node development output, thin exact-RID module leaves, optional static web leaves, managed smoke, and package aggregation. |
| `create-sunder-package` | `node` and `react-node` scaffolds. |

The TypeScript SDK intentionally has a narrower surface than the coordinated .NET SDK family. Worker code receives Host-mediated settings, state, files, secrets, role-local workspace, storage migrations, structured logging, callbacks, auth, RPC, and staged lifecycle callbacks. It does not expose App modules, managed Stack adapters, Avalonia, Registry clients, or package install/update APIs. Browser code imports only `@sunder/sdk/browser`; the main export is for Node and build contexts.

## Package And Runtime Trust

A Worker remains a supervised full-trust child process. It is not a sandbox and runs with the current user's operating-system permissions. Package trust and execution-runtime trust are separate:

- The package archive carries a content-indexed CommonJS module, its contracts, and an exact execution-runtime requirement.
- The Node executable and its licenses live in a signed `.sunderruntime` artifact installed in the Host runtime store.
- Runtime resolves `sunder.runtime.node`, a compatible `24.x` release, the current RID, and `sunder.node-worker-module.v1` before launch.
- A missing, incompatible, retired, revoked, or untrusted execution runtime fails closed. Runtime never substitutes `process.execPath`, a `PATH` Node, or adjacent development metadata.

The fixed package requirement is:

```json
{
  "targetFramework": "node24",
  "executionRuntime": {
    "id": "sunder.runtime.node",
    "versionRange": ">=24.18.1 <25.0.0",
    "moduleAbi": "sunder.node-worker-module.v1"
  }
}
```

The Host projects only the selected exact-RID package target, validates every package and runtime artifact byte, and launches the trusted runtime with the projected `bin/worker.cjs` path substituted into its closed launch recipe. The package module is data, not an executable package entry point.

## Worker Protocol

Runtime and Worker use exact wire identity `sunder.worker.v1`. Stdin/stdout carry strict UTF-8 JSON with Content-Length framing. Stdout is protocol-only; stderr is bounded diagnostic logging. Protocol limits cover headers, frames, JSON depth, outstanding calls, remembered IDs, write queues, stream queues, startup, activation acknowledgement, cancellation drain, and shutdown.

The Host challenges the Worker with exact package, activation, session, generation, and protocol identity. The ready response must bind that challenge and advertise exactly the providers declared in the package manifest. Host-to-Worker unary and server-stream calls use strict keyed RPC descriptor V1. Worker-to-Host calls are stamped by the Host with the activation's caller identity; caller identity supplied by Worker code is rejected.

The immutable ready catalog may also advertise settings schemas, state and secret storage migrations, callback handlers, and an auth handler. Migration plans are Host-authoritative and run before activation. Callback and auth requests carry Host-owned session identity, URI, generation, expiry, and cancellation authority.

Large RPC payloads stay off the framed JSON control plane. Provider handlers use invocation-bound content APIs to register package/private files, open caller content as private temporary files, and discard those files early. Host fencing binds every operation to the invocation, provider activation, caller audience, Runtime generation, expiry, hash, and use count.

There are no automatic restart loops. Crash, malformed protocol, invalid provider output, startup timeout, and unexpected exit fault the exact activation. Session replacement and dev reload cancel and drain work, request graceful shutdown, and finally terminate the process tree after the deadline.

## Commands

```bash
npm install
npm test
npm run sunder:dev
npm run build
npm run smoke
npm run package
```

- `sunder:dev` watches sources and emits `dist/sunder-dev` with the same manifest, `bin/worker.cjs` entry point, and execution-runtime requirement as production.
- `build` emits a thin Runtime leaf and optional web App leaf for the selected exact RID. The module is RID-neutral, so all six target records may be built on one supported development host.
- `smoke` passes the canonical development package to `sunder dev package smoke`; it never invokes local Node directly.
- `package` builds all six exact-RID Runtime leaves, plus six App leaves when configured, and deterministically aggregates them into one universal `.sunderpkg`.

Managed smoke requires an approved execution runtime for the current RID. Install one from the configured Registry when available:

```bash
sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1
```

The repository pins Node `24.18.1` as its current npm/build toolchain and pins `esbuild` `0.28.1` and Vite `8.2.0` for deterministic package production. Those development pins do not place a Node distribution in the package.

## Exact-RID Leaves And Aggregation

The six supported RIDs are `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Each build emits one canonical target leaf under `dist/targets/<rid>`. All Runtime target records use the same module bytes and launch contract.

Aggregation factors byte-identical Worker modules and their notices into `payload/runtime/shared`, places public/package contract descriptors in `payload/shared`, and retains six exact-RID manifest target records. The resulting archive therefore selects an exact runtime artifact without duplicating the RID-neutral package module.

Package-owned native addons and sidecars are declared with an exact-RID `nativeAssets` map:

```json
{
  "nativeAssets": {
    "linux-x64": [
      {
        "sourcePath": "native/linux-x64/sqlite.node",
        "targetPath": "native/sqlite.node"
      }
    ]
  }
}
```

Native sources must be project-contained regular files. They cannot escape through symlinks or collide with generated payload paths. Configured and detected native files remain in `payload/runtime/<rid>` even when multiple RIDs have identical bytes. Node/.NET runtime binaries, runtime packs, SDK/toolchain assets, and the active build-time Node executable are rejected; they belong only in signed execution-runtime artifacts.

Every successful package writes deterministic `<id>.<version>.package-evidence.json` beside the archive. It binds the archive hash and size, payload and metadata bytes, per-target shared/exact-RID ownership, native asset hashes, and a zero-byte execution-runtime closure assertion. CI retains this file as release evidence; it is not part of the package archive.

These leaves are also accepted by `AggregateSunderPackageTask`. In a primary .NET package project, include them as `SunderNodePackageTargetLeaf` items with `DiscoveryRoot` set to the same `dist/targets` root used by the producer. Do not include two leaves for the same role/RID key.

## React And WebView Template

Create a combined package with:

```bash
npm create sunder-package@latest my-package -- --template react-node --package-id my.company.package --package-name "My Package"
cd my-package
npm install
npm test
npm run sunder:dev
```

The template contains a Vite/React `web` App target and the runtime-backed TypeScript Worker target. Browser code imports only `@sunder/sdk/browser`. The Host injects exact `sunder.bridge.v1` APIs carrying opaque provider handles; web code never receives Runtime credentials, local filesystem paths, or broker endpoint references. Manifest-declared RPC actions remain default-deny outside the exact installed grant.

The Sunder CLI remains a thin Runtime/Registry client and does not scaffold packages. Node and web authoring use `npm create sunder-package@latest` and `@sunder/package-tool`; managed .NET/Avalonia authoring uses `dotnet new sunder-package`.
