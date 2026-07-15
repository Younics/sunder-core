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
- Package view names, settings views, settings schemas, background services, auth handlers, and extension contributions are registered in code, not in the generated manifest.

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
  "hostRoles": ["app", "runtime"],
  "icon": "assets/icon.png",
  "dependsOn": [
    {
      "packageId": "sunder.package.host",
      "versionRange": ">=1.0.0 <2.0.0"
    }
  ],
  "sdkApiVersion": 1,
  "sdkPackageVersion": "1.1.0",
  "requiredSdkCapabilities": [
    "sdk-baseline-1-1.v1",
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
- `hostRoles`: exact compiled Host roles: `app`, `runtime`, both in canonical order, or the single value `contract-only`.
- `sdkApiVersion`: SDK activation generation, exactly `1` for a V1 manifest.
- `sdkPackageVersion`: strict SemVer 2.0 version read from the actual resolved `Sunder.Sdk` reference. A conflicting `SunderSdkPackageVersion` override fails the build.
- `requiredSdkCapabilities`: non-empty, distinct V1-form capability ids.

Additional generated fields:

- `summary`: package description, omitted when not declared.
- `icon`: package icon asset path, omitted when not declared.
- `dependsOn`: runtime package dependency list, omitted when no dependencies are declared.
- `hostRoles`: inferred from public module interfaces in compiled entry-assembly metadata. Package authors do not declare it manually.
- `requiredSdkCapabilities`: Host-required SDK capabilities inferred by `Sunder.Package.Build`. Current build tooling always seeds `sdk-baseline-1-1.v1`, `core.v1`, `packaging.v1`, and `contributions.v1`.
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

The generated manifest records the entry assembly and its inferred Host roles, not the module type. Archive and activation validation read ECMA-335 metadata directly and reject a role declaration that does not match the entry assembly before any reflection load occurs.

Contract-only packages declare no App or Runtime module. They can carry shared contracts used by dependent packages without creating a package module load context of their own.

Host discovery rules:

- The package entry assembly is loaded from package `lib` output.
- Runtime finds at most one public, non-abstract `ISunderRuntimePackageModule`; App finds at most one `ISunderAppPackageModule`.
- A single module class may implement both roles, but each host invokes only its own role.
- The module type must have a public parameterless constructor.
- A missing role is valid; multiple implementations of the same role fail activation in that host.
- Each host constructs its role module directly, configures services, builds an isolated role-specific provider, and then registers contributions. The module itself is not composed from that provider.

Current module API:

```csharp
public interface ISunderRuntimePackageModule
{
    void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context);

    void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services);
}
```

## Contributions

Runtime contributions are registered through `ISunderRuntimeContributionRegistry`; App extensions use `ISunderAppContributionRegistry`. `Sunder.Sdk.Avalonia` adds `IAvaloniaPackageContributionRegistry` and control/view/settings registration extensions.

Runtime registry capabilities:

- `RegisterBackgroundService<TService>()`
- `RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)`
- `RegisterSettingsSchema(PackageSettingsSchema schema)`

App registry capabilities:

- `RegisterPackageView<TView>(PackageViewRegistration registration)`
- `RegisterSettingsView<TView>()`
- `RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)`

`IPackageExtensionCatalog` lets packages discover active extension contributions. Hosts that support live package activation also implement `IPackageExtensionCatalogMonitor`; its `Changed` event includes a revision, lifecycle reason, and per-extension-point additions/removals.

The activating package owns every registration it contributes. Extension-point ids and contract assemblies identify the contract but do not transfer ownership to the host package; `GetExtensionContributions` reports the canonical id of the contributing package.

Callback and auth flows are modeled separately. `IPackageCallbackHandler` is the generic callback-session contract. `IPackageAuthHandler` is the auth-specific status/disconnect contract and requires both `auth.v1` and `callbacks.v1`.

Settings schema identity is host-owned. `PackageSettingsSchema` contains only summary, sections, and fields; the activating manifest supplies package id and display name. Construction rejects duplicate ids/keys, invalid select/boolean defaults, duplicate select values, and all secret defaults before registration.

View registrations use globally unique stable ids, conventionally prefixed with the package id, and user-facing view names. The App constructs registered controls from the package App service provider, so controls may use constructor injection:

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
- Icon files must be 1 MiB or smaller, carry a recognized raster signature matching the file extension, and use dimensions no larger than 8192 by 8192 pixels.
- The build maps source `Assets/**` into output `assets/**`.
- The runtime serves active package assets through authenticated `/api/v1/packages/{packageId}/assets/{assetPath}` endpoints.
- The app loads validated raster package icons directly and falls back to the first character of the package name.
- Icon load failures are written to `AppSessionLog`; they are not shown as package UI errors.

Supported image content types in runtime/registry paths:

- BMP
- GIF
- ICO
- JPG/JPEG
- PNG
- WebP

SVG and SVGZ package icons are rejected. The current renderer does not provide a sanitizer that can reliably prohibit scripts, external resources, and unsafe compressed expansion, so accepting untrusted SVG is not part of the V1 format policy.

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

- `Sunder.Package.Build` normalizes `SunderDevOutputPath` with a trailing separator and only permits the direct `TargetDir/sunder-dev` child. Existing directories are deleted only when they contain the `.sunder-generated-output` marker written by the target. Filesystem, project, target, output, `Assets`, `src`, unrelated, nested, symlink, and unmarked roots are never removed.
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
- Content index entries include path, SHA-256 hash, size, and a canonical role: `manifest`, `assembly`, `native`, `asset`, or `file` according to path.
- Writers emit files in ordinal path order with fixed ZIP timestamps so identical inputs produce identical archives.
- Current archives do not contain signature files.
- Relative archive paths use `/`, visible ASCII, non-empty segments, and no `.` or `..` segments. Rooted, drive, UNC, backslash, control/NUL, Windows-reserved, trailing-dot/space, and platform-dependent character forms are rejected.
- Only the exact roots `manifest/`, `payload/lib/`, and `payload/assets/` are accepted. The content index excludes only the exact canonical `manifest/content-index.json`; a payload file also named `content-index.json` is indexed normally.
- Metadata uses a closed, case-sensitive JSON schema; unknown or wrong-case members fail parsing. Validation rejects null collection entries, duplicate normalized paths, case collisions, directory/file collisions, ZIP symbolic-link/reparse entries, links in extracted trees, missing manifest/index files, missing entry assemblies, invalid icons, malformed or non-lowercase SHA-256 values, negative or mismatched sizes, incorrect index roles, files outside canonical roots, and unindexed files.
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

## Stack Archive Validation

Stack archives use the same portable path and extraction limits. Files are restricted to `manifest/sunder-stack.json`, `manifest/content-index.json`, and `payload/fragments/`, `payload/files/`, or `payload/media/`. Fragment ids and payload-relative paths are validated before exporter streams are opened or any disk materialization occurs. Fragment payloads must be strict JSON objects no larger than 4 MiB. Export payload streams are bounded to 64 MiB per file and 256 MiB total. Media is limited to PNG, JPEG, WebP, or GIF, 10 MiB per file, and 8192 by 8192 pixels; declared content type and both path/file-name extensions must match the detected signature, and image inspection stops after a size failure.

`features` are optional hints. Unknown optional feature ids produce warnings and may be ignored. `requiredFeatures` are reader requirements; malformed, duplicate, or unsupported required ids fail validation. Current reader features are `fragment-json.v1`, `media.v1`, and `required-inputs.v1`.

Stack validation scans UTF-8 text files up to 8 MiB for known provider tokens, private keys, sensitive JSON values, and environment-style secret assignments. This is defense in depth, not proof that an archive contains no secret: binary files, unknown token formats, encoded/encrypted values, and larger non-fragment text may not be classified. Contributors must still omit secrets or replace them with required import inputs.

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
