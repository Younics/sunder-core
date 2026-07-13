# Sunder Package Development

This is the package-author manual for creating, debugging, packaging, validating, and publishing Sunder packages.

See [Sunder SDK Compatibility](SUNDER-SDK-COMPATIBILITY.md) for Host/SDK/package versioning rules. `Sunder.Package.Build` generates SDK compatibility metadata automatically from SDK usage.

## Prerequisites

- .NET 10 SDK.
- Sunder SDK packages available from the configured package feed or local source build.
- `Sunder.Package.Templates` installed as a `dotnet new` template package.
- A Sunder desktop app install or source build for development loading.
- A Sunder CLI build/install when validating or publishing from the command line.

Install the template from a local template package:

```powershell
dotnet new install .\path\to\Sunder.Package.Templates.1.1.0.nupkg
```

Install the template from a feed when published:

```powershell
dotnet new install Sunder.Package.Templates
```

## Create A Package

Create a headless Runtime package:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
```

Create package files directly in the specified output folder:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --createInPlace --output .\MyPackage
```

Create a package with an Avalonia App view:

```powershell
dotnet new sunder-package --name MyUiPackage --packageId my.company.ui --packageName "My UI Package" --withAvalonia
```

Create a package that exposes contracts:

```powershell
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --withContracts
```

Create an extension package that depends on a host package:

```powershell
dotnet new sunder-package --name MyExtension --packageId my.company.extension --packageName "My Extension" --withHostDependency --hostPackageId sunder.package.agent
```

Create an extension package that also references a host contracts NuGet package:

```powershell
dotnet new sunder-package --name MyTypedExtension --packageId my.company.typedextension --packageName "My Typed Extension" --withHostContracts --hostPackageId sunder.package.agent --hostContractsPackageId Sunder.Package.Agent.Contracts --hostContractsVersion <host-contracts-version>
```

Template options:

| Option | Meaning |
| --- | --- |
| `--packageId <id>` | Required runtime package id written into generated metadata |
| `--packageName <name>` | Required display name written into generated metadata and starter view |
| `--withAvalonia` | Adds exact V1 Avalonia SDK references and a default App package view |
| `--withStacks` | Adds the exact V1 Stack SDK reference and a Runtime Stack contributor example |
| `--withContracts` | Adds a `*.Contracts` project for public extension points |
| `--createInPlace` | Creates package files directly in the specified output folder instead of under a child project folder |
| `--withHostDependency` | Adds runtime dependency metadata for another package |
| `--hostPackageId <id>` | Required with `--withHostDependency` or `--withHostContracts`; runtime package id that this package depends on |
| `--withHostContracts` | Adds host dependency metadata, a NuGet reference to the host package's contracts package, and a compile-safe extension stub |
| `--hostContractsPackageId <id>` | Required with `--withHostContracts`; NuGet package id for host contracts |
| `--hostContractsVersion <version>` | Required with `--withHostContracts`; NuGet package version for host contracts |

## Project Files

Generated package projects reference the coordinated `1.1.0` versions of `Sunder.Sdk` and `Sunder.Package.Build`. Avalonia and Stack contracts are exact-version opt-ins.

Current generated package project shape:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Sunder.Sdk" Version="1.1.0" />
    <PackageReference Include="Sunder.Package.Build" Version="1.1.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Rules:

- Package projects target `net10.0`.
- Package projects reference `Sunder.Sdk`.
- Package projects use `Sunder.Package.Build` for generated output.
- UI packages reference Avalonia libraries they directly use.
- Package projects do not reference `Sunder.App`.
- Package projects do not reference `Sunder.Runtime.Host`.
- Extension packages depend on host contracts packages when they need typed host extension APIs.

## Sunder SDK Overview

`Sunder.Sdk` contains the headless Runtime/App lifecycle and extension contracts. `Sunder.Sdk.Avalonia` is an explicit opt-in for packages that register Avalonia views, settings, workspaces, or theme resources. Neither package requires references to Host implementation projects.

SDK areas:

