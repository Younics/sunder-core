# Sunder.Package.Build

> **Release/source channel:** The NuGet README describes that published build package. The repository copy tracks current source and may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

`Sunder.Package.Build` contains the MSBuild targets and tasks that turn a Sunder package project into local development output and distributable `.sunderpkg` archives.

Reference this package from App projects with `Sunder.Sdk`, or from Runtime worker projects with `Sunder.Sdk.Worker`. A package family can opt its canonical Worker project into direct composition with `SunderPackagePrimaryProject=true`; a separate aggregate wrapper is not required.

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

## What It Does

`Sunder.Package.Build` adds build targets that:

- Generate an archive-format V1, Manifest V3 package with exact role/RID targets from SDK metadata, MSBuild declarations, and build output.
- Emit an unpacked, content-indexed, consumer-valid `sunder-dev` archive tree after `dotnet build`.
- Create a `.sunderpkg` archive after `dotnet publish`.
- Provide an explicit `PackSunderPackage` MSBuild target.
- Copy App managed output or a Runtime worker's complete self-contained publish closure into canonical payload layers, with source assets under `payload/shared/assets`.
- Expose a post-publish-file-computation, pre-index signing extension over a private copy of C# worker binaries.
- Validate descriptorVersion 2 RPC contracts, generate C# bindings and immutable canonical snapshots, derive descriptor and ABI hashes, and copy descriptors into `payload/shared/contracts`.
- Project only matching `runtimes/<rid>` content into each declared exact role/RID layer.
- Exclude host boundary assemblies such as `Sunder.Sdk` and core Avalonia assemblies from private package output.

Package authors do not maintain `sunder-package.json` by hand.

## Expected Project Shape

A package family normally contains one canonical Worker/package project, an optional App project, and a package-local Protocol project. Set `SunderPackagePrimaryProject=true` only on the canonical Worker project. Point `SunderPackageAppProject` at the optional App csproj; it is a build-only input invoked through the MSBuild task and is never a Worker compile input or runtime dependency. Add the outer-only `BuildReference=false`/`ReferenceOutputAssembly=false` edge shown below so a first restore of the canonical csproj also restores the App; its condition omits the edge from guarded inner Worker evaluations.

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">$(NETCoreSdkRuntimeIdentifier)</RuntimeIdentifier>
  <RuntimeIdentifiers>win-x64;win-arm64;linux-x64;linux-arm64;osx-x64;osx-arm64</RuntimeIdentifiers>
  <SelfContained>true</SelfContained>
  <UseAppHost>true</UseAppHost>
  <PublishSingleFile>false</PublishSingleFile>
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

Generated package families keep one package `Version` in `Sunder.Package.props`, imported by the primary, App, and Protocol projects. Custom projects should likewise define one normal MSBuild `Version` source rather than overriding different leaves independently.

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
| `SunderPackageUsePrebuiltLeaves` | Uses existing leaves without running Worker or App `Build`; requires `BuildProjectReferences=false` |
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

An isolated .NET worker declares exactly one `runtime/<RID>` target with `Kind="worker"` per self-contained executable build. It requires `OutputType=Exe`, `SelfContained=true`, `UseAppHost=true`, `PublishSingleFile=false`, and use of the Worker V3 API so capability inference finds `worker-protocol.v3`. The entry point is the official project apphost under `lib/`: `$(TargetName).exe` for Windows and extensionless `$(TargetName)` for Linux/macOS. It cannot be renamed or factored into a shared aggregate layer. Every Runtime target also receives `rpc.v2` and must carry the SDK-version-matched baseline. The emitted leaf includes the complete self-contained publish closure and does not require an `ISunderRuntimePackageModule`. Aggregate the six native leaves for a universal package.

For platform signing, append a project target to `SunderPackageWorkerSigningTargets`. That target receives a private copied instance of the complete closure as `@(SunderPackageWorkerSigningFile)` and the official apphost relative path as `$(SunderPackageWorkerEntryPointRelativePath)`. It runs after `ComputeFilesToPublish` without exposing NuGet or runtime-pack cache files to mutation, and before Sunder copies, indexes, and validates the leaf. Sign the supplied files in place and fail closed; later byte changes fail pack validation because packing never regenerates the established content index. Platform binary signatures do not constitute a Sunder package signature or publisher attestation.

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

`SunderContractBundle` accepts only a descriptorVersion 2 descriptor whose `contractId` and complete SemVer `version`, including build metadata, exactly match `ContractId` and `Version`. `Exposure` is required and is either `package` or `public`; `DescriptorPath` defaults to `contracts/&lt;file name&gt;` and must stay under `contracts/`. The build canonicalizes the immutable descriptor snapshot and derives `descriptorSha256` and `abiSha256`; hash metadata must never be authored.

`SunderUsesContract` requires an existing `app` or `runtime` `Role`, an exact local `ContractVersion`, an explicit lowercase Boolean `Required`, and one or more unique `Actions` from `discover`, `invoke`, and `subscribe`. Version ranges are not accepted. `SunderRpcProvider` requires an existing target `Role`, exact local `ContractId` and `ContractVersion`, and `Audience="owner"` or `Audience="public"`. Public providers require public bundle exposure; package-exposed bundles can have owner-audience providers only. Uses are unique by `(Role, ContractId)` and providers by `(Role, ProviderId)`. Every Runtime target and every App target participating in RPC receives `rpc.v2`; legacy `rpc.v1` is removed.

For signed release fan-in, first produce validated leaves in their final RID-specific locations with binaries signed through `SunderPackageWorkerSigningTargets` before indexing, then explicitly pack without permitting project builds:

```powershell
dotnet msbuild .\MyPackage.csproj `
  -t:PackSunderPackage `
  -p:Configuration=Release `
  -p:SunderPackageUsePrebuiltLeaves=true `
  -p:BuildProjectReferences=false
```

This mode never invokes Worker or App `Build` and fails before reading inputs unless `BuildProjectReferences=false`. It defaults to all configured RIDs; set `SunderPackageComposeRuntimeIdentifiers=linux-x64` to compose one RID. Missing leaves, stale or mismatched package metadata, duplicate targets, changed signed bytes, and invalid indexes still fail through normal aggregate and package validation.

Additional prebuilt leaves remain supported through `SunderPackageTargetLeaf` and `SunderNodePackageTargetLeaf`. Every additional item requires explicit `DiscoveryRoot` metadata matching its producer. `AggregateSunderPackage` holds those roots while locating and locking leaves, verifies package-wide metadata agreement (including identical contract bundles), rejects duplicate exact targets and projection collisions, and factors identical files into global or role-shared layers before validating the canonical output. Role-scoped uses and providers are unioned by `(Role, ContractId)` and `(Role, ProviderId)`: identical declarations from multiple RID leaves deduplicate, while conflicting declarations fail aggregation. App leaves therefore declare only App uses/providers and Runtime leaves only Runtime uses/providers; shared descriptor payload remains package-wide.

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
