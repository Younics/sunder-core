# Package Anatomy

> **Applies to:** Sunder SDK `1.1.x`, package manifest V3, .NET 10, and Runtime protocol revision 8.

A Sunder package is published as one versioned universal unit containing shared content and zero or more exact App/Runtime RID targets. Registry and local-file installation derive a strict shared-plus-current-RID installed projection while retaining package-wide metadata and canonical source provenance. App and Runtime run in separate processes and never share package object instances.

## Target Roles

| Role | Activation | Owns |
| --- | --- | --- |
| Runtime | Exact-RID Worker V3 executable | Headless work, settings schemas, callbacks/auth, owner/public RPC providers, and persistent Runtime data. |
| App | `ISunderAppPackageModule` | Avalonia views/settings views, shell integration, notifications, and App background processes. |

Each App entry assembly contains at most one top-level public, non-abstract, non-generic module with a public parameterless constructor. Build tooling infers App targets from that compiled metadata. Each Runtime leaf explicitly declares one exact-RID `worker` target and packages a complete self-contained executable closure. A shared-only package contains RPC descriptor bundles and no executable target.

The worker owns its process-local object graph and lifecycle; Sunder does not load a Runtime package assembly or construct package-owned Runtime DI. Never use static state to communicate between App and Runtime.

## Process Boundary

```text
Package App role
  runs in Sunder.App
       |
       | authenticated owner RPC and package data APIs
       v
Package Worker V3 Runtime role
  runs as a supervised child process
```

Use owner RPC unary methods for commands and queries, owner RPC server streams for ordered live events, and Runtime-owned package storage for durable data. Do not send local filesystem paths across the boundary; App and Runtime do not assume a shared filesystem.

## Capability Availability

| Capability | Runtime activation | App activation |
| --- | --- | --- |
| `ContentRootPath` | Read-only Runtime shadow content. | Read-only generation-owned App snapshot. |
| `Storage.State`, `Storage.Files` | Direct Runtime-owned persistence. | Authenticated proxy to the active Runtime role. |
| `Settings`, `Secrets` | Direct Runtime-owned persistence. | Authenticated proxy to the active Runtime role. |
| `Storage.RoleLocalWorkspace` | Runtime-local persistent workspace. | Separate App-local persistent workspace. |
| `Logging` | Persistent Runtime package logs. | App session log. |
| `ISunderRpcClient` | Available after activation for declared Runtime imports. | Available after publication for declared App imports, including owner RPC. |
| `IPackageCallbackClient` | Unavailable (`IsAvailable == false`). | Available only after publication; handlers run in Runtime. |
| Views and settings views | Not available. | Registered through `Sunder.Sdk.Avalonia`. |
| Settings schemas and package lifecycle | Advertised by `SunderWorkerOptions`; worker lifecycle callbacks run in-process. | Not available. |

An App-only package can use its App role-local workspace and App logging, but Runtime-backed state, files, settings, secrets, owner RPC, and callbacks require an active Runtime role for that package. Packages needing durable shared data should include both roles.

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
  MyPackage.Worker/
    Program.cs
  MyPackage.App/
    PackageModule.cs
```

The generated `*.Protocol` project is non-packable and package-local. It contains bundled descriptors plus generated DTO, client, and provider adapters; Worker and App consume it through private project references. Keep Avalonia types out of Worker code. The aggregate project builds one self-contained Worker leaf per exact RID and combines validated leaves into one universal package.

The compiled [quickstart package](../../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/) demonstrates the Worker, App, Protocol, and Aggregate topology with a generated owner RPC binding.

## Package Dependencies Versus Protocol Helpers

These solve different problems:

- `[assembly: SunderPackageDependency(...)]` says another Sunder package must be installed and ready at Runtime. Runtime validates versions and activates dependencies before dependents.
- Package-local protocol source supplies DTOs, generated bindings, and adapters used by this package's targets. A coordinated package family may also distribute a helper NuGet package, but that is a build-time convenience only.

Cross-package Runtime behavior is defined by a language-neutral RPC descriptor bundled into every package that provides or consumes it. CLR types are never shared between package processes or App load contexts. A consumer that requires a specific provider package also declares a Sunder runtime dependency on that provider.

Only assemblies on an explicit Host allowlist use Host-provided type identity. Package-authored assemblies always remain activation-local.

## Target Selection

Runtime keeps packages without a Runtime target in dependency readiness and snapshot planning but creates no Runtime provider or worker process for them. App activates only the selected exact App target. Shared-only packages have no executable activation.

## Boundaries To Keep

- Reference `Sunder.Sdk.Worker` from Worker leaves and `Sunder.Sdk` from App or Protocol projects; opt into `Sunder.Sdk.Avalonia` and `Sunder.Sdk.Stacks` only when used.
- Reference `Sunder.Package.Build` as `PrivateAssets="all"`.
- Do not reference `Sunder.App`, `Sunder.Runtime.Host`, `Sunder.Runtime.Contracts`, or other host implementation projects.
- Treat `ContentRootPath` and installed package files as read-only.
- Do not maintain `sunder-package.json` in source.
- Do not treat collectible load contexts as a security sandbox. See [Package Trust And Security](SECURITY.md).

The [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md) is the normative source for identity, exact targets, layered payloads, projections, RPC metadata, generated output, and archive rules.