| Area | Primary Types | What It Provides |
| --- | --- | --- |
| Packaging | `PackageId`, `SemanticVersion`, `PackageVersionRange`, package attributes | Canonical identity/version validation and runtime package dependencies |
| Runtime lifecycle | `ISunderRuntimePackageModule` | Headless Runtime service and contribution registration |
| App lifecycle | `ISunderAppPackageModule` | App-side service and Avalonia contribution registration |
| Package context | `IPackageContext` | Package id, version, install path, storage, settings, secrets, logging |
| Runtime contributions | `ISunderRuntimeContributionRegistry` | Background services, Runtime extensions, configuration schemas |
| App contributions | `ISunderAppContributionRegistry` | App-side extensions; `Sunder.Sdk.Avalonia` adds view/settings registration extensions |
| Views | `PackageViewRegistration`, `PackageViewPlacement` | Shell-visible Avalonia package views |
| Workspaces | `IPackageWorkspaceFactory` | Factory-created package workspaces/views |
| Extensions | `PackageExtensionPoint<T>`, `IPackageExtensionCatalog` | Typed package-to-package contribution points and active contribution discovery |
| Extension changes | `IPackageExtensionCatalogMonitor` | Structured change events when packages activate, deactivate, install, update, or fault |
| Settings | `PackageConfigurationSchema`, `IPackageSettings` | Host-rendered schema and validated writable settings, stored separately from state |
| Storage | `IPackageStorageContext`, `IPackageFileStore`, `IPackageKeyValueStore` | Package-scoped mutable files and key-value state |
| Role-local workspace | `IPackageRoleLocalWorkspace` | Activation-owned paths for the current App or Runtime role; callers do not dispose it |
| Secrets | `IPackageSecrets` | Package-scoped secret values |
| Logging | `IPackageLogging`, `IPackageEventLogger` | Package event logging and `ILoggerFactory` access |
| Notifications | `IPackageNotificationService` | User-visible package notifications |
| Background processes | `IBackgroundProcessQueue`, `BackgroundProcessRequest` | Host-visible queued work with progress, cancellation, and indicator placement |
| Shell integration | `IPackageShellViewService`, `IPackageViewNavigationTarget` | Shell navigation and hotbar/workspace integration |
| Settings navigation | `IPackageSettingsNavigationService` | Open global settings or another package's settings when the host supports it |
| Installed sessions | `IPackageInstalledSessionControl` | Load, unload, and query installed package ids |
| Development sessions | `IPackageDevelopmentSessionControl` | Optional host-local development loading with explicit availability and structured outcomes |
| Callbacks | `IPackageCallbackHandler` | Generic browser/local callback sessions |
| Auth | `IPackageAuthHandler` | Auth status and disconnect integration |
| Theme resources | `SunderThemeKeys` | Semantic resource keys for package UI |
| Compatibility | `SunderSdkCapabilities`, generated manifest fields | Host/package SDK compatibility metadata |

Minimal package module:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace MyCompany.Package;

public sealed class PackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddTransient<MyViewModel>();
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<MyView>(new PackageViewRegistration(
            id: "my.company.package.main",
            name: "My Package"));
    }
}
```

Register a Host-rendered configuration schema:

```csharp
using Sunder.Sdk.Configuration;

registry.RegisterConfigurationSchema(new PackageConfigurationSchema(
    PackageId: "my.company.package",
    PackageDisplayName: "My Package",
    Summary: "Package settings.",
    Sections:
    [
        new PackageConfigurationSection(
            SectionId: "general",
            Title: "General",
            Description: null,
            Fields:
            [
                new PackageConfigurationField(
                    Key: "enabled",
                    Label: "Enabled",
                    Kind: PackageConfigurationFieldKind.Boolean,
                    DefaultValue: "true")
            ])
    ]));
