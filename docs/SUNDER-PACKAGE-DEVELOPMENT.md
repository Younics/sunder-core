# Sunder Package Development

This is the package-author manual for creating, debugging, packaging, validating, and publishing Sunder packages.

## Prerequisites

- .NET 10 SDK.
- Sunder SDK packages available from the configured package feed or local source build.
- `Sunder.Package.Templates` installed as a `dotnet new` template package.
- A Sunder desktop app install or source build for development loading.
- A Sunder CLI build/install when validating or publishing from the command line.

Install the template from a local template package:

```powershell
dotnet new install .\path\to\Sunder.Package.Templates.1.0.0.nupkg
```

Install the template from a feed when published:

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
dotnet new sunder-package --name MyExtension --packageId my.company.extension --packageName "My Extension" --withHostDependency --hostPackageId sunder.package.agent
```

Create an extension package that also references a host contracts NuGet package:

```powershell
dotnet new sunder-package --name MyTypedExtension --packageId my.company.typedextension --packageName "My Typed Extension" --withHostDependency --hostPackageId sunder.package.agent --withHostContracts --hostContractsPackageId Sunder.Package.Agent.Contracts --hostContractsVersion 1.0.0
```

Template options:

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

## Project Files

Generated package projects reference `Sunder.Sdk` and `Sunder.Package.Build`.

Current generated package project shape:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" />
    <PackageReference Include="Sunder.Sdk" Version="1.0.0" />
    <PackageReference Include="Sunder.Package.Build" Version="1.0.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Rules:

- Package projects target `net10.0`.
- Package projects reference `Sunder.Sdk`.
- Package projects use `Sunder.Package.Build` for generated output.
- UI packages reference Avalonia libraries they directly use.
- Package projects do not reference `Sunder.App`.
- Package projects do not reference `Sunder.Runtime.Host`.
- Extension packages depend on host contracts packages when they need typed host extension APIs.

## Package Metadata

Edit `PackageMetadata.cs` to define identity, summary, icon, and runtime dependencies.

Standalone package metadata:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Summary = "Adds a custom Sunder workspace.",
    Icon = "assets/icon.png")]
```

Extension package metadata:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.extension",
    Name = "My Extension",
    Summary = "Extends another Sunder package.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.0.0 <2.0.0")]
```

The package version is controlled by MSBuild properties such as `Version`.

## Package Module

Every runtime package exposes one public module implementing `ISunderPackageModule`.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace MyCompany.Package;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddTransient<MyViewModel>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<MyView>(new PackageViewRegistration(
            "my.company.package.default",
            "My Package"));
    }
}
```

Current module rules:

- The package entry assembly contains exactly one public, non-abstract `ISunderPackageModule` implementation.
- The module type has a public parameterless constructor.
- Services are registered in `ConfigureServices`.
- Views, settings, background services, configuration schemas, and extensions are registered in `RegisterContributions`.

## Package Context

`IPackageContext` gives the module host-provided package services.

Available context members:

- `PackageId`
- `Version`
- `InstallPath`
- `Storage`
- `Configuration`
- `Secrets`
- `LoggerFactory`
- `Logging`

Use package storage/configuration/secrets abstractions for mutable package data. Do not write mutable state into the installed package folder.

## Views

Package views are Avalonia controls registered by code.

```csharp
registry.RegisterPackageView<MyView>(new PackageViewRegistration(
    id: "my.company.package.main",
    name: "My Package",
    icon: "assets/icon.png",
    defaultPlacement: PackageViewPlacement.Middle));
```

View guidance:

- Use stable view ids scoped under the package id.
- Use short user-facing names.
- Keep package UI independent of `Sunder.App` internals.
- Use Sunder semantic theme resources for shell-sensitive surfaces and text.
- Use normal Avalonia patterns and libraries inside package UI.

## Theme Resources

`Sunder.Sdk.Theming.SunderThemeKeys` contains semantic resource keys.

Common keys:

- `Sunder.Brush.Background.App`
- `Sunder.Brush.Surface.Base`
- `Sunder.Brush.Surface.Raised`
- `Sunder.Brush.Surface.Workspace`
- `Sunder.Brush.Foreground.Primary`
- `Sunder.Brush.Foreground.Secondary`
- `Sunder.Brush.Accent`
- `Sunder.Brush.Warning`
- `Sunder.Brush.Danger`
- `Sunder.Radius.Medium`
- `Sunder.Spacing.Medium`
- `Sunder.FontSize.Body`

