# Sunder.Package.Build

> **Release/source channel:** The NuGet README describes that published build package. The repository copy tracks current source and may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

`Sunder.Package.Build` contains the MSBuild targets and tasks that turn a Sunder package project into local development output and distributable `.sunderpkg` archives.

The ordinary C# entry point is `[SunderService]` in `Sunder.Sdk.Services`: declared public async
methods, generated interface injection, package-version private contracts and
role-reviewed imported APIs. See the [ordinary-service authoring and migration
contract](https://github.com/Younics/sunder-core/blob/main/docs/package-development/ORDINARY-SERVICES.md).
RPC-prefixed declaration aliases are retired. Advanced descriptor/runtime APIs and
the historical readers below do not provide a second source-authoring mode.

Reference this tooling from the canonical package project with `Sunder.Sdk.Worker`
and, for UI, `Sunder.Sdk.Avalonia`. A single source-authored `SunderPackageProject=true`
library generates isolated Worker/App outputs, composition and one development tree.

## Install

```powershell
dotnet add package Sunder.Package.Build --private-assets all
```

Typical package project reference:

```xml
<ItemGroup>
  <PackageReference Include="Sunder.Sdk.Worker" Version="[1.1.0,1.2.0)" />
  <PackageReference Include="Sunder.Package.Build" Version="[1.1.0,1.2.0)" PrivateAssets="all" />
</ItemGroup>
```

Use `PrivateAssets="all"` because this package is build tooling for the package project. It should not flow as a runtime dependency of your package.
The NuGet package exposes no compile or runtime assembly asset; its task assembly and private dependencies live only under `tasks/net10.0` and are loaded by `buildTransitive/Sunder.Package.Build.targets`.

## Source Task Loading

Mutable task sources use one shared MSBuild request on `Sunder.Package.Build.targets`
to build the producer and publish a complete content-addressed immutable snapshot.
One source lease covers `Build` through snapshot publication, closing the old
`Clean`/build-to-stage gap; consumers stage only from snapshots, never live producer outputs.
`IBuildEngine6` global-property enumeration strips consumer globals while preserving
explicit canonical inputs. Owner compiler globals such as `DefineConstants` require
opt-in by name through `SunderPackageBuildTaskSourceGlobalProperties`.
Lock contention uses short MSBuild dependency requests with fresh identities, not
long `Yield` waits that can starve the holder's nested build.
Publication reuses `_SunderStagePackageBuildTaskSet` markers, atomic copy, and validation.
Snapshots remain under the owner's `obj/sunder-package-build-task-sources`;
no automatic cache garbage collection was added.
See [task-loading details and regression coverage](https://github.com/Younics/sunder-core/blob/main/docs/package-development/IMPLEMENTATION-FIRST-RPC.md#task-loading-reliability).

## What It Does

`Sunder.Package.Build` adds build targets that:

- Generate an archive-format V1, Manifest V1 package with exact role/RID targets from SDK metadata, MSBuild declarations, and build output.
- Emit an unpacked, content-indexed, consumer-valid `sunder-dev` archive tree after `dotnet build`.
- Create a `.sunderpkg` archive after `dotnet publish`.
- Provide an explicit `PackSunderPackage` MSBuild target.
- Copy App managed output or a Runtime worker's thin framework-dependent module closure into canonical payload layers, with source assets under `payload/shared/assets`.
- Expose a post-publish-file-computation, pre-index signing extension over a private copy of C# worker binaries.
- Validate descriptorVersion 1 RPC contracts, generate C# bindings and immutable canonical snapshots, derive descriptor and ABI hashes, and copy descriptors into `payload/shared/contracts`.
- Project only matching `runtimes/<rid>` content into each declared exact role/RID layer.
- Exclude host boundary assemblies such as `Sunder.Sdk` and core Avalonia assemblies from App output; Runtime Workers instead retain the package dependencies declared by their `.deps.json` graph.
- Exclude PDB/MDB symbols and generated DLL-matched XML documentation companions from Runtime Worker closures while retaining explicitly authored XML content.

Package authors do not maintain `sunder-package.json` by hand.

## Expected Project Shape

A new source-authored family has one .NET 10 library project with
`SunderPackageProject=true`, annotated services and optional `[SunderPackageView]`
source. Ordinary Build generates separate implementation assemblies below
`bin/<configuration>/<tfm>/sunder-roles/worker/<rid>` and `sunder-roles/app/<rid>`.
The App receives role-local generated clients. No combined implementation DLL is emitted.

Ordinary generated APIs live in package-specific namespaces: package ID
`sunder.package.agent` produces `Sunder.Generated.Packages.Sunder_Package_Agent_Services`.
Canonical ID segments are initial-capitalized and joined with underscores; an ID
starting with a digit receives a leading underscore. Own and imported interface names
derive from the service ID: `embedding-provider` exposes `IEmbeddingProviderService`.
Import the generated namespace explicitly. SDK authoring attributes remain in `Sunder.Sdk.Services`.
Selected packages expose `IPackageApi` in their own generated Services namespace,
independent of the lock's local alias. Internal clients use the corresponding
`_Internal` namespace. Owned roles use authored model types. Imports expose one
model per verified authority/source identity in the authority's flat `_Models`
namespace, shared across contracts and reexports. Equal schemas alone do not merge
distinct source models. Necessary generated request envelopes use a contract-prefixed
name in the same Models namespace.

Inject `Sunder.Sdk.Services.IServiceProviders<TService>` for explicit provider
selection. `ListAsync` returns entries with a ready-to-call `Service`; `WatchAsync`
retains both catalog revision and sequence cursors. DI owns the catalog and its
proxies. Construction performs no discovery or calls, and selected entries stay
bound to their exact provider activation without replacement or replay. Catalog
injection requires the same reviewed role-specific consumer use as the service;
implementing an imported provider does not grant consumption.

Declaration-only services receive the same source-shaped generated interfaces as
concrete services. For example, `EmbeddingProviderService` generates
`IEmbeddingProviderService`. Imported natural interfaces belong to the contract's
authority package namespace and are shared by selected-package clients, dynamic
catalogs and provider implementations. Colliding service names from distinct
contracts within the same authority fail with a generated-name diagnostic.
They omit transport context and empty
request/completion values. Nonempty requests keep their descriptor-derived type;
the generator does not guess a producer's original multiargument signature.
Provider registration and the adaptation to the wire handler are generated.

Use `SunderContent` for content values, including nested model members and streamed
results. Generated visitors register and decode them through the existing invocation
or call scope. Dispose received content when finished; retained caller content stays
bounded by cancellation, the Host deadline and the receiver's DI lifetime.
`ServiceProvider<T>.Reference` supplies a `ServiceReference<T>` for an existing
manifest-declared provider. Its decoded `Service` uses the receiver's reviewed grants
and exact activation, including session generation. A reference adds no access grant
and does not export arbitrary local objects or callbacks.

An imported provider binds with `[SunderServiceProvider(typeof(ISelectedService))]`.
Its public async methods use the exact generated method names and value types;
each may independently accept the real `SunderRpcInvocationContext` before or
after the request, and an optional final `CancellationToken`. Context-free methods
need neither parameter. The class need not implement the C# interface when these
out-of-band parameters differ from the consumer signature. Normal context-free
interface implementations, including explicit implementations, remain supported.
Sunder generates a provider-local adapter that passes the original Host context;
it never constructs a replacement context or maps to a different wire operation.
Ambiguous overloads, mismatched values/nullability, duplicate contexts and authored
method-ID overrides fail against the locked descriptor. Source implementations may
use `Task` or `ValueTask` for the same unary result; streams remain `IAsyncEnumerable<T>`.

Existing explicitly composed families use a canonical executable Worker project with
`SunderPackagePrimaryProject=true` and an optional `SunderPackageAppProject`. That App
is a build-only input; the outer-only project-reference example below restores it
without making it a Worker compile input.

Associated Apps receive required uses of package-private services by default.
Concrete public services default to optional owning-App use; declaration-only public
interfaces require explicit `[SunderAppUse]`. The attribute defaults to optional
use and all method-applicable actions. For example,
`[SunderAppUse(Actions = ["discover", "invoke"])]` overrides the default with a
narrowed optional use, while `Required = true` adds provider readiness. Explicit
actions must be nonempty, distinct and descriptor-applicable. The export and
role-isolated App receipt preserve this policy without changing wire/ABI identity
or granting Worker uses. Pack checks the exact policy against fresh source,
including when every App use was removed from a prebuilt leaf.

Owned Runtime consumption is explicit: annotate the owning service with
`[SunderRuntimeUse]`, optionally setting `Required = true` and a nonempty subset
of applicable `Actions`. Without it, even a service with a local provider grants
no Runtime use. Runtime policy participates in neutral/exact-RID export checks
and Worker receipt/manifest generation; App receipts omit it. Pack rechecks owned
Runtime uses alongside lock-derived uses against current source. Policy changes
preserve descriptor/ABI identity and do not require a new frozen wire snapshot.

An imported provider can declare a narrower Runtime use of a canonical service
already present in its pinned materialization closure:

```csharp
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

[SunderServiceProvider(typeof(AgentServices.IToolSourceService))]
[SunderRuntimeUse(typeof(AgentServices.IExecutionTargetService), Actions = ["discover"])]
public sealed partial class ToolSourceProvider : AgentServices.IToolSourceService
{
    // Implement the selected provider's ordinary service methods.
}
```

The typed attribute binds only the validated generated imported service symbol;
authored namespace lookalikes, foreign metadata and unselected/private targets fail.
Repeat it for distinct targets. Conflicting policies for the same contract fail.
`Required` still defaults to `false`, and explicit actions are a nonempty applicable
subset. `discover` enables catalog/reference binding, not invocation or subscription.
The compiler retains exact source intent in export/receipt `roleUses`, includes it
in the input fingerprint, and emits the normal target bundle, `usesContracts` and
catalog composition. This does not add a package API selection, change provider
identity or dependency presence, or grant App use. Legacy provider-only Update
checks the typed intent against the proposed pinned closure before committing.

Generated concrete providers expose both the ordinary instance registration factory
and static `DeclareSunderRpcProvider(ISunderRpcServiceHandler)`. The latter advertises
generated identity and immutable declared errors while forwarding to a caller-owned
activation gate, without constructing the provider. This supports workers that must
commit startup before resolving persistent services. The gate validates the later
instance registration's complete identity/error tuple and owns waiting, cancellation
and retirement. Owned, imported and semantic platform bindings share this path.

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">$(NETCoreSdkRuntimeIdentifier)</RuntimeIdentifier>
  <RuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64</RuntimeIdentifiers>
  <SunderPackagePrimaryProject>true</SunderPackagePrimaryProject>
  <SunderPackageAppProject Condition="Exists('MyPackage.App/MyPackage.App.csproj')">MyPackage.App/MyPackage.App.csproj</SunderPackageAppProject>
</PropertyGroup>

<ItemGroup>
  <ProjectReference Include="$(SunderPackageAppProject)"
                    Condition="'$(SunderPackageAppProject)' != '' and '$(SunderPackageInnerBuild)' != 'true'"
                    ReferenceOutputAssembly="false"
                    Private="false"
                    BuildReference="false"
                    PrivateAssets="all" />
</ItemGroup>
```

The package's transitive build props establish the fixed Worker profile before .NET SDK publish defaults are evaluated: `SelfContained=false`, `UseAppHost=false`, `PublishSingleFile=false`, and `PublishTrimmed=false`. Package projects do not author runtime-profile overrides; incompatible values are rejected during manifest generation.

Generated manifests and their contract copies use the final intermediate output
directory, including the target framework and RID selected by the .NET SDK. This
default is resolved when manifest generation runs, so direct source imports work
even when they appear before the SDK's targets. Explicit `SunderGeneratedPackageManifest`
overrides are honored. Ordinary Build/Rebuild keeps generated files in build outputs
and preserves authoring source, accepted baselines and locks.

Project-neutral RPC target results use immutable, hash-addressed export snapshots
under `obj/<configuration>/<tfm>/sunder-rpc-exports`. This keeps a returned export
valid when root and App builds overlap or another evaluation cleans/regenerates
the mutable neutral tree. Readers still verify the exact returned hash and owned
marker. Ordinary Clean preserves these handoff snapshots; they are not package
payload or compiler inputs.

The export's global intermediate path and role flags are removed from project
references. Source SDK/helper projects retain their own compiler and file-write
tracking directories during concurrent root/App builds.

Build still inspects `ISunderAppPackageModule` metadata and infers exact `avalonia` App targets. It never infers a Runtime target from `ISunderRuntimePackageModule`; every Runtime leaf must explicitly declare one exact-RID `worker` target. A project with no App module or explicit worker emits zero targets and is valid only when it declares at least one contract bundle. Ordinary non-primary leaf projects and App-only packages retain their existing build and pack behavior.

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Summary = "Adds a custom Sunder workspace.",
    Icon = "assets/icon.png")]
```

```csharp
using Sunder.Sdk.Worker;

await SunderWorker.RunAsync(_ => new SunderWorkerOptions([]));
```

## Build Development Output

### Reference A Generated Role From Implementation Tests

An ordinary .NET test/tool project can explicitly reference the canonical package's
generated role with the standard `ProjectReference` target and global-property metadata:

```xml
<ItemGroup>
  <ProjectReference Include="../MyPackage/MyPackage.csproj"
                    Targets="GetSunderPackageRoleReference"
                    AdditionalProperties="SunderPackageReferenceRole=worker"
                    Aliases="Worker" />
  <ProjectReference Include="../MyPackage/MyPackage.csproj"
                    Targets="GetSunderPackageRoleReference"
                    AdditionalProperties="SunderPackageReferenceRole=app"
                    Aliases="App" />
</ItemGroup>
```

Use `extern alias Worker;` / `extern alias App;` when testing both role-local type
identities. The explicit target builds the canonical package and returns only the
selected role DLL for the evaluated RID (the native SDK RID when omitted).
`BuildProjectReferences=false` resolves that already-built role through `GetTargetPath`
without rebuilding it. Missing role outputs and values other than `worker`/`app` fail
with `package-authoring.reference-role-unavailable` / `reference-role-invalid`.
Package-to-package RPC consumers use contract selection and generated facades.

Direct solution builds and Worker/App references rendezvous on the same effective
configuration, platform, framework and RID before acquiring a package-operation
lease. Reference-role selection does not become a separate package build identity.
Clean uses the same normalization so a later reference cleanup cannot erase an
already rebuilt role behind a cached Build result. Other caller build properties
remain intact. Internal role cleanup leaves source-reference cleanup to the authored
project's ordinary Clean, including when foreign RIDs are declared.

Contended operation leases wait through short MSBuild dependency requests, releasing
the scheduler node needed by the holder's nested builds. This coordination runs in
the main MSBuild process; compiler tasks and RPC-plan lease validation remain in the
isolated task host for mutable tooling. Publication locks, ownership checks and
timeouts are unchanged.

### Confirm A Published Frozen Archive

After Registry accepts the exact retained `.sunderpkg`, run:

```powershell
dotnet msbuild Package.csproj -t:ImportSunderContractPublication `
  -p:SunderPackagePublicationPath=/releases/package.sunderpkg `
  -p:SunderRegistryOrigin=https://registry.example/
```

This explicit online target verifies canonical archive bytes, exact Registry
publication/content identity, complete public inventory and owned public/private
tuples before atomically enriching matching frozen baseline snapshots. It performs
no upload, Accept, Freeze or source build; locks and descriptor/ABI identities stay
unchanged. Exact repeats preserve mtimes and failures preserve the prior baseline.
Use `SunderOwnedContractsPath` only for an existing baseline below the source root.
Development localhost needs the explicit `SunderAllowDevelopmentRegistryOrigin=true`
option. See [publication evidence](../../../../../docs/package-development/IMPLEMENTATION-FIRST-RPC.md#import-verified-publication-evidence)
for history, initially-Yanked releases and evidence-only release commits.

### Select And Restore External Package APIs

Explicit commands own `sunder-contracts.lock.json`. Run them on the canonical
source project with a valid contract lock. Fresh projects may select imports before
first owned Accept. This initial dependency-configuration phase validates exact
inventory/hash/cache evidence, generated import names and stable source inputs,
while permitting complete source to reference several imports not selected yet.
Only the lock is committed. Select every import, then run `AcceptSunderContracts`
to compile and validate the complete graph before first generation. Existing
accepted projects retain full source/compiler checks on selection. Prior generated
receipt/export/marker evidence requires restoring the original baseline, including
outputs from another configuration or RID.

```powershell
dotnet msbuild Package.csproj -t:AddSunderPackageApi `
  -p:SunderPackageId=example.dependency `
  -p:SunderPackageVersionRange=">=1.0.0 <2.0.0" `
  -p:SunderPackageCandidatePath=./dependency.sunderpkg

dotnet msbuild Package.csproj -t:AcceptSunderContracts
dotnet build Package.csproj --no-restore
```

For published selection, use `SunderRegistryOrigin=https://registry.example/`.
Registry Add selects the highest non-yanked stable version in the reviewed range, retains
the complete public inventory, restores package-authority descriptors and creates
the consumer facade. Package presence defaults to `required`; provider readiness
defaults to optional for each contract. `SunderPackageApiPresence=optional` changes
package presence independently of required-provider overrides. Selection compares
SemVer precedence, with descending ordinal complete-version text breaking equal
precedence ties. An explicit `SunderPackageIncludePrerelease=true` enables
prereleases. Local candidates require no Registry and omit `registryOrigin` in the
lock; exact inventories, hashes and reviewed ranges remain mandatory. Present null
or malformed origins are invalid. Local updates require an explicit candidate. A
targeted update may explicitly bind a local selection to a Registry; an existing
Registry origin cannot be overridden. Restore never rewrites selections.

| Target | Selection properties |
| --- | --- |
| `AddSunderPackageApi` | `SunderPackageId`, reviewed range, local candidate or Registry origin; optional presence/prerelease/local name |
| `UpdateSunderPackageApi` | Optional `SunderPackageId`; targeted omission retains range, prerelease and presence; global update rejects overrides |
| `RemoveSunderPackageApi` | `SunderPackageId`; requires an empty required-contract set |
| `ListSunderPackageApis` | None; reads the committed lock |
| `RequireSunderPackageApi`, `UnrequireSunderPackageApi` | `SunderPackageId`, `SunderContractId`; changes provider readiness only |
| `ImplementSunderPackageApi` | `SunderPackageId`, `SunderContractId`; new artifacts also require range and candidate or origin |
| `UnimplementSunderPackageApi` | `SunderPackageId`, `SunderContractId`; bounded source analysis must find no remaining provider binding |

Consumer and provider intents share one artifact selection. Removing a consumer
retains provider intent and shared metadata. Updates refuse to remove contracts
still required or implemented. `SunderPackageApiLocalName` resolves consumer-name
or provider-key collisions. Implement validates the generated interface in memory;
the next ordinary Build requires its local provider implementation. Commands stage
source checks and commit by replacing the whole lock. Generated output changes on
Build, and failed commands preserve the prior selection and generated facade.

For local unpublished development, add/update/implement may use
`-p:SunderPackageCandidatePath=/path/to/exact.sunderpkg`. The canonical archive
supplies the same inventory/content projection as Registry. Its ownership remains
unconfirmed until Registry accepts publication. To hydrate an **existing** lock
offline:

```powershell
dotnet msbuild Package.csproj -t:RestoreSunderContracts `
  -p:SunderPackageCandidatePath=/path/to/exact.sunderpkg
```

Restore verifies exact committed metadata and leaves every lock byte unchanged.
`SunderContractCacheRoot` overrides the default `~/.sunder/contracts/v1/sha256`
location. Readers and writers share the cross-language cache lease. Explicit
restore recovers interrupted publication and quarantines corrupt regular files
before installing verified replacements; unmarked nonempty roots remain rejected.
See [shared cache behavior](https://github.com/Younics/sunder-core/blob/main/docs/package-development/CONTRACT-RESTORE-CACHE.md).
Explicit shared cache GC and equivalent Node package API commands are implemented.
GC includes original catalog/ownership-receipt pairs and proven repair
artifacts, marks direct and metadata-only platform references, and recovers shared
C#/Node tombstones. Platform restore/provider generation and `ImplementSunderPlatformApi`,
`UpdateSunderPlatformApi`, `UnimplementSunderPlatformApi`, `ListSunderPlatformApis`
use an SDK-derived stable range and exact canonical catalog content.
`SunderPlatformCatalogPath` selects a local catalog without a Registry, or supplies
exact catalog evidence during restore. It can be combined with package candidates
for cold-cache restore. Catalog signatures and trust bundles are not part of this
format. Set `SunderAllowDevelopmentRegistryOrigin=true` explicitly for
canonical `http://localhost[:nondefault-port]/` origins, including offline Build,
Check/Accept/Freeze and Pack of their locks. HTTPS remains the default; Debug does
not enable this mode implicitly. Origin-free local selections need no such opt-in. See
[platform imports](https://github.com/Younics/sunder-core/blob/main/docs/package-development/PLATFORM-CONTRACT-IMPORTS.md).

### Build The Package

Build the package project:

```powershell
dotnet build .\MyPackage\MyPackage.csproj
```

The canonical build remains one exact current-RID Worker leaf internally, emitted under the RID-specific output, and then composes that leaf with the optional App leaf into a non-RID development tree:

```text
bin/Debug/net10.0/sunder-dev/
  manifest/
    sunder-package.json
    content-index.json
  payload/
    shared/
      lib/
        MyPackage.dll
        MyPackage.pdb
      assets/
        icon.png
    app/
      shared/
      <rid>/
    runtime/
      shared/
      <rid>/
```

The exact Worker input remains at `bin/Debug/net10.0/<current-rid>/sunder-dev`; composition never overwrites a leaf. `Clean` removes only the safely marked composed tree. The optional App project is built exactly once, while guarded inner Worker builds suppress App build and composition recursion.

Load this folder into Sunder App during development:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

## Pack A Package Archive

Use the canonical explicit pack target:

```powershell
dotnet msbuild .\MyPackage\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
```

For a primary project, the target sequentially invokes guarded `Build` targets on the same csproj for every RID in `SunderPackageRuntimeIdentifiers`, builds the optional App exactly once, aggregates the validated leaves, writes one deterministic universal `.sunderpkg`, then re-extracts and validates that exact archive before reporting success. The project is invoked through the MSBuild task, never through a self `ProjectReference`, and inner builds are intentionally not parallel. By default the archive is beside the non-RID Release output:

```text
bin/Release/net10.0/MyPackage.1.0.0.sunderpkg
```

`dotnet publish` also invokes the pack task and writes its archive to the publish directory when a pipeline separately requires publish output:

```powershell
dotnet publish .\MyPackage\MyPackage.csproj -c Release
```

Single-project packages keep one normal MSBuild `Version` in the authored project
or an intentionally shared props file. Generated internal roles inherit it.

## Archive Shape

`.sunderpkg` files are zip archives with this layout:

```text
manifest/
  sunder-package.json
  content-index.json
payload/
  shared/
  app/
    shared/
    <rid>/
  runtime/
    shared/
    <rid>/
```

The content index records canonical physical paths, hashes, and sizes; target role is represented by the physical payload layer rather than duplicated in index metadata. Runtime install and registry publish paths validate the archive before accepting it.

## Assets And Icons

Place package assets under `Assets/` in the project directory. They are copied into the global payload with logical paths under `assets/`.

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
| `SunderPackageRuntimeIdentifiers` | Semicolon-separated inferred target RIDs; defaults to all six supported exact RIDs |
| `SunderPackagePrimaryProject` | Opts the canonical Worker/package csproj into current-RID build composition and universal pack fan-out |
| `SunderPackageAppProject` | Optional build-only App csproj path; pair it with the documented non-building, non-compiling restore edge, and tooling explicitly builds it once |
| `SunderPackageComposeRuntimeIdentifiers` | RID leaves selected for primary aggregation; defaults to all configured RIDs and may be set to one RID for prebuilt fan-in |
| `SunderPackageUsePrebuiltLeaves` | Uses retained leaves without Worker/App builds; canonical generated-role projects require qualified evidence and `BuildProjectReferences=false` |
| `SunderPackagePrebuiltRoleInputsFile` | Compact exact-RID/path/SHA-256 selection file for canonical generated-role prebuilt Pack |
| `SunderPackageRoleInputSourceRoot` | Reviewed source root retained with the same relative layout on native producers and fan-in |
| `RuntimeIdentifier` | Required exact RID for an explicit `worker` target; must equal that target's `Rid` |
| `SunderPackageAssetsDirectory` | Physical source directory copied to logical `assets/`; defaults to the project `Assets` directory |
| `SunderDevOutputPath` | Overrides a leaf path only when it normalizes to the direct `TargetDir/sunder-dev` child; existing output must carry the generated marker |
| `SunderPackageComposedOutputRoot` | Overrides the primary non-RID output root; defaults to `bin/<Configuration>/<TFM>` |
| `SunderPackageComposedOutputPath` | Overrides the composed tree path, which must remain the direct `sunder-dev` child of the composed output root |
| `SunderPackagePrimaryTargetLeafRoot` | Overrides the root containing prebuilt `<rid>/sunder-dev` Worker leaves |
| `SunderPackageAppTargetLeaf` | Overrides the optional App project's prebuilt `sunder-dev` path |
| `SunderPackageAppTargetDiscoveryRoot` | Overrides the App leaf discovery root paired with `SunderPackageAppTargetLeaf` |
| `SunderPackageFileName` | Overrides the default archive file name |
| `SunderPackageOutputPath` | Overrides output path for the explicit `PackSunderPackage` target |
| `SunderPublishPackageOutputPath` | Overrides archive output path during `dotnet publish` |
| `SunderPackageWorkerSigningTargets` | Semicolon-separated project targets that sign a private copy of the computed C# worker publish closure before indexing |

Compatibility metadata is inferred automatically from authored assemblies, including async/iterator/lambda bodies. Arbitrary copy-local dependencies are not treated as authored. For unusual reflection or dynamic scenarios, declare the reachable capabilities and acknowledge every unresolved call site reported by the build:

```xml
<ItemGroup>
  <SunderSdkCapability Include="callbacks.v1" />
  <SunderSdkDynamicAccess Include="MyCompany.Package.DynamicFactory.CreateHandler" />
</ItemGroup>
```

`SunderSdkPackageVersion` is available as a verification-only override: it must match the informational/package version on the resolved `Sunder.Sdk` reference. The resolved version is emitted as `sdkVersion` on every inferred C# target.

Explicit target items replace the inferred App role/RID matrix. Runtime authoring requires one explicit exact-RID worker item:

```xml
<ItemGroup>
  <SunderPackageTarget Include="runtime/linux-x64"
                       Role="runtime"
                       Rid="linux-x64"
                       Kind="worker"
                       EntryPoint="lib/MyPackage" />
</ItemGroup>
```

An isolated .NET worker declares exactly one `runtime/<RID>` target with `Kind="worker"` per framework-dependent build. It requires `TargetFramework=net10.0`, `OutputType=Exe`, `SelfContained=false`, `UseAppHost=false`, `PublishSingleFile=false`, `PublishTrimmed=false`, and use of the Worker V1 API so capability inference finds `worker-protocol.v1`. The manifest entry point is the official managed module `lib/$(TargetName).dll`, accompanied by its canonical `.runtimeconfig.json`, `.deps.json`, and package-owned managed/native dependencies. Runtime packs, apphosts, shared-framework binaries, symbols, XML documentation, and SDK/toolchain assets are not package payload. The module cannot be renamed or factored into a shared aggregate layer. Every Runtime target also receives `rpc.v1`, carries the SDK-version-matched baseline, and binds `sunder.runtime.dotnet >=10.0.0 <11.0.0` with module ABI `sunder.dotnet-worker-module.v1`. Aggregate the six exact-RID leaves for a universal package.

For platform signing, append a project target to `SunderPackageWorkerSigningTargets`. That target receives a private copied instance of the Worker publish closure, after generated XML documentation companions are removed, as `@(SunderPackageWorkerSigningFile)` and the official managed-module relative path as `$(SunderPackageWorkerEntryPointRelativePath)`. It runs after `ComputeFilesToPublish` without exposing NuGet or runtime-pack cache files to mutation, and before Sunder copies, indexes, and validates the leaf. Sign applicable supplied files in place and fail closed; later byte changes fail pack validation because packing never regenerates the established content index. Platform binary signatures do not constitute a Sunder package signature or publisher attestation.

Schema-first RPC metadata is authored with strict MSBuild items. Every imported or provided contract must have a local bundle, so validation never depends on another installed package:

```xml
<ItemGroup>
  <SunderRpcBinding Include="Contracts/example.rpc.json"
                    Namespace="Example.Package.Generated" />
  <SunderContractBundle Include="Contracts/example.rpc.json"
                        ContractId="example.rpc"
                        Version="2.0.0"
                        DescriptorPath="contracts/example.rpc.json"
                        Exposure="public" />
  <SunderUsesContract Include="example.rpc"
                      Role="app"
                      ContractVersion="2.0.0"
                      Required="true"
                      Actions="discover;invoke" />
  <SunderRpcProvider Include="example.rpc.default"
                     Role="runtime"
                     ContractId="example.rpc"
                     ContractVersion="2.0.0"
                     Audience="owner" />
</ItemGroup>
```

`SunderRpcBinding` requires `Namespace`; optional `OutputFile` selects a relative generated `.cs` path. Set `Accessibility="Internal"` when the generated binding should stay assembly-local. Optional `Accessibility` accepts only the exact values `Public` and `Internal`, defaults to `Public` when omitted or blank, and fails the build before generated output is replaced when invalid. `Internal` uniformly applies to all top-level generated C# declarations, including DTOs, provider interfaces, clients, and handlers. It is intended for same-assembly bindings hidden behind handwritten public authoring contracts; use `Public` when generated types cross an assembly boundary. Per-type or mixed accessibility is not supported.

For [implementation-first C# providers](https://github.com/Younics/sunder-core/blob/main/docs/package-development/IMPLEMENTATION-FIRST-RPC.md#value-rules), generated codecs support SDK-profile nullable/tagged record unions, inferred authored constant tags, concrete variant roots, and recursive models. They reuse SDK validation for whole values and exactly-one-variant matching; generated metadata supplies required synthetic tags and a `[ThreadStatic]` depth guard bounds recursion. General scalar/array/map untagged unions remain SDK-profile unsupported, not unfinished source-codec work; no new profile is introduced. Narrow CLR integer codecs (`byte`, `sbyte`, `short`, `ushort`, `int`) use the SDK safe-integer reader plus checked ranges, accepting fraction/exponent spellings of integral values representable by the target type without a descriptor or ABI change.

`SunderContractBundle` accepts only a descriptorVersion 1 descriptor whose `contractId` and complete SemVer `version`, including build metadata, exactly match `ContractId` and `Version`. `Exposure` is required and is either `package` or `public`; `DescriptorPath` defaults to `contracts/&lt;file name&gt;` and must stay under `contracts/`. The build canonicalizes the immutable descriptor snapshot and derives `descriptorSha256` and `abiSha256`; hash metadata must never be authored.

`SunderUsesContract` requires an existing `app` or `runtime` `Role`, an exact local `ContractVersion`, an explicit lowercase Boolean `Required`, and one or more unique `Actions` from `discover`, `invoke`, and `subscribe`. Version ranges are not accepted. `SunderRpcProvider` requires an existing target `Role`, exact local `ContractId` and `ContractVersion`, and `Audience="owner"` or `Audience="public"`. Public providers require public bundle exposure; package-exposed bundles can have owner-audience providers only. Uses are unique by `(Role, ContractId)` and providers by `(Role, ProviderId)`. Every Runtime target and every App target participating in RPC receives `rpc.v1`.

For canonical generated-role release fan-in, freeze before native Build and retain
the whole `sunder-targets/<rid>` bundle: `sunder-dev`, `role-inputs.v1.json` and its
digest sidecar. Sign through `SunderPackageWorkerSigningTargets` before indexing.
Select each retained evidence file by exact RID, relative path and independently
retained SHA-256, then pack with an installed or explicitly retained immutable toolset:

```powershell
dotnet msbuild .\MyPackage.csproj `
  -t:PackSunderPackage `
  -p:Configuration=Release `
  -p:SunderPackageUsePrebuiltLeaves=true `
  -p:BuildProjectReferences=false `
  -p:SunderPackagePrebuiltRoleInputsFile=C:/artifacts/selection.json
```

This mode reevaluates reviewed inputs without Build, Compile, Restore or source-generator
execution. It defaults to all declared RIDs; use `RuntimeIdentifier` or
`SunderPackageComposeRuntimeIdentifiers` for a subset with exactly matching selections.
Source, dependency, toolset, baseline, lock, RPC projection and retained payload checks
run again before transactional archive publication. See the
[qualified role-input contract](https://github.com/Younics/sunder-core/blob/main/docs/package-development/PREBUILT-ROLE-INPUTS.md)
for the closed selection format, portable bundle layout, bounds and failure behavior.

Additional prebuilt leaves remain supported through `SunderPackageTargetLeaf` and `SunderNodePackageTargetLeaf`. Every additional item requires explicit `DiscoveryRoot` metadata matching its producer. `AggregateSunderPackage` holds those roots while locating and locking leaves, verifies package-wide metadata agreement (including identical contract bundles), rejects duplicate exact targets and projection collisions, and factors identical non-native files into global or role-shared layers before validating the canonical output. Exact-RID native assets always remain in their role/RID layer. Role-scoped uses and providers are unioned by `(Role, ContractId)` and `(Role, ProviderId)`: identical declarations from multiple RID leaves deduplicate, while conflicting declarations fail aggregation. App leaves therefore declare only App uses/providers and Runtime leaves only Runtime uses/providers; shared descriptor payload remains package-wide.

## Validate Before Publishing

Use the Sunder CLI to validate package artifacts before publishing:

```powershell
sunder dev package validate .\MyPackage\bin\Release\net10.0\MyPackage.1.0.0.sunderpkg
```

Validation checks canonical format and integrity, including archive safety, manifest shape, exact target unions, target entry points, package id/version, icons, content-index hashes/sizes, and unindexed files. It does not execute code, attest a publisher, or make an artifact trusted.

## More Documentation

- Package author manual: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Build, validate, publish, and version: https://github.com/Younics/sunder-core/blob/main/docs/package-development/BUILD-PUBLISH-VERSIONING.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- Sunder SDK: https://www.nuget.org/packages/Sunder.Sdk
