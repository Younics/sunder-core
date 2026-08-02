# Getting Started

> **Source channel:** Applies to current source on the Sunder SDK `1.1.x` line, package manifest V1, .NET 10, and Runtime protocol revision 3. Use the matching `sdk/v*` tag for released template behavior.

This guide creates a package, runs it as a development package, and produces a validated distributable artifact. The repository also contains a [compiled quickstart package](../samples/Sunder.Package.Quickstart/) used by the Sunder Core build.

## Prerequisites

- .NET 10 SDK.
- Sunder App and the `sunder` CLI, or source builds of both.
- Access to NuGet.org or another feed containing the coordinated Sunder `1.1.x` packages.

Check the SDK and CLI:

```powershell
dotnet --version
sunder --help
```

## Install The Template

Install the template from the `1.1` line:

```powershell
dotnet new install Sunder.Package.Templates::1.1.0
```

Use `dotnet new uninstall Sunder.Package.Templates` before changing to a different template line. When testing an unreleased source build, install the locally packed `.nupkg` instead.

## Create A Package

Create a headless Runtime package:

```powershell
dotnet new sunder-package `
  --name MyPackage `
  --packageId my.company.package `
  --packageName "My Package"
```

Create an App and Runtime package with a default Avalonia view:

```powershell
dotnet new sunder-package `
  --name MyPackage `
  --packageId my.company.package `
  --packageName "My Package" `
  --withAvalonia
```

Use `--output .\MyPackage` when you need an explicit destination. The generated aggregate contains `Sunder.Package.props` as its single package-version source, package metadata, Runtime code, an always-present non-packable `*.Protocol` project, and optional App/Stack code. The protocol project starts with an RPC descriptor plus generated DTO, client, and provider adapters. It does **not** contain a source `sunder-package.json`; `Sunder.Package.Build` generates the canonical manifest from compiled metadata and MSBuild items.

### Template Options

| Option | Result |
| --- | --- |
| `--packageId <id>` | Required lowercase dot-separated runtime identity. |
| `--packageName <name>` | Required user-facing name. |
| `--withAvalonia` | Adds `Sunder.Sdk.Avalonia`, Avalonia, and a default App view. |
| `--noDefaultView` | Keeps Avalonia support but omits the starter shell view. |
| `--withStacks` | Adds `Sunder.Sdk.Stacks` and a compiled exporter/importer stub. |
| `--withHostDependency` | Adds an installed-package dependency. |
| `--hostPackageId <id>` | Package id required by `--withHostDependency`. |
| `--hostPackageVersionRange <range>` | Runtime dependency range; default `>=1.1.0 <1.2.0`. |

## Build

```powershell
dotnet restore .\MyPackage\MyPackage.csproj
dotnet build .\MyPackage\MyPackage.csproj --no-restore
```

The build emits `MyPackage/bin/Debug/net10.0/sunder-dev/`. That folder is generated output and may be deleted and recreated on every build. Do not edit it.

## Run In Sunder

Close any older invocation using the same development package, then launch:

```powershell
Sunder.App.exe `
  --dev-package .\MyPackage\bin\Debug\net10.0\sunder-dev `
  --watch
```

On macOS or Linux, invoke the corresponding Sunder executable and use `/` path separators.

The App sends its complete development-package set to the already-running Runtime through an invocation-owned lease. Runtime validates and activates the Runtime role, watches the generated folder when `--watch` is present, and publishes an App-safe package snapshot. App does not load files directly from the development output. Rebuilding triggers a generation-fenced reload; closing App releases its lease and restores an installed package with the same id, if one exists.

Load a dependency and its extension in one invocation so Runtime can validate the complete graph:

```powershell
Sunder.App.exe `
  --dev-package .\HostPackage\bin\Debug\net10.0\sunder-dev `
  --dev-package .\ExtensionPackage\bin\Debug\net10.0\sunder-dev `
  --watch
```

## Pack And Validate

Set the strict SemVer package version once in the generated `Sunder.Package.props`. The aggregate, Runtime, App, and Protocol projects all import this file:

```xml
<Project>
  <PropertyGroup>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
```

Create and validate the artifact:

```powershell
dotnet msbuild .\MyPackage\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
sunder dev package validate .\MyPackage\bin\Release\net10.0\MyPackage.1.0.0.sunderpkg
```

Install the exact validated file locally:

```powershell
sunder package install --file .\MyPackage\bin\Release\net10.0\MyPackage.1.0.0.sunderpkg
```

See [Build, Validate, Publish, And Version](BUILD-PUBLISH-VERSIONING.md) before publishing to a Registry. The [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md) is the normative source for generated manifest and archive rules.

## Next Steps

- [Package Anatomy](PACKAGE-ANATOMY.md) explains App and Runtime roles.
- [Activation, DI, And Disposal](ACTIVATION-AND-DI.md) defines safe lifecycle behavior.
- [Avalonia Views](AVALONIA.md) adds shell UI.
- [Runtime Operations](RUNTIME-OPERATIONS.md) connects App UI to Runtime services.
- [Troubleshooting](TROUBLESHOOTING.md) covers common first-run failures.
