# SUNDER_PACKAGE_NAME_TEXT

This package is authored in one `Sunder.Package.Template.csproj`. Add services,
ordinary value types and optional UI source; Sunder owns typed APIs, DI/bootstrap,
isolated Worker/App compilation and package composition.

## Source

- `Services/SampleService.cs` declares `[SunderService]` and ordinary string/Task/stream methods.
- Optional `Views/` source declares `[SunderPackageView]` and injects the generated
  `ISampleService` from the canonical package's flat `_Services` namespace.
- Optional `Stacks/` source implements the selected platform extension.
- `PackageMetadata.cs` owns package identity and ordinary package dependencies.

Folders are organizational. The App has generated role-local RPC clients and does
not reference the Worker implementation assembly. All coordinated SDK references
use `SUNDER_TEMPLATE_PACKAGE_VERSION_RANGE`.

Author each business record once. Owned roles reuse that source; imported records
come from the authority package's generated `_Models` namespace. Sunder generates
transport envelopes, codecs and clients from validated projection metadata.

## Build, Run And Pack

Set the package version in the project file. The initial service is private: its
generated version follows that complete package version automatically. If this
package includes Stack support, select the platform contract below before Build.

```bash
dotnet restore Sunder.Package.Template.csproj
dotnet build Sunder.Package.Template.csproj --no-restore
dotnet run --project Sunder.Package.Template.csproj --no-build
dotnet msbuild Sunder.Package.Template.csproj -t:PackSunderPackage -p:Configuration=Release
```

Retain `sunder-contracts.lock.json`. Private-only source needs no owned baseline,
Accept or Freeze step. Ordinary builds remain offline and never change selections.
Only explicitly public APIs use reviewed Accept/Freeze and immutable public history.

Build creates `bin/Debug/net10.0/sunder-dev` for the current RID. Internal outputs are
under `sunder-roles/<role>/<rid>`. Pack builds every declared RID into one universal
archive, with shared descriptors/content and exact role/RID payloads. Workers are
framework-dependent modules; Sunder manages the shared execution runtime.

Run delegates to `sunder dev package run`; install Sunder and put its CLI on PATH,
or set `-p:SunderCliPath=...`. Use `dotnet run --no-build -- --watch` to watch rebuilt
development output. Runtime acquisition remains explicit. No package module is
executed directly outside its Host lifecycle.

## Optional Stack Contributor

With `--withStacks`, select the platform import before the first Build:

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

A fresh project can select imports before its first Build. One command selects the
complete public API from an unpublished local archive:

```bash
dotnet msbuild Sunder.Package.Template.csproj -t:AddSunderPackageApi -p:SunderPackageId=example.weather -p:SunderPackageVersionRange=">=1.0.0 <2.0.0" -p:SunderPackageApiPresence=optional -p:SunderPackageCandidatePath=./weather.sunderpkg
dotnet build Sunder.Package.Template.csproj --no-restore
```

Inject `Sunder.Generated.Packages.Example_Weather_Services.IPackageApi` to call descriptor-derived
services and import `Sunder.Generated.Packages.Example_Weather_Models` for their values.
These namespaces derive from `example.weather`, not an import alias. The generated
proxy owns discovery, cancellation and stream disposal; construction performs no calls.
For business selection among implementations, inject `IServiceProviders<TService>`
from `Sunder.Sdk.Services`, call `ListAsync`, and use the selected `provider.Service`.
Its `provider.Reference` is a typed reference to that exact activation; entries are
borrowed and need no disposal.
Public APIs are selected as a whole; package-private contracts stay private. No
descriptor, hash, contract-version inventory or Protocol reference is authored.
Imported selection does not grant additional caller authority.

A real implementation may bind several imported contracts by repeating
`SunderServiceProvider`; explicit interface methods resolve name collisions.
Use `SunderContent` for content and dispose it and its read streams normally.
Generated bindings manage registration and scopes. A transient `[JsonIgnore]`
property transfers only when explicitly marked `SunderServiceMember`; it remains
excluded from normal JSON. Include server invocation context only for genuine
authenticated-caller policy. Long operations declare `SunderMethod.TimeoutSeconds`
(up to 2100) on the contract method, subject to Host limits.

Reference availability is strict even for a non-null nullable reference. Mark a
direct nullable reference property with `SunderOptionalServiceReference` only when
safe absence should transfer as null; permission and unsafe failures still propagate.
For narrow imported consumption, a class can declare
`[SunderRuntimeUse(typeof(IExecutionTargetService), Actions = ["discover"])]` using
the selected service's canonical namespace. That requests discovery, not invocation.
Replayable content watches may opt into `CompleteOnResponseContentRetirement = true`
on their provider implementation method; consumers then resnapshot/resume after
authenticated response-authority retirement. Other failures remain failures.
Generated JSON metadata covers the supported model graph without reflection-cache
retention of collectible types. Authors supply no `Unit` or empty-request records.

Package presence and provider readiness are independent. Add defaults to required
package presence, optional provider readiness, and both App/Runtime consumer roles.
Existing narrowed role selections survive Update and Restore. `RequireSunderPackageApi` and
`UnrequireSunderPackageApi` review a contract's readiness override. Discovery includes
authorized providers; selected-package resolution requires one matching active provider.

Use `SunderRegistryOrigin` instead of a candidate for published selection.
`UpdateSunderPackageApi` changes selections explicitly using a new local candidate or the locked origin.
`RestoreSunderContracts` restores exact existing selections offline/from that origin.
`SunderPackageCandidatePath` supports local unpublished development; publication still
independently establishes authority. Content hashes are provenance, not routing pins.

- Ordinary services: https://github.com/Younics/sunder-core/blob/main/docs/package-development/ORDINARY-SERVICES.md
- Package development: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
