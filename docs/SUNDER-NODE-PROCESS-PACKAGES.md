# Sunder TypeScript And JavaScript Service Packages

> **Source channel:** This page tracks current source and may be ahead of npm. For a release, use the page from the matching `sdk/v*` tag and the README embedded in each npm tarball.

`npm create sunder-package@latest` scaffolds a runtime-backed service package. Choose
`--language ts` (the default) or `--language js` for independently authored checked
JavaScript. The `node` preset supplies a Worker; `react-node` adds a hosted React/Vite
web App to the same universal package. Service source uses `defineService`; tooling
generates the Worker bootstrap, registrations, codecs and package-local clients.

The Worker is one bundled CommonJS module at `bin/worker.cjs`; the package contains
no Node executable or runtime distribution. The Host launches the module through an
exact, separately trusted Node 24 execution runtime.

These are authoring presets, not a closed package model. The canonical format permits future Python, Rust, Go, and other Worker toolchains, while `web` App targets are framework-agnostic. Every toolchain must emit the canonical Manifest V1, content index, payload layers, exact-RID target records, and a module ABI supported by an approved execution runtime.

## npm Package Boundaries

| Package | Included subset |
| --- | --- |
| `@sunder/sdk` | `defineService`, source-typed semantic options, managed `context.api(token)`, Worker V1 package APIs and advanced descriptor/wire APIs. Browser ownership is in `/browser`; the optional `/react` adapter supplies `ApiProvider`/`useApi`. |
| `@sunder/package-tool` | TS/checked-JS service analysis, generated `#sunder/api` runtime/types, public lifecycle and exact imports, development output, exact-RID Worker/web leaves, managed smoke and package aggregation. |
| `create-sunder-package` | `node` and `react-node` presets in TS or genuine JS/JSX. |

The Node SDK intentionally has a narrower surface than the coordinated .NET SDK family. Worker code receives Host-mediated settings, state, files, secrets, role-local workspace, storage migrations, structured logging, callbacks, auth, RPC, and staged lifecycle callbacks. It does not expose App modules, managed Stack adapters, Avalonia, Registry clients, or package install/update APIs. Browser code uses `#sunder/api` with `@sunder/sdk/browser` and, optionally, `@sunder/sdk/react`; the main export is for Node and build contexts. React is an optional peer, not a dependency of headless Workers.

## Author Ordinary Services

Configure the source module in `sunder.package.json`:

```json
{
  "schemaVersion": 3,
  "id": "example.package",
  "name": "Example package",
  "version": "1.0.0",
  "workerCapabilities": [],
  "services": { "module": "src/services.ts" }
}
```

For TypeScript, `src/services.ts` can contain:

```ts
import { defineService } from "@sunder/sdk";

export const greeting = defineService({
  greet(name: string): string { return `Hello, ${name}!`; },
}, {
  constraints: { "greet.name": { minLength: 1, maxLength: 120 } },
});
```

For JavaScript, configure `src/services.js` and write checked JSDoc directly:

```js
// @ts-check
import { defineService } from "@sunder/sdk";

export const greeting = defineService({
  /** @param {string} name @returns {string} */
  greet(name) { return `Hello, ${name}!`; },
}, {
  constraints: { "greet.name": { minLength: 1, maxLength: 120 } },
});
```

