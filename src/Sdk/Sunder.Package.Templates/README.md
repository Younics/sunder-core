# Sunder.Package.Templates

`Sunder.Package.Templates` provides the `dotnet new sunder-package` template for creating Sunder package projects.

The template scaffolds a headless package project with exact V1 SDK/build references, package metadata, separate Runtime/App role examples, and async package storage usage. Avalonia, Stacks, public contracts, runtime host dependencies, and typed host contracts are explicit opt-ins.

## Install

Install the template package from NuGet.org:

```powershell
dotnet new install Sunder.Package.Templates
```

Install a specific version:

```powershell
dotnet new install Sunder.Package.Templates::<version>
```

## Create A Package

Create a headless Runtime package:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
```

Create package files directly in the specified output folder:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --createInPlace --output .\MyPackage
```

Create a package with an Avalonia App view:

```powershell
dotnet new sunder-package --name MyUiPackage --packageId my.company.ui --packageName "My UI Package" --withAvalonia
```

Create a package that exposes public contracts:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --withContracts
```

Create an extension package that depends on a host package:

```powershell
dotnet new sunder-package --name MyExtension --packageId my.company.extension --packageName "My Extension" --withHostDependency --hostPackageId sunder.package.agent --hostPackageVersionRange ">=1.1.0 <1.2.0"
```

Create an extension package that also references a host contracts NuGet package:

```powershell
dotnet new sunder-package --name MyTypedExtension --packageId my.company.typedextension --packageName "My Typed Extension" --withHostContracts --hostPackageId sunder.package.agent --hostPackageVersionRange ">=1.1.0 <1.2.0" --hostContractsPackageId Sunder.Package.Agent.Contracts --hostContractsVersionRange "[1.1.0,1.2.0)"
```

## Template Options

| Option | Meaning |
| --- | --- |
| `--packageId <id>` | Required runtime package id written into generated metadata. |
| `--packageName <name>` | Required display name written into generated metadata and starter view. |
| `--withAvalonia` | Adds Avalonia and `Sunder.Sdk.Avalonia`; includes a default App package view unless `--noDefaultView` is set. |
| `--noDefaultView` | With `--withAvalonia`, omits the default view while retaining Avalonia references for settings or custom contributions. |
| `--withStacks` | Adds `Sunder.Sdk.Stacks` and separate Runtime Stack exporter/importer registrations. |
| `--withContracts` | Adds a `*.Contracts` project for public extension points. |
| `--createInPlace` | Creates package files directly in the specified output folder instead of under a child project folder. |
| `--withHostDependency` | Adds runtime dependency metadata for another package. |
| `--hostPackageId <id>` | Required with `--withHostDependency` or `--withHostContracts`; runtime package id that this package depends on. |
| `--hostPackageVersionRange <range>` | Runtime SemVer range for the host dependency; defaults to `>=1.1.0 <1.2.0`. |
| `--withHostContracts` | Adds host dependency metadata, a NuGet reference to the host package's contracts package, and a compile-safe extension stub. |
| `--hostContractsPackageId <id>` | Required with `--withHostContracts`; NuGet package id for host contracts. |
| `--hostContractsVersionRange <range>` | Bounded NuGet range for host contracts; defaults to the generated Sunder SDK minor range. |

## Generated Project

A standard generated package includes:

```text
MyPackage/
  MyPackage.csproj
  PackageMetadata.cs
  PackageModule.cs
  Assets/
    icon.png
  PackageRuntimeState.cs
```

Without `--createInPlace`, the template keeps the project under a child `MyPackage/` folder in the selected output. With `--createInPlace`, the project files above are written directly into the selected output folder.

Generated package projects reference:

- `Sunder.Sdk`
- `Sunder.Package.Build`
- `Sunder.Sdk.Avalonia` and Avalonia only with `--withAvalonia`
- `Sunder.Sdk.Stacks` only with `--withStacks`

Generated projects use a bounded minor range derived from the template package version for all coordinated Sunder SDK and build packages. A 1.1 template emits `[1.1.0,1.2.0)`.

With `--withContracts`, the sibling `*.Contracts` project is packable, carries a public `Sunder.Sdk` dependency because its API exposes `PackageExtensionPoint<T>`, and starts at contracts package version `1.0.0`. Version and publish that contracts package independently when other packages consume it.

Package identity and dependencies are emitted from `PackageMetadata.cs`; `Sunder.Package.Build` generates `sunder-package.json` during build.

Generated code can use `Sunder.Sdk.Packaging.PackageId`, `SemanticVersion`, and `PackageVersionRange` as the canonical validators. `context.ContentRootPath` is read-only package content. Writable local-path integrations use `context.Storage.RoleLocalWorkspace`; the host activation owns its lifecycle and package code does not dispose it.

## Build And Run

Build the generated package project:

```powershell
dotnet build .\MyPackage\MyPackage.csproj
```

The build emits an unpacked development package under `bin/Debug/net10.0/sunder-dev/`.

Load the dev package into an installed Sunder App:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

## Publish

Publish the generated package project to create a `.sunderpkg` archive:

```powershell
dotnet publish .\MyPackage\MyPackage.csproj -c Release
```

Validate before publishing to a registry:

```powershell
sunder package validate .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

## More Documentation

- Package author manual: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Getting started: https://github.com/Younics/sunder-core/blob/main/docs/package-development/GETTING-STARTED.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- Sunder SDK: https://www.nuget.org/packages/Sunder.Sdk
