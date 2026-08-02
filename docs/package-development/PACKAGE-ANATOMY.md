# Package Anatomy

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, .NET 10, and Runtime protocol revision 3.

A Sunder package is one installed and versioned universal unit containing shared content and zero or more exact App/Runtime RID targets. App and Runtime run in separate processes and never share package object instances.

## Target Roles

| Role | Module | Owns |
| --- | --- | --- |
| Runtime | `ISunderRuntimePackageModule` | Headless services, background services, settings schemas, callbacks/auth, typed operations/streams, and RPC providers. |
| App | `ISunderAppPackageModule` | Avalonia views/settings views, shell integration, notifications, and App background processes. |

Each managed target entry assembly contains at most one top-level public, non-abstract, non-generic module for its role, with a public parameterless constructor. Build tooling infers exact targets from compiled module metadata and emits one target per selected RID. A shared-only package contains RPC descriptor bundles and no executable target.

When one class implements both interfaces, each host still creates a different module instance, service provider, load context, and lifecycle. Never use instance or static state to communicate between roles.

## Process Boundary

```text
Package App role
  runs in Sunder.App
       |
       | authenticated, package-scoped Runtime operations/data APIs
       v
Package Runtime role
  runs in Sunder.Runtime.Host
```

Use typed Runtime operations for commands and queries, typed Runtime streams for ordered live events, and Runtime-owned package storage for durable data. Do not send local filesystem paths across the boundary; App and Runtime do not assume a shared filesystem.

## Capability Availability

| Capability | Runtime activation | App activation |
| --- | --- | --- |
| `ContentRootPath` | Read-only Runtime shadow content. | Read-only generation-owned App snapshot. |
| `Storage.State`, `Storage.Files` | Direct Runtime-owned persistence. | Authenticated proxy to the active Runtime role. |
| `Settings`, `Secrets` | Direct Runtime-owned persistence. | Authenticated proxy to the active Runtime role. |
| `Storage.RoleLocalWorkspace` | Runtime-local persistent workspace. | Separate App-local persistent workspace. |
| `Logging` | Persistent Runtime package logs. | App session log. |
| `IPackageRuntimeClient` | Unavailable (`IsAvailable == false`). | Available only after the App generation is published. |
| `IPackageCallbackClient` | Unavailable (`IsAvailable == false`). | Available only after publication; handlers run in Runtime. |
| Views and settings views | Not available. | Registered through `Sunder.Sdk.Avalonia`. |
| Settings schemas and package background services | Registered in Runtime. | Not available. |

An App-only package can use its App role-local workspace and App logging, but Runtime-backed state, files, settings, secrets, operations, and callbacks require an active Runtime role for that package. Packages needing durable shared data should implement both roles.

All `IPackageContext` capabilities are scoped to one activation and must not escape it. Host capability implementations are thread-safe unless a member explicitly says otherwise; that guarantee does not make package services, returned Avalonia controls, streams, or caller-owned collections thread-safe. Follow each member's ownership/threading contract and marshal control access to the App dispatcher. `RoleLocalWorkspace` is host-owned; package code must not dispose it.

## Typical Aggregate Project

```text
MyPackage/
  MyPackage.csproj
  PackageMetadata.cs
  Assets/
  MyPackage.Protocol/
    Contracts/
    Generated/
  MyPackage.Runtime/
    PackageModule.cs
  MyPackage.App/
    PackageModule.cs
```

The generated `*.Protocol` project is non-packable and package-local. It contains bundled descriptors plus generated DTO, client, and provider adapters; Runtime and App consume it through private project references. Keep Avalonia types out of Runtime-only code. The aggregate project combines validated leaves into one universal package.

The compiled [quickstart package](../samples/Sunder.Package.Quickstart/) demonstrates one assembly with separate Runtime and App registrations.

## Package Dependencies Versus Protocol Helpers

These solve different problems:

- `[assembly: SunderPackageDependency(...)]` says another Sunder package must be installed and ready at Runtime. Runtime validates versions and activates dependencies before dependents.
- Package-local protocol source supplies DTOs, generated bindings, and adapters used by this package's targets. A coordinated package family may also distribute a helper NuGet package, but that is a build-time convenience only.

Cross-package Runtime behavior is defined by a language-neutral RPC descriptor bundled into every package that provides or consumes it. CLR types are never shared between package load contexts. A consumer that requires a specific provider package also declares a Sunder runtime dependency on that provider.

Only assemblies on an explicit Host allowlist use Host-provided type identity. Package-authored assemblies always remain activation-local.

## Target Selection

Runtime keeps packages without a Runtime target in dependency readiness and snapshot planning but creates no Runtime module, provider, process, or collectible load context for them. App activates only the selected exact App target. Shared-only packages have no executable activation.

## Boundaries To Keep

- Reference `Sunder.Sdk`; opt into `Sunder.Sdk.Avalonia` and `Sunder.Sdk.Stacks` only when used.
- Reference `Sunder.Package.Build` as `PrivateAssets="all"`.
- Do not reference `Sunder.App`, `Sunder.Runtime.Host`, `Sunder.Runtime.Contracts`, or other host implementation projects.
- Treat `ContentRootPath` and installed package files as read-only.
- Do not maintain `sunder-package.json` in source.
- Do not treat collectible load contexts as a security sandbox. See [Package Trust And Security](SECURITY.md).

The [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md) is the normative source for identity, exact targets, layered payloads, projections, RPC metadata, generated output, and archive rules.
