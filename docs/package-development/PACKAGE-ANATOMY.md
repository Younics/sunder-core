# Package Anatomy

A package is one versioned `.sunderpkg` with shared content and exact role/RID
targets. A .NET author maintains one Library project; Sunder generates isolated
Worker and optional App assemblies. Source folders organize code, not authority.

```text
MyPackage/
  MyPackage.csproj
  PackageMetadata.cs
  Services/
  Views/                       # optional Avalonia source
  Stacks/                      # optional platform contributor
  Assets/
  sunder-contracts.lock.json
  sunder-contracts.owned.json   # when reviewing owned public APIs
```

`SunderPackageProject=true` enables generation. Build composes the current RID at
`bin/Debug/net10.0/sunder-dev`; direct `PackSunderPackage` combines all declared RIDs.
Internal `sunder-roles/<role>/<rid>` outputs are generated, not authored projects.
The [Quickstart](../../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/README.md)
and [single-project guide](SINGLE-PROJECT-AUTHORING.md) demonstrate this layout.

## Roles And Processes

| Role | Owns |
| --- | --- |
| Worker | Services, storage/secrets, settings schemas, callbacks/auth and background lifecycle |
| App | Views/view models, navigation, notifications and presentation-only state |
| Shared payload | Contract descriptors, portable content and shared package dependencies |

The Host launches the exact-RID framework-dependent Worker module using a verified
official runtime. The App loads only its selected App projection. Generated clients
cross the authenticated RPC boundary; mutable objects, static state and local paths
are not a communication channel between roles.

Registration does not construct views or execute package methods. Generated
constructor composition owns service lifetimes and disposal. App code injects a
generated service interface, not the concrete Worker implementation.

## Capability Availability

| Capability | Worker | App |
| --- | --- | --- |
| Content root | Read-only selected package content | Read-only generation snapshot |
| State, files, settings, secrets | Runtime-owned persistence | Authenticated proxy to its active Worker |
| Role-local workspace | Worker-local workspace | Separate App-local workspace |
| Logging | Persistent Runtime package log | App session log |
| Generated service clients | Declared Runtime use after activation | Declared App use after publication |
| Callback client | Unavailable | Calls handlers owned by the Worker |
| Views | Unavailable | Avalonia contribution registrations |

An App-only package can use its App-local workspace and logging. Runtime-backed
data/callbacks require its active Worker. Capabilities are activation-scoped and
must not escape that lifetime. Host capability implementations are thread-safe
unless documented otherwise; package services, controls, streams and collections
still need their own concurrency/ownership discipline.

## Dependencies And Contracts

Select another package's public API or a provider contract through the package
tooling. The reviewed lock pins its exact descriptor/model authority and generates
the required runtime dependency. Do not duplicate that dependency with an attribute.
Ordinary non-API package dependencies may use `[assembly: SunderPackageDependency(...)]`.

Importers consume the authority package's generated `_Services` and `_Models`
namespaces. CLR implementation assemblies are not shared across packages; Host
allowlisted SDK types are the exception. Declaration-only interfaces remain useful
for extension points without an owned implementation.

## Package Boundaries

- Reference coordinated Sunder SDKs and `Sunder.Package.Build` (`PrivateAssets="all"`).
- Keep backend/native dependencies in Worker ownership and Avalonia in App ownership.
- Use explicit role metadata only for ambiguous helper/dependency ownership.
- Treat installed content as read-only; use package storage for mutable data.
- Do not author generated manifests, transport bindings or separate role projects.
- Package projects do not reference App/Host implementation assemblies.
- Collectible load contexts are not an OS security sandbox.

The [package standard](../SUNDER-PACKAGE-STANDARD.md) owns archive/target/projection
rules; [security](SECURITY.md) owns the trust and containment model.
