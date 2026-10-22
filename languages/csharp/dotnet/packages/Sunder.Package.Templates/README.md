# Sunder.Package.Templates

> **Release/source channel:** The NuGet README describes that published template version. The repository copy may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

`dotnet new sunder-package` creates one authored .NET project with ordinary services
and optional Avalonia views. Sunder generates typed APIs, composition and isolated
Worker/App outputs. Node/React presets use `npm create sunder-package@latest` and the
same package/runtime model.

## Install And Create

```powershell
dotnet new install Sunder.Package.Templates
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
dotnet new sunder-package --name MyUiPackage --packageId my.company.ui --packageName "My UI Package" --withAvalonia
```

Use `--output` to select an empty destination.

| Option | Meaning |
| --- | --- |
| `--packageId <id>` | Required lowercase dot-separated package identity. |
| `--packageName <name>` | Required user-facing name. |
| `--withAvalonia` | Adds Avalonia references and a starter package view. |
| `--noDefaultView` | With Avalonia, retains SDK references but omits the starter view. No empty App role is emitted until UI source is added. |
| `--withStacks` | Adds the domain Stack contributor; requires explicit local or Registry platform-contract selection. |
| `--withPackageDependency` | Adds an installed-package dependency, independently of API selection. |
| `--dependencyPackageId <id>` | Required with `--withPackageDependency`. |
| `--dependencyVersionRange <range>` | Required reviewed dependency range; there is no SDK-derived default. |

## Generated Shape

```text
MyPackage/
  MyPackage.csproj
  PackageMetadata.cs
  sunder-contracts.lock.json
  Assets/icon.png
  Services/SampleService.cs
  Views/                         # optional Avalonia source
    Avalonia.PackageReferences.props
    DefaultPackageView.axaml
    DefaultPackageView.axaml.cs
    DefaultPackageViewModel.cs
  Stacks/                        # optional Stack source/references
```

The annotated service generates `ISampleService`, which the view model receives
through its constructor. For `my.company.package`, it lives in
`Sunder.Generated.Packages.My_Company_Package_Services`. Author business records once;
owned roles reuse them and importers receive generated records in the authority
package's `_Models` namespace. `[SunderPackageView]` supplies view metadata. Default DI,
Worker entry, App module, provider registration and RPC lifetime are generated.
Source folders do not define role boundaries, and authors maintain no App/Protocol
project, manual descriptor, client scope or default bootstrap.

Stable templates reference the coordinated SDK minor range. Prerelease templates
pin their exact coordinated SDK release. The package's own version is `1.0.0` in
its project file. The starter service is private, so its descriptor version follows
the complete package version. Explicitly public APIs have reviewed contract versions.

## Build, Run And Package

With `--withStacks`, select the platform import described below before the first
Build. Local selection requires no Registry.

```powershell
dotnet restore .\MyPackage\MyPackage.csproj
dotnet build .\MyPackage\MyPackage.csproj --no-restore
dotnet run --project .\MyPackage\MyPackage.csproj --no-build
dotnet msbuild .\MyPackage\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
```

Retain the contract lock. Private-only source needs no owned baseline or Accept/Freeze
step. Explicitly public contracts use reviewed acceptance and immutable history.
Normal Build/Rebuild are offline and never change selections automatically.

Build composes the current RID at `bin/Debug/net10.0/sunder-dev`. Internal role outputs
are under `sunder-roles/<role>/<rid>`; Pack builds all declared RIDs and emits one
validated `.sunderpkg`. Runtime payloads carry managed modules and package-owned
dependencies, not apphosts or embedded .NET runtimes. First-restore flags avoid
downloading SDK runtime/apphost packs that Sunder's managed launch does not use.

`dotnet run` delegates to `sunder dev package run`. The Sunder CLI must be on PATH
or selected with `-p:SunderCliPath=...`. Forward `-- --watch` to reload changed
development output. Runtime acquisition is explicit through Sunder; a missing
managed runtime is not replaced with a direct Worker launch or hidden download.
Qualified prebuilt packing consumes exact retained role-input evidence without
rebuilding; ordinary Pack builds current source. See the
[prebuilt contract](https://github.com/Younics/sunder-core/blob/main/docs/package-development/PREBUILT-ROLE-INPUTS.md).

## Optional Stack Contributor

Select `sunder.stack.contributor` with `ImplementSunderPlatformApi` before first Build.
Use `SunderPlatformCatalogPath` for a local Core catalog, or `SunderRegistryOrigin` for
a published catalog. Build validates the private service and imported contributor
together. Generated composition registers the exact selected adapter automatically.

Local creation and development require no Registry. Published selection uses the
explicitly chosen Registry's administrator-published catalog.
Missing selection reports `platform-api.import-not-found`; unsupported SDK/catalog
version lines report `platform-api.catalog-version-unsupported`. No catalog signing
key or trust bundle is needed. The legacy embedded SDK descriptor does not create a
Registry selection.

See the generated README and the package-development documentation for explicit
public package API selection, optional presence/readiness, updates and offline restore.
Selected C# packages expose `IPackageApi` in their canonical flat `_Services`
namespace. Use `IServiceProviders<TService>` for multiple implementations and
`provider.Service` / `provider.Reference` for calls and typed references. Content
uses `SunderContent` with normal async disposal. Generated bindings own transport;
type availability does not infer consumer grants.
For narrow imported use, `SunderRuntimeUse(typeof(...), Actions = ["discover"])`
declares only discovery against an already selected canonical service type.
`SunderOptionalServiceReference` explicitly opts a direct nullable reference member
into safe-absence-as-null behavior; the default remains strict. Replayable content
watches can opt into provider-local `CompleteOnResponseContentRetirement`.
The ordinary-service guide covers the failure boundaries and transient
`JsonIgnore` / `SunderServiceMember` distinction. Compiled JSON metadata and automatic
empty-request/completion handling require no authored resolver or `Unit` type.

- Ordinary services: https://github.com/Younics/sunder-core/blob/main/docs/package-development/ORDINARY-SERVICES.md
- Package development: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- SDK compatibility: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-SDK-COMPATIBILITY.md
