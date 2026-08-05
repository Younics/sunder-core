# Sunder.Package.Templates

> **Release/source channel:** The NuGet README describes that published template version. The repository copy tracks current source and may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

`Sunder.Package.Templates` provides `dotnet new sunder-package`, an aggregate .NET scaffold for one universal Sunder package.

It is the current managed .NET Runtime/Avalonia preset, not the only valid authoring route. Node process/web presets use `npm create sunder-package@latest`; future Python, Rust, Go, other process toolchains, other .NET UI approaches, and other web frameworks may emit the same canonical package format with their own tooling.

## Install

```powershell
dotnet new install Sunder.Package.Templates
```

## Create

Headless Runtime package:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
```

Avalonia App plus Runtime targets:

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
    Generated/
      SampleRpc.g.cs
    PackageProtocol.cs
  MyPackage.Runtime/
    MyPackage.Runtime.csproj
    PackageModule.cs
  MyPackage.App/                 # only with --withAvalonia
    MyPackage.App.csproj
    PackageModule.cs
```

`MyPackage.Protocol` is always generated, non-packable, and package-local. It embeds the language-neutral descriptor and checked-in generated DTO, client, and provider adapter source. Runtime and App reference it with `PrivateAssets="all"`; it is not a separately published ABI.

Both role leaves bundle the same descriptor through `SunderContractBundle`. The aggregate project validates package-wide metadata agreement, combines exact RID targets, and factors identical content into `payload/shared`.

Coordinated Sunder references use the bounded minor range derived from the template package version. Avalonia and Stack dependencies appear only when selected. The dependency option adds only installed-package metadata; it does not add a NuGet reference to another package's implementation or protocol helpers.

## Build And Package

```powershell
dotnet restore .\MyPackage\MyPackage.csproj
dotnet build .\MyPackage\MyPackage.csproj --no-restore
dotnet msbuild .\MyPackage\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
```

Build emits `bin/Debug/net10.0/sunder-dev` with the canonical manifest, content index, shared descriptor, and exact target layers. Edit the package version only in `Sunder.Package.props`; every generated project imports it. The canonical pack target emits one validated `.sunderpkg` under `bin/Release/net10.0`.

Package identity and dependencies come from `PackageMetadata.cs`; authors do not maintain a source package manifest. Use `context.Storage.RoleLocalWorkspace` for writable local paths and treat `context.ContentRootPath` as read-only.

## Documentation

- Package development: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- SDK compatibility: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-SDK-COMPATIBILITY.md
