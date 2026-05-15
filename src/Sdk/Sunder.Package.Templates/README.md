# Sunder.Package.Templates

`Sunder.Package.Templates` provides the `dotnet new sunder-package` template for creating Sunder runtime package projects.

The template scaffolds a package project that references `Sunder.Sdk` and `Sunder.Package.Build`, declares package metadata, includes a package module, and can optionally include an Avalonia view, public contracts project, runtime host dependency, and typed host contracts reference.

## Install

Install the template package from NuGet.org:

```powershell
dotnet new install Sunder.Package.Templates
```

Install a specific version:

```powershell
dotnet new install Sunder.Package.Templates
```

## Create A Package

Create a standard package with a default shell view:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
```

Create a package with no default view:

```powershell
dotnet new sunder-package --name MyHeadlessPackage --packageId my.company.headless --packageName "My Headless Package" --noDefaultView
```

Create a package that exposes public contracts:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --withContracts
```

Create an extension package that depends on a host package:

```powershell
dotnet new sunder-package --name MyExtension --packageId my.company.extension --packageName "My Extension" --withHostDependency --hostPackageId sunder.package.host
```

Create an extension package that also references a host contracts NuGet package:

```powershell
dotnet new sunder-package --name MyTypedExtension --packageId my.company.typedextension --packageName "My Typed Extension" --withHostDependency --hostPackageId sunder.package.host --withHostContracts --hostContractsPackageId Sunder.Host.Package.Contracts --hostContractsVersion <host-contracts-version>
```

## Template Options

| Option | Meaning |
| --- | --- |
| `--packageId <id>` | Required runtime package id written into generated metadata. |
| `--packageName <name>` | Required display name written into generated metadata and starter view. |
| `--withContracts` | Adds a sibling `*.Contracts` project for public extension points. |
| `--noDefaultView` | Omits the default shell-visible package view. |
| `--withHostDependency` | Adds runtime dependency metadata for another package. |
| `--hostPackageId <id>` | Required with `--withHostDependency`; runtime package id that this package depends on. |
| `--withHostContracts` | Adds host dependency metadata, a NuGet reference to the host package's contracts package, and a compile-safe extension stub. |
| `--hostContractsPackageId <id>` | Required with `--withHostContracts`; NuGet package id for host contracts. |
| `--hostContractsVersion <version>` | Required with `--withHostContracts`; NuGet package version for host contracts. |

## Generated Project

A standard generated package includes:

```text
MyPackage/
  MyPackage.csproj
  PackageMetadata.cs
  PackageModule.cs
  Assets/
    icon.png
  PackageViews/
    DefaultPackageView.cs
    DefaultPackageViewModel.cs
```

Generated package projects reference:

- `Sunder.Sdk`
- `Sunder.Package.Build`
- Avalonia packages used by the starter view

Generated projects use NuGet floating versions for `Sunder.Sdk` and `Sunder.Package.Build`, so restores resolve the latest stable Sunder SDK/build tooling from the configured package sources.

Package identity and dependencies are emitted from `PackageMetadata.cs`; `Sunder.Package.Build` generates `sunder-package.json` during build.

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
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- Sunder SDK: https://www.nuget.org/packages/Sunder.Sdk
