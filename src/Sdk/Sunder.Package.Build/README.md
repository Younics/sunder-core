# Sunder.Package.Build

`Sunder.Package.Build` contains the MSBuild targets and tasks that turn a Sunder package project into local development output and distributable `.sunderpkg` archives.

Reference this package from Sunder package projects together with `Sunder.Sdk`.

## Install

```powershell
dotnet add package Sunder.Package.Build --private-assets all
```

Typical package project reference:

```xml
<ItemGroup>
  <PackageReference Include="Sunder.Sdk" Version="[1.1.0,1.2.0)" />
  <PackageReference Include="Sunder.Package.Build" Version="[1.1.0,1.2.0)" PrivateAssets="all" />
</ItemGroup>
```

Use `PrivateAssets="all"` because this package is build tooling for the package project. It should not flow as a runtime dependency of your package.
The NuGet package exposes no compile or runtime assembly asset; its task assembly and private dependencies live only under `tasks/net10.0` and are loaded by `buildTransitive/Sunder.Package.Build.targets`.

## What It Does

`Sunder.Package.Build` adds build targets that:

- Generate a universal V1 manifest with exact role/RID targets from `Sunder.Sdk` metadata, MSBuild declarations, and build output.
- Emit an unpacked, content-indexed, consumer-valid `sunder-dev` archive tree after `dotnet build`.
- Create a `.sunderpkg` archive after `dotnet publish`.
- Provide an explicit `PackSunderPackage` MSBuild target.
- Copy common managed output into `payload/shared/lib` and source assets into `payload/shared/assets`.
- Validate, canonicalize, hash, and copy schema-first RPC descriptors into `payload/shared/contracts`.
- Project only matching `runtimes/<rid>` content into each declared exact role/RID layer.
- Exclude host boundary assemblies such as `Sunder.Sdk` and core Avalonia assemblies from private package output.

Package authors do not maintain `sunder-package.json` by hand.

## Expected Project Shape

A package project declares package metadata in C# and normally exposes a Runtime module, an App module, or both. Each active role permits at most one public implementation. Build inspects compiled metadata and, by default, emits one exact target for every supported RID (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`). App targets use `avalonia`; Runtime targets use `dotnet`. A project with no module emits zero targets and is valid only when it declares at least one contract bundle.

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

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
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

Load this folder into Sunder App during development:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

## Publish A Package Archive

Publish the package project:

```powershell
dotnet publish .\MyPackage\MyPackage.csproj -c Release
```

The publish target validates the indexed staging tree, writes a deterministic `.sunderpkg`, then re-extracts and validates that exact archive before reporting success. The archive is written to the publish directory:

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
| `SunderPackageAssetsDirectory` | Physical source directory copied to logical `assets/`; defaults to the project `Assets` directory |
| `SunderDevOutputPath` | Overrides the generated path only when it normalizes to the direct `TargetDir/sunder-dev` child; existing output must carry the generated marker |
| `SunderPackageFileName` | Overrides the default archive file name |
| `SunderPackageOutputPath` | Overrides output path for the explicit `PackSunderPackage` target |
| `SunderPublishPackageOutputPath` | Overrides archive output path during `dotnet publish` |

Compatibility metadata is inferred automatically from authored assemblies, including async/iterator/lambda bodies. Arbitrary copy-local dependencies are not treated as authored. For unusual reflection or dynamic scenarios, declare the reachable capabilities and acknowledge every unresolved call site reported by the build:

```xml
<ItemGroup>
  <SunderSdkCapability Include="callbacks.v1" />
  <SunderSdkDynamicAccess Include="MyCompany.Package.DynamicFactory.CreateHandler" />
</ItemGroup>
```

`SunderSdkPackageVersion` is available as a verification-only override: it must match the informational/package version on the resolved `Sunder.Sdk` reference. The resolved version is emitted as `sdkVersion` on every inferred C# target.

Explicit target items replace the inferred role/RID matrix. `Role` and `Rid` identify the exact target; `Kind` and `EntryPoint` default from the C# role but can be declared explicitly:

```xml
<ItemGroup>
  <SunderPackageTarget Include="runtime/linux-x64"
                       Role="runtime"
                       Rid="linux-x64"
                       Kind="dotnet"
                       EntryPoint="lib/MyPackage.dll" />
</ItemGroup>
```

Schema-first RPC metadata is authored with strict MSBuild items. Every imported or provided contract must have a local bundle, so validation never depends on another installed package:

```xml
<ItemGroup>
  <SunderContractBundle Include="Contracts/chat-provider.rpc.json"
                        ContractId="example.agent.chat-provider"
                        Version="1.0.0"
                        DescriptorPath="contracts/chat-provider.rpc.json" />
  <SunderUsesContract Include="example.agent.chat-provider"
                      VersionRange=">=1.0.0 &lt;2.0.0"
                      Required="false"
                      Actions="discover;invoke;subscribe" />
  <SunderRpcProvider Include="example.package.chat"
                     ContractId="example.agent.chat-provider"
                     ContractVersion="1.0.0"
                     Role="runtime" />
</ItemGroup>
```

`SunderContractBundle` requires `ContractId` and `Version`; `DescriptorPath` defaults to `contracts/&lt;file name&gt;` and must stay under `contracts/`. `SunderUsesContract` requires a strict `VersionRange`, an explicit Boolean `Required`, and one or more unique `Actions` from `discover`, `invoke`, and `subscribe`. `SunderRpcProvider` requires `ContractId`, `ContractVersion`, and an exact `app` or `runtime` `Role` that exists in the generated target matrix. Provider hashes are always derived from the matching descriptor bundle.

Aggregate projects can combine validated leaf trees without rebuilding them:

```xml
<PropertyGroup>
  <SunderPackageAggregateProject>true</SunderPackageAggregateProject>
</PropertyGroup>
<ItemGroup>
  <SunderPackageTargetLeaf Include="Runtime/bin/$(Configuration)/$(TargetFramework)/sunder-dev" />
  <SunderPackageTargetLeaf Include="App/bin/$(Configuration)/$(TargetFramework)/sunder-dev" />
</ItemGroup>
```

`AggregateSunderPackage` verifies package-wide metadata agreement, rejects duplicate exact targets and projection collisions, and factors identical files into global or role-shared layers before validating the canonical output.

## Validate Before Publishing

Use the Sunder CLI to validate package artifacts before publishing:

```powershell
sunder package validate .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

Validation checks archive safety, manifest shape, exact target unions, target entry points, package id format, SemVer version, icon existence, content index hashes, content index sizes, and unindexed files.

## More Documentation

- Package author manual: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Build, validate, publish, and version: https://github.com/Younics/sunder-core/blob/main/docs/package-development/BUILD-PUBLISH-VERSIONING.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- Sunder SDK: https://www.nuget.org/packages/Sunder.Sdk
