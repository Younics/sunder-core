# Sunder Package Standard

This document describes the current Sunder package standard implemented by `Sunder.Sdk`, `Sunder.Package.Build`, `Sunder.PackageManagement`, `Sunder.Runtime.Host`, and the Registry.

## Core Rules

- Sunder has one runtime extension unit: `Package`.
- Package identity and runtime dependencies are authored in C# metadata attributes.
- `Sunder.Package.Build` generates `sunder-package.json`; package authors do not maintain it by hand.
- `dotnet build` emits an unpacked `sunder-dev` folder.
- `dotnet publish` emits a `.sunderpkg` package archive into `$(PublishDir)`.
- The runtime validates package metadata and archive content before install or update.
- Runtime package dependencies and NuGet contracts dependencies are separate concepts.
- Package view names, settings views, configuration schemas, background services, auth handlers, and extension contributions are registered in code, not in the generated manifest.

## Package Identity

Package ids use lowercase dot-separated ASCII identifiers.

Valid examples:

- `sunder.package.agent`
- `sunder.package.agent.provider.openai`
- `sunder.package.agent.tools.web`

Rules:

- Use ASCII lowercase letters, digits, and dots.
- Do not use spaces, underscores, or display-name casing.
- Do not rename a package id after publishing.
- Keep `name` short and human-facing.
- Use `summary` for one sentence of package description.
- Use SemVer-compatible package versions such as `1.0.0` or `1.2.0-beta.1`.

## Metadata Attributes

Every package assembly declares exactly one `SunderPackageAttribute`.

Standalone package:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.example",
    Name = "Example Package",
    Summary = "Adds an example Sunder workspace.",
    Icon = "assets/icon.png")]
```

Extension package:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.example.extension",
    Name = "Example Extension",
    Summary = "Adds behavior to Example Package.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.example",
    VersionRange = ">=1.0.0 <2.0.0")]
```

Metadata fields:

| Field | Required | Source | Meaning |
| --- | --- | --- | --- |
| `Id` | Yes | `SunderPackageAttribute` | Stable runtime package id |
| `Name` | Yes | `SunderPackageAttribute` | User-facing package name |
| `Summary` | No | `SunderPackageAttribute` | One-sentence package description |
| `Icon` | No | `SunderPackageAttribute` | Relative package asset path for the package icon |
| `PackageId` | Yes for dependencies | `SunderPackageDependencyAttribute` | Runtime dependency package id |
| `VersionRange` | Yes for dependencies | `SunderPackageDependencyAttribute` | Required dependency version range |

The package version comes from MSBuild package/project version properties, not from C# attributes.

## Generated Manifest

`Sunder.Package.Build` generates `sunder-package.json` from compiled metadata, MSBuild properties, and build output.

Current manifest shape:

```json
{
  "manifestVersion": 1,
  "id": "sunder.package.example",
  "name": "Example Package",
  "summary": "Adds an example Sunder workspace.",
  "version": "1.0.0",
  "entryAssembly": "Example.Package.dll",
  "icon": "assets/icon.png",
  "dependsOn": [
    {
      "packageId": "sunder.package.host",
      "versionRange": ">=1.0.0 <2.0.0"
    }
  ],
  "sdkVersion": "1.0.0",
  "targetFramework": "net10.0"
}
```

Required fields:

- `manifestVersion`: manifest format version, currently `1`.
- `id`: stable package id.
- `name`: user-facing package name.
- `version`: SemVer-compatible package version.
- `entryAssembly`: package entry assembly file name.

Optional fields:

- `summary`: package description.
- `icon`: package icon asset path.
- `dependsOn`: runtime package dependency list.
- `sdkVersion`: referenced Sunder SDK version when available.
- `targetFramework`: package target framework.

Fields not used by the current generated manifest:

- `displayName`
- `sunderApiRange`
- `kind`
- `runtime`
- `runtime.moduleType`
- `shell`
- `shell.views`
- `contributions`
- `configuration`
- `requestedPermissions`

## Module Discovery

