# Sunder.Package.Build

`Sunder.Package.Build` contains the MSBuild targets and tasks that turn a Sunder package project into local development output and distributable `.sunderpkg` archives.

Reference this package from Sunder runtime package projects together with `Sunder.Sdk`.

## Install

```powershell
dotnet add package Sunder.Package.Build --private-assets all
```

Typical package project reference:

```xml
<ItemGroup>
  <PackageReference Include="Sunder.Sdk" Version="1.0.0" />
  <PackageReference Include="Sunder.Package.Build" Version="1.0.0" PrivateAssets="all" />
</ItemGroup>
```

Use `PrivateAssets="all"` because this package is build tooling for the package project. It should not flow as a runtime dependency of your package.

## What It Does

`Sunder.Package.Build` adds build targets that:

- Generate `sunder-package.json` from `Sunder.Sdk` metadata attributes, MSBuild properties, and build output.
- Emit an unpacked `sunder-dev` folder after `dotnet build`.
- Create a `.sunderpkg` archive after `dotnet publish`.
- Provide an explicit `PackSunderPackage` MSBuild target.
- Copy package runtime files into `lib`.
- Copy source assets from `Assets/**` into `assets/**`.
- Exclude host boundary assemblies such as `Sunder.Sdk` and core Avalonia assemblies from private package output.

Package authors do not maintain `sunder-package.json` by hand.

## Expected Project Shape

A runtime package project should declare package metadata in C# and include exactly one public `ISunderPackageModule` implementation.

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Summary = "Adds a custom Sunder workspace.",
    Icon = "assets/icon.png")]
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace MyCompany.Package;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
    }
}
```

## Build Development Output

Build the package project:

```powershell
dotnet build .\MyPackage\MyPackage.csproj
```

The build emits an unpacked dev package next to the build output:

```text
bin/Debug/net10.0/sunder-dev/
  sunder-package.json
  lib/
    MyPackage.dll
    MyPackage.pdb
    MyPackage.deps.json
  assets/
    icon.png
```

Load this folder into Sunder App during development:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

## Publish A Package Archive

Publish the package project:

```powershell
dotnet publish .\MyPackage\MyPackage.csproj -c Release
```

The publish target writes a `.sunderpkg` archive to the publish directory:

```text
bin/Release/net10.0/publish/MyPackage.1.0.0.sunderpkg
```

Use the explicit pack target when you want an archive without a full publish operation:

```powershell
dotnet msbuild .\MyPackage\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
```

## Archive Shape

`.sunderpkg` files are zip archives with this layout:

```text
manifest/
  sunder-package.json
  content-index.json
payload/
  lib/
  assets/
```

The content index records package file paths, hashes, sizes, and roles. Runtime install and registry publish paths validate the archive before accepting it.

## Assets And Icons

Place package assets under `Assets/` in the project directory. They are copied into package output under `assets/`.

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

Icon paths must be relative package asset paths. Do not use absolute paths or parent directory traversal.

## Useful Properties

Common MSBuild properties used by the package targets:

| Property | Purpose |
| --- | --- |
| `Version` | Package version used in generated metadata and default archive name |
| `SunderDevOutputPath` | Overrides the generated `sunder-dev` output directory |
| `SunderPackageFileName` | Overrides the default archive file name |
| `SunderPackageOutputPath` | Overrides output path for the explicit `PackSunderPackage` target |
| `SunderPublishPackageOutputPath` | Overrides archive output path during `dotnet publish` |

## Validate Before Publishing

Use the Sunder CLI to validate package artifacts before publishing:

```powershell
sunder package validate .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

Validation checks archive safety, manifest shape, required files, package id format, SemVer version, entry assembly existence, icon existence, content index hashes, content index sizes, and unindexed files.

## More Documentation

- Package author manual: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- Sunder SDK: https://www.nuget.org/packages/Sunder.Sdk
