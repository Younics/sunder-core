# @sunder/package-tool

The published tarball documents its release; this repository tracks current source.
The tool builds thin Node 24 Worker V1 packages and optional Vite web App targets.
It emits one bundled CommonJS `bin/worker.cjs`, never a Node executable or runtime
distribution. The Host launches it with `sunder.runtime.node`, range
`>=24.18.1 <25.0.0`, ABI `sunder.node-worker-module.v1`.

## Source services

```json
{
  "schemaVersion": 3,
  "id": "example.package",
  "name": "Example",
  "version": "1.0.0",
  "workerCapabilities": [],
  "services": { "module": "src/services.ts" }
}
```

Use `.js` for independently authored checked JavaScript. One module exports
`defineService(methods, optionalSemanticOptions)` definitions. The pinned TypeScript
6.0.3 checker derives descriptors from supported source types, checked JSDoc and
inference. It never executes the module for discovery. Method bodies stay ordinary
code; incomplete/nonportable boundaries receive source-located diagnostics. Optional
constraints, public names, errors, idempotency and use policies are semantic metadata,
not procedure schemas. See [SDK examples and supported values](../sdk/README.md).

Worker bootstrap, registration and dispatch are generated. Export `configure(context)`
for real lifecycle hooks instead of authoring `runWorker` or provider registrations.
The former `rpc.module/export` and router/schema authoring surface is retired.

## Generate, develop, build and pack

```sh
sunder-package generate --project .
sunder-package dev --project . --watch
sunder-package build --project . --rid linux-x64
sunder-package smoke --project .
sunder-package package --project .
```

`generate` publishes the complete marker-owned `.sunder/generated/rpc` tree. `dev`
publishes canonical `dist/sunder-dev`; `build` emits the requested/current exact-RID
Runtime and optional App leaves. `package` builds all six supported RID leaves and
emits a deterministic universal archive plus ownership evidence. For prebuilt leaves,
use `--discovery-root <root>` and optional repeated `--leaf <path>` arguments.

Smoke delegates to the managed Host using `sunder dev package smoke`. Set
`SUNDER_CLI` when necessary. An execution runtime must already be explicitly selected:

```sh
sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1
```

Normal generation/build/dev/pack is offline. It never chooses a Registry, refreshes
dependencies, accepts API drift, freezes a release or rewrites tracked locks.

### Advanced: descriptor-only generation

The tooling package, not the runtime SDK, owns descriptor-to-TypeScript emission:

```sh
sunder-rpc-gen descriptor.json bindings.ts
```

`generateTypeScriptBindings` is also exported from `@sunder/package-tool` for tool
hosts. Normal service packages use `sunder-package generate` instead. Emitted code
imports supported binding helpers through the explicit, browser-safe
`@sunder/sdk/generated` runtime entry point; it does not load the generator at runtime.
That generated-code ABI remains a compatibility obligation, not an invitation to
handwrite marshalling or import package-tool into a Worker or browser App.

### Development watch

`dev --watch` coalesces saves and publishes a complete canonical dev tree only after
the affected builds and RPC source validation succeed. React/TS/JS UI-only edits
reuse the built Worker and its validated RPC snapshot. Worker-only edits reuse web
assets when their inputs and consumed generated APIs are unchanged. Shared inputs,
package/configuration changes and reviewed contract changes invalidate both roles;
unknown inputs are handled conservatively.

Syntax/build errors keep watch running and retain the last successful dev output.
Fixing the source retries all pending edits, including edits made during a failed
or superseded build. Generated `.sunder` and configured output directories are
excluded from source feedback. Web assets remain staged Vite builds for the Host's
embedded UI; watch does not start a Vite development server.

## Generated package APIs and ordinary tooling

`#sunder/api` contains `Self`, selected-package tokens, generated `Errors`, imported
`Implementations`, service declaration `Services` tokens, and advanced `Catalogs`. Normal calls use `context.api(token)`;
browser owners use `createBrowserContext(Self)`, and the optional React adapter
provides `ApiProvider`/`useApi`. Calls and streams manage exact selection and cleanup.

For an ordinary owned concrete `defineService`, the frontend normalizes omitted
public `appUse` to optional readiness and all applicable actions. Private App use
keeps its required default. Explicit policy wins. Public declaration-only services
remain opt-in, and local/imported provider implementations add no consumer intent.
This is declaration-owned source policy, not permission inferred from public
descriptors, types or call sites. Receipts and source-verified Pack carry that policy
outside descriptor/ABI identity; Runtime broker use remains explicit.

