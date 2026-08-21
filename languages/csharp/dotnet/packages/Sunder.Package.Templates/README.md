# Sunder.Package.Templates

> **Release/source channel:** The NuGet README describes that published template version. The repository copy tracks current source and may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

`Sunder.Package.Templates` provides `dotnet new sunder-package`, a canonical .NET Worker/package scaffold that builds development output and one universal Sunder package directly.

It is the current .NET Worker V3/Avalonia preset, not the only valid authoring route. Node worker/web presets use `npm create sunder-package@latest`; future Python, Rust, Go, other worker toolchains, other .NET UI approaches, and other web frameworks may emit the same canonical package format with their own tooling.

## Install

```powershell
dotnet new install Sunder.Package.Templates
```

## Create

Headless Worker package:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
```

Avalonia App plus Worker targets:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --withAvalonia
```

Stack provider example:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --withStacks
```

Package dependency metadata:

```powershell
dotnet new sunder-package --name MyExtension --packageId my.company.extension --packageName "My Extension" --withHostDependency --hostPackageId sunder.package.agent --hostPackageVersionRange ">=2.0.0 <3.0.0"
```

Use `--output .\MyPackage` to choose an explicit empty destination.

## Options

| Option | Meaning |
| --- | --- |
| `--packageId <id>` | Required lowercase dot-separated package id. |
| `--packageName <name>` | Required user-facing package name. |
| `--withAvalonia` | Adds an Avalonia App leaf and default package view. |
| `--noDefaultView` | Retains the Avalonia leaf but omits its starter shell view. |
| `--withStacks` | Adds `Sunder.Sdk.Stacks` and a Stack RPC provider example. |
| `--withHostDependency` | Adds `[SunderPackageDependency]` metadata. |
| `--hostPackageId <id>` | Required package id for `--withHostDependency`. |
| `--hostPackageVersionRange <range>` | Dependency range; defaults to `>=1.1.0 <1.2.0`. |

## Generated Shape

Every generated package contains:

```text
MyPackage/
  Sunder.Package.props           # single Version source for every generated project
  MyPackage.csproj
  PackageMetadata.cs
  Assets/
    icon.png
  MyPackage.Protocol/
    MyPackage.Protocol.csproj
    Contracts/
      sample.rpc.json
    PackageProtocol.cs
  MyPackage.Worker/
    Program.cs
    SampleRpcProvider.cs
  MyPackage.App/                 # only with --withAvalonia
    MyPackage.App.csproj
    PackageModule.cs
```

`MyPackage.csproj` is the canonical Worker/package project and sets `SunderPackagePrimaryProject=true`. Worker source remains under `MyPackage.Worker/`, but there is no separate Worker csproj or Aggregate wrapper. `MyPackage.Protocol` is always generated, non-packable, and package-local. It embeds the language-neutral descriptor and generates DTO, client, and provider adapter source under `obj` during build; generated bindings are not checked in. `SampleRpcProvider.cs` implements the starter owner-only provider, and `Program.cs` registers it with the canonical Worker V3 overload on `SunderWorker`. Worker and App reference Protocol with `PrivateAssets="all"`; it is not a separately published ABI.

Both role leaves bundle the same descriptorVersion 2 contract with package exposure. Worker declares its owner-only sample provider, while the optional App leaf declares an exact `2.0.0` role-scoped use. `SunderPackageAppProject` treats the App csproj as a non-compiling build-only input; an outer-only restore edge lets the canonical restore include it, while explicit composition builds it exactly once. The canonical project validates package-wide metadata agreement, combines all six exact-RID Worker V3 targets with optional App targets during pack, and factors identical content into `payload/shared`.

Coordinated Sunder references use the bounded minor range derived from the template package version. Avalonia and Stack dependencies appear only when selected. RPC uses and providers bind exact contract version `2.0.0` and never carry `VersionRange`; the dependency option deliberately retains its independently authored installed-package version range and does not add a NuGet reference to another package's implementation or protocol helpers.

## Build And Package

```powershell
dotnet restore .\MyPackage\MyPackage.csproj
dotnet build .\MyPackage\MyPackage.csproj --no-restore
dotnet msbuild .\MyPackage\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
```

Build emits an exact current-RID Worker leaf under `bin/Debug/net10.0/<rid>/sunder-dev` and composes it with the optional App into `bin/Debug/net10.0/sunder-dev`. Output uses archive format 1, manifest version 3, descriptor and ABI hashes, `rpc.v2`, `worker-protocol.v3`, the matching SDK baseline, the content index, shared descriptors, and exact target layers. Edit the package version only in `Sunder.Package.props`; every generated project imports it. The canonical pack target sequentially builds all six Worker RIDs and emits one validated universal `.sunderpkg` under `bin/Release/net10.0`.

Package identity and dependencies come from `PackageMetadata.cs`; authors do not maintain a source package manifest. Runtime code executes only through the self-contained Worker V3 apphost and uses `SunderWorkerContext` for Host-mediated package data and RPC.

## Documentation

- Package development: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- SDK compatibility: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-SDK-COMPATIBILITY.md
