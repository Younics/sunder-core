# Sunder.Sdk

`Sunder.Sdk` contains the public contracts used to build Sunder runtime packages.

Use this package when you want to create a package that can be loaded by the Sunder runtime, contribute Avalonia views to the Sunder shell, register background services, expose typed extension points, or consume package-scoped storage, configuration, secrets, logging, and theme resources.

SDK/Host compatibility is capability-based. `Sunder.Package.Build` infers SDK requirements automatically; see `docs/SUNDER-SDK-COMPATIBILITY.md` in the Sunder Core repository for the full policy.

## Install

```powershell
dotnet add package Sunder.Sdk
```

Most runtime packages should also reference `Sunder.Package.Build` so builds generate the Sunder manifest, development output, and distributable archive:

```powershell
dotnet add package Sunder.Package.Build --private-assets all
```

For a new package project, the quickest path is the template package:

```powershell
dotnet new install Sunder.Package.Templates
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
```

## Package Shape

A Sunder runtime package is a .NET assembly that declares package metadata and exposes exactly one public module implementing `ISunderPackageModule`.

Typical package projects:

- Target `net10.0`.
- Reference `Sunder.Sdk`.
- Reference `Sunder.Package.Build` with `PrivateAssets="all"`.
- Reference Avalonia packages when they provide UI.
- Do not reference `Sunder.App` or `Sunder.Runtime.Host`.

## Package Metadata

Package identity and runtime dependencies are declared with assembly attributes from `Sunder.Sdk.Packaging`.

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Summary = "Adds a custom Sunder workspace.",
    Icon = "assets/icon.png")]
```

Extension packages can declare runtime package dependencies:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.extension",
    Name = "My Extension",
    Summary = "Extends another Sunder package.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.host",
    VersionRange = ">=1.0.0 <2.0.0")]
```

Package id rules:

- Use lowercase dot-separated ASCII identifiers such as `my.company.package`.
- Do not use spaces, underscores, or display-name casing.
- Do not rename a package id after publishing.
- Keep `Name` short and user-facing.
- Use `Summary` for one sentence of package description.

The package version comes from normal MSBuild properties such as `Version`, not from the metadata attributes.

## Package Module

Every runtime package exposes one public, non-abstract module with a public parameterless constructor.

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
            id: "my.company.package.default",
            name: "My Package",
            icon: "assets/icon.png",
            defaultPlacement: PackageViewPlacement.Middle));
    }
}
```

Use `ConfigureServices` for dependency injection setup. Use `RegisterContributions` for shell-visible and runtime-visible package contributions.

## Contributions

`IPackageContributionRegistry` currently supports:

- `RegisterPackageView<TView>(PackageViewRegistration registration)`
- `RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration)`
- `RegisterSettingsView<TView>()`
- `RegisterSettingsViewFactory<TFactory>()`
- `RegisterBackgroundService<TService>()`
- `RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)`
- `RegisterConfigurationSchema(PackageConfigurationSchema schema)`

Package views are Avalonia controls registered by code. Use stable view ids scoped under your package id.

```csharp
registry.RegisterPackageView<MyView>(new PackageViewRegistration(
    "my.company.package.main",
    "My Package",
    icon: "assets/icon.png"));
```

## Package Context

`IPackageContext` gives your module access to host-provided package services:

- `PackageId`
- `Version`
- `InstallPath`
- `Storage`
- `Configuration`
- `Secrets`
- `LoggerFactory`
- `Logging`

Use package storage, configuration, and secrets abstractions for mutable package data. Do not write mutable state into the installed package folder.

## Extension Catalog

Packages can query installed/active contributions through `IPackageExtensionCatalog`:

```csharp
var providers = extensionCatalog.GetExtensions(MyExtensionPoints.Providers);
```

When a package needs to update open UI or cached capability lists as other packages activate/deactivate, inject `IPackageExtensionCatalog` and cast to `IPackageExtensionCatalogMonitor`. `Changed` provides a revision, lifecycle reason, and extension-point changes including package id and contribution type.

Use the change details to refresh only affected state, for example execution-target UI when `sunder.package.agent:execution-targets` changes.

## Callback Sessions

`IPackageCallbackHandler` is the generic host callback contract for browser or local callback flows. `IPackageAuthHandler` remains the auth-specific status/disconnect surface.

Register callback handlers in `ConfigureServices`. Auth-capable packages can register the same implementation as both `IPackageAuthHandler` and `IPackageCallbackHandler`.

## Theme Resources

Package UI can use semantic Sunder theme keys from `Sunder.Sdk.Theming.SunderThemeKeys`. These keys let package UI match the active Sunder shell theme without referencing app internals.

Common resource keys include:

- `Sunder.Brush.Background.App`
- `Sunder.Brush.Surface.Base`
- `Sunder.Brush.Surface.Raised`
- `Sunder.Brush.Surface.Workspace`
- `Sunder.Brush.Foreground.Primary`
- `Sunder.Brush.Foreground.Secondary`
- `Sunder.Brush.Accent`
- `Sunder.Radius.Medium`
- `Sunder.Spacing.Medium`
- `Sunder.FontSize.Body`

Example Avalonia usage:

```xml
<Border Background="{DynamicResource Sunder.Brush.Surface.Workspace}"
        CornerRadius="{DynamicResource Sunder.Radius.Medium}"
        Padding="{DynamicResource Sunder.Spacing.Medium}">
  <TextBlock Text="Hello from my package"
             Foreground="{DynamicResource Sunder.Brush.Foreground.Primary}" />
</Border>
```

## Runtime Dependencies And Contracts

Runtime package dependencies and NuGet contracts dependencies are separate concepts.

Use `[assembly: SunderPackageDependency(...)]` when your installed package requires another installed Sunder package at runtime.

Use a normal NuGet package reference when you need compile-time contracts from another package, such as a `*.Contracts` package that declares extension points or contribution interfaces.

## Build And Publish

With `Sunder.Package.Build` referenced, package builds generate an unpacked development package:

```powershell
dotnet build .\MyPackage\MyPackage.csproj
```

The generated `sunder-dev` folder can be loaded into Sunder App for local development:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

Publishing produces a distributable `.sunderpkg` archive:

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
- Sunder overview: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER.md
