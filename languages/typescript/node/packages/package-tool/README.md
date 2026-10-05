# @sunder/package-tool

> **Release/source channel:** The npm tarball README describes that published version. The repository copy tracks current source and may be ahead of npm; use the matching `sdk/v*` tag when auditing a release.

Builds canonical Manifest V1 packages for TypeScript Worker V1 modules. Node packages contain one bundled CommonJS module at `bin/worker.cjs`; they do not contain Node executables, runtime distributions, launch sidecars, or package-level signing material. The Host launches the module through a separately trusted execution runtime.

The fixed Node 24 profile is:

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

The tool also builds framework-agnostic static `web` App targets. Vite is the current adapter and React is only a scaffold preset. Other language toolchains may emit the same canonical manifest, content index, payload layers, and Host-supported Worker V1 protocol without depending on this package.

## Commands

```sh
sunder-package contracts accept --project .
sunder-package contracts check --project .
sunder-package contracts freeze [key] --project .
sunder-package generate --project .
sunder-package dev --project . [--watch]
sunder-package build --project . [--rid linux-x64]
sunder-package smoke --project .
sunder-package package --project .
```

- `generate` atomically refreshes configured TypeScript RPC bindings.
- `dev` emits `dist/sunder-dev` with the same `bin/worker.cjs` entry point and execution-runtime requirement as production. Watch mode rebuilds that canonical tree safely.
- `build` emits one exact-RID Runtime leaf and an optional exact-RID web App leaf. The Worker module is RID-neutral, so any supported RID can be built on any development host.
- `smoke` builds `sunder-dev` and passes it to `sunder dev package smoke` through the managed Host/Runtime path. Set `SUNDER_CLI` when `sunder` is not on `PATH`.
- `package` builds all six exact-RID Runtime leaves, plus App leaves when configured, and writes a deterministic universal `.sunderpkg`. Passing `--discovery-root` selects aggregation-only mode for externally produced leaves.

Smoke requires a trusted Node execution runtime for the current RID. Install one explicitly through the configured Registry before smoke when needed:

```sh
sunder runtime ensure sunder.runtime.node ">=24.18.1 <25.0.0" sunder.node-worker-module.v1
```

## RPC Authoring

### Exact Contract Restore

`sunder-package contracts restore --project <path> [--cache-root <path>]` now
hydrates existing local or Registry-backed locks without evaluating package source,
resolving new versions, changing lock bytes or publishing generated bindings.
The reader consumes the same closed lock and receipt format as C#. Restore verifies
the complete inventory, package authority, exact canonical descriptor/ABI tuple,
length and strong ETag with redirects disabled and bounded request/body reads.

The default cache is `~/.sunder/contracts/v1/sha256`. C# and Node share cache leases,
pending-publication recovery, ownership markers and corruption quarantine. Readers
copy complete validated pairs under the lease and never perform repair. Explicit
restore can resume either producer's interrupted publication. Replays preserve
cache mtimes. Cache integrity does not grant publication or runtime authority.

Offline `contracts restore --candidate <archive.sunderpkg>` validates an exact
existing lock through the local Sunder CLI's Format-owned archive inspector. Install
a matching Sunder CLI or set `SUNDER_CLI` to its executable path. The operation
requires no Runtime/Registry connection, rejects changed or mismatched candidates,
and reproduces the same cache bytes as exact Registry restore. The complete archive
is validated before public descriptor snapshots are exported; private bodies are
excluded. Candidate paths never enter the lock.