```

Use package-scoped settings, state, and secrets:

```csharp
var enabled = await context.Settings.GetValueAsync("enabled", cancellationToken);
await context.Settings.SetValueAsync("enabled", "false", cancellationToken);
await context.Storage.State.SetValueAsync("last-run", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
await context.Secrets.SetSecretAsync("api-key", apiKey, cancellationToken);
```

`Settings.GetValueAsync` returns the stored value or the schema default. Use `GetStoredValueAsync` when the distinction matters, and `DeleteValueAsync` to make the default effective again. Runtime validates settings against non-secret schema fields. `Storage.State` is opaque operational state and is never enumerated as settings; secret fields use `Secrets`.

Define and consume a typed extension point:

```csharp
public interface IMyProvider
{
    string ProviderId { get; }
}

public static class MyExtensionPoints
{
    public static readonly PackageExtensionPoint<IMyProvider> Providers = new("my.company.package:providers");
}

registry.RegisterExtension(
    MyExtensionPoints.Providers,
    services.GetRequiredService<MyProvider>());
```

Discover active extension contributions:

```csharp
var providers = extensionCatalog.GetExtensions(MyExtensionPoints.Providers);
foreach (var provider in providers)
{
    Console.WriteLine(provider.ProviderId);
}
```

Observe extension catalog changes when open UI or cached state depends on other packages:

```csharp
if (extensionCatalog is IPackageExtensionCatalogMonitor monitor)
{
    monitor.Changed += (_, args) =>
    {
        if (args.IncludesExtensionPoint(MyExtensionPoints.Providers.Id))
        {
            RefreshProviders();
        }
    };
}
```

Publish a package notification:

```csharp
await notificationService.PublishAsync(new PackageNotificationRequest(
    Title: "Import complete",
    Message: "Your package import finished successfully.",
    Severity: PackageNotificationSeverity.Success),
    cancellationToken);
```

Queue a background process for long-running package work:

```csharp
backgroundProcesses.Enqueue(new BackgroundProcessRequest(
    Title: "Pull model image",
    GroupKey: "my.company.package:model-pulls",
    Indicator: BackgroundProcessIndicator.Settings,
    ConcurrencyMode: BackgroundProcessConcurrencyMode.SequentialWithinGroup,
    CanCancel: true,
    ExecuteAsync: async context =>
    {
        context.ReportIndeterminate("Pulling model image...");
        await PullModelImageAsync(context.CancellationToken);
        context.ReportProgress(100, "Model image is ready.");
    },
    Metadata: new Dictionary<string, string>
    {
        ["image"] = "my-model:latest",
    }));
```

`BackgroundProcessIndicator.Hidden` keeps internal work out of all host footer indicators. Use `Main`, `Packages`, or `Settings` to show the process in exactly one host indicator surface. Use `GroupKey` only for concurrency, deduplication, and package-side listing.

Register callback and auth integration:

```csharp
services.AddSingleton<MyOAuthHandler>();
services.AddSingleton<IPackageCallbackHandler>(serviceProvider => serviceProvider.GetRequiredService<MyOAuthHandler>());
services.AddSingleton<IPackageAuthHandler>(serviceProvider => serviceProvider.GetRequiredService<MyOAuthHandler>());
```

`IPackageCallbackHandler` is generic Runtime callback/session handling. App package code starts and polls a registered handler through `IPackageContext.Callbacks`, passing at most 16 bounded string parameters. The returned status contains the host-launchable URI, terminal state, message, and expiry. Runtime and preflight contexts expose `Callbacks.IsAvailable == false`.

The Runtime owns the loopback listener, redirect URI, session identity, expiry, duplicate-callback rejection, and unload/shutdown cancellation. Packages must never start an `HttpListener`, `TcpListener`, Kestrel endpoint, or another callback listener. A handler can release in-flight provider work by implementing `CancelCallbackAsync`.

`IPackageAuthHandler` is the auth-specific status and disconnect projection over the same generic callback machinery. OAuth-style packages commonly use both; non-auth callback flows only need `IPackageCallbackHandler`.

## SDK Compatibility Metadata

`Sunder.Package.Build` generates SDK compatibility metadata into `sunder-package.json`. Package authors should not normally maintain this manually.

Generated compatibility fields:

| Field | Meaning |
| --- | --- |
| `sdkApiVersion` | Broad SDK activation generation, currently `1` |
| `sdkPackageVersion` | Informational `Sunder.Sdk` package/build version used at build time |
| `requiredSdkCapabilities` | Granular Host-required SDK capabilities inferred from SDK usage |
| `sdkVersion` | Optional SDK version metadata when supplied by build properties |

Example generated manifest fragment:

```json
{
  "sdkApiVersion": 1,
  "sdkPackageVersion": "1.1.0",
  "requiredSdkCapabilities": [
    "core.v1",
    "packaging.v1",
    "contributions.v1",
    "views.v1",
    "extensions.v1"
  ],
  "targetFramework": "net10.0"
}
```

The Runtime Host validates SDK compatibility before package activation. Older Hosts reject packages that require unsupported SDK API versions or capabilities with a clear compatibility error.

Capability inference fails closed when types/metadata cannot be inspected or package code uses reflection/dynamic access. In the latter case, declare every SDK capability reachable through that dynamic path explicitly:

```xml
<ItemGroup>
  <SunderSdkCapability Include="callbacks.v1" />
</ItemGroup>
```

See [Sunder SDK Compatibility](SUNDER-SDK-COMPATIBILITY.md) for the V1 Host/SDK/package rules.

## Package Metadata

Edit `PackageMetadata.cs` to define identity, summary, icon, and runtime dependencies.

Standalone package metadata:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Summary = "Adds a custom Sunder workspace.",
    Icon = "assets/icon.png")]
```

Extension package metadata:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.extension",
    Name = "My Extension",
    Summary = "Extends another Sunder package.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.1.0 <1.2.0")]
