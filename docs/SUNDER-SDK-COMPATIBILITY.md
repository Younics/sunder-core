# Sunder SDK Compatibility

This document defines how Sunder keeps independently installed Apps/Hosts and packages compatible as the SDK evolves.

## Compatibility Boundary

The compatibility boundary is the generated package manifest, `Sunder.Sdk` public contracts, and Host support for those contracts. The Runtime Host must validate SDK compatibility before loading a package assembly because packages use the Host-bundled `Sunder.Sdk.dll`.

## Version Fields

- `manifestVersion`: package manifest/archive schema version. This is not the SDK API version.
- `sdkApiVersion`: broad SDK activation generation. Current value is `1`.
- `sdkPackageVersion`: informational `Sunder.Sdk` package/build version used by `Sunder.Package.Build`.
- `requiredSdkCapabilities`: granular Host-required SDK features inferred from SDK contract usage.

Missing SDK compatibility metadata is treated as legacy SDK API `1` so older generated packages can still load on compatible Hosts.

## Compatibility Rules

- Existing shipped public SDK contracts must not be broken in place.
- Current `Sunder.Sdk.*` contracts are SDK API `1`.
- Breaking changes are added through new contracts/capabilities, not by changing existing contracts.
- Version only the contract area that changes; do not version the whole SDK namespace by default.
- A new Host may support multiple capability generations side by side.
- An old Host must reject unsupported package SDK requirements before assembly load.

## Capabilities

Current SDK capabilities are:

| Capability | SDK Surface |
| --- | --- |
| `core.v1` | `ISunderPackageModule`, `IPackageContext` |
| `packaging.v1` | package identity and dependency attributes |
| `contributions.v1` | `IPackageContributionRegistry` |
| `views.v1` | package view registration and placement |
| `settings-views.v1` | settings view registration |
| `workspaces.v1` | package view/workspace factories |
| `background-services.v1` | package background services |
| `background-processes.v1` | queued background process API, progress reporting, cancellation, and indicator placement |
| `extensions.v1` | extension points, contribution registration, extension catalog queries |
| `extensions.changes.v1` | extension catalog change monitoring |
| `configuration.schema.v1` | package configuration schema contracts |
| `configuration.values.v1` | package configuration value access |
| `storage.v1` | package storage/file/key-value abstractions |
| `secrets.v1` | package secret storage abstraction |
| `logging.v1` | package logging abstractions |
| `notifications.v1` | package notifications |
| `shell-view.v1` | shell view/hotbar/navigation services |
| `callbacks.v1` | generic callback sessions |
| `auth.v1` | auth status/disconnect integration |
| `theming.v1` | semantic Sunder theme keys |

`Sunder.Package.Build` infers required capabilities automatically by scanning SDK contract usage in the compiled package assembly. Package authors should not normally author these fields by hand.

Manual MSBuild capability entries are reserved for unusual dynamic/reflection scenarios:

```xml
<ItemGroup>
  <SunderSdkCapability Include="callbacks.v1" />
</ItemGroup>
```

## Callback And Auth

`callbacks.v1` is the generic browser/local callback-session capability. It is not auth-specific.

`auth.v1` is only for auth-specific Host/App integration, including status and disconnect behavior. OAuth packages normally require both `callbacks.v1` and `auth.v1`; non-auth callback packages require only `callbacks.v1`.

## Extension Catalog Changes

Use `IPackageExtensionCatalogMonitor` for structured extension catalog changes. It exposes `Changed` with `PackageExtensionCatalogChangedEventArgs` including revision, reason, and per-extension-point additions/removals.

`IPackageExtensionCatalogChangeNotifier` remains the simple compatibility invalidation contract.

## Future Breaking Changes

When a breaking SDK change is needed:

1. Add a new contract or namespace for the changed area, for example `callbacks.v2`.
2. Add a new capability constant and annotate the new contract surface.
3. Keep the previous capability working where practical.
4. Update the Host compatibility profile to declare support.
5. Let `Sunder.Package.Build` infer the new capability from SDK usage.
6. Deprecate old capabilities through docs/warnings before removal.

The Host error for unsupported requirements must be explicit, for example:

```text
Package 'example.package' requires SDK capability 'callbacks.v2', but this Sunder Host does not support it.
```
