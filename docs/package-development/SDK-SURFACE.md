# SDK surface map

The SDK is currently **UNFROZEN / UNRELEASED**. This map explains intended usage;
it does not turn public generated-code dependencies into private APIs. After the
first supported baseline, compatible fixes remain allowed and public contract
changes require the compatibility/release gates in
[the policy](../SUNDER-SDK-COMPATIBILITY.md).

## Four usage layers

| Layer | .NET | Node / browser |
| --- | --- | --- |
| Normal authoring | Template, ordinary service classes/models, generated `I<Service>`, content/references, settings/logging/storage and App services | Template, `defineService`, ordinary TS or checked JS, generated `Self` / `Services`, content/references, managed browser/React contexts |
| Advanced integration | Low-level RPC, manual modules, Worker bootstrap, compatibility metadata | Descriptor utilities, `runWorker`, low-level RPC and explicit transport integration |
| Generated runtime support | Public marshalling, exact-binding, resource lifetime and call helpers in `Sunder.Sdk` | Explicit browser-safe `@sunder/sdk/generated`; referenced by emitted code, not a normal authoring recipe |
| Build tooling | `Sunder.Package.Build`, internal `Rpc/Generation`, editor generator, templates | `@sunder/package-tool` source analysis/emission and `sunder-rpc-gen`; `create-sunder-package` starters |

Generated runtime support remains shipped and supported even when hidden from
IntelliSense or marked `@internal` in source. Those markers are documentation,
not assembly/package visibility or permission boundaries. Public signatures and
the types they expose must still work for unmodified supported generated consumers.

The six public NuGets remain `Sunder.Sdk`, `Sunder.Sdk.Avalonia`,
`Sunder.Sdk.Stacks`, `Sunder.Sdk.Worker`, `Sunder.Package.Build` and
`Sunder.Package.Templates`. Host DTOs, archive inspection and Registry DTOs are not
new package-author dependencies. The four `Sunder.Sdk*` NuGets include XML API
documentation; generated XML companions still do not belong in runtime `.sunderpkg`
payloads. The npm SDK has no runtime dependency on its generator or package tool.

## Reviewable inventory

From the Core checkout, after installing the pinned Node workspace dependencies:

```sh
node scripts/compatibility/inspect-sdk-surface.mjs > sdk-surface-review.json
```

This read-only report enumerates every type/member in the current developer-assembly
API snapshots and every semantic export of the SDK's five explicit npm entry points,
plus the package-tool API, with source input hashes and a usage category. API snapshot tests independently
compare those expectations to compiled assemblies. The report is a source review
aid, not a binary or supported-release receipt; internal tooling is listed separately.
Protected release snapshots and the supported-release ledger are never regenerated
by this command.

## Approved unreleased changes

- `IBackgroundProcessQueue` / `BackgroundProcess*` became
  `IBackgroundTaskQueue` / `BackgroundTask*`, with `TaskId`, `TaskChanged` and
  `ListTasks`. Sunder itself uses the queue, so the common types have no `Package`
  prefix. Activation-scoped adapters still enforce package ownership. The existing
  capability identifier `background-processes.v1` is intentionally unchanged.
- Development status has independent SDK-owned `HostState`, `RuntimeReadiness`
  and `AppReadiness` enums. Unknown is not ready; activation is not DOM readiness
  or evidence of a business operation. Applied revisions still require exact
  Runtime/full-source and applicable published App evidence.
- Build-time generators no longer inflate the runtime SDK surface. C# emission
  is internal to Build; npm emission and its CLI belong to package-tool. Binding
  support remains in the runtime SDK rather than being copied into every package.
- Dependency template switches describe packages and require an explicit reviewed
  version range; they do not inherit a package version from the SDK version.

These are intentional pre-freeze source/ABI changes, not additive fixes to an
already supported release. Older development artifacts need rebuilding; earlier
qualification results retain their own exact source/artifact scope.