```

The package version is controlled by MSBuild properties such as `Version`.

## Package Module

An entry assembly may expose one Runtime role, one App role, or one class implementing both. Each host discovers and invokes only its own role.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;

namespace MyCompany.Package;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<MyRuntimeService>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterBackgroundService<MyRuntimeService>();
    }
}
```

Current module rules:

- Each role has at most one public, non-abstract implementation with a public parameterless constructor.
- Runtime services are registered in `ConfigureRuntimeServices`; App services are registered in `ConfigureAppServices`.
- Background services and configuration schemas are Runtime-only.
- Views and settings views are App-only and require `Sunder.Sdk.Avalonia`.
- Extensions are registered in the host catalog that consumes them; some packages intentionally register equivalent extension objects in both isolated host service providers.
- Extension catalog implementations must return canonical owner ids from `GetExtensionContributions`; ownerless fallback results are not valid V1 contributions.

Open package UI that depends on other packages should observe `IPackageExtensionCatalogMonitor.Changed` and refresh only when relevant extension points change. The event identifies the lifecycle reason and extension point additions/removals.

Use `IPackageCallbackHandler` for generic callback flows. Auth packages can also implement `IPackageAuthHandler` for auth-specific status and disconnect behavior.

## Package Context

`IPackageContext` gives the module host-provided package services.

Available context members:

- `PackageId`
- `Version`
- `InstallPath`
- `Storage`
- `Settings`
- `Secrets`
- `LoggerFactory`
- `Logging`

Use package settings for schema-declared preferences, storage for opaque operational data, and secrets for sensitive values. Do not write mutable state into the installed package folder.

Use `Storage.RoleLocalWorkspace` only when a library requires a local path. It is scoped to the current activation and host role, App and Runtime roots are independent, and package code does not dispose it.

Runtime operation, stream, callback, auth, and Stack contributor handlers run under a package-session lease. Their cancellation token links the HTTP request, Runtime shutdown, and generation retirement. Once retirement starts the generation admits no new leases. Reload has a bounded drain deadline and never disposes package services or load contexts while a valid lease is running, so package handlers must not suppress or ignore cancellation.

Host-provided services can also be injected into package services and views. App modules can use `IBackgroundProcessQueue`, `IPackageNotificationService`, `IPackageShellViewService`, `IPackageSettingsNavigationService`, and `IPackageInstalledSessionControl`. `IPackageDevelopmentSessionControl` is optional; UI must check `Availability` and show `UnavailableReason` instead of assuming App-local paths are visible to Runtime. Runtime does not construct or invoke App modules.

## Views

Package views are Avalonia controls registered by an App module through `Sunder.Sdk.Avalonia`.

```csharp
registry.RegisterPackageView<MyView>(new PackageViewRegistration(
    id: "my.company.package.main",
    name: "My Package",
    icon: "assets/icon.png",
    defaultPlacement: PackageViewPlacement.Middle));
```

View guidance:

- Use stable view ids scoped under the package id.
- Use short user-facing names.
- Keep package UI independent of `Sunder.App` internals.
- Use Sunder semantic theme resources for shell-sensitive surfaces and text.
- Use normal Avalonia patterns and libraries inside package UI.

## Theme Resources

`Sunder.Sdk.Avalonia.Theming.SunderThemeKeys` contains semantic resource keys.

Common keys:

- `Sunder.Brush.Background.App`
- `Sunder.Brush.Surface.Base`
- `Sunder.Brush.Surface.Raised`
- `Sunder.Brush.Surface.Workspace`
- `Sunder.Brush.Foreground.Primary`
- `Sunder.Brush.Foreground.Secondary`
- `Sunder.Brush.Accent`
- `Sunder.Brush.Warning`
- `Sunder.Brush.Danger`
- `Sunder.Radius.Medium`
- `Sunder.Spacing.Medium`
- `Sunder.FontSize.Body`

Package UI can use these via Avalonia dynamic resources when it needs to match the shell theme.

## Icons And Assets

The template includes `Assets/icon.png` and metadata `Icon = "assets/icon.png"`.

Asset behavior:

- Source `Assets/**` is copied to dev output `assets/**`.
- `.sunderpkg` archives store package assets under `payload/assets/**`.
- Runtime asset URLs are served through the runtime host, not direct file paths.
- Registry package icons are extracted from package artifacts during publish.

Recommended icon setup:

```text
MyPackage/
  Assets/
    icon.png
  PackageMetadata.cs
```

```csharp
[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Icon = "assets/icon.png")]
```

## Build Dev Output

Build the package project:

```powershell
dotnet build .\MyPackage\MyPackage.csproj
```

Build output includes:

```text
MyPackage/bin/Debug/net10.0/sunder-dev/
  sunder-package.json
  lib/
  assets/
```

This folder is what `Sunder.App --dev-package` consumes.

## Load In Sunder App

Load one dev package into an installed app:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

Load multiple dev packages:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\HostPackage\bin\Debug\net10.0\sunder-dev" --dev-package ".\ExtensionPackage\bin\Debug\net10.0\sunder-dev"
```

The app passes dev-package folders only as Runtime startup inputs. Runtime validates and activates the package, then exposes generation-scoped UI snapshots to the App; the App does not inspect the dev output directories.

Add `--watch` to reload after build output changes:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev" --watch
```

Runtime owns watcher lifecycle, parent-folder replacement handling, debounce/stability checks, and generation-fenced reload transactions. Reload state reaches the App through the authenticated Runtime event stream. Package runtime logs are likewise discovered, parsed, bounded, and streamed by Runtime rather than read from package storage by the App.

## Debug Runtime Package Code

Start a runtime host on a debug URL and wait for a debugger:

```powershell
& "C:\Path\To\Sunder.Runtime.Host.exe" --wait-for-debugger --urls http://127.0.0.1:5276
```

Start the app against that runtime and load the dev package:

```powershell
& "C:\Path\To\Sunder.App.exe" --runtime-url http://127.0.0.1:5276 --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

The runtime also supports `SUNDER_WAIT_FOR_DEBUGGER=1`.

## Publish A Package Artifact

Publish the package project:

```powershell
dotnet publish .\MyPackage\MyPackage.csproj -c Release
```

The `.sunderpkg` is written to the publish directory, for example:

```text
MyPackage/bin/Release/net10.0/publish/MyPackage.1.0.0.sunderpkg
```

Use the explicit pack target when you want a package artifact without a full publish operation:

```powershell
dotnet msbuild .\MyPackage\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
```

## Validate A Package Artifact

Validate before publishing:

```powershell
sunder package validate .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

Validation checks archive safety, manifest shape, required files, package id format, SemVer version, entry assembly existence, icon existence, content index hashes, content index sizes, and unindexed files.

## Install Locally

Install or upgrade from a local file through the runtime host:

```powershell
sunder install --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

Allow downgrade or same-version reinstall when needed:

```powershell
sunder install --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg --allow-downgrade --reinstall
```

The runtime must be reachable at the configured runtime URL. The default is `http://127.0.0.1:5275/`.

## Publish To A Registry

Sign in before authenticated publish:

```powershell
sunder auth login
```

Publish to the configured Registry:

```powershell
sunder publish --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

Publish without moving the `latest` dist tag:

```powershell
sunder publish --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg --no-latest
```

Publish to a local development Registry endpoint when the Registry server is running in Development:

```powershell
sunder publish --file .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg --dev-local --registry-api-url http://localhost:5288/
```

Authenticated publish uses the encrypted Registry credential owned by Runtime after `sunder auth login`. `--dev-local` remains available for a development Registry endpoint that enables local publish.
