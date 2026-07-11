# Sunder SDK Compatibility

This document defines the Sunder V1 compatibility contract for independently installed Apps/Hosts and packages.

## Compatibility Boundary

The compatibility boundary is the generated package manifest, public `Sunder.Sdk*` contract assemblies, and Host support for those contracts. The Runtime Host must validate SDK compatibility before loading a package assembly because packages use Host-bundled SDK contract assemblies.

## Version Fields

- `manifestVersion`: package manifest/archive schema version. This is not the SDK API version.
- `sdkApiVersion`: broad SDK activation generation. Current value is `1`.
- `sdkPackageVersion`: required strict SemVer 2.0 `Sunder.Sdk` package/build version used by `Sunder.Package.Build`.
- `requiredSdkCapabilities`: granular Host-required SDK features inferred from SDK contract usage.
- `sdkVersion`: optional SDK version metadata when supplied by build properties. Compatibility decisions use `sdkApiVersion` and `requiredSdkCapabilities`.

V1 is a clean format boundary: `sdkApiVersion` must be exactly `1`, `sdkPackageVersion` must be valid SemVer 2.0, and `requiredSdkCapabilities` must contain distinct V1-form ids. Missing compatibility metadata is rejected before assembly load.

## Compatibility Rules

- Existing shipped public SDK contracts must not be broken in place.
- Current `Sunder.Sdk.*` contracts are SDK API `1`.
- An old Host must reject unsupported package SDK requirements before assembly load.

## Capabilities

Current SDK capabilities are:

| Capability | SDK Surface |
| --- | --- |
| `core.v1` | `ISunderRuntimePackageModule`, `ISunderAppPackageModule`, `IPackageContext` |
| `packaging.v1` | package identity and dependency attributes |
| `contributions.v1` | `ISunderRuntimeContributionRegistry`, `ISunderAppContributionRegistry` |
| `views.v1` | package view registration and placement |
| `settings-views.v1` | settings view registration |
| `settings-navigation.v1` | package settings navigation service |
| `workspaces.v1` | package view/workspace factories |
| `background-services.v1` | package background services |
| `background-processes.v1` | queued background process API, progress reporting, cancellation, and indicator placement |
| `extensions.v1` | extension points, contribution registration, extension catalog queries |
| `extensions.changes.v1` | extension catalog change monitoring |
| `configuration.schema.v1` | package configuration schema contracts |
| `configuration.values.v1` | package configuration value access |
| `storage.v1` | package storage/file/key-value abstractions |
| `local-workspace.v1` | package-scoped local workspace leases |
| `secrets.v1` | package secret storage abstraction |
| `logging.v1` | package logging abstractions |
| `notifications.v1` | package notifications |
| `shell-view.v1` | shell view/hotbar/navigation services |
| `package-sessions.v1` | package session load/unload/status service |
| `stacks.v1` | `Sunder.Sdk.Stacks` Stack import/export data contracts |
| `stacks.contributions.v1` | `Sunder.Sdk.Stacks` Stack contributor extension contracts |
| `callbacks.v1` | generic callback sessions |
| `auth.v1` | auth status/disconnect integration |
| `theming.v1` | semantic Sunder theme keys |

`Sunder.Package.Build` infers required capabilities automatically by scanning SDK contract usage in the compiled package assembly. Capability annotations are recognized from the base `Sunder.Sdk` assembly and explicit `Sunder.Sdk.*` contract assemblies such as `Sunder.Sdk.Avalonia` and `Sunder.Sdk.Stacks`. Package authors should not normally author these fields by hand.

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