Package UI can use these via Avalonia dynamic resources when it needs to match the shell theme.

## Icons And Assets

The template includes `Assets/icon.png` and metadata `Icon = "assets/icon.png"`.

Asset behavior:

- Source `Assets/**` is copied to dev output `assets/**`.
- `.sunderpkg` archives store package assets under `payload/assets/**`.
- Runtime asset URLs are served through the runtime host, not direct file paths.
- Registry package icons are extracted from package artifacts during publish.

Recommended icon setup:

```text
MyPackage/
  Assets/
    icon.png
  PackageMetadata.cs
```

```csharp
[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Icon = "assets/icon.png")]
```

## Build Dev Output

Build the package project:

```powershell
dotnet build .\MyPackage\MyPackage.csproj
```

Build output includes:

```text
MyPackage/bin/Debug/net10.0/sunder-dev/
  sunder-package.json
  lib/
  assets/
```

This folder is what `Sunder.App --dev-package` consumes.

## Load In Sunder App

Load one dev package into an installed app:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

Load multiple dev packages:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\HostPackage\bin\Debug\net10.0\sunder-dev" --dev-package ".\ExtensionPackage\bin\Debug\net10.0\sunder-dev"
```

Use the template helper script when generated from the package template:

```powershell
$env:SUNDER_APP_PATH = "C:\Path\To\Sunder.App.exe"
.\run-sunder-dev.ps1
```

The app sends dev-package folders to the runtime host through the local protocol. The runtime validates and activates the runtime side, then the app activates package UI contributions from the original dev-package folders.

## Debug Runtime Package Code

Start a runtime host on a debug URL and wait for a debugger:

```powershell
& "C:\Path\To\Sunder.Runtime.Host.exe" --wait-for-debugger --urls http://127.0.0.1:5276
```

Start the app against that runtime and load the dev package:

```powershell
& "C:\Path\To\Sunder.App.exe" --runtime-url http://127.0.0.1:5276 --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

Use the generated debug helper script:

```powershell
$env:SUNDER_APP_PATH = "C:\Path\To\Sunder.App.exe"
$env:SUNDER_RUNTIME_HOST_PATH = "C:\Path\To\Sunder.Runtime.Host.exe"
.\debug-sunder-runtime.ps1 -RuntimeUrl http://127.0.0.1:5276
```

The runtime also supports `SUNDER_WAIT_FOR_DEBUGGER=1`.

## Publish A Package Artifact

Publish the package project:

```powershell
dotnet publish .\MyPackage\MyPackage.csproj -c Release
```

The `.sunderpkg` is written to the publish directory, for example:

```text
MyPackage/bin/Release/net10.0/publish/MyPackage.1.0.0.sunderpkg
```

Use the explicit pack target when you want a package artifact without a full publish operation:

```powershell
dotnet msbuild .\MyPackage\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
```

## Validate A Package Artifact

Validate before publishing:

```powershell
sunder package validate .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

Validation checks archive safety, manifest shape, required files, package id format, SemVer version, entry assembly existence, icon existence, content index hashes, content index sizes, and unindexed files.

## Install Locally

Install or upgrade from a local file through the runtime host:

```powershell
sunder install --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

Allow downgrade or same-version reinstall when needed:

```powershell
sunder install --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg --allow-downgrade --reinstall
```

The runtime must be reachable at the configured runtime URL. The default is `http://127.0.0.1:5275/`.

## Publish To A Registry

Sign in before authenticated publish:

```powershell
sunder auth login
```

Publish to the configured Registry:

```powershell
sunder publish --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

Publish without moving the `latest` dist tag:

```powershell
sunder publish --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg --no-latest
```

Publish to a local development Registry endpoint when the Registry server is running in Development:

```powershell
sunder publish --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg --dev-local --registry-url http://localhost:5288/
```

Authentication token sources for publish:

- Saved CLI token from `sunder auth login`.
- `SUNDER_REGISTRY_TOKEN` environment variable.
- `--token <token>` command-line option.
- `--dev-local` for a development Registry endpoint that enables local publish.
