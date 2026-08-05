# SUNDER_PACKAGE_NAME_TEXT

This scaffold builds one universal Sunder package from Runtime and optional Avalonia App leaves.

## Projects

- `Sunder.Package.Template.Protocol` is an always-present, non-packable package-local project containing `Contracts/sample.rpc.json`, generated DTO/client/provider adapters, and an embedded descriptor loader.
- `Sunder.Package.Template.Runtime` contains the Runtime module.
- `Sunder.Package.Template.App` is generated with `--withAvalonia`.
- `Sunder.Package.Template.csproj` builds the leaves and aggregates their exact targets into one canonical package.

Runtime and App consume the Protocol project through private project references. The generated CLR helpers remain local to this package; cross-package compatibility is the bundled language-neutral RPC descriptor.

## Build

Set the package version once in `Sunder.Package.props`; the aggregate and every generated leaf import it.

```bash
dotnet build Sunder.Package.Template.csproj
dotnet msbuild Sunder.Package.Template.csproj -t:PackSunderPackage -p:Configuration=Release
```

Build emits `bin/Debug/net10.0/sunder-dev` with `manifest/*`, `payload/shared`, and exact App/Runtime RID layers. The canonical pack target emits one validated `.sunderpkg` under `bin/Release/net10.0`.

All coordinated Sunder references use `SUNDER_TEMPLATE_PACKAGE_VERSION_RANGE`. `Sunder.Sdk.Avalonia` and `Sunder.Sdk.Stacks` are included only by their matching options. `--withHostDependency` adds only runtime package dependency metadata.

Package identity and dependencies come from `PackageMetadata.cs`; `Sunder.Package.Build` generates all manifests and indexes. Treat `context.ContentRootPath` as read-only. Use `context.Storage.RoleLocalWorkspace` for writable local paths, `context.Storage.State` for opaque operational state, `context.Settings` for schema-declared preferences, and `context.Secrets` for sensitive values.

TypeScript worker leaves produced by `npm create sunder-package@latest` may join the same aggregate through `SunderNodePackageTargetLeaf`; set each item's `DiscoveryRoot` metadata to the producer's `dist/targets` root. Do not include two leaves for the same exact role/RID key.

## Documentation

- Package development: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
