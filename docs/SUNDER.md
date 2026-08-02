# Sunder

Sunder Core is the public core of the Sunder local-first package platform. It contains the Avalonia desktop shell, current-user Host gateway (`Sunder.Host.Supervisor`), nested Runtime worker (`Sunder.Runtime.Host`), CLI, package SDK/build pipeline, package template, package archive validation, and public Registry DTO contracts.

The canonical package model is authoring-tool neutral. Current presets cover managed .NET Runtime targets, Avalonia App targets, Node process Runtime targets, and framework-agnostic web App targets with React/Vite as one template. Python, Rust, Go, other process toolchains, other .NET UI approaches, and other web frameworks are valid future authoring routes when they emit the canonical package format and target a kind/protocol the Host supports; a new toolchain does not create a new runtime package type.

This document is the canonical current-state project map and architecture overview. In current documentation, **Host** or **Supervisor** means the public current-user gateway, **Runtime worker** means the nested `Sunder.Runtime.Host` process, and **standalone Runtime** means the direct-loopback development fallback. Package authoring, CLI commands, app development arguments, and Registry behavior are documented in the sibling Sunder docs.

## Project Map

Primary Sunder Core projects:

| Area | Project | Responsibility |
| --- | --- | --- |
| Desktop app | `src/Host/Sunder.App` | Avalonia shell, package UI activation, package marketplace/install UX |
| Current-user Host (Supervisor) | `src/Host/Sunder.Host.Supervisor` | Stable authenticated loopback API, durable Runtime worker lifecycle, private worker gateway |
| Host client/contracts | `src/Host/Sunder.Host.Client`, `src/Host/Sunder.Host.Contracts` | Host discovery, credentials, protocol, and lifecycle DTOs |
| Runtime worker | `src/Host/Sunder.Runtime.Host` | Nested local package state, package install/update/uninstall, runtime activation, and private worker API; direct loopback only as a standalone development fallback |
| CLI | `src/Host/Sunder.Cli` | Registry browse/install/publish commands and current-user Host-gatewayed Runtime worker commands |
| SDK | `src/Sdk/Sunder.Sdk` | Public package contracts, package module API, package context, theme keys |
| Stack SDK | `src/Sdk/Sunder.Sdk.Stacks` | Optional public Stack import/export and contributor contracts |
| Avalonia SDK | `src/Sdk/Sunder.Sdk.Avalonia` | Optional App role, Avalonia contribution contracts, and themes |
| Build tooling | `src/Sdk/Sunder.Package.Build` | MSBuild targets/tasks for manifests, dev output, and `.sunderpkg` archives |
| Templates | `src/Sdk/Sunder.Package.Templates` | `dotnet new sunder-package` template |
| Package format | `src/Sunder.Package.Format` | Shared `.sunderpkg` archive inspection and validation |
| Registry contracts | `src/Sunder.Registry.Contracts` | Public DTOs and API contracts used by CLI/app/web/server |

First-party Agent packages live in the separate public `Younics/sunder-agent-package` repository. Registry implementation projects live in the separate private `Younics/sunder-registry` repository.

## Runtime Shape

Sunder has one installable runtime extension unit: `Package`.

Important related concepts:

| Concept | Current meaning |
| --- | --- |
| Package | Runtime unit installed, loaded, enabled, disabled, updated, and uninstalled by the Runtime worker (`Sunder.Runtime.Host`) |
| Dev package | Unpacked `sunder-dev` build output used for local package development |
| `.sunderpkg` | Distributable package archive produced from the dev output |
| Protocol helper package | NuGet package that distributes RPC descriptors and optional compile-time DTOs, bindings, or package-local adapters |
| Bundle | Registry install recipe that points to multiple packages, not a runtime package kind |
| Theme | App-side UI styling data, not managed by the Runtime worker as a runtime package |

## App, Host, And Runtime Worker Boundary

`Sunder.App`, the current-user Host gateway (`Sunder.Host.Supervisor`), and its nested Runtime worker (`Sunder.Runtime.Host`) are separate processes. The App stages and launches the versioned Host payload for the current user without installing a machine service or requiring elevation. The Host may remain alive after the App UI closes and owns:

- the stable authenticated loopback endpoint and per-user Host identity
- durable Runtime desired state and lifecycle operations
- nested Runtime worker startup, crash recovery, and shutdown
- a private per-worker Unix socket or current-user-only named pipe
- credential separation and selective forwarding of Runtime APIs

The nested Runtime worker (`Sunder.Runtime.Host`) owns:

- installed package records
- package archive validation
- package graph activation
- runtime services and background services
- package configuration, secrets, auth callbacks, and runtime faults
- package asset serving for active or installed packages
- dev-package watching, debounce/stability checks, and generation-fenced reloads
- package-log discovery, bounded parsing, snapshots, and live streaming

`Sunder.App` owns:

- Avalonia shell UI
- app-side package view activation
- package workspace view caching
- settings views and view placement
- desktop notifications and client-local app-side fault containment
- visual theme resources and app branding

App and CLI Runtime-worker requests normally pass through the public current-user Host gateway. The Host remains reachable while the worker is stopped or replaced and returns typed unavailability responses instead of exposing the private worker endpoint. A standalone Runtime uses direct loopback only as a development fallback.

Development packages are coordinated by the Runtime worker. The Runtime worker validates and activates runtime contributions, owns directory watching and reload transactions, and publishes bounded sequence-based lifecycle events. The App observes session generations and activates app-side views/settings from authenticated UI snapshots without reading dev package or package-log directories.

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

The local Runtime worker owns:

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

For managed package development, `dotnet build` emits `sunder-dev`; the canonical `PackSunderPackage` target emits `.sunderpkg` beside the target output, and `dotnet publish` also emits an archive when normal publish output is required.

## Current Docs

- `docs/SUNDER-PACKAGE-STANDARD.md`: package metadata, generated manifest, dev output, and package archive shape.
- `docs/SUNDER-PACKAGE-DEVELOPMENT.md`: package author workflow from template to publish.
- `docs/SUNDER-CLI.md`: implemented CLI command reference.
- `docs/SUNDER-APP.md`: desktop app behavior, dev arguments, package icons, and theme/branding notes.
- `docs/SUNDER-HOST-SERVICE.md`: canonical current-user Host gateway launch, payload activation, state, distribution, and removal.

## Future Design Docs

- `docs/design/SUNDER-HOST-ARCHITECTURE.md`: proposed future Host architecture. It does not describe current behavior.
