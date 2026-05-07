# Sunder Package Template

Creates a Sunder runtime package project that can be built into a `sunder-dev` folder and loaded into an installed `Sunder.App` instance.

## Common commands

```bash
dotnet new sunder-package --name MyPackage
dotnet new sunder-package --name MyPackage --withContracts
dotnet new sunder-package --name MyPackage --noDefaultView
dotnet new sunder-package --name MyExtension --withHostDependency --hostPackageId sunder.agent.core
dotnet new sunder-package --name MyTypedExtension --withHostDependency --hostPackageId sunder.agent.core --withHostContracts --hostContractsPackageId Sunder.Agent.Core.Contracts --hostContractsVersion 1.0.0
```

Generated package projects reference:

- `Sunder.Sdk`
- `Sunder.Package.Build`

You can build the generated package with:

```bash
dotnet build MyPackage/MyPackage.csproj
```

Then run it inside installed Sunder with the included scripts.

Package identity and dependencies are emitted from `PackageMetadata.cs`; `Sunder.Package.Build` generates `sunder-package.json` during build.

Use `--withHostDependency` when the generated package should declare a dependency on another package and scaffold integration notes.

Use `--withHostContracts` together with `--hostContractsPackageId` and `--hostContractsVersion` when the host package already publishes a `*.Contracts` package and you want the generated project to restore it immediately.
