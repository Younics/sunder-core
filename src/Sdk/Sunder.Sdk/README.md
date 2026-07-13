# Sunder.Sdk

`Sunder.Sdk` contains package-author contracts only. Runtime Host owns package state persistence,
settings, encrypted secrets, storage allocation, platform credential integration, and persistent
package logs. App activation receives Runtime-backed package capabilities and never creates those
resources locally.

Package storage keys are opaque, case-sensitive tokens. Missing values are represented by `null`, and
key lists are stable ordinally sorted snapshots. Package file paths are relative capability paths;
absolute paths and parent traversal are rejected by Runtime.

`Sunder.Sdk` contains the public contracts used to build Sunder runtime packages.

Avalonia view/settings/workspace contracts and theme resources are distributed separately in `Sunder.Sdk.Avalonia`. Stack import/export and Stack contributor contracts are distributed separately in `Sunder.Sdk.Stacks`.

Use this package for explicit Runtime/App lifecycle roles, background services, typed extension points, and package-scoped storage, settings, secrets, and logging. Add `Sunder.Sdk.Avalonia` only for Avalonia views/settings, workspaces, and theme resources.

SDK/Host compatibility is capability-based. `Sunder.Package.Build` infers SDK requirements automatically; see `docs/SUNDER-SDK-COMPATIBILITY.md` in the Sunder Core repository for the full policy.

## Install

```powershell
dotnet add package Sunder.Sdk
```

App/UI packages also install:

```powershell
dotnet add package Sunder.Sdk.Avalonia
```

Stack-contributing packages also install the coordinated Stack contracts package:

```powershell
dotnet add package Sunder.Sdk.Stacks
```

Most runtime packages should also reference `Sunder.Package.Build` so builds generate the Sunder manifest, development output, and distributable archive:

```powershell
dotnet add package Sunder.Package.Build --private-assets all
```

For a new package project, the quickest path is the template package:

```powershell
dotnet new install Sunder.Package.Templates
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
```

## Package Shape

A Sunder package is a .NET assembly that declares package metadata and may expose one `ISunderRuntimePackageModule`, one `ISunderAppPackageModule`, or a single class implementing both roles.

Typical package projects:

- Target `net10.0`.
- Reference `Sunder.Sdk`.
- Reference `Sunder.Package.Build` with `PrivateAssets="all"`.
- Reference `Sunder.Sdk.Avalonia` and Avalonia packages only when they register Avalonia UI behavior.
- Do not reference `Sunder.App` or `Sunder.Runtime.Host`.

## Package Metadata

Package identity and runtime dependencies are declared with assembly attributes from `Sunder.Sdk.Packaging`.

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Summary = "Adds a custom Sunder workspace.",
    Icon = "assets/icon.png")]
```

Extension packages can declare runtime package dependencies:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.extension",
    Name = "My Extension",
    Summary = "Extends another Sunder package.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.host",
    VersionRange = ">=1.1.0 <1.2.0")]
```

Package id rules:

- Use lowercase dot-separated ASCII identifiers such as `my.company.package`.
- Do not use spaces, underscores, or display-name casing.
- Do not rename a package id after publishing.
- Keep `Name` short and user-facing.
- Use `Summary` for one sentence of package description.

The package version comes from normal MSBuild properties such as `Version`, not from the metadata attributes.

## Package Module

Every runtime package exposes one public, non-abstract module with a public parameterless constructor.

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

Runtime and App roles have separate service providers and explicit `ConfigureRuntimeServices`/`ConfigureAppServices` and contribution methods. A host never invokes the other host's role.

`Sunder.Sdk.Packaging` owns the canonical `PackageId`, strict `SemanticVersion`, and `PackageVersionRange` primitives used by package authors, build tooling, Hosts, and Registry clients. Their `Parse`/`TryParse` methods are the V1 validators; do not implement a second package identity or version grammar.

## Contributions

`ISunderRuntimeContributionRegistry` supports background services, Runtime extensions, and configuration schemas. Base `ISunderAppContributionRegistry` supports App extensions; `Sunder.Sdk.Avalonia` adds:

- `RegisterPackageView<TView>(PackageViewRegistration registration)`
- `RegisterPackageViewFactory<TFactory>(PackageViewRegistration registration)`
- `RegisterSettingsView<TView>()`
- `RegisterSettingsViewFactory<TFactory>()`
- `RegisterExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract contribution)`

Package views are Avalonia controls registered by code. Use stable view ids scoped under your package id.

```csharp
registry.RegisterPackageView<MyView>(new PackageViewRegistration(
    "my.company.package.main",
    "My Package",
    icon: "assets/icon.png"));
```

## Package Context

`IPackageContext` gives your module access to host-provided package services:

- `PackageId`
- `Version` (the canonical SemVer 2.0 string from the package manifest)
- `InstallPath`
- `Storage`
- `Settings`
- `Secrets`
- `LoggerFactory`
- `Logging`

