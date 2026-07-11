<div align="center">
  <img src="src/Host/Sunder.App/Assets/Images/logo.png" alt="Sunder logo" width="128" />
  <h1>Sunder Core</h1>
  <p><strong>Local-first desktop package platform for .NET and Avalonia.</strong></p>
  <p>Build installable packages that contribute UI, services, settings, background work, and runtime capabilities to a desktop shell.</p>
  <p>
    <a href="docs/SUNDER.md"><strong>Overview</strong></a> &middot;
    <a href="docs/SUNDER-PACKAGE-DEVELOPMENT.md"><strong>Package Development</strong></a> &middot;
    <a href="docs/SUNDER-CLI.md"><strong>CLI</strong></a> &middot;
    <a href="https://github.com/Younics/sunder-agent-package"><strong>Agent Packages</strong></a>
  </p>
  <p>
    <a href="LICENSE"><img alt="License: GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-blue.svg"></a>
    <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4.svg">
    <a href="https://www.nuget.org/packages/Sunder.Sdk"><img alt="Sunder.Sdk NuGet" src="https://img.shields.io/nuget/v/Sunder.Sdk?label=Sunder.Sdk"></a>
    <a href="https://www.nuget.org/packages/Sunder.Sdk.Stacks"><img alt="Sunder.Sdk.Stacks NuGet" src="https://img.shields.io/nuget/v/Sunder.Sdk.Stacks?label=Stacks"></a>
    <a href="https://www.nuget.org/packages/Sunder.Package.Build"><img alt="Sunder.Package.Build NuGet" src="https://img.shields.io/nuget/v/Sunder.Package.Build?label=Sunder.Package.Build"></a>
    <a href="https://www.nuget.org/packages/Sunder.Package.Templates"><img alt="Sunder.Package.Templates NuGet" src="https://img.shields.io/nuget/v/Sunder.Package.Templates?label=Templates"></a>
  </p>
</div>

---

Sunder Core is the public foundation of Sunder: the desktop app, local runtime host, CLI, package SDK, package build pipeline, package template, archive validation, and public Registry contracts.

