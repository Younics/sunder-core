# Package Anatomy

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, .NET 10, and Runtime protocol revision 3.

A Sunder package is one installed and versioned unit, but it may contain two independently activated roles. App and Runtime run in separate processes and never share package object instances.

## The Two Roles

| Role | Module | Owns |
| --- | --- | --- |
| Runtime | `ISunderRuntimePackageModule` | Headless services, background services, settings schemas, callbacks/auth, typed operations/streams, and Runtime extension contributions. |
| App | `ISunderAppPackageModule` | Avalonia views/settings views, shell integration, notifications, App background processes, and App extension contributions. |

An entry assembly may contain one Runtime module, one App module, one class implementing both interfaces, or no module for a contract-only package. Build tooling infers exact `hostRoles` from compiled metadata. Each declared role permits at most one top-level public, non-abstract, non-generic implementation with a public parameterless constructor.

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

All `IPackageContext` capabilities are scoped to one activation and must not escape it. Capability implementations are thread-safe unless their member documentation states otherwise. `RoleLocalWorkspace` is host-owned; package code must not dispose it.

## Typical Project

```text
MyPackage/
  MyPackage.csproj
  PackageMetadata.cs
  PackageModule.cs
  Runtime/
  App/
  Assets/
```

Keep shared request/response records and extension contracts host-neutral. Keep Avalonia types out of code loaded exclusively by Runtime. A single project is supported, but folders or companion authored projects help prevent accidental cross-role coupling.

The compiled [quickstart package](../samples/Sunder.Package.Quickstart/) demonstrates one assembly with separate Runtime and App registrations.

## Package Dependencies Versus Contracts

These solve different problems:

- `[assembly: SunderPackageDependency(...)]` says another Sunder package must be installed and ready at Runtime. Runtime validates versions and activates dependencies before dependents.
- A NuGet `PackageReference` to `*.Contracts` supplies compile-time types such as extension points and interfaces.

Typed extension contracts should live in a separately versioned `*.Contracts` NuGet package. The implementation package depends on those contracts, and extension packages normally need both the contracts NuGet reference and a Sunder runtime dependency on the host package.

Session-wide contract sharing is limited to assemblies whose simple names end in `.Contracts`. Matching includes name, culture, and public-key token; substitution remains within a major assembly version and never substitutes an older assembly. Unsigned assemblies claiming the same identity must be byte-identical.

## Role Pruning

Runtime keeps App-only and contract-only packages in dependency readiness and snapshot planning, but does not create a Runtime module, provider, or collectible package load context for them. App activates only App modules and materializes the contract dependencies required by the App dependency closure. A Runtime-only package has no App module or view.

## Boundaries To Keep

- Reference `Sunder.Sdk`; opt into `Sunder.Sdk.Avalonia` and `Sunder.Sdk.Stacks` only when used.
- Reference `Sunder.Package.Build` as `PrivateAssets="all"`.
- Do not reference `Sunder.App`, `Sunder.Runtime.Host`, `Sunder.Runtime.Contracts`, or other host implementation projects.
- Treat `ContentRootPath` and installed package files as read-only.
- Do not maintain `sunder-package.json` in source.
- Do not treat collectible load contexts as a security sandbox. See [Package Trust And Security](SECURITY.md).

The [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md) is the normative source for identity, metadata, host-role, generated-output, and archive rules.