Host-provided services can also be injected into package services and views, including `IBackgroundProcessQueue` for long-running package work, `IPackageNotificationService` for user-visible notifications, `IPackageShellViewService` for hotbar and panel navigation, `IPackageSettingsNavigationService` for opening settings, `IPackageInstalledSessionControl` for installed package activation, and optional `IPackageDevelopmentSessionControl` for host-local development output.

Use `Settings` for schema-declared user preferences, `Storage.State` for opaque operational state, and `Secrets` for sensitive values. Settings are writable and persisted independently from state. `GetValueAsync` returns a stored setting or its schema default; `GetStoredValueAsync` returns only a stored value. Setting writes reject undeclared keys, secret fields, and values that do not satisfy the schema.

```csharp
var effective = await context.Settings.GetValueAsync("enabled", cancellationToken);
var stored = await context.Settings.GetStoredValueAsync("enabled", cancellationToken);
await context.Settings.SetValueAsync("enabled", "false", cancellationToken);
await context.Settings.DeleteValueAsync("enabled", cancellationToken);
```

Do not use `Storage.State` as a settings store or write mutable data into the installed package folder.

`Storage.RoleLocalWorkspace` provides paths for APIs such as SQLite, process working directories, and atomic directory trees. It belongs to the current package activation and host role. App and Runtime workspaces are separate, the host owns their lifecycle, and package code must not dispose the capability.

## Package Runtime Operations

Use package Runtime operations when App UI needs to query or command package-owned Runtime services. The operation channel is scoped to the current package: App code cannot invoke another package's handlers. Define coarse-grained package DTOs and a stable lowercase operation id, register the handler from the Runtime module, and inject `IPackageRuntimeClient` into App services.

```csharp
using Sunder.Sdk.Runtime;

public sealed record ListItemsRequest(int Offset, int Limit);
public sealed record ListItemsResponse(IReadOnlyList<string> Items);

public static class PackageOperations
{
    public static PackageRuntimeOperation<ListItemsRequest, ListItemsResponse> ListItems { get; }
        = new("items.list");
}

public sealed class ListItemsHandler
    : IPackageRuntimeOperationHandler<ListItemsRequest, ListItemsResponse>
{
    public ValueTask<ListItemsResponse> HandleAsync(
        ListItemsRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ListItemsResponse(["one", "two"]));
    }
}
```

Register the Runtime service and handler:

```csharp
public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    => services.AddSingleton<ListItemsHandler>();

public void RegisterRuntimeContributions(
    ISunderRuntimeContributionRegistry registry,
    IServiceProvider services)
    => registry.RegisterRuntimeOperation(
        PackageOperations.ListItems,
        services.GetRequiredService<ListItemsHandler>());
```

Invoke it from App-owned code:

```csharp
var response = await runtimeClient.InvokeAsync(
    PackageOperations.ListItems,
    new ListItemsRequest(Offset: 0, Limit: 100),
    cancellationToken);
```

Requests may run concurrently and are cancelled when the caller disconnects, Runtime shuts down, or the package session generation begins retirement. A retiring generation stops admitting new leases and reload waits only for a bounded drain deadline. Handlers must promptly observe cancellation; a handler that ignores it causes reload to fail while the old generation remains loaded and undisposed. Keep operations coarse-grained, use paging for large collections, do not send local filesystem paths, and do not model the channel as remote SQL or a generic repository. Hosts enforce bounded request and response payloads.

For ordered updates, define `PackageRuntimeStream<TRequest, TEvent>`, implement `IPackageRuntimeStreamHandler<TRequest, TEvent>`, register it with `RegisterRuntimeStream`, and consume it with `IPackageRuntimeClient.SubscribeAsync`. Each subscription is independent, retains its Runtime package activation until the stream completes, and must observe cancellation. The wire stream uses bounded newline-terminated `event`, `completed`, and `error` JSON envelopes. EOF without a terminal envelope and a trailing partial record are transport failures; reconnect and replay semantics remain package-defined.

## Settings And Package Sessions

Use `IPackageSettingsNavigationService` when a package needs to open global Sunder settings or another package's settings page:

```csharp
var opened = await settingsNavigation.OpenPackageSettingsAsync(
    "my.company.package",
    cancellationToken: cancellationToken);
```

Use `IPackageInstalledSessionControl` for installed package ids. Development loading is a separate optional capability because a remote Runtime cannot consume an App-local path. Check `IPackageDevelopmentSessionControl.Availability` before enabling development UI and handle its structured outcome:

```csharp
if (developmentSessions.Availability.IsAvailable)
{
    var result = await developmentSessions.LoadDevelopmentPackageAsync(
        new PackageDevelopmentSessionLoadRequest(devOutputPath, Watch: true),
        cancellationToken);
    if (!result.IsSuccess)
    {
        ShowMessage(result.Message);
    }
}
```

Development session control may be absent or report an explicit unavailable reason. Unsupported operations return `PackageDevelopmentSessionOperationOutcome.Unsupported`; they do not claim that an arbitrary absolute path can cross the App/Runtime boundary.

