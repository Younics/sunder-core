# Sunder.Package.Templates

`Sunder.Package.Templates` provides the `dotnet new sunder-package` template for creating Sunder runtime package projects.

The template scaffolds a package project that references `Sunder.Sdk` and `Sunder.Package.Build`, declares package metadata, includes a package module, and can optionally include an Avalonia view, public contracts project, runtime host dependency, and typed host contracts reference.

## Install

Install the template package from a configured NuGet feed:

```powershell
dotnet new install Sunder.Package.Templates
```

Install a specific version:

```powershell
dotnet new install Sunder.Package.Templates::1.0.0
```

## Create A Package

Create a standard package with a default shell view:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --sunderVersion 1.0.0
```

Create a package with no default view:

```powershell
dotnet new sunder-package --name MyHeadlessPackage --packageId my.company.headless --packageName "My Headless Package" --noDefaultView
```

Create a package that exposes contracts:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --withContracts
```

Create an extension package that depends on a host package:

```powershell
dotnet new sunder-package --name MyExtension --packageId my.company.extension --packageName "My Extension" --withHostDependency --hostPackageId sunder.package.host
```

Create an extension package that also references a host contracts NuGet package:

```powershell
dotnet new sunder-package --name MyTypedExtension --packageId my.company.typedextension --packageName "My Typed Extension" --withHostDependency --hostPackageId sunder.package.host --withHostContracts --hostContractsPackageId Sunder.Package.Host.Contracts --hostContractsVersion 1.0.0
```

## Template Options

| Option | Meaning |
| --- | --- |
| `--packageId <id>` | Runtime package id written into generated metadata |
| `--packageName <name>` | Display name written into generated metadata and starter view |
| `--sunderVersion <version>` | Version used for `Sunder.Sdk` and `Sunder.Package.Build` package references |
| `--withContracts` | Adds a sibling `*.Contracts` project for public extension points |
| `--noDefaultView` | Omits the default shell-visible package view |
| `--withHostDependency` | Adds runtime dependency metadata for another package |
| `--hostPackageId <id>` | Runtime package id that this package depends on |
| `--withHostContracts` | Adds a NuGet reference to the host package's contracts package |
| `--hostContractsPackageId <id>` | NuGet package id for host contracts |
| `--hostContractsVersion <version>` | NuGet package version for host contracts |

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
run-sunder-dev.ps1
debug-sunder-runtime.ps1
```

Generated package projects reference:

- `Sunder.Sdk`
- `Sunder.Package.Build`
- Avalonia packages used by the starter view

The generated metadata uses `Sunder.Sdk.Packaging` attributes:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Summary = "Adds a custom Sunder package.",
    Icon = "assets/icon.png")]
```

`Sunder.Package.Build` generates `sunder-package.json` during build. Do not edit or maintain the generated manifest by hand.

## Build And Run

Build the generated package project:

```powershell
dotnet build .\MyPackage\MyPackage.csproj
```

The build emits:

```text
MyPackage/bin/Debug/net10.0/sunder-dev/
  sunder-package.json
  lib/
  assets/
```

Load the dev package into an installed Sunder App:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

Or use the generated helper script:

```powershell
$env:SUNDER_APP_PATH = "C:\Path\To\Sunder.App.exe"
.\run-sunder-dev.ps1
```

## Debug Runtime Code

Use the generated debug helper script when you want the runtime host to wait for a debugger:

```powershell
$env:SUNDER_APP_PATH = "C:\Path\To\Sunder.App.exe"
$env:SUNDER_RUNTIME_HOST_PATH = "C:\Path\To\Sunder.Runtime.Host.exe"
.\debug-sunder-runtime.ps1 -RuntimeUrl http://127.0.0.1:5276
```

The runtime also supports `SUNDER_WAIT_FOR_DEBUGGER=1`.

## Publish

Publish the package project to create a `.sunderpkg` archive:

```powershell
dotnet publish .\MyPackage\MyPackage.csproj -c Release
```

Example output:

```text
MyPackage/bin/Release/net10.0/publish/MyPackage.1.0.0.sunderpkg
```

Validate before publishing to a registry:

```powershell
sunder package validate .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

Publish with the Sunder CLI after signing in:

```powershell
sunder auth login
sunder publish --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

## Contracts And Host Dependencies

Use `--withContracts` when your package exposes public extension points or contribution interfaces for other package authors. The generated `*.Contracts` project is a NuGet contracts package, not a Sunder runtime package.

Use `--withHostDependency` when the generated package requires another installed Sunder package at runtime. This writes `[assembly: SunderPackageDependency(...)]` metadata.

Use `--withHostContracts` when that host package also publishes a contracts NuGet package that your extension needs at compile time.

Runtime package dependencies and NuGet contracts dependencies are separate concepts.

## More Documentation

- Package author manual: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- Sunder SDK: https://www.nuget.org/packages/Sunder.Sdk
