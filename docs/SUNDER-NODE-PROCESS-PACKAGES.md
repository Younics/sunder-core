# Sunder TypeScript Process Runtime Packages

> **Source channel:** This page tracks current source and may be ahead of npm. For a release, use the page from the matching `sdk/v*` tag and the README embedded in each npm tarball.

`npm create sunder-package@latest` scaffolds a TypeScript Runtime package that communicates with Sunder through `sunder.worker.v1` and ships as Node Single Executable Application (SEA) binaries. The `react-node` template adds a hosted React/Vite web App target to the same universal package.

These are current authoring presets, not a closed package model. The canonical format permits future Python, Rust, Go, and other `process` toolchains, while `web` App targets are framework-agnostic and may use React, Vue, Svelte, Solid, or another static web build. Other .NET UI approaches may likewise be added as Host-supported App target kinds. Every toolchain must emit the canonical manifest, content index, payload layers, and exact targets, and the Host must support its declared kind/protocol.

## npm Package Boundaries

| Package | Included subset |
| --- | --- |
| `@sunder/sdk` | RPC descriptor parse/validation/generation, `sunder.worker.v1` process worker/client/content APIs, and browser bridge types. |
| `@sunder/package-tool` | Canonical Node development output, pinned native SEA leaves, optional static web leaves, smoke, and package aggregation. |
| `create-sunder-package` | `node` and `react-node` scaffolds. |

The TypeScript SDK is not feature-parity with managed `Sunder.Sdk`. It does not expose managed module/DI contracts, package storage/settings/secrets/logging, callbacks, background services, managed Stack adapters, Avalonia, Registry clients, or package install/update APIs. Browser code must import only `@sunder/sdk/browser`; the main export is for Node/build contexts.

## Trust boundary

A `process` Runtime target is a supervised full-trust child process. It is not a sandbox. It runs with the current user's operating-system permissions and can access anything that user can access.

The Runtime Host starts only the declared entry point from the selected exact-RID projection. Projection files are hash validated, executable bits are stripped from every projected file, and Unix owner-execute is granted only to the selected declared process entry point. Archive mode bits are never trusted.

The child receives an explicit environment containing package identity/version, activation and session IDs, canonical content/data/state paths, a projection working directory, and package-local temporary paths. The Host does not inherit arbitrary environment variables, does not pass `NODE_OPTIONS`, and does not expose Runtime bearer/auth tokens.

## Worker protocol

Worker stdin/stdout use strict UTF-8 JSON with Content-Length framing. Stdout is protocol-only; stderr is bounded diagnostic logging. Protocol limits cover headers, frames, JSON depth, outstanding calls, remembered IDs, write queues, stream queues, startup, activation acknowledgement, cancellation drain, and shutdown.

The Host challenges the worker with the exact package, activation, session, and protocol version. The ready response must bind that challenge and advertise exactly the providers declared in the package manifest. Host-to-worker unary and server-stream calls use the same validated Sunder RPC descriptors as managed providers. Worker-to-Host calls are stamped by the Host with the process activation's caller identity; caller identity supplied by a worker is rejected.

Large RPC payloads remain off the framed JSON control plane. Provider handlers use the invocation-bound `context.content` client to register files from package content or private data, open caller content as private temporary files, and discard those files early. The Host fences every operation to the active invocation, provider activation, caller audience, Runtime generation, expiry, hash, and use count, and removes remaining materialized files when the invocation ends.

There are no automatic restart loops. Crash, malformed protocol, invalid provider output, startup timeout, and unexpected exit fault the exact activation. Session replacement and dev reload first cancel and drain caller/callee/session leases, then request graceful shutdown and finally kill the process tree if it does not exit by the deadline.

## Commands

```bash
npm install
npm test
npm run sunder:dev
npm run build
npm run smoke
npm run package
```

`sunder:dev` may use the developer's current Node only through content-bound metadata adjacent to `dist/sunder-dev`. That metadata is outside the canonical package tree and Runtime accepts it only for a local path-based dev package. Installed, Registry, and remote packages reject external Node metadata.

Production builds acquire a SHA-256-verified Node `24.18.1` distribution, revalidate cached distributions, verify the binary's reported version, bundle one CommonJS script, and generate SEA data with `useSnapshot=false`, `useCodeCache=false`, and `execArgvExtension="none"`. `esbuild` `0.28.1` and `postject` `1.0.0-alpha.6` are exact lockfile pins. macOS output is re-signed ad hoc for local tests or with release signing hooks. Windows unsigned output requires an explicit opt-in.

The six exact RIDs are `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Each SEA is built and protocol-smoked on its matching native CI runner before universal package fan-in; cross-building a production SEA is rejected. Upstream Node SEA skips macOS x64 CI, so Sunder's native `osx-x64` smoke is mandatory.

## React and WebView template

Create a combined package with:

```bash
npm create sunder-package@latest my-package -- --template react-node --package-id my.company.package --package-name "My Package"
cd my-package
npm install
npm test
npm run sunder:dev
```

The template contains two target implementations:

- a Vite/React `web` App target whose static entry point and assets are projected into the selected exact RID App layer;
- a TypeScript `process` Runtime target compiled into the pinned Node SEA for the same exact RID.

`npm run sunder:dev` watches both source trees and emits one canonical current-RID development package. `npm run build` emits the current RID's App and Runtime leaves. `npm run smoke` performs the native worker handshake. `npm run package` aggregates all available exact-RID leaves into one deterministic universal archive.

Browser code imports only `@sunder/sdk/browser`. The Host injects a narrow bridge carrying opaque RPC provider handles; the web target never receives Runtime credentials, local filesystem paths, or Runtime broker endpoint references. Installing or updating a package consents to its exact manifest-declared RPC actions and Runtime records version/manifest-fenced grants; undeclared actions remain default-deny. This authorization boundary does not make package code or web content trusted.

## Aggregate packages

Each production build emits a canonical one-target leaf under `dist/targets/<rid>`. These leaves are accepted by `AggregateSunderPackageTask`. In a generated .NET aggregate project, include them as `SunderNodePackageTargetLeaf` items with `DiscoveryRoot` set to the same `dist/targets` root used by the producer. Do not include two leaves for the same exact role/RID key.

The Sunder CLI remains a thin Runtime/Registry client and does not scaffold packages. Node and web authoring use `npm create sunder-package@latest` and `@sunder/package-tool`; managed .NET/Avalonia authoring uses `dotnet new sunder-package`.
