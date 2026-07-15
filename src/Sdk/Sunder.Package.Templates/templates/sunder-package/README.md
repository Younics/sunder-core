# Sunder Package Template

Creates a Sunder runtime package project that can be built into a `sunder-dev` folder and loaded into an installed `Sunder.App` instance.

## Common commands

```bash
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --createInPlace --output ./MyPackage
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --withContracts
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --withAvalonia
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --withStacks
dotnet new sunder-package --name MyExtension --packageId my.company.extension --packageName "My Extension" --withHostDependency --hostPackageId sunder.package.agent --hostPackageVersionRange ">=1.0.0 <2.0.0"
dotnet new sunder-package --name MyTypedExtension --packageId my.company.typedextension --packageName "My Typed Extension" --withHostContracts --hostPackageId sunder.package.agent --hostPackageVersionRange ">=1.0.0 <2.0.0" --hostContractsPackageId Sunder.Package.Agent.Contracts --hostContractsVersion <host-contracts-version>
```

Generated package projects reference:

- `Sunder.Sdk`
- `Sunder.Package.Build`

All Sunder references use exact version `1.1.0`. `Sunder.Sdk.Avalonia` and `Sunder.Sdk.Stacks` are added only by their matching opt-ins. Stack support registers its exporter and importer separately even when one class implements both interfaces.

You can build the generated package with:

```bash
dotnet build MyPackage/MyPackage.csproj
```

Then load `MyPackage/bin/Debug/net10.0/sunder-dev` into an installed Sunder app with `--dev-package`.

Package identity and dependencies are emitted from `PackageMetadata.cs`; `Sunder.Package.Build` generates `sunder-package.json` during build.

Use `Sunder.Sdk.Packaging.PackageId`, `SemanticVersion`, and `PackageVersionRange` for package identity/version validation. `context.ContentRootPath` is read-only package content. If the package needs a writable local filesystem path, use `context.Storage.RoleLocalWorkspace`; the current App or Runtime activation owns that capability and package code must not dispose it.

`PackageRuntimeState` demonstrates opaque operational data in `context.Storage.State`. For user preferences, register a `PackageSettingsSchema` with `RegisterSettingsSchema` and use `context.Settings`; settings are validated and persisted separately. Sensitive fields use `context.Secrets` and cannot declare defaults.

Use `--withHostDependency` when the generated package should declare a dependency on another package and scaffold integration notes.

Use `--withHostContracts` together with `--hostPackageId`, `--hostContractsPackageId`, and `--hostContractsVersion` when the host package already publishes a `*.Contracts` package and you want the generated project to restore it immediately. This also adds runtime host dependency metadata.

Use `--createInPlace` when the specified output folder should be the package project folder itself.

Use `--noDefaultView` with `--withAvalonia` when the package needs Avalonia settings or custom UI contributions but should not scaffold a shell-visible default view.

`--packageId` and `--packageName` are required so generated packages do not keep template runtime identity metadata.

Development output is normally supplied at Runtime startup with `--dev-package`. Package UI that integrates development loading must resolve optional `IPackageDevelopmentSessionControl`, check `Availability`, and keep Load/Watch controls disabled with `UnavailableReason` when App-local paths cannot reach Runtime.
