# Sunder Core -- AI Agent Rules

This repository contains the public Sunder core projects: the desktop app, local runtime host, CLI, package SDK, package build tooling, package template, package archive validation, and public Registry DTO contracts.

## Project Map

- `languages/csharp/dotnet/host/Sunder.App` -- Avalonia desktop shell, package UI activation, marketplace/install UX.
- `languages/csharp/dotnet/host/Sunder.Runtime.Host` -- local installed package state, package validation/install/update/uninstall, runtime activation, local HTTP API.
- `languages/csharp/dotnet/host/Sunder.Cli` -- thin command-line client over Registry and runtime APIs.
- `languages/csharp/dotnet/host/Sunder.Runtime.Contracts` -- host-neutral DTOs for app/CLI/runtime communication.
- `languages/csharp/dotnet/packages/Sunder.Sdk` -- public package author contracts and abstractions.
- `languages/csharp/dotnet/packages/Sunder.Sdk.Avalonia` -- optional Avalonia package UI contracts and Sunder theme resources.
- `languages/csharp/dotnet/packages/Sunder.Sdk.Worker` -- standalone .NET worker process protocol and authoring runtime.
- `languages/csharp/dotnet/packages/Sunder.Package.Build` -- MSBuild targets/tasks for generated manifests, `sunder-dev`, and `.sunderpkg` archives.
- `languages/csharp/dotnet/packages/Sunder.Package.Templates` -- `dotnet new sunder-package` template.
- `languages/csharp/dotnet/libraries/Sunder.Package.Format` -- shared package archive inspection and validation used by core and private Registry code.
- `languages/csharp/dotnet/libraries/Sunder.Registry.Contracts` -- public Registry API DTO contracts used by app, CLI, Registry web, and Registry server.
- `languages/typescript/node` -- TypeScript SDK, Node package tooling, templates, and SEA fixtures.

## Boundaries

- `Sunder.App` owns Avalonia shell UI, app-side package activation, package view caching, package icon loading, and app-side faults.
- `Sunder.Runtime.Host` owns installed package records, Registry credentials and authenticated calls, package validation/install/update/uninstall, runtime activation, configuration/secrets/auth callbacks, and runtime faults.
- `Sunder.Cli` should stay a thin client over Registry and runtime APIs.
- `Sunder.Runtime.Contracts` contains dependency-free DTOs only; do not put host implementation or persistence code there.
- `Sunder.Sdk` contains package author contracts only; do not put host implementation details there.
- `Sunder.Package.Build` owns build-time package output behavior; do not duplicate manifest/dev-output/archive logic elsewhere.
- `Sunder.Package.Format` owns archive validation and inspection.

## Public NuGet Surface

The public developer packages are:

- `Sunder.Sdk`
- `Sunder.Sdk.Avalonia`
- `Sunder.Sdk.Stacks`
- `Sunder.Sdk.Worker`
- `Sunder.Package.Build`
- `Sunder.Package.Templates`

Do not publish `Sunder.Runtime.Contracts`, `Sunder.Package.Format`, or `Sunder.Registry.Contracts` as public NuGet packages unless there is a concrete external-consumer requirement.

## Avalonia Guidance

- Use Avalonia-native patterns and compiled bindings where the project already uses them.
- Preserve the graphite/gray Sunder visual language with subtle amber accents unless a task explicitly changes branding.
- Use `app.ico` for executable/window/taskbar icons and `logo.png` for in-app Sunder branding badges.
- Package image icons render directly; rounded dark containers are for glyph fallback only.
- Package icon load failures should be logged to `AppSessionLog`, not surfaced as package UI errors.
- Package UI should use semantic Sunder theme resources for shell-sensitive surfaces/text.

## Build And Test

Primary build:

```powershell
dotnet build Sunder.Core.slnx --no-restore
```

Useful targeted tests:

- `dotnet test languages/csharp/dotnet/tests/Sunder.App.Tests/Sunder.App.Tests.csproj --no-restore`
- `dotnet test languages/csharp/dotnet/tests/Sunder.Runtime.Host.Tests/Sunder.Runtime.Host.Tests.csproj --no-restore`
- `dotnet test languages/csharp/dotnet/tests/Sunder.Package.Format.Tests/Sunder.Package.Format.Tests.csproj --no-restore`

If a running app/runtime locks normal build outputs on Windows, build the affected project to alternate output and intermediate paths.
