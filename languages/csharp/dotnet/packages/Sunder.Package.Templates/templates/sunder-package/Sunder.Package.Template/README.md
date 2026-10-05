# SUNDER_PACKAGE_NAME_TEXT

This package is authored in one `Sunder.Package.Template.csproj`. Add services,
ordinary value types and optional UI source; Sunder owns typed APIs, DI/bootstrap,
isolated Worker/App compilation and package composition.

## Source

- `Services/SampleService.cs` owns the annotated greeting and its value types.
- Optional `Views/` source declares `[SunderPackageView]` and injects the generated
  `ISampleService`. No provider discovery, caller scope or JSON mapper is needed.
- Optional `Stacks/` source implements the selected platform extension.
- `PackageMetadata.cs` owns package identity and ordinary package dependencies.

Folders are organizational. The App has generated role-local RPC clients and does
not reference the Worker implementation assembly. All coordinated SDK references
use `SUNDER_TEMPLATE_PACKAGE_VERSION_RANGE`.

## Build, Run And Pack

Set the package version in the project file. Accept the initial owned contract once:

If this package includes Stack support, perform the local platform selection below
before the first Accept command.

```bash
dotnet restore Sunder.Package.Template.csproj
dotnet msbuild Sunder.Package.Template.csproj -t:AcceptSunderContracts -p:SunderContractKey=sample -p:SunderContractNextVersion=2.0.0
dotnet build Sunder.Package.Template.csproj --no-restore
dotnet run --project Sunder.Package.Template.csproj --no-build
dotnet msbuild Sunder.Package.Template.csproj -t:FreezeSunderContracts
dotnet msbuild Sunder.Package.Template.csproj -t:PackSunderPackage -p:Configuration=Release
```

Retain `sunder-contracts.owned.json` and `sunder-contracts.lock.json`. Ordinary builds
remain offline and do not accept, freeze or update selections. Later contract edits
use explicit Accept; breaking changes require the reviewed old/new version pair and
acknowledgement. Freeze is required before release Pack.

Build creates `bin/Debug/net10.0/sunder-dev` for the current RID. Internal outputs are
under `sunder-roles/<role>/<rid>`. Pack builds every declared RID into one universal
archive, with shared descriptors/content and exact role/RID payloads. Workers are
framework-dependent modules; Sunder manages the shared execution runtime.

Run delegates to `sunder dev package run`; install Sunder and put its CLI on PATH,
or set `-p:SunderCliPath=...`. Use `dotnet run --no-build -- --watch` to watch rebuilt
development output. Runtime acquisition remains explicit. No package module is
executed directly outside its Host lifecycle.

## Optional Stack Contributor

With `--withStacks`, select the platform import before first Accept:

```bash
dotnet msbuild Sunder.Package.Template.csproj -t:ImplementSunderPlatformApi -p:SunderContractId=sunder.stack.contributor -p:SunderPlatformCatalogPath=./catalog.json
```

`Stacks/StackSupport.cs` implements domain behavior. Generated composition registers
the provider and wraps the SDK semantic adapter; the exact platform lock supplies
its descriptor and identity. A local Core-exported catalog needs no Registry. To select
a published catalog, use `SunderRegistryOrigin=https://registry.example/` instead.
No catalog signing key or trust bundle is needed.
Exact `RestoreSunderContracts` can hydrate an existing selection without updating it.

## Consume Another Package's API

A fresh project can select imports before first Accept. One command selects the
complete public API from an unpublished local archive:

```bash
dotnet msbuild Sunder.Package.Template.csproj -t:AddSunderPackageApi -p:SunderPackageId=example.weather -p:SunderPackageVersionRange=">=1.0.0 <2.0.0" -p:SunderPackageApiPresence=optional -p:SunderPackageCandidatePath=./weather.sunderpkg
dotnet msbuild Sunder.Package.Template.csproj -t:AcceptSunderContracts -p:SunderContractKey=sample -p:SunderContractNextVersion=2.0.0
dotnet build Sunder.Package.Template.csproj --no-restore
```

The generated package facade exposes typed catalog and selected-provider operations.
Public APIs are selected as a whole; package-private contracts stay private. No
descriptor, hash, contract-version inventory or Protocol reference is authored.
Imported selection does not grant additional caller authority.

Package presence and provider readiness are independent. Add defaults to required
package presence and optional provider readiness. `RequireSunderPackageApi` and
`UnrequireSunderPackageApi` review a contract's readiness override. Discovery includes
authorized providers; selected-package resolution requires one matching active provider.

Use `SunderRegistryOrigin` instead of a candidate for published selection.
`UpdateSunderPackageApi` changes selections explicitly using a new local candidate or the locked origin.
`RestoreSunderContracts` restores exact existing selections offline/from that origin.
`SunderPackageCandidatePath` supports local unpublished development; publication still
independently establishes authority. Content hashes are provenance, not routing pins.

- Package development: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
