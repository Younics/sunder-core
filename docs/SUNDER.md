# Sunder

Sunder Core is the public core of the Sunder local-first package platform. It contains the Avalonia desktop shell, local runtime host, CLI, package SDK/build pipeline, package template, package archive validation, and public Registry DTO contracts.

This document is the current-state overview. Package authoring, CLI commands, app development arguments, and Registry behavior are documented in the sibling Sunder docs.

## Project Map

Primary Sunder Core projects:

| Area | Project | Responsibility |
| --- | --- | --- |
| Desktop app | `src/Host/Sunder.App` | Avalonia shell, package UI activation, package marketplace/install UX |
| Runtime host | `src/Host/Sunder.Runtime.Host` | Local package state, package install/update/uninstall, runtime activation, local HTTP API |
| CLI | `src/Host/Sunder.Cli` | Registry browse/install/publish commands and runtime package commands |
| SDK | `src/Sdk/Sunder.Sdk` | Public package contracts, package module API, package context, theme keys |
| Build tooling | `src/Sdk/Sunder.Package.Build` | MSBuild targets/tasks for manifests, dev output, and `.sunderpkg` archives |
| Templates | `src/Sdk/Sunder.Package.Templates` | `dotnet new sunder-package` template |
| Package management | `src/Sunder.PackageManagement` | Shared `.sunderpkg` archive inspection and validation |
| Registry contracts | `src/Registry/Sunder.Registry.Shared` | Public DTOs and API contracts used by CLI/app/web/server |

First-party Agent packages live in the separate public `Younics/sunder-agent-package` repository. Registry implementation projects live in the separate private `Younics/sunder-registry` repository.

## Runtime Shape

Sunder has one installable runtime extension unit: `Package`.

Important related concepts:

| Concept | Current meaning |
| --- | --- |
| Package | Runtime unit installed, loaded, enabled, disabled, updated, and uninstalled by `Sunder.Runtime.Host` |
| Dev package | Unpacked `sunder-dev` build output used for local package development |
| `.sunderpkg` | Distributable package archive produced from the dev output |
| Contracts package | NuGet package used by developers when one package exposes typed extension contracts |
| Bundle | Registry install recipe that points to multiple packages, not a runtime package kind |
| Theme | App-side UI styling data, not managed by `Sunder.Runtime.Host` as a runtime package |

## App And Runtime Boundary

`Sunder.App` and `Sunder.Runtime.Host` are separate processes.

`Sunder.Runtime.Host` owns:

- installed package records
- package archive validation
- package graph activation
- runtime services and background services
- package configuration, secrets, auth callbacks, and runtime faults
- package asset serving for active or installed packages

`Sunder.App` owns:

- Avalonia shell UI
- app-side package view activation
- package workspace view caching
- settings views and view placement
- desktop notifications and app-side fault reporting
- visual theme resources and app branding

Development packages are loaded through both sides. The runtime host validates and activates runtime contributions, then the app activates app-side views and settings contributions for packages reported as active.

## Registry Boundary

The Registry is remote catalog and distribution infrastructure. It is not the source of truth for what is installed on a local machine. This repository contains only the public Registry DTO contracts used by the app and CLI; Registry implementation lives outside this repository.

The Registry owns:

- package catalog metadata
- immutable package versions
- artifact storage for `.sunderpkg` files
- extracted package icon media
- publisher ownership and package management permissions
- dist tags such as `latest`
- search, details, download, install-plan, and update-resolution APIs

The local runtime owns:

- installed versions
- enabled or disabled state
- local configuration and secrets
- active runtime package session state

## Build Notes

Most Sunder projects target `.NET 10`.

If an app or runtime process locks normal build outputs, build affected projects to alternate output and intermediate paths:

```powershell
dotnet build .\src\Host\Sunder.App\Sunder.App.csproj --no-restore -p:OutputPath=.\artifacts\tmp\sunder-app\bin\ -p:IntermediateOutputPath=.\artifacts\tmp\sunder-app\obj\
```

For package development, `dotnet build` emits `sunder-dev`, and `dotnet publish` emits `.sunderpkg` in the publish directory.

## Current Docs

- `docs/SUNDER-PACKAGE-STANDARD.md`: package metadata, generated manifest, dev output, and package archive shape.
- `docs/SUNDER-PACKAGE-DEVELOPMENT.md`: package author workflow from template to publish.
- `docs/SUNDER-CLI.md`: implemented CLI command reference.
- `docs/SUNDER-APP.md`: desktop app behavior, dev arguments, package icons, and theme/branding notes.
