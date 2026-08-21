# SUNDER_PACKAGE_NAME_TEXT

This scaffold builds current-RID development output and one universal Sunder package directly from a canonical Worker/package project plus optional Avalonia App leaves.

## Projects

- `Sunder.Package.Template.Protocol` is an always-present, non-packable package-local project containing the descriptorVersion 2 `Contracts/sample.rpc.json`, build-generated DTO/client/provider adapters under `obj`, and an embedded descriptor loader.
- `Sunder.Package.Template.csproj` is the self-contained canonical Worker/package project. Its source stays under `Sunder.Package.Template.Worker/`; that folder has no separate csproj.
- `Sunder.Package.Template.App` is generated with `--withAvalonia`.

Worker and App consume the Protocol project through private project references. The App csproj is also supplied to the canonical project through build-only `SunderPackageAppProject`; tooling retains it in the outer restore graph without making it a Worker compile/runtime reference, and guarded inner Worker builds omit it. Generated CLR helpers are not checked in and remain local to this package; cross-package compatibility is the bundled language-neutral RPC descriptor and its ABI hash.

## Build

Set the package version once in `Sunder.Package.props`; every generated project imports it.

```bash
dotnet build Sunder.Package.Template.csproj
dotnet msbuild Sunder.Package.Template.csproj -t:PackSunderPackage -p:Configuration=Release
```

Build emits the exact current-RID Worker leaf under `bin/Debug/net10.0/<rid>/sunder-dev`, then composes it with the optional App under `bin/Debug/net10.0/sunder-dev`. Output has archive format 1, manifest version 3, descriptor and ABI hashes, `rpc.v2`, `worker-protocol.v3`, the SDK baseline, `payload/shared`, and exact App/Runtime RID layers. The canonical pack target sequentially builds all six Worker leaves and emits one validated universal `.sunderpkg` under `bin/Release/net10.0`.

All coordinated Sunder references use `SUNDER_TEMPLATE_PACKAGE_VERSION_RANGE`. `Sunder.Sdk.Avalonia` and `Sunder.Sdk.Stacks` are included only by their matching options. RPC uses and providers bind the exact contract version `2.0.0`; only the independent installed-package dependency added by `--withHostDependency` retains a version range.

Package identity and dependencies come from `PackageMetadata.cs`; `Sunder.Package.Build` generates all manifests and indexes. Worker code uses `SunderWorkerContext` for Host-mediated settings, state, files, secrets, logging, and RPC.

TypeScript worker leaves produced by `npm create sunder-package@latest` may join the same composition through `SunderNodePackageTargetLeaf`; set each item's `DiscoveryRoot` metadata to the producer's `dist/targets` root. Do not include two leaves for the same exact role/RID key.

## Documentation

- Package development: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