The generated manifest records the entry assembly, not the module type.

Runtime discovery rules:

- The package entry assembly is loaded from package `lib` output.
- The runtime finds public, non-abstract types implementing `ISunderPackageModule`.
- Exactly one package module type must be discoverable.
- The module type must have a public parameterless constructor.
- Zero or multiple module types fail validation/activation.

Current module API:

```csharp
public interface ISunderPackageModule
{
    void ConfigureServices(IServiceCollection services, IPackageContext context);

    void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services);
}
```

## Contributions

Package contributions are registered in `RegisterContributions`.

Current contribution registry capabilities:

- `RegisterPackageView<TView>(PackageViewRegistration registration)`
- `RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration)`
- `RegisterSettingsView<TView>()`
- `RegisterSettingsViewFactory<TFactory>()`
- `RegisterBackgroundService<TService>()`
- `RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)`
- `RegisterConfigurationSchema(PackageConfigurationSchema schema)`

View registrations use stable view ids and user-facing view names:

```csharp
registry.RegisterPackageView<DefaultPackageView>(new PackageViewRegistration(
    "sunder.package.example.default",
    "Example"));
```

## Package Icons

Package icon paths are relative package asset paths. The template uses `assets/icon.png`, backed by source file `Assets/icon.png`.

Rules:

- Icon paths must be relative.
- Icon paths must not contain parent directory traversal.
- Icon files must exist at build time when `Icon` is declared.
- The build maps source `Assets/**` into output `assets/**`.
- The runtime serves active package assets through `/api/packages/{packageId}/assets/{assetPath}`.
- The app loads PNG/SVG/raster package icons directly and falls back to the first character of the package name.
- Icon load failures are written to `AppSessionLog`; they are not shown as package UI errors.

Supported image content types in runtime/registry paths:

- BMP
- GIF
- ICO
- JPG/JPEG
- PNG
- SVG/SVGZ
- WebP

## Dev Package Output

`dotnet build` emits an unpacked dev package folder next to the build output.

Current layout:

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

Build behavior:

- `Sunder.Package.Build` removes the previous dev output before emitting a new one.
- Package assemblies and private dependencies are copied to `lib`.
- Native runtime assets under build output `runtimes` are copied under `lib/runtimes`.
- Source files under `Assets` are copied to `assets`.
- Host boundary assemblies such as `Sunder.Sdk` and core Avalonia assemblies are excluded from private package output.

## Package Archive

`.sunderpkg` is the distributable package archive.

Current archive layout:

```text
manifest/
  sunder-package.json
  content-index.json
payload/
  lib/
  assets/
```

Current archive behavior:

- The archive is a zip file with the `.sunderpkg` extension.
- `manifest/sunder-package.json` is copied from generated package metadata.
- `manifest/content-index.json` records every package file except itself.
- Content index entries include path, SHA-256 hash, size, and role.
- Current archives do not contain signature files.
- Current validation rejects unsafe archive paths, missing manifest/index files, missing entry assemblies, missing icons, hash mismatches, size mismatches, duplicate indexed paths, and unindexed files.

## Install And Update

Runtime install/update uses the same `.sunderpkg` validation path.

Rules:

- Local installs stage and validate the archive before committing installed state.
- Package id mismatches are rejected during update.
- Dependency version ranges are checked before activation.
- Missing required dependencies block activation.
- The runtime keeps local enabled/disabled state separate from Registry catalog state.
- Registry-backed installs and updates download `.sunderpkg` files and then call the local runtime install/update APIs.

## Contracts Packages

A contracts package is a NuGet package, not a Sunder runtime package.

Contracts package rules:

- It has no `sunder-package.json`.
- It contains public extension point declarations and typed contribution contracts.
- It does not reference the host package implementation.
- It does not reference `Sunder.App` or `Sunder.Runtime.Host`.
- It references `Sunder.Sdk` only when it needs SDK extension-point types.

Runtime package dependencies belong in `[assembly: SunderPackageDependency(...)]`. NuGet contracts dependencies belong in normal project package references.