## Background Processes

Use `IBackgroundProcessQueue` for package work that should keep running outside the current button click or view lifecycle, such as downloads, imports, model pulls, or indexing.

```csharp
queue.Enqueue(new BackgroundProcessRequest(
    Title: "Import dataset",
    GroupKey: "my.company.package:imports",
    Indicator: BackgroundProcessIndicator.Settings,
    ConcurrencyMode: BackgroundProcessConcurrencyMode.SequentialWithinGroup,
    CanCancel: true,
    ExecuteAsync: async context =>
    {
        context.ReportIndeterminate("Importing dataset...");
        await ImportAsync(context.CancellationToken);
        context.ReportProgress(100, "Import complete");
    },
    Metadata: new Dictionary<string, string>
    {
        ["dataset"] = "customers",
    }));
```

`BackgroundProcessIndicator.Hidden` keeps the process out of all footer indicators. `Main`, `Packages`, and `Settings` show it in exactly one host indicator surface. `GroupKey` is for concurrency and package-side listing; it is not used for UI placement.

## Extension Catalog

Packages can query installed/active contributions through `IPackageExtensionCatalog`:

```csharp
var providers = extensionCatalog.GetExtensions(MyExtensionPoints.Providers);
```

Use `GetExtensionContributions` whenever package ownership affects exported dependencies, settings navigation, attribution, or lifecycle decisions. Every contribution has a canonical non-empty owner id; catalogs cannot fall back to ownerless entries.

When a package needs to update open UI or cached capability lists as other packages activate/deactivate, inject `IPackageExtensionCatalog` and cast to `IPackageExtensionCatalogMonitor`. `Changed` provides a revision, lifecycle reason, and extension-point changes including package id and contribution type.

Use the change details to refresh only affected state, for example execution-target UI when `sunder.package.agent:execution-targets` changes.

## Callback Sessions

`IPackageCallbackHandler` is the generic Runtime callback contract for browser or local callback flows. Register it under a stable `CallbackHandlerId`. App code uses `IPackageContext.Callbacks.StartAsync` with a bounded string dictionary, opens the returned URI with `OpenLaunchUriAsync`, and polls `GetStatusAsync` until terminal. Runtime and preflight contexts explicitly report this App capability as unavailable.

The host owns the callback listener, redirect path, leases, expiry, duplicate completion, and unload/shutdown cancellation. Package callback handlers must not create listeners. Implement `CancelCallbackAsync` when a provider task or delegate can remain in flight after `StartCallbackAsync` returns. `IPackageAuthHandler` remains the auth-specific status/disconnect surface projected over generic sessions.

Register callback handlers in `ConfigureServices`. Auth-capable packages can register the same implementation as both `IPackageAuthHandler` and `IPackageCallbackHandler`.

## Theme Resources

Package UI can use semantic Sunder theme keys from `Sunder.Sdk.Avalonia.Theming.SunderThemeKeys`. These keys let package UI match the active Sunder shell theme without referencing app internals.

Common resource keys include:

- `Sunder.Brush.Background.App`
- `Sunder.Brush.Surface.Base`
- `Sunder.Brush.Surface.Raised`
- `Sunder.Brush.Surface.Workspace`
- `Sunder.Brush.Foreground.Primary`
- `Sunder.Brush.Foreground.Secondary`
- `Sunder.Brush.Accent`
- `Sunder.Radius.Medium`
- `Sunder.Spacing.Medium`
- `Sunder.FontSize.Body`

Example Avalonia usage:

```xml
<Border Background="{DynamicResource Sunder.Brush.Surface.Workspace}"
        CornerRadius="{DynamicResource Sunder.Radius.Medium}"
        Padding="{DynamicResource Sunder.Spacing.Medium}">
  <TextBlock Text="Hello from my package"
             Foreground="{DynamicResource Sunder.Brush.Foreground.Primary}" />
</Border>
```

## Runtime Dependencies And Contracts

Runtime package dependencies and NuGet contracts dependencies are separate concepts.

Use `[assembly: SunderPackageDependency(...)]` when your installed package requires another installed Sunder package at runtime.

Use a normal NuGet package reference when you need compile-time contracts from another package, such as a `*.Contracts` package that declares extension points or contribution interfaces.

## Build And Publish

With `Sunder.Package.Build` referenced, package builds generate an unpacked development package:

```powershell
dotnet build .\MyPackage\MyPackage.csproj
```

The generated `sunder-dev` folder can be loaded into Sunder App for local development:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

Publishing produces a distributable `.sunderpkg` archive:

```powershell
dotnet publish .\MyPackage\MyPackage.csproj -c Release
```

Validate before publishing to a registry:

```powershell
sunder package validate .\MyPackage\bin\Release\net10.0\publish\MyPackage.1.0.0.sunderpkg
```

## More Documentation

- Package author manual: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- Sunder overview: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER.md
