# SUNDER_PACKAGE_NAME_TEXT

This scaffold builds one universal Sunder package from exact-RID Worker V3 and optional Avalonia App leaves.

## Projects

- `Sunder.Package.Template.Protocol` is an always-present, non-packable package-local project containing the descriptorVersion 2 `Contracts/sample.rpc.json`, build-generated DTO/client/provider adapters under `obj`, and an embedded descriptor loader.
- `Sunder.Package.Template.Worker` is a self-contained executable that starts the canonical Worker V3 overload on `SunderWorker` and registers the owner-only sample provider.
- `Sunder.Package.Template.App` is generated with `--withAvalonia`.
- `Sunder.Package.Template.csproj` builds the leaves and aggregates their exact targets into one canonical package.

Worker and App consume the Protocol project through private project references. Generated CLR helpers are not checked in and remain local to this package; cross-package compatibility is the bundled language-neutral RPC descriptor and its ABI hash.

## Build

Set the package version once in `Sunder.Package.props`; the aggregate and every generated leaf import it.

```bash
dotnet build Sunder.Package.Template.csproj
dotnet msbuild Sunder.Package.Template.csproj -t:PackSunderPackage -p:Configuration=Release
```

Build emits `bin/Debug/net10.0/sunder-dev` with archive format 1, manifest version 3, descriptor and ABI hashes, `rpc.v2`, `worker-protocol.v3`, the SDK baseline, `payload/shared`, and exact App/Runtime RID layers. The canonical pack target emits one validated `.sunderpkg` under `bin/Release/net10.0`.

All coordinated Sunder references use `SUNDER_TEMPLATE_PACKAGE_VERSION_RANGE`. `Sunder.Sdk.Avalonia` and `Sunder.Sdk.Stacks` are included only by their matching options. RPC uses and providers bind the exact contract version `2.0.0`; only the independent installed-package dependency added by `--withHostDependency` retains a version range.

Package identity and dependencies come from `PackageMetadata.cs`; `Sunder.Package.Build` generates all manifests and indexes. Worker code uses `SunderWorkerContext` for Host-mediated settings, state, files, secrets, logging, and RPC.

TypeScript worker leaves produced by `npm create sunder-package@latest` may join the same aggregate through `SunderNodePackageTargetLeaf`; set each item's `DiscoveryRoot` metadata to the producer's `dist/targets` root. Do not include two leaves for the same exact role/RID key.

## Documentation

- Package development: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
