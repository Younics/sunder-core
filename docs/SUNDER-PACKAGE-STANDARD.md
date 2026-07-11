# Sunder Package Standard

This document describes the current Sunder package standard implemented by `Sunder.Sdk`, optional SDK contract packages such as `Sunder.Sdk.Avalonia` and `Sunder.Sdk.Stacks`, `Sunder.Package.Build`, `Sunder.Package.Format`, `Sunder.Runtime.Host`, and the Registry.

See [Sunder SDK Compatibility](SUNDER-SDK-COMPATIBILITY.md) for Host/SDK/package versioning rules.

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
- Use strict SemVer 2.0 package versions such as `1.0.0`, `1.2.0-beta.1`, or `1.2.0+build.7`.

## Versions And Ranges

Package versions and `sdkPackageVersion` use strict SemVer 2.0. Core numbers and numeric prerelease identifiers cannot contain leading zeroes. Prerelease precedence follows SemVer identifier rules; build metadata is preserved in formatting and value identity but does not affect precedence.

V1 dependency ranges intentionally support only:

- one exact version, such as `1.2.3`;
- one comparison using `<`, `<=`, `>`, `>=`, or `=`, such as `>=1.2.3`;
- space-conjoined comparisons, such as `>=1.2.3 <2.0.0`.

Wildcards, comma conjunctions, hyphen ranges, caret/tilde ranges, unions, and whitespace between an operator and version are rejected. Build tooling formats accepted ranges with one ASCII space between comparisons.

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
  "sdkApiVersion": 1,
  "sdkPackageVersion": "1.0.0",
  "requiredSdkCapabilities": [
    "core.v1",
    "packaging.v1",
    "contributions.v1",
    "views.v1"
  ],
  "targetFramework": "net10.0"
}
```

Required fields:

- `manifestVersion`: manifest format version, currently `1`.
- `id`: stable package id.
- `name`: user-facing package name.
- `version`: strict SemVer 2.0 package version.
- `entryAssembly`: package entry assembly file name.
- `sdkApiVersion`: SDK activation generation, exactly `1` for a V1 manifest.
- `sdkPackageVersion`: strict SemVer 2.0 version of the referenced SDK package/build.
- `requiredSdkCapabilities`: non-empty, distinct V1-form capability ids.

Additional generated fields:

- `summary`: package description, omitted when not declared.
- `icon`: package icon asset path, omitted when not declared.
- `dependsOn`: runtime package dependency list, omitted when no dependencies are declared.
- `requiredSdkCapabilities`: Host-required SDK capabilities inferred by `Sunder.Package.Build`. Current build tooling always seeds `core.v1`, `packaging.v1`, and `contributions.v1`.
- `sdkVersion`: SDK version metadata when supplied by build properties.
- `targetFramework`: package target framework, emitted when available.

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

Host discovery rules:

- The package entry assembly is loaded from package `lib` output.
- Runtime finds at most one public, non-abstract `ISunderRuntimePackageModule`; App finds at most one `ISunderAppPackageModule`.
- A single module class may implement both roles, but each host invokes only its own role.
- The module type must have a public parameterless constructor.
- A missing role is valid; multiple implementations of the same role fail activation in that host.

Current module API:

```csharp
public interface ISunderRuntimePackageModule
{
    void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context);

    void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services);
}
```

## Contributions

Runtime contributions are registered through `ISunderRuntimeContributionRegistry`; App extensions use `ISunderAppContributionRegistry`. `Sunder.Sdk.Avalonia` adds `IAvaloniaPackageContributionRegistry` and Control/workspace/view/settings registration extensions.

Runtime registry capabilities:

- `RegisterBackgroundService<TService>()`
- `RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)`
- `RegisterConfigurationSchema(PackageConfigurationSchema schema)`

App registry capabilities:

- `RegisterPackageView<TView>(PackageViewRegistration registration)`
- `RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration)`
- `RegisterSettingsView<TView>()`
- `RegisterSettingsViewFactory<TFactory>()`
- `RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)`

`IPackageExtensionCatalog` lets packages discover active extension contributions. Hosts that support live package activation also implement `IPackageExtensionCatalogMonitor`; its `Changed` event includes a revision, lifecycle reason, and per-extension-point additions/removals.

Callback and auth flows are modeled separately. `IPackageCallbackHandler` is the generic callback-session contract. `IPackageAuthHandler` is the auth-specific status/disconnect contract.

View registrations use stable view ids and user-facing view names:

```csharp
registry.RegisterPackageView<DefaultPackageView>(new PackageViewRegistration(
    "sunder.package.example.default",
    "Example"));
```

## Package Icons

Package icon paths are relative package asset paths. The template uses `assets/icon.png`, backed by source file `Assets/icon.png`.

Rules:

- Icon paths must use the portable archive path grammar described below.
- Icon files must exist at build time when `Icon` is declared.
- The build maps source `Assets/**` into output `assets/**`.
- The runtime serves active package assets through authenticated `/api/v1/packages/{packageId}/assets/{assetPath}` endpoints.
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
    MyPackage.runtimeconfig.json
  assets/
    icon.png
```

Build behavior:

- `Sunder.Package.Build` removes the previous dev output before emitting a new one.
- Package assemblies and private dependencies are copied to `lib`.
- Package `.deps.json`, `.runtimeconfig.json`, and `.pdb` files are copied to `lib` when present.
- Native runtime assets under build output `runtimes` are copied under `lib/runtimes`.
- Source files under `Assets` are copied to `assets`.
- Host boundary assemblies such as `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, `Sunder.Sdk.Stacks`, and core Avalonia assemblies are excluded from private package output.

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
- Writers emit files in ordinal path order with fixed ZIP timestamps so identical inputs produce identical archives.
- Current archives do not contain signature files.
- Relative archive paths use `/`, visible ASCII, non-empty segments, and no `.` or `..` segments. Rooted, drive, UNC, backslash, control/NUL, Windows-reserved, trailing-dot/space, and platform-dependent character forms are rejected.
- Validation rejects duplicate normalized paths, case collisions, directory/file collisions, ZIP symbolic-link/reparse entries, links in extracted trees, missing manifest/index files, missing entry assemblies, missing icons, malformed or non-lowercase SHA-256 values, negative or mismatched sizes, and unindexed files.
- Package and Stack extraction is streamed to a temporary sibling directory and atomically published only after extraction completes. Owning validation/install/publish callers remove completed staging directories when validation or later work fails.

Default package and Stack extraction limits:

| Limit | Default |
| --- | ---: |
| ZIP entries | 4,096 |
| Uncompressed bytes per entry | 256 MiB |
| Total uncompressed bytes | 1 GiB |
| Compression ratio per entry | 200:1 |
| Relative path length | 240 characters |
| Relative path depth | 32 segments |
| Each manifest/content-index JSON document | 1 MiB |

All extraction operations honor cancellation before and during streamed reads/writes.

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