The pinned TypeScript checker derives request/result/stream schemas from supported
ordinary types and JSDoc. Records, lists, maps, optional/null values, literal unions,
`bigint`, bytes and explicit SDK JSON/content types use existing validated codecs.
`constraints` adds semantic facets; it is not a schema-constructor tree. Incomplete
or unsupported boundaries fail with source diagnostics. Source discovery does not
execute package code. See [source types and constraints](package-development/IMPLEMENTATION-FIRST-RPC.md#node-source-types-and-constraints)
for the actual SDK surface and supported boundaries.

Normal packages author neither a Worker `entry` nor a provider-registration list.
Export an optional `configure(context)` for real Worker lifecycle/package hooks;
tooling owns providers. The retired router/schema DSL and `rpc.module/export` config
are not a second authoring mode.

## Managed Package APIs And Lifecycle

Generation publishes `#sunder/api` JS and `.d.ts` together. The creator supplies the
ordinary package-import mapping used by tsc/checkJs, editors and Vite. Worker code
uses its existing `context.api(Self)`; browser/React code uses the owners below.
Acquisition performs no discovery. Calls and lazy streams own selection,
cancellation/deadlines and cleanup; stale, cloned or wrong-owner tokens fail closed.

`Self` uses source service keys: the example above exposes `api.greeting.greet(name)`.
Selected packages use the lock's local name to generate a token such as `Dependency`.
One public contract exposes its services directly; multiple contracts get full-ID
camel-case scopes. For `example.dependency.first` and `example.dependency.second`,
both with `messages.send`, callers use
`api.exampleDependencyFirst.messages.send(request)` and
`api.exampleDependencySecond.messages.send(request)`. Imported arguments/results
come from the descriptor, including an explicit `{}` for an empty request; they do
not reconstruct the producer's source signature.

Private services default to package exposure and use the full package SemVer,
including prerelease/build metadata. They need no owned baseline or independent
Accept/Freeze. Public services opt in with `exposure: "public"` and require explicit
`contracts accept`, `check` and `freeze` before release packaging. Public history
cannot be hidden by demotion, removal or baseline deletion. Explicit
`contracts migrate-private` preserves complete old private records in the immutable
`retiredPrivateContracts` archive. Publication import validates exact package bytes
and Registry evidence; a private-only import has no baseline transition.

Tokens grant no authority. Private owner App use defaults to required applicable
actions. Ordinary owned concrete public services default to optional App readiness
and all applicable actions, so making a private service public preserves its own
UI access without another role flag. Explicit `appUse` overrides readiness/actions.
Public declaration-only services remain opt-in; implementations of local declarations
or imported contracts add no consumer grants. Worker broker consumption always
requires independent explicit `runtimeUse`. Package imports store reviewed
`consumerRoles` for App, Runtime or both; Add defaults to both, Update preserves the
selection, and Restore never widens or migrates it. See the
[tool lifecycle and selection guide](../languages/typescript/node/packages/package-tool/README.md#private-and-public-versions).

## Package And Runtime Trust

A Worker remains a supervised full-trust child process. It is not a sandbox and runs with the current user's operating-system permissions. Package trust and execution-runtime trust are separate:

- The package archive carries a content-indexed CommonJS module, its contracts, and an exact execution-runtime requirement.
- Host downloads the official Node archive, verifies its vendor checksum and materializes the executable/license into a private managed root.
- Runtime resolves `sunder.runtime.node`, a compatible `24.x` release, the current RID, and `sunder.node-worker-module.v1` before launch.
- Missing, incompatible or unhealthy runtime bytes fail before launch. Runtime never substitutes `process.execPath`, a `PATH` Node or development metadata.

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

Crash, malformed protocol, invalid provider output, startup timeout, and unexpected
exit fault the exact activation. Host-managed recovery may create a fresh activation
after cleanup is proven, with bounded retries; it does not replay interrupted calls
or retarget old endpoints. Session replacement and dev reload cancel and drain work,
request graceful shutdown, and finally terminate the process tree after the deadline.

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

Managed smoke requires a verified execution runtime for the current RID. Acquire one
through the Host's official vendor source:

```bash
sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1
```

The repository pins Node `24.18.1`, TypeScript `6.0.3`, `esbuild` `0.28.1` and Vite
`8.2.0` for package tooling. Those development pins do not place a Node distribution
in the package.

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

Native sources must be project-contained regular files. They cannot escape through
symlinks or collide with generated payload paths. Native files stay under the exact
RID even when bytes match. Runtime binaries/packs, SDK/toolchain assets and build-time
Node are excluded from packages; Host-managed official runtime roots are separate.

Every successful package writes deterministic `<id>.<version>.package-evidence.json` beside the archive. It binds the archive hash and size, payload and metadata bytes, per-target shared/exact-RID ownership, native asset hashes, and a zero-byte execution-runtime closure assertion. CI retains this file as release evidence; it is not part of the package archive.

These leaves are also accepted by `AggregateSunderPackageTask`. In a primary .NET package project, include them as `SunderNodePackageTargetLeaf` items with `DiscoveryRoot` set to the same `dist/targets` root used by the producer. Do not include two leaves for the same role/RID key.

## React And WebView Template

Create a combined package with:

```bash
npm create sunder-package@latest my-package -- --preset react-node --language js --package-id my.company.package --package-name "My Package"
cd my-package
npm install
npm test
npm run sunder:dev
```

Use `--language ts` for TypeScript/TSX. Both variants contain a Vite/React `web` App
and a generated Worker. The Host injects exact `sunder.bridge.v1` APIs carrying
opaque provider handles; web code never receives Runtime credentials, local
filesystem paths or broker endpoint references. Manifest-declared RPC actions
remain default-deny outside the exact installed grant.

Catalog entries include the Host-stamped activation ID, epoch and session
generation needed for exact typed-service binding. Their `endpointReference` is
the same document-local opaque alias as `providerHandle`, **not** a broker endpoint
reference. Invocation still requires that document's handle, origin and nonce;
raw broker references cannot be used as handles. Navigation retires those aliases.
Do not treat a document-local alias as a portable broker endpoint.

## Browser And React Lifetimes

These examples consume the `greeting` service above. A plain DOM view owns one
browser context and returns a teardown function to its UI/root owner:

```ts
import { createBrowserContext } from "@sunder/sdk/browser";
import { Self } from "#sunder/api";

export function mountGreeting(button: HTMLButtonElement, output: HTMLElement) {
  const context = createBrowserContext(Self);
  const api = context.api(Self);
  async function greet() {
    try {
      const message = await api.greeting.greet("Sunder");
      if (!context.signal.aborted) output.textContent = message;
    } catch (error) {
      if (!context.signal.aborted) {
        output.textContent = error instanceof Error ? error.message : "Service unavailable";
      }
    }
  }
  button.addEventListener("click", greet);
  return () => {
    button.removeEventListener("click", greet);
    context.dispose();
  };
}
```

For React, keep the root owner outside component effects, provide it once, and use
the optional hook adapter. This JSX is ordinary JavaScript:

```jsx
// @ts-check
import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { createBrowserContext } from "@sunder/sdk/browser";
import { ApiProvider, useApi } from "@sunder/sdk/react";
import { Self } from "#sunder/api";

function Greeting() {
  const api = useApi(Self);
  const [message, setMessage] = useState("Loading…");
  useEffect(() => {
    const cancellation = new AbortController();
    void api.greeting.greet("Sunder", { signal: cancellation.signal }).then(
      value => { if (!cancellation.signal.aborted) setMessage(value); },
      error => {
        if (!cancellation.signal.aborted) {
          setMessage(error instanceof Error ? error.message : "Service unavailable");
        }
      },
    );
    return () => cancellation.abort();
  }, [api]);
  return <p aria-live="polite">{message}</p>;
}

/** @param {HTMLElement} element */
export function mountGreeting(element) {
  const context = createBrowserContext(Self);
  const root = createRoot(element);
  root.render(<StrictMode><ApiProvider context={context}><Greeting /></ApiProvider></StrictMode>);
  return () => {
    root.unmount();
    context.dispose();
  };
}
```

Call the returned teardown when removing the UI root. `ApiProvider` supplies the
application-owned context; it does not dispose that root for you. `useApi` owns an
effect-scoped child, so StrictMode cleanup/remount and multiple roots stay isolated.
Invoke services from effects or event handlers, not during render. Neither example
polls the bridge: `createBrowserContext` waits for `sunder:ready`, validates the
immutable bridge and follows its native signal and document retirement.

### Advanced RPC Boundaries

Descriptor parsers/generators and low-level Worker RPC APIs remain available for
tooling, protocol integration and explicitly owned discovery/content workflows.
`Catalogs` from `#sunder/api` exposes explicit list/get/watch/provider selection;
normal views use the managed APIs above. Content-bearing methods require an
existing caller-owned content scope, kept open until consumption completes. See
the [SDK content-scope guidance](../languages/typescript/node/packages/sdk/README.md#use-generated-apis).
These advanced wire APIs do not restore schema-constructor authoring or add a
browser content/session protocol.

The Sunder CLI remains a thin Runtime/Registry client and does not scaffold packages. Node and web authoring use `npm create sunder-package@latest` and `@sunder/package-tool`; managed .NET/Avalonia authoring uses `dotnet new sunder-package`.