`Self` service properties follow source definition keys. Every selected package
scopes services under each full contract ID, even with one public contract, e.g.
`context.api(Dependency).example_Dependency_First.messages.send({})`. Scopes retain
the canonical contract symbol's underscore separators and lower its initial letter
(`example.messages` becomes `example_Messages`); a leading digit is escaped with `_`.
`Services.Dependency.example_Dependency_First.messages` has the same scope, while
the canonical token path is `Services.Contracts.CExample_Dependency_First.messages`.
`Services.Self` retains the owned source keys. Same
service names in different contracts are valid; normalized collisions within one
scope fail explicitly. Imported signatures use descriptor request/result values,
not reconstructed producer-language arguments. Runtime JS and `.d.ts` share this
projection, and a local projection change retires the prior token generation
without changing descriptor or ABI hashes.

Imported provider handlers always use `camel(serviceId) + pascal(methodId)`:
`messagesSend` implements `messages/send` with either one or several services.
Normalized collisions in that contract's handler scope fail explicitly. Owned and
local-declaration implementation methods keep their source spelling and arguments.
Typed Runtime-use evidence names the canonical C# service projection as
`Sunder.Generated.Packages.<Authority>_Services.<ContractSymbol>.<NaturalInterfaceName>`,
including the contract scope independently of the selected package's size.

Each token also exports a **type-only** models namespace. A named `Item` used by
several owned services appears once as `SelfModels.Item`; selected package models
use `<TokenName>Models.Item`. For example:

```ts
import type { SelfModels, WeatherModels } from "#sunder/api";
```

Checked JavaScript uses `import("#sunder/api").SelfModels.Item` in JSDoc. There is
no runtime models object. Grouping uses resolved declarations in the complete
TS/checkJs program, including named JSDoc typedefs, transparent values and unions.
Equal schemas from different declarations do not merge. Portable-name collisions
fail with source locations. Owned bindings reuse source model types; imported
models retain their ultimate authority and reviewed projection metadata.

Every compiler receipt/export requires `modelProjection` immediately after
`abiSha256`, including explicit empty `models` and `serviceReferences` maps.
The public-only `.sunder/generated/rpc/contracts/rpc-model-projection.v1.json`
sidecar is shipped and indexed exactly once at
`payload/shared/contracts/rpc-model-projection.v1.json`. Private projection evidence
stays in compiler receipts/exports. Role leaves are checked against their exact
source/selection projection and combined into one public aggregate document.

Package selections pin the sidecar's byte count and SHA-256 independently of the
public inventory. Canonical CLI inspection exports it beside `inspection.json`
and descriptor files (at most 130 files). Explicit online authoring selection and
restore inspect the bounded canonical candidate; normal builds consume validated
cache evidence offline. Every selection, including the input to `api update`, must
contain complete projection pins. Missing metadata is rejected; restore the current
reviewed lock. It never falls back to descriptor-derived model names.