Platform hydration validates the canonical Core catalog and exact lock pins. Local
catalogs require no Registry, signing keys or trust bundle. Supply `--platform-catalog`
together with `--candidate` when both are needed for a cold cache. Any remaining
materialized imports require complete verified cache evidence; local restore never
falls back to a default Registry. V3 package-consumer imports
use restored descriptors during offline generation/build. Set
`SUNDER_CONTRACT_CACHE` for ordinary compilation when using a nondefault cache;
selection, restore and GC also accept `--cache-root`. See
[the shared cache protocol](https://github.com/Younics/sunder-core/blob/main/docs/package-development/CONTRACT-RESTORE-CACHE.md).

### Package Consumer APIs

Fresh projects may configure all dependencies before first owned Accept, even when
complete source already refers to several not-yet-selected provider interfaces.
Initial configuration validates inventories, exact hashes, generated import names
and source/configuration/lock mutation fences; it commits only `sunder-contracts.lock.json`.
First Accept then compiles and validates every provider and the complete source graph.
Accepted projects keep full source validation on selection. A missing baseline with
prior receipt/export/marker evidence is rejected. No dependency/API list belongs in the router:

```sh
sunder-package api add example.weather --version ">=1.0.0 <2.0.0" \
  --candidate ./weather.sunderpkg --presence optional --project .
sunder-package contracts accept --project .
sunder-package api update example.weather --include-prerelease --project .
sunder-package api require example.weather example.weather.forecast --project .
sunder-package api unrequire example.weather example.weather.forecast --project .
sunder-package api list --project .
sunder-package api remove example.weather --project .
```

Add defaults to stable selection and required package presence, with optional
per-contract readiness. `--local-name` resolves a reported generated-name collision.
`--candidate <archive.sunderpkg>` uses canonical offline inspection while retaining
explicit reviewed range intent and exact hashes, without requiring a Registry.
Use `--registry https://registry.example/` for published selection. Local locks omit
`registryOrigin`; present null/malformed origins are invalid. Targeted update preserves
omitted options and requires a candidate for a local selection; it can explicitly bind
that selection to a Registry. Existing Registry origins cannot be overridden. Global
`api update` rejects per-selection overrides.
Add/update validate the complete proposed lock and source before an atomic commit.
Required contracts must be unrequired before removal. Removal retains cached bytes;
source references to a removed facade fail on subsequent compilation.

`packageApis` from `#sunder/rpc` exposes
`packageApis.weather(rpc).CExample_Weather_Forecast`. Each contract supports
`list`, `get`, `watch`, `createClient(scope, provider)` and `resolveSelectedPackage`.
Discovery retains all authorized exact-tuple providers and lifecycle/reset events;
selected resolution requires exactly one active provider from the selected package
within its reviewed range/prerelease intent. Endpoint lifetime handles remain private.
Package content selected at restore is provenance, not a routing pin.

This slice generates Runtime consumer uses and lock-derived required/optional
dependencies. Associated Apps retain their separate owned projection.
Use `sunder dev package prepare-imports` with local candidates/catalogs or an explicit
Registry to prepare unpublished development activation through the Host.

### Platform Providers

`platform implement <contract> --platform-catalog <catalog.json> [--local-name <key>]`
selects a local Core catalog without a Registry; use `--registry <origin>` for a
published catalog. These commands, together with
`platform update [contract]`, `platform unimplement <contract>` and `platform list`
manage provider-only platform intents. The range derives from the coordinated SDK
minor line; version/prerelease/presence/candidate overrides are rejected. Global
`api update` includes platform intents in its atomic source-checked lock transition.

Original selected catalogs are retained in the shared cache and revalidated before
offline generation. The Node provider shape is the same reserved
`rpcImports.<key>.provider(...)` builder as package-authority imports, with exact
platform receipt/manifest groups and no package dependency or consumer use.
`contracts restore --platform-catalog <path>` hydrates existing selections only.
Local updates require `--platform-catalog`; a targeted Registry binding requires a
contract ID. See [the platform workflow](https://github.com/Younics/sunder-core/blob/main/docs/package-development/PLATFORM-CONTRACT-IMPORTS.md).

### Implement An Imported Extension

Select the public contract before adding its handlers:

```sh
sunder-package api implement example.dependency example.rpc.v1.minimal \
  --version ">=1.0.0 <2.0.0" --registry https://registry.example/ \
  --local-name extension --project .
```

When the artifact is already selected, omit the selection flags to reuse its reviewed
intent. A local `--candidate` can supply the exact archive as for consumer add. The
default import key is the final contract-ID segment followed by `Provider`;
`--local-name` resolves a key collision. Implement and consumer add share one artifact
record. The new provider intent creates no consumer use.

Use the generated member directly in the configured router (the same form works in JS):

```ts
import { rpc } from "@sunder/sdk";
import { rpcImports } from "#sunder/rpc";

export const packageRpc = rpc.router({
  contracts: {},
  providers: {
    implementation: rpcImports.extension.provider({
      handlers: {
        example: {
          ping(request, context) {
            context.signal.throwIfAborted();
            return request;
          },
        },
      },
    }),
  },
});
```

Service/method keys and handler types come from the restored descriptor. Ordinary
generation requires every selected provider surface to have a complete local
implementation. One implementation defaults to `<contract-id>.provider`; multiple
implementations of a contract each require a distinct explicit `id`. `audience`
defaults to `public` and may be narrowed to `owner`. Generated builders and adapters
validate the complete runtime shape and reuse the existing wire codecs.

After removing the local binding, run
`sunder-package api unimplement <package> <contract> --project .`. The bounded source
check refuses live bindings and preserves the previous lock on failure. Consumer
intent, still-required authority selections and cache bytes remain protected.
Provider constraints make dependency presence required even when a coexisting
consumer import is optional; they do not change that consumer's readiness policy.

### Development Localhost Registry

Add `--development-registry` to API/platform commands, `contracts restore`,
`generate`, `build`, `dev`, `smoke`, `package` and Check/Accept/Freeze when using a
lock with canonical `http://localhost[:nondefault-port]/` origins. The mode is
explicit per invocation; `dev` alone does not enable it. HTTPS is the default.
Other HTTP hosts, IP literals, credentials, extra paths, queries/fragments and
noncanonical ports are rejected. Update/restore retain locked origins and reject
existing-origin overrides. Origin-free local selections need no Development opt-in.

### Explicit Contract Cache GC

Use `sunder-package contracts gc --lock <reviewed-lock> [--lock <reviewed-lock> ...]`
with optional `--cache-root` and `--grace-days` (1..365, default 7). Supply every
workspace lock whose entries should be retained. GC is offline and takes source
leases before the shared cache lease. It preserves referenced and recent entries,
collects only validated unreferenced pairs and ownership-proven retained artifacts,
and reports unproven artifacts rather than deleting them.
This includes catalog pairs and publication/repair evidence. Direct imports
and metadata-only platform inventory references retain their original catalog bytes.
Deletion ownership checks never grant publication authority.

GC stages exact preimages in tombstones before deleting bytes. Interrupted C#/Node
batches can be resumed with the original reviewed lock set; additional locks may
protect newly referenced entries. `More candidates` requests another explicit pass.
Pruned entries require restore. Normal builds and package API changes do not run GC.

### Owned V3 Source And Releases

Owned contracts accept `includeSchemaDialect: true` when exact migration requires
retaining the existing `$schema` annotation. Only a literal boolean is accepted;
`true` emits the fixed V1 Draft 2020-12 URI and omission/`false` emits no annotation.
It preserves the value ABI while retaining an existing descriptor hash; changing
the option after acceptance is still checked against the immutable baseline.

The bounded implementation-first TS/JS path retains ordinary package, capability, dependency, native-asset and App fields, with only this RPC configuration:

```json
{
  "schemaVersion": 3,
  "id": "example.package",
  "name": "Example Package",
  "version": "7.6.5",
  "entry": "src/worker.ts",
  "workerCapabilities": [],
  "rpc": { "module": "src/rpc.ts", "export": "packageRpc" }
}
```

Use `.js` for independently authored JavaScript. The configured export is a local `const` initialized with `rpc.router({ contracts, providers })`. Contract keys are lower-kebab, provider keys camel, definition keys Pascal, and record properties camel. Providers refer to local contract keys, not descriptors or identities. A single binding defaults to `<contract-id>.provider`; multiple bindings require explicit IDs on every provider. Declaration-only contracts are valid. Package exposure implies owner audience; public exposure defaults to public audience. Providers never grant consumer uses, including App uses.

A configured same-package App receives required uses of package-private contracts by default. Public contracts contribute no App use unless their owning `rpc.contract` declares `appUse`. For example, `appUse: { actions: ["discover", "invoke"] }` declares an optional narrowed use. Explicit policy defaults `required` to false and omitted actions to all descriptor-applicable actions; `required: true` adds provider readiness. Action arrays must be nonempty, distinct subsets of `discover`, `invoke` (unary methods), and `subscribe` (streams), emitted in that order. Null, unknown or inapplicable actions fail. Policy is source/receipt metadata and does not change canonical descriptor/ABI bytes or frozen baselines.

`app-authoring-receipt.v1.json` declares `role: "app"` and carries exactly the resolved App policy with no providers or external grants. App policy alone grants no Worker uses. Runtime-only leaves contain providers and explicit Runtime uses, App-only leaves contain owned App uses, and prebuilt Pack rejects incomplete, broadened or stale projections, including deletion of the entire use array. An absent App contributes no App uses. The generated runtime adapter verifies consumer policies before provider binding.

An owned `rpc.contract` can independently declare `runtimeUse: { required: true, actions: ["discover", "invoke"] }`. Both exposures default to no Runtime use, including contracts with a local provider. Explicit policy defaults to `required: false` and all applicable actions; explicit arrays use the same nonempty/distinct/applicable rules as App policy. Declaration-only contracts can declare uses. Worker receipts retain normalized `runtimeUse` metadata and combine its exact grants with lock-derived package-API uses. App receipts omit Runtime policy/grants. Neither role's policy changes descriptors, ABI hashes or frozen baselines; source-policy changes require fresh output and stale prebuilt packing fails. Imported-provider bindings cannot declare owned policy or acquire consumer uses implicitly.

`createPackageApi(getSunder().rpc)` from the generated `browser.js` exposes one typed entry per local key; `await api.messages.resolve({ signal })` returns a typed client. It requires exactly one active same-package provider with the exact contract/version/descriptor/ABI tuple, rejects catalog reset, and never ranks or retries ambiguous matches. Package version/content is not turned into an installation pin. This is the local App path; selected Runtime consumer facades use `packageApis` above.

New owned source starts with this exact `sunder-contracts.lock.json`, with two-space indentation and one terminal LF. Explicit package API commands may populate it before first Accept; ordinary builds keep it read-only:

```json
{
  "lockVersion": 1,
  "packageArtifacts": {},
  "packageImports": {},
  "providerImports": {},
  "platformImports": {}
}
```

For new source, run **`contracts accept` before the first `generate`, `dev`, or `build`**, review and retain `sunder-contracts.owned.json`, then run `contracts freeze` before release packing. Contracts default to `1.0.0` independently of package version; a new selected key can choose a different stable initial version with `--next-version`. Subsequent compilation injects accepted versions before descriptor generation, contextual types, bindings, provider registrations and runtime capture. Stable IDs are derived independently from source and checked against the baseline; local keys are never inferred as aliases.

The earlier baseline-free V3 generate/dev/build route remains development-only when accepted baseline evidence is absent. It cannot bootstrap an existing receipt/export into a release: Check, Freeze and Pack require the baseline; first Accept requires new source, no prior receipt/export and a valid exact lock, which may already contain imports. Restore a missing baseline for existing outputs. Baseline hashes or explicit absence participate in input fingerprints and a private generated marker fences deletion of an accepted baseline. Baseline, receipt/export and contract-lock evidence also block V3-to-V2 or non-RPC fallback. Genuinely non-RPC V3 projects need no RPC lock. Platform generation requires original catalog evidence matching the exact local or Registry selection; arbitrary descriptor-first import admission remains closed.

#### Check, Accept And Freeze

```sh
sunder-package contracts check --project .
sunder-package contracts accept [key] --project .
sunder-package contracts accept greeting --project . --next-version 2.0.0
sunder-package contracts accept echo --project . \
  --current-version 1.1.1 --next-version 2.0.0 --acknowledge-breaking-change
sunder-package contracts freeze [key] --project .
```

This is a closed command grammar. Check checks the complete set and accepts no key or version flags. Freeze accepts only an optional key and obtains release package version from `sunder.package.json`. Version selection requires an exact key. For a **new key with no current snapshot or retained history**, `--next-version <stable-version>` alone is permitted, without an old version or breaking acknowledgement. Existing current or historical keys require a current/next version pair. Acknowledgement is a boolean flag without a value; breaking, unclassified, exposure and removal changes require the exact key, pair and acknowledgement. Targeted acceptance still proves the complete source set and unchanged snapshots outside the selected key. The deep-importable command helper also validates its operation at runtime before project filesystem access; unknown JavaScript values cannot fall through to Freeze.

The new-key exception follows the current C# path: `GenerateSunderRpcBindingsTask.OwnedContracts.cs` permits next-only bootstrap, `GenerateSunderRpcBindingsTask.cs` constructs an empty baseline after the first source compilation, and `SunderRpcAuthoringDocuments.Evolution.cs` requires a version pair only when a previous snapshot exists. The planner still validates strict stable SemVer and the independently source-derived identity. A missing baseline plus any prior generated receipt/export remains non-bootstrapable, even with an explicit next version. This does not add prerelease allocation or implicit adoption policy.

Accept replaces unissued drafts without inventing history or repeated bumps. Changes to frozen/published snapshots retain their complete immutable history and automatically allocate compatible Patch/Minor floors. Every historical floor is anchored to its own version, using the highest history plus the candidate major line. Arbitrary-digit SemVer is bounded to 256 characters; an overflowing automatic Patch/Minor can select a fitting higher stable bump. No new prerelease progression policy is inferred. Later package releases can reuse an unchanged frozen/published contract without rewriting its original release/publication evidence.

Accept compiles at current identities, classifies and reparses the proposed baseline, then **recompiles the full source, contextual types and runtime binding graph at allocated identities**. Both compilations retain the original physical baseline/absence snapshot and must agree on physical bytes and input membership. Only the baseline is committed; `rpc-bindings.ts` remains the sole generated-tree publisher. Check changes no files; byte-identical Accept/Freeze preserve mtimes.

#### Canonical Baseline And Compatibility Rules

The private Node reader/writer follows the package-owned schema and the C# lossless snapshot writer: strict UTF-8, no BOM/CR/null, two-space indentation, schema member order, ordinal contract-map order, ASCII-case collision rejection and exactly one final LF. The portable baseline-key grammar is broader than the lower-kebab source frontend, including ordinally ordered numeric keys. Limits are 268,435,456 outer bytes, depth 16, 128 families and 256 immutable history entries per family. A 2,097,152-value/property preparse structural budget admits the full maximum schema shape. File reads are size-admitted and streamed in bounded chunks with stable file/handle checks.

Embedded descriptors use the existing SDK strict descriptor/schema machinery, canonical descriptor/ABI projections and domain-separated ABI hashes; generated runtime bindings retain SDK instance validation. Counts, exact canonical bytes, IDs, versions, hashes, states and release/publication evidence must agree. All admitted snapshot evidence fields are retained losslessly; ordinary lifecycle writers never manufacture published state, Registry evidence, store authority or platform trust. Platform baselines are rejected. Current versions and immutable history must strictly increase in SemVer precedence. This lifecycle rule is deliberately stricter than the raw compatibility validator's exact immutable-tuple reuse, which succeeds before allocation checks even for old majors and prereleases.

After Registry accepts a frozen archive, explicitly confirm publication:

```sh
sunder-package contracts publication import --project . --package /releases/package.sunderpkg --registry https://registry.example/
```

The command uses the canonical CLI archive inspector (`SUNDER_CLI` may select the
installed Sunder CLI), then verifies exact Registry artifact hash/length, content
identity, immutable publication identity and complete public inventory. Matching
frozen public/private snapshots receive publication evidence atomically; draft
current source and original evidence on reused published tuples are preserved.
Initially-Yanked publication is valid evidence. Failed/conflicting imports preserve
the baseline; identical imports preserve mtimes. The retained archive is rechecked
at commit. This command uploads nothing and does not mutate locks, Accept, Freeze
or rebuild. Localhost HTTP needs `--development-registry`; there is no implicit
feed or trust override. See [publication evidence](../../../../../docs/package-development/IMPLEMENTATION-FIRST-RPC.md#import-verified-publication-evidence).

The private compatibility proof ports Format's bounded conservative algorithm over normalized ABI: request **old ⊆ new**, response/event/error details **new ⊆ old**. Canonical equality is Unchanged; ABI equality is Patch. Named graph equality covers reachable and unused definitions. Unequal recursive graphs, changed tagged unions, numeric/exact-format changes and other unproved forms are conservatively Major. No TypeScript assignability or sampled instances are used as compatibility proof. Bounds are 65,536 proof steps, depth 128, 1,024 UTF-16 code units per reason pointer and 64 reasons including truncation; exhaustion is Major. Reasons are ordinally sorted path/code pairs and do not print schema literals.

ABI service/method/error/definition/property membership is own-property-only, including reference traversal and optional schema fields. Inherited JavaScript names cannot stand in for removed declarations: removing a declared `constructor` error is Major with `/errors/constructor` / `error-removed`. Names outside the existing wire grammar are rejected by the strict parser. Source-derived method/property maps use null prototypes, and canonical outer maps preserve own prototype-sensitive data keys and ordinal numeric ordering.

Baseline writes use the existing mutual-exclusion primitive through a **lease-only** path, never generated-output recovery or wildcard cleanup. Safe parent-directory identities are retained, stages are created exclusively and flushed, and exact stage bytes, physical input set and original baseline expectation are rechecked before atomic installation. New-file installation is exclusive; cleanup can remove only this invocation's still byte-proven regular stage. Neighboring tracked files and unproven stages remain intact.

#### Source-Verified Pack

Release packing requires every current owned contract, including declaration-only contracts, to be frozen or published. Prebuilt aggregation performs the full fresh ordinary-source analysis and compiles a production Worker in memory; its bytes must match the selected prebuilt Worker modules. Aggregate bundles, providers, uses and import groups must exactly cover the role-scoped source/lock projection. Stale prebuilds, incomplete unions, generated-artifact fallback and imported/authority-field laundering fail closed.

After archive/evidence creation, Pack acquires the baseline lease and revalidates baseline bytes and retained parent directory, source, config, exact lock, compiler/producer modules and runtime reference/resolution snapshots immediately before committing outputs. The ownership-evidence path is locked alongside the archive and package tree. Failed validation preserves the previous archive and evidence. This is package-owned source/release verification; it does not establish external publication authority.

#### Package Import Execution Status (2026-09-16)

The later package-import checkpoint (2026-09-16) passes **175 SDK tests** and the
complete package-tool suite: **581 passed, one Linux-only skip**, with
`SUNDER_TEST_PACKED_CLOSURE=1`. The packed case now includes consumer/provider imports
and unimplementation through actual production-only installed TS/JS tools. The npm
test script bounds compiler-heavy test-file concurrency to two; per-test deadlines
remain in force. These are local tooling results, not live Registry or native six-RID
qualification.

#### V3 Scaffold Execution Status (2026-09-13)

With Node 24.18.1/npm 11.19.0, the final SDK suite passed **175** tests and the full
package-tool suite with `SUNDER_TEST_PACKED_CLOSURE=1` passed **517** tests with one
Linux-only skip on macOS. The creator suite passed **7** tests and both V3 template
Worker/browser typechecks passed. The expanded packed acceptance is included in
the full tool result, with a bounded 20-minute test deadline.

Actual SDK/tool/create tarballs exercise production-only TS/JS tooling installs and
both independently installed scaffold dependency graphs. Both presets run explicit
Accept/Check, standalone typechecks, development and production builds, real Worker
unary/stream/invalid-wire cases, Freeze, and release packaging (six Runtime targets
or twelve Runtime/App targets). Separate tests validate generated browser resolution,
App-only receipt/leaf grants and stale prebuilt projection rejection. Tests retain
the canonical lock, source and baseline bytes across ordinary builds.

The first full run exposed two isolated Vite fixtures missing their real SDK value
dependency; their 14-test owning scope and the final full run pass with that dependency
installed. A 10-minute packed-test cancellation under concurrent verification was
resolved by bounding the expanded fixture at 20 minutes; it passed in 551.50 seconds
in the final run. These test runs establish no Registry publication or native six-RID
activation evidence.

#### Earlier Owned-Release Execution Status (2026-09-13)

Executed with Node **24.18.1**, npm **11.19.0** (npm's pinned bin directory precedes Node's on `PATH`), and fixture/install scratch directories under the approved temporary root:

| Command from `languages/typescript/node` | Actual result |
| --- | --- |
| `npm run test --workspace @sunder/sdk` | **174 passed**, 0 failed, 0 skipped; includes typecheck/build |
| `SUNDER_TEST_PACKED_CLOSURE=1 npm run test --workspace @sunder/package-tool` | **514 passed**, 0 failed, 1 platform skip out of 515; includes typecheck/build |

The skip is the existing Linux boot/PID-namespace case on macOS. The packed-closure test actually packed SDK/tool tarballs, installed with `npm install --omit=dev`, ran TS and JS Accept → Check → Freeze → Generate → Build → Pack, exercised the Worker protocol at accepted versions, packed prebuilt leaves, and rejected loaded and post-archive producer mutations. Both installed TS/JS paths also reject unacknowledged frozen `constructor` error removal and incompatible shared handlers in both provider orders. It verified that the installed SDK still has zero runtime dependencies. The private classifier suite executes **199 shared recipe cases** (79 proof/inverse, 32 presence/nullability directions, 16 minimum versions, 72 forward/reversed history cases), plus six boundary/coverage tests and 61 own-member/prototype/canonical-map regression tests. Three existing C# baseline writer fixtures roundtrip byte-for-byte through the Node reader/writer.

The review-fix rerun adds **72 passing regression tests** over the previous 436-pass tool run: 61 ABI/member-map cases, ten actual TS/JS source/lifecycle/shared-handler cases, and one frozen-baseline/outer-map case. The latter retains own fields, immutable snapshot bytes and ordinal numeric keys. The handler cases include valid explicit types and contextual inference, real Worker dispatch, cold Accept rejection, and rejection through Check/Accept/Freeze/Pack against existing frozen state.

The subsequent bootstrap-grammar/runtime-operation rerun adds **six more passing tests**: three planner grammar cases, actual TS and JS next-only first acceptance, and runtime operation validation. The packed closure now first accepts its selected contract at `2.0.0`, generates/builds and exercises live `2.0.0` registrations while the baseline is still draft, then freezes for independent package version `7.6.5`. It also verifies that next-only selection cannot adopt existing baseline-free generated receipts, and that unknown deep-imported JavaScript operations cannot freeze a draft. Later breaking acceptance in that packed fixture advances the contract to `3.0.0`.

Those are historical owned-release results. Subsequent checkpoints implement both V3 presets, package/provider/platform imports, optional dependencies and explicit verified publication import. The live Registry lane covers consumers, providers, platform policy, publication baselines and normal Host installation. Current local authoring needs no Registry or catalog trust bundle; new prerelease allocation remains unsupported.

#### Source Compilation And Generated Outputs

`generate --project`, `dev`, and `build` use TypeScript **6.0.3** and Node declarations **24.13.3**, exact runtime dependencies of the package tool, without importing or executing package code during analysis. Only the pinned Node ambient type root is selected; its declarations and resolution inputs use the same bounded capture/revalidation as other compiler inputs. This supports ordinary Node APIs such as `process` even in a production-only installed tooling closure. The SDK still has zero runtime dependencies. Metadata accepts only frozen SDK constructors, literals and bounded local `const` aliases in the configured module. Reexports, arbitrary calls, computed keys, spreads, accessors, conditionals, environment access and dynamic imports cannot supply metadata. Handler bodies are ordinary code, not metadata. Inline TS handlers receive generated contextual types in memory; JS receives equivalent JSDoc contexts. Source and generated backend types are checked together with the fixed strict ES2023 profile. `RpcHandlers<typeof contract>` supports explicitly typed shared handlers. Custom tsconfig policies/plugins and RPC-source suppression directives are not used to bypass checking.

Generated codecs validate complete wire requests before dispatch and wire results/events/error details before delivery, including required members, unknown fields, types and descriptor facets. Validation imports the browser-safe SDK surface. Native `bigint`, `Uint8Array` and float32 inputs are checked before conversion; recursive conversion has a 256-reference-depth guard. Nested conversions avoid repeating whole-subtree validation, while independent/reentrant public calls still validate their own roots and failure cleanup restores the depth counter.

Shared handler records retain every applicable generated handler type. Contextual inference chooses a deterministic contract-key context, and each original shared-handler expression is separately checked against its own binding's generated type. This preserves contextual inference while preventing a broader SDK-inferred return type from hiding another contract's narrower generated requirement. TS and JS both reject incompatible shared bodies regardless of provider enumeration; compatible explicitly typed and contextually inferred shared handlers remain supported. These checking-only expressions never enter runtime bundles or authored source.

The analyzer enforces source byte/node/reference/constant/contract/provider limits and reuses the strict V1 descriptor parser and descriptor-generated codecs. Authoring name collisions fail instead of allocating numeric suffixes. Four frozen fixture families, containing seven contract tuples, are checked independently from TS and JS; C#-only suffix/synthetic-request controls remain valid Node source. Node tests execute every shared `proof-cases.json` and `history-cases.json` recipe, specified inverse and four-state direction against the actual classifier; frozen expectations are unchanged. Package-consumer imports use the exact lock/cache prepass above.

`.sunder/generated/rpc/` contains canonical descriptor snapshots, per-contract bindings/declarations, `imports.ts|js`, `browser.ts|js`, `index.d.ts`, `authoring-receipt.v1.json`, and tool-private adapter/context files. The existing marker/output lease stages and replaces the complete set. Failed/stale plans preserve prior output; identical generation preserves mtimes. Source, entry, config, lock, compiler inputs and positive/negative resolution probes are revalidated under the lease and before publication. Builds never rewrite locks or baselines.

The entire current generated module set is an in-memory compiler/bundler overlay, including facade and declaration entries. Owned generated paths never fall back to physical output or enter the physical-input fingerprint/probe set. This permits clean source-helper imports of generated facades and subsequent schema edits without reading or invalidating against the previous generation. Source checking and generated-only declaration emission are separate, so a runtime `helper.js` and its shipped `helper.d.ts` do not collide during emission. Integer enums and integer/float64 literals retain narrow backend types matching the DSL; float32 descriptor values retain `number` because decoding rounds to binary32.

Local package-import aliases into generated output also resolve without any existing output tree. The bounded target selector supports exact and single-wildcard `imports` keys, strings/null, nested conditions in declared order, and local alias chains, using the same Node/import/require/default conditions as Worker bundling. Arrays and cyclic/over-depth alias graphs fail closed. Non-owned targets still use esbuild's resolver. Every native resolved file is reclassified before loading, so package exports or other indirect paths into an owned root cannot read stale generated files.

Raw metadata `const` records are not assumed immutable merely because their bindings are constant. The source frontend rejects mutations and unsafe escapes outside the closed const/DSL graph, including property/index writes, mutation calls, aliases, captured containers and raw metadata exports. Direct readonly reuse through constants and DSL constructors remains supported. Recorded handler bodies are not interpreted as metadata; ordinary implementation state remains ordinary code. The generated binder independently compares actual contract service/method keys with the source projection as well as checking provider handlers.

Validated hoisted integer/string enum arrays retain literal tuple types in the in-memory checking overlay, including aliases and explicitly typed shared callbacks. Authors do not need `as const`. Existing array type constraints remain checked; unrelated arrays and handler bodies are not rewritten. These refinements occur only after metadata mutation/escape and descriptor validation and never alter authored source, runtime bundle inputs or canonical descriptor tuples. Ordinary metadata-object property reads remain unsupported.

Physical compiler reads are bounded before TypeScript can create a source file, rather than only after a program has been parsed. Authored sources and local declarations share the existing **4 MiB normalized UTF-8** budget, including the RPC module, Worker entry and transitive helpers; CRLF normalizes to LF. Reads stream through 64 KiB buffers, with size checks before allocation, stable handle/path identity checks, strict UTF-8 and rejection of authored-path symlinks. Dependency/SDK/compiler declarations have a separate 4 MiB/file and 32 MiB aggregate budget; other dependency source has 4 MiB/file and 16 MiB aggregate. Each source/declaration/dependency category allows 512 files. Compiler configuration reads allow 1 MiB/file and 16 MiB total; all compiler read paths (including missing paths) and realpath observations are each limited to 4,096. Resource/path failures are latched and abort even if TypeScript turns a host read exception into a diagnostic. Positive, negative and canonical-path observations remain fenced before publication.

All Worker bundles explicitly select the `node` condition plus the standard syntax-selected `import`/`require` and `default` conditions. The nonstandard implicit `module` condition is disabled and legacy package lookup uses `main`, not `module`. TypeScript uses the matching Node condition. V3 bundling ignores authored tsconfig transforms, matching the fixed source-checking profile.

V3 `loadProject` also captures a non-executing esbuild runtime graph. Every bundled implementation is read into a bounded immutable byte snapshot, including JS hidden from TypeScript resolution by a sibling declaration file. Later bundles replay captured resolutions and bytes, not current disk implementations. Revalidation compares implementation bytes, positive/negative ancestor `package.json` inputs, and a fresh resolver pass over captured source to detect newly preferred files or changed conditional exports/imports. Static non-builtin external imports are rejected. These are build-input invariants, not a sandbox or proof of arbitrary runtime filesystem/dynamic-loading behavior or handwritten declaration truthfulness.

Runtime capture is limited to 512 files, 16 MiB per file and 64 MiB total, 8,192 import edges, and ancestor depth 64. Resolution configuration is limited to 4,096 paths (including absent paths), 1 MiB per present file and 16 MiB total. Producer implementation snapshots are taken synchronously at tool-module initialization, including loaded SDK generator/authoring dependencies, TypeScript compiler JS and package-tool implementation modules, including the private lifecycle, baseline, classifier, CLI and lease code. They allow 256 files, 16 MiB each and 64 MiB total. Watch mode observes producer directories as well as source/baseline changes. A producer change causes a restart-required diagnostic; newly read declarations or package metadata cannot bless old cached producer code. The source/import budgets remain unchanged.

Worker source uses only the frozen hook:

```ts
import { runWorker } from "@sunder/sdk";
import { packageRpc } from "./rpc.js";

void runWorker(() => ({ providers: packageRpc.providerRegistrations }));
```

The esbuild hook injects a footer into the analyzed module and binds generated registrations through a private symbol before importing Worker code runs. Adapters map authored keys using the existing backend naming model and normalize synchronous unary results with `Promise.resolve`. There is no public `rpc.bind`, authored identity metadata, runtime compiler or second JSON dispatcher. Unbundled routers fail as unbound. Browser code uses the generated browser entry, never the Worker router. Receipts/context files are intermediate outputs, not package content.

Before binding, the adapter compares the exact runtime provider key set, closed provider fields, normalized local contract/ID/audience and required explicit IDs with the analyzed projection, and checks referenced contract identity/exposure plus exact service/method key sets. Added, removed or changed providers fail before Worker configuration. DSL declaration attributes and copied facets are frozen rather than silently repaired, and adapters retain validated handler references instead of looking up mutable handler properties on each invocation. This is an authoring/runtime consistency check, not an authorization claim.

### Descriptor-First Migration

Existing templates and external descriptor fixtures still use `schemaVersion: 2`, independently of Package Manifest V1. They are concrete migration inputs, not a completed template cutover or permanent dual-authoring policy. Contracts and binding inputs must be strict RPC descriptor V1 documents. Hashes are derived from one immutable canonical descriptor snapshot and are never authored.

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
  ]
}
```

`workerCapabilities` is required even when empty. The closed optional set is `settings.schema.v1`, `settings.v1`, `storage.v1`, `storage.key-migration.v1`, `role-local-workspace.v1`, `secrets.v1`, `logging.v1`, `callbacks.v1`, `auth.v1`, and `rpc.extended-deadline.v1`. The tool adds `core.v1`, `execution-runtime.v1`, `rpc.v1`, `worker-protocol.v1`, and the SDK baseline. `auth.v1` closes over `callbacks.v1`; `storage.key-migration.v1` closes over `storage.v1`.

Each provider and use binds one exact local contract version. App declarations require an App target. Package exposure controls descriptor distribution, provider audience controls visibility, and manifest actions remain default-deny authorization.

The retained descriptor-first migration path and V3 owned development both stamp each generated contract bundle with `source: "owned"`, `authorityKind: "package"`, and `authorityId` equal to the canonical package `id`. Source `dependencies` still contain only `packageId` and `versionRange`; generated `dependsOn` entries additionally declare `presence: "required"`. These fields are not reader defaults or new source-authoring fields.

Dependency ranges must use canonical single-space-separated SemVer constraints and permit at least one strict semantic version within the 256-character version limit. Configuration and manifest validation reject contradictory or adjacent exclusive bounds, including length-bounded prerelease and core succession. Comparisons ignore build metadata; range text is validated without whitespace normalization.

Aggregation requires explicit bundle provenance and dependency presence. Owned bundles cannot carry selected-authority fields. Descriptor-first migration inputs still reject nonempty import groups and optional dependencies. V3 source-verified builds derive package/platform groups from the exact lock, validated cache and source; every target leaf must match its fresh role-scoped projection. Associated App leaves retain their owned-only projection. Imported metadata is never accepted and discarded.

### Platform Catalog Helpers

Private `platform-catalog.ts` validates canonical catalog content and immutable pins. The explicitly selected Registry supplies publication authority; no catalog signing key, trust bundle or crypto dependency is needed. Input is strict compact UTF-8 without terminal LF, bounded to 64 MiB, depth 16 and 262,144 values/property names before DOM parsing. Selection checks same-origin metadata and complete inventory; restore preserves existing origins/pins and generation remains offline. Successful local parsing does not publish a release or grant Runtime actions.

Core engineering derives the real Stack `2.0.0` descriptor/ABI across 48 definitions and six methods with unchanged hashes. Its check/accept/freeze/export operations prepare a catalog for Registry administrator import. Catalog revocation and retained historical reads remain Registry lifecycle operations. See the [platform-catalog boundary](../../../../../specs/rpc/v1/authoring/platform-catalog/README.md).

## Package-Owned Native Assets

Declare package-owned native addons or sidecars by exact RID. Each source and target path is project-relative, and the target path is relative to the projected Runtime package root:

```json
{
  "nativeAssets": {
    "linux-x64": [
      {
        "sourcePath": "native/linux-x64/sqlite.node",
        "targetPath": "native/sqlite.node"
      }
    ],
    "osx-arm64": [
      {
        "sourcePath": "native/osx-arm64/sqlite.node",
        "targetPath": "native/sqlite.node"
      }
    ]
  }
}
```

Only the six supported RIDs are accepted, with at most 256 assets across the package. Sources must be project-contained regular files and cannot escape through symlinks. Target paths cannot collide with the Worker, notices, contracts, or each other on portable filesystems. Recognized PE, ELF, and thin/fat Mach-O binaries must identify a shared library for the configured RID; executable images, managed BCL assemblies, COFF/object/archive toolchain inputs, runtime fingerprints, and exact or header-mutated active Node executable content are rejected independently of names and executable mode.

## Package Output

Every generated target is exact-RID. Runtime target records agree on `bin/worker.cjs`, Node 24, the execution-runtime requirement, SDK version, and capabilities. Aggregation factors byte-identical Worker modules and notices into `payload/runtime/shared`, keeps contract descriptors in `payload/shared`, and always retains configured or detected native files in `payload/runtime/<rid>` even when their bytes match across RIDs.

Target-leaf producers and recursive aggregators coordinate through an explicit discovery root. Built-in commands use `dist/targets`; custom `package --leaf` selections must also pass `--discovery-root <path>`.

Generated directories and archives use cross-process locks, staged replacement, crash recovery, immutable snapshots, and deterministic ordering. Failed generation or packaging preserves the prior valid output.

Packaging also writes `<id>.<version>.package-evidence.json` beside the archive. This deterministic evidence binds the archive SHA-256 and size, metadata and payload bytes, each target's shared/exact-RID ownership, native asset hashes, and the execution-runtime closure counts derived by classifying the actual projected files. Packaging rejects a nonzero closure, so successful evidence records zero classified runtime files and bytes. The evidence is not embedded in the package archive.

## RPC Bindings And Registry

Configure generated bindings with `rpcBindings` entries containing a project-relative descriptor `path` and relative `.ts` `outputFile`. Outputs are rooted at `.sunder/generated/rpc`, replaced as one marker-owned set, and generated before dev, Worker, and web builds.

`fetchRegistryContract` reads immutable public RPC V1 descriptors from an exact Registry contract ID/version. It rejects redirects and untrusted download URLs, bounds reads to 4 MiB, and verifies exposure, size, descriptor SHA-256, ABI SHA-256, optional ETag, identity, and canonical bytes. `fetchRegistryContractBindings` performs the same verification before generation.