First-party AI Agent packages live in [`Younics/sunder-agent-package`](https://github.com/Younics/sunder-agent-package). The private Registry implementation is not part of this repository.

## What You Get

| Area | What it does |
| --- | --- |
| Desktop shell | Avalonia app that hosts package views, settings views, marketplace/install UX, and Sunder visual theme resources. |
| Runtime host | Local package state, archive validation, install/update/uninstall, activation, runtime services, and package asset serving. |
| CLI | Commands for Registry discovery/publishing and local runtime package operations. |
| SDK | Public contracts for package metadata, modules, views, extensions, configuration, secrets, storage, logging, callbacks, and theme resources. |
| Build tooling | MSBuild targets that generate manifests, emit `sunder-dev`, and create distributable `.sunderpkg` archives. |
| Templates | `dotnet new sunder-package` scaffolding for package authors. |

## Quick Start

Create a Sunder package project with the public template:

```powershell
dotnet new install Sunder.Package.Templates
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
dotnet build .\MyPackage\MyPackage.csproj
```

The build emits an unpacked development package:

```text
MyPackage/bin/Debug/net10.0/sunder-dev/
```

Load it into Sunder App during development:

```powershell
Sunder.App.exe --dev-package .\MyPackage\bin\Debug\net10.0\sunder-dev
```

Publish a distributable package archive:

```powershell
dotnet publish .\MyPackage\MyPackage.csproj -c Release
sunder package validate .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

## Architecture

```text
Sunder.App
  |  desktop shell, package UI, settings, notifications
  v
Sunder.Runtime.Host
  |  local install state, validation, activation, package services
  v
installed packages / dev packages / .sunderpkg archives

Sunder.Cli <-> Registry API / Runtime API
```

`Sunder.App` and `Sunder.Runtime.Host` are separate processes. The runtime owns installed package state and activation. The app owns shell UI and app-side package views.

## Repository Map

| Area | Project | Responsibility |
| --- | --- | --- |
| Desktop app | `src/Host/Sunder.App` | Avalonia shell, package UI activation, marketplace/install UX |
| Runtime host | `src/Host/Sunder.Runtime.Host` | Registry credentials and package orchestration, local package state, runtime activation, local HTTP API |
| CLI | `src/Host/Sunder.Cli` | Registry and runtime commands |
| Runtime contracts | `src/Host/Sunder.Runtime.Contracts` | Host-neutral DTOs for app/CLI/runtime communication |
| SDK | `src/Sdk/Sunder.Sdk` | Public package author contracts |
| Avalonia SDK | `src/Sdk/Sunder.Sdk.Avalonia` | Optional Avalonia UI contracts and theme resources |
| Stack SDK | `src/Sdk/Sunder.Sdk.Stacks` | Optional public Stack import/export and contribution contracts |
| Build tooling | `src/Sdk/Sunder.Package.Build` | Manifest generation, dev output, `.sunderpkg` archives |
| Templates | `src/Sdk/Sunder.Package.Templates` | `dotnet new sunder-package` template |
| Package format | `src/Sunder.Package.Format` | `.sunderpkg` archive inspection and validation |
| Registry contracts | `src/Sunder.Registry.Contracts` | Public Registry API DTO contracts |

## Public Package Author Surface

These are the public NuGet packages intended for package authors:

| Package | Purpose |
| --- | --- |
| [`Sunder.Sdk`](https://www.nuget.org/packages/Sunder.Sdk) | Headless Runtime package roles, host services, configuration, background services, and extension APIs. |
| [`Sunder.Sdk.Avalonia`](https://www.nuget.org/packages/Sunder.Sdk.Avalonia) | App package roles, Avalonia views/settings/workspaces, and Sunder theme resources. |
| [`Sunder.Sdk.Stacks`](https://www.nuget.org/packages/Sunder.Sdk.Stacks) | Optional Stack import/export models and Stack contributor contracts. |
| [`Sunder.Package.Build`](https://www.nuget.org/packages/Sunder.Package.Build) | Build-time targets and tasks for Sunder manifests, dev output, and archives. |
| [`Sunder.Package.Templates`](https://www.nuget.org/packages/Sunder.Package.Templates) | `dotnet new sunder-package` project template. |

`Sunder.Runtime.Contracts`, `Sunder.Package.Format`, and `Sunder.Registry.Contracts` are source projects used by Sunder, but they are not the public package-author SDK surface.

## Build From Source

```powershell
dotnet restore Sunder.Core.slnx
dotnet build Sunder.Core.slnx --no-restore
```

Most projects target `.NET 10`.

## Tests

```powershell
dotnet test tests/Sunder.App.Tests/Sunder.App.Tests.csproj --no-restore
dotnet test tests/Sunder.Runtime.Host.Tests/Sunder.Runtime.Host.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Format.Tests/Sunder.Package.Format.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj --no-restore
```

## Documentation

| Document | Start here for |
| --- | --- |
| [`docs/SUNDER.md`](docs/SUNDER.md) | Current architecture and project overview |
| [`docs/SUNDER-PACKAGE-DEVELOPMENT.md`](docs/SUNDER-PACKAGE-DEVELOPMENT.md) | Creating, debugging, packaging, validating, and publishing packages |
| [`docs/SUNDER-PACKAGE-STANDARD.md`](docs/SUNDER-PACKAGE-STANDARD.md) | Package metadata, manifest, dev output, and archive shape |
| [`docs/SUNDER-SDK-COMPATIBILITY.md`](docs/SUNDER-SDK-COMPATIBILITY.md) | Host/SDK/package compatibility policy |
| [`docs/SUNDER-CLI.md`](docs/SUNDER-CLI.md) | CLI command reference |
| [`docs/SUNDER-APP.md`](docs/SUNDER-APP.md) | Desktop app behavior, dev arguments, package icons, and theme guidance |

## Releases

Release automation is tag-driven:

| Tag prefix | Release |
| --- | --- |
| `app/v*` | Sunder desktop app |
| `host/v*` | Sunder runtime host |
| `cli/v*` | Sunder CLI |
| `sdk/v*` | SDK contract packages, build tooling, and templates |

## Contributing

Issues and pull requests are welcome. Please read [`CONTRIBUTING.md`](CONTRIBUTING.md) before opening a larger change, and discuss substantial API or architecture changes first.

For vulnerability reports, use the private process in [`SECURITY.md`](SECURITY.md).

## License

Sunder Core is distributed under the [GNU General Public License v3.0](LICENSE).