The immutable sidecar cache is
`<contractCacheRoot>/model-projections/sha256/<sha>.json`, with adjacent
`<sha>.receipt.json` containing `schemaVersion`, `modelProjectionSha256`, then
`modelProjectionBytes`. Reads also validate package identity, complete public
descriptor linkage and selected pins. Natural `SunderContent` and
`ServiceReference<typeof service>` signatures generate asynchronous, explicit
resource visitors and typed receiver-bound clients. Each ordinary service exposes
a `Providers` catalog; existing providers supply `.Reference` and `.Service`.
Role closure starts from all provided methods, reviewed `invoke` unary methods,
and reviewed `subscribe` stream methods, including their requests, outputs and
declared error details. Only reachable references require an existing same-role
target `discover` use; `discover` alone and shared Models/type availability activate
no method graph. Complete metadata/target validation still covers unused public
inventory. References never add imports or grants, and target invocation remains
broker-authorized. The full eleven-field locator is validated on send and receive.
Received content retains a generated scope until its last content disposal,
either original call/enumeration cancellation, the deadline, or owner retirement.
Generated `ServiceStream<T>` results accept independent enumeration cancellation
through `.withCancellation(signal)` or `[Symbol.asyncIterator](signal)`; both
original signal links survive exhaustion and early iteration exit while content
is held. TS and checked JavaScript infer content returns and forwarded service
streams from implementation types. Authors use normal models and `context.api(token)`,
without writing resource traversal or call-scope plumbing.
See [natural resource APIs and ownership](../sdk/README.md#natural-content-and-provider-references).

Bound imported providers may author `runtimeUses: [{ service:
Services.Contracts.<CanonicalContract>.<service>, actions: ["discover"] }]` in
their existing `defineService` options. These are typed, source-checked targets;
contract IDs, versions and hashes come from the validated import closure.
Provider-only recursive reference closure stays inside each pinned artifact's
public package-authority inventory and creates no uses or package API facade.
Explicit Runtime intent then supplies the exact ordinary `usesContracts` subset.
Missing discovery still fails during Update, including unchanged artifact selections.
App receives neither the intent nor its grants.

Optional generated export/receipt `roleUses` records are ordered by contract and
service, use the shared nine-field evidence shape, and participate in source
fingerprints/recompilation checks. Receipt uses must match the recorded policy
exactly; extra/missing intent, widened actions, changed requiredness, tuple or
canonical service type are rejected. Normal builds remain offline and do not
treat authored `roleUses` JSON as authority.

`methods.<sourceMethod>.timeoutSeconds` is an optional `defineService` hint:
integer `0..2100`, with `0`/omission selecting the Host default. The compiler maps
nonzero hints to wire service/method IDs in optional `methodPolicies` immediately
after each contract's `modelProjection`. Export/receipt/sidecar, imported and
reexported metadata, generated-member agreement and input fingerprints preserve it;
descriptor and ABI bytes remain unchanged. Generated calls apply the hint and
request the existing `rpc.extended-deadline.v1` target capability; the Host and
inherited parent still bound the effective deadline. Authors supply no RPC scope
options or participant/retirement plumbing.

The creator supplies this ordinary `package.json` mapping:

```json
{
  "imports": {
    "#sunder/api": {
      "types": "./.sunder/generated/rpc/api.d.ts",
      "default": "./.sunder/generated/rpc/api.js"
    }
  }
}
```

Run generation before standalone `tsc`, checkJs or Vite. Runtime JS and declarations
are published together; editors use the same package-import mapping. Browser code
does not import Worker source or depend on Node ambient declarations. The authoring
checker uses a fixed strict ES2023 profile, including exact optional properties;
arbitrary tsconfig transforms/plugins do not override that contract. Node resolution
uses `node` and syntax-selected import/require/default conditions, not implicit
`module` conditions. Native resolution still goes through the existing bundler.

## Private and public versions

Private source versions equal the full manifest package SemVer, including prerelease
and build metadata. Private-only projects need neither an owned baseline nor
Accept/Freeze. A changed private source snapshot still has exact descriptors/hashes
and source-verified output; released package artifacts remain immutable.

Public source requires explicit reviewed history:

```sh
sunder-package contracts accept [key] --project .
sunder-package contracts check --project .
sunder-package contracts freeze [key] --project .
```

A new selected public key can supply `--next-version`. Existing version transitions
use exact `--current-version` and `--next-version`; breaking/unclassified/exposure/
removal changes also require `--acknowledge-breaking-change`. Public history cannot
be bypassed by making source private, renaming/removing it, deleting a baseline or
switching configuration. New public prerelease allocation remains unsupported.
Accept recompiles at allocated identities and commits only the baseline; the existing
generation publisher owns outputs. Freeze does not manufacture publication evidence.

Accept records the current source projection alongside descriptor/ABI identity.
Model names, service-reference policy, method policy and exported source shapes
participate in public version classification independently of wire compatibility.
Missing historical projection evidence is unknown, not unchanged: restore the
reviewed baseline or explicitly accept a new Major tuple. Current source cannot
backfill an existing frozen or published tuple. Already-qualified projection reviews
retain their original record meaning and exact bytes.

Active private-only baseline records are unsupported; private contracts use the
package version. Any public current/history remains governed by public review.
Retained `retiredPrivateContracts` is inert archive evidence: it cannot be extended,
removed, overwritten or enriched. Active plus archived entries share the
128-entry/32,896-snapshot bound and existing byte/depth/structural limits.

After publication, `contracts publication import --project . --package <archive>
--registry <origin>` verifies the retained archive and exact Registry publication.
It uploads nothing, preserves unrelated snapshots/archive evidence, and does not
change selection, Accept or Freeze.

Publication import uses the canonical Sunder CLI inspector for foreign-language
archives too. Private bundle tuples must agree with its hash-bound manifest and
the Registry's exact artifact/content identity; current private versions retain the
full package SemVer. Public current/history identities are classified first and
still require their exact frozen snapshots. Retired private snapshots are neither
current-version inputs nor publication-update targets. A verified private-only
publication needs no baseline transition: replay preserves bytes/mtime, and an
absent baseline stays absent. Missing public baseline/evidence still fails.

## Package selection and consumer roles

```sh
sunder-package api add example.weather --version ">=1.0.0 <2.0.0" --candidate ./weather.sunderpkg
sunder-package api update example.weather --consumer-roles app,runtime
sunder-package api require example.weather example.weather.forecast
sunder-package api unrequire example.weather example.weather.forecast
sunder-package api list
sunder-package api remove example.weather
```

Use an explicit `--registry https://registry.example/` for published selection. Local
candidates need no Registry; their locks omit `registryOrigin`. Present origins stay
canonical and pinned. A targeted update can bind a local selection to a Registry;
an existing origin cannot be overridden. `--include-prerelease`/`--exclude-prerelease`
are explicit tri-state review inputs. `--local-name` selects the generated token alias.

Add defaults to required package presence, optional provider readiness, and stored
`consumerRoles: ["app","runtime"]`. Only present package roles receive grants.
Targeted Update preserves roles unless `--consumer-roles app|runtime|app,runtime`
is supplied. Global updates reject per-selection overrides. Required readiness is
coalesced independently within each role. App policy never supplies Worker grants;
authority dependencies supply no consumer grants. An App-only selection in a
canonical package with no App fails instead of granting Runtime access.

All readers, including Restore and Update, require explicit valid role intent.
Missing or malformed roles fail rather than implying Runtime access or widening
grants. Selection commands atomically write only the reviewed lock; no-op preserves
bytes/mtime. The proposal and source/evidence are rechecked under the lease.

## Extension and platform providers

`api implement <package> <contract>` selects one public package-authority extension;
`platform implement <contract> --platform-catalog <catalog.json>` selects a local
Core platform contract. Published selections use an explicit Registry. Implement with
`defineService(methods, { implements: Implementations.<key> })` using lock-derived
types. Multiple implementations need distinct `providerId` values. Provider intent
never creates consumer permission. Platform APIs remain provider-only.

The corresponding update/unimplement/list commands retain exact selected provenance.
Remove source bindings before unimplementing. Platform versions follow the coordinated
SDK line, not caller-authored catalog ranges. `Catalogs` retains explicit typed
discovery, lifecycle/reset events and opaque endpoint-backed selection for advanced
domain workflows; ordinary tokens do not require that plumbing.

## Exact restore, local inputs and cache

`contracts restore --project . [--candidate <archive>] [--platform-catalog <catalog>]`
hydrates existing selections only. Candidate inspection uses the installed Format-
owning Sunder CLI (`SUNDER_CLI`); it validates the full archive before exporting public
descriptors. Local inputs never fall back to a Registry. Restored cache pairs preserve
exact descriptor/ABI hashes, lengths, inventory and authority evidence.

The shared default cache is `~/.sunder/contracts/v1/sha256`. Use
`SUNDER_CONTRACT_CACHE` for normal builds or `--cache-root` for selection/restore/GC.
Readers use existing leases and never repair implicitly. Explicit restore handles
interrupted publication and quarantine. Explicit `contracts gc --lock <lock> ...
--grace-days <1..365>` retains every supplied reviewed lock and recent content; normal
builds never run GC. Cache integrity grants no publication or Runtime authority.

Canonical Development `http://localhost[:nondefault-port]/` requires
`--development-registry` on each relevant invocation. Other HTTP origins and locked-
origin overrides remain rejected. Local origin-free development needs no opt-in.

## Input and output invariants

Source/type/metadata analysis is bounded, checks resolved symbol identity, rejects
unsafe metadata mutation, and validates runtime method/options inventory. Compiler
reads are size-admitted before parsing; authored source has a 4 MiB normalized budget,
65,536 syntax/type traversal nodes, depth 64 and 4,096 constants. Descriptor/value
limits remain RPC V1. Runtime snapshots bound files, bytes and resolution edges.
Producer changes require tool restart. These are deterministic build checks, not a
sandbox for executing package code.

Current generated modules are virtual inputs during checking/bundling. Stale disk
outputs never supply current types. Source/reference/config/lock/baseline and positive/
negative resolution evidence are revalidated before transactional publication.
Identical output preserves mtimes. Prebuilt packing reanalyzes source, compares fresh
Worker bytes and requires the exact complete role-scoped bundle/use/provider/import
projection. Failure preserves previous archives and evidence.

## Capabilities, native assets and archive layout

`workerCapabilities` is required even when empty. Optional APIs include settings,
storage, key migrations, role-local workspace, secrets, logging, callbacks/auth and
extended deadlines. The tool adds the fixed core/runtime/protocol/SDK capabilities;
auth closes over callbacks and key migration closes over storage.

Declare native addons using `nativeAssets.<exact-rid>[]` with project-contained
`sourcePath` and Runtime-relative `targetPath`. The six supported RIDs and aggregate
256-asset limit remain. Portable path collisions, symlink escapes, executable/runtime
payloads, managed BCL files and toolchain artifacts are rejected. Native shared-library
headers must match the selected RID. Byte-identical Worker/notices can be factored
into `payload/runtime/shared`; native assets remain in exact-RID layers. Descriptors
are neutral `payload/shared` data. App and Worker implementation payloads stay isolated.

The adjacent `<id>.<version>.package-evidence.json` binds archive hash/length, payload
ownership, native hashes and the verified zero embedded-execution-runtime closure.
It is not embedded publication authority.
