# Sunder.Sdk

> **Release/source channel:** The NuGet README describes that published SDK version. The repository copy tracks current source and may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

`Sunder.Sdk` contains package-author contracts only. Runtime Host owns package state persistence,
settings, encrypted secrets, storage allocation, platform credential integration, and persistent
package logs. App activation receives Runtime-backed package capabilities and never creates those
resources locally.

`PackageStorageValidation` defines the shared storage/settings contract: case-sensitive portable ASCII
keys are limited to 256 characters, string values to 1 MiB of UTF-8, portable `/`-separated relative
paths to 1024 characters with 255-character segments, and files to 16 MiB. Missing values are represented
by `null`, and key lists are stable ordinally sorted snapshots. File-name casing follows the host filesystem;
Runtime contains file-store paths beneath their package root and rejects symbolic-link/reparse-point traversal.

`Sunder.Sdk` contains the public contracts used to build Sunder packages.

Avalonia view/settings contracts and theme resources are distributed separately in `Sunder.Sdk.Avalonia`. Stack import/export and Stack contributor contracts are distributed separately in `Sunder.Sdk.Stacks`. Standalone .NET Runtime workers use the exact coordinated `Sunder.Sdk.Worker` package.

Use this package for App lifecycle roles, shared package contracts, schema-first RPC, and package-scoped capability abstractions. Runtime executables use `Sunder.Sdk.Worker`, which depends on the exact coordinated `Sunder.Sdk` version. Add `Sunder.Sdk.Avalonia` only for Avalonia views/settings and theme resources.

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

Most package projects should also reference `Sunder.Package.Build` so builds generate the Sunder manifest, development output, and distributable archive:

```powershell
dotnet add package Sunder.Package.Build --private-assets all
```

For a new package project, the quickest path is the template package:

```powershell
dotnet new install Sunder.Package.Templates
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package"
```

Decision summary:

| Need | Package/tool |
| --- | --- |
| Worker V3 Runtime leaf | `Sunder.Sdk.Worker` |
| App or Protocol project | `Sunder.Sdk` |
| Avalonia views/settings | Add `Sunder.Sdk.Avalonia` to that App leaf |
| Stack contributor | Add `Sunder.Sdk.Stacks` to the implementing leaf |
| Canonical C# build/archive | `Sunder.Package.Build` with `PrivateAssets="all"` |
| New canonical .NET package | Install `Sunder.Package.Templates` as a scaffold tool |
| Node worker or web App | Use the coordinated npm packages; `@sunder/sdk` is an RPC/worker/browser subset, not this .NET API surface |

## Package Shape

A Sunder package is one universal archive with shared content and exact App/Runtime RID targets. An App target may expose one App module; a Runtime target is an independently executable exact-RID Worker V3 process. A shared-only package may contain descriptor bundles without executable targets.

Typical package projects:

- Target `net10.0`.
- Reference `Sunder.Sdk.Worker` from Worker leaves and `Sunder.Sdk` from App or Protocol projects.
- Reference `Sunder.Package.Build` with `PrivateAssets="all"`.
- Set `SunderPackagePrimaryProject=true` on the canonical Worker/package csproj; a separate Aggregate wrapper is not needed.
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

## App Module

An App target may expose one public `ISunderAppPackageModule`. It must be non-abstract and have a public parameterless constructor.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace MyCompany.Package;

public sealed class PackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddTransient<MyPackageViewModel>();
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterPackageView<MyPackageView>(new PackageViewRegistration(
            "my.company.package.main",
            "My Package"));
    }
}
```

The App constructs the module through its public parameterless constructor, configures and builds the App-specific provider, and then registers contributions; the module itself is not resolved from that provider. Runtime workers own their process-local composition and immutable contribution catalog through `SunderWorkerOptions`; the worker-native `ISunderWorkerLifecycleService` contract lives in `Sunder.Sdk.Worker`. `Sunder.Sdk` does not expose a managed Runtime module, background-service, or generation-participant contract.

`Sunder.Sdk.Packaging` owns the canonical `PackageId`, strict `SemanticVersion`, and `PackageVersionRange` primitives used by package authors, build tooling, Hosts, and Registry clients. Their `Parse`/`TryParse` methods are the V1 validators; do not implement a second package identity or version grammar.

## Contributions

`Sunder.Sdk.Avalonia` adds these registrations to `ISunderAppContributionRegistry`:

- `RegisterPackageView<TView>(PackageViewRegistration registration)`
- `RegisterSettingsView<TView>()`

Package views are Avalonia controls registered by code and constructed from the package App service provider, so their constructors may request registered dependencies. View ids must be globally unique and stable; conventionally prefix them with your package id.

```csharp
registry.RegisterPackageView<MyView>(new PackageViewRegistration(
    "my.company.package.main",
    "My Package",
    iconAssetPath: "assets/icon.png"));
```

## Package Context

`IPackageContext` gives App modules and compatible Worker contexts access to host-provided package services:

- `PackageId`
- `Version` (the canonical SemVer 2.0 string from the package manifest)
- `ContentRootPath`
- `Storage`
- `Settings`
- `Secrets`
- `Logging`

Host-provided services can also be injected into package services and views, including `IBackgroundProcessQueue` for long-running package work, `IPackageNotificationService` for user-visible notifications, `IPackageShellViewService` for hotbar and panel navigation, and `IPackageSettingsNavigationService` for opening settings.

Use `Settings` for schema-declared user preferences, `Storage.State` for opaque operational state, and `Secrets` for sensitive values. Settings are writable and persisted independently from state. `GetValueAsync` returns a stored setting or its schema default; `GetStoredValueAsync` returns only a stored value. Setting writes reject undeclared keys, secret fields, and values that do not satisfy the schema.

```csharp
var effective = await context.Settings.GetValueAsync("enabled", cancellationToken);
var stored = await context.Settings.GetStoredValueAsync("enabled", cancellationToken);
await context.Settings.SetValueAsync("enabled", "false", cancellationToken);
await context.Settings.DeleteValueAsync("enabled", cancellationToken);
```

Do not use `Storage.State` as a settings store or write mutable data into the installed package folder.

Do not place imported or otherwise opaque domain identifiers directly in physical state or secret keys. Create a deterministic portable key instead; the opaque identifier remains unchanged in the domain model and is hashed only at the storage boundary:

```csharp
using Sunder.Sdk.Storage;

var physicalKey = PackageStorageKeyFactory.Create(
    "workspace.config",
    version: 2,
    importedWorkspaceId);
await context.Storage.State.SetValueAsync(physicalKey, json, cancellationToken);
```

If an earlier package version wrote nonportable dynamic keys, declare their exact shape and ask the Runtime-owned store to migrate them before normal storage access:

```csharp
if (context.Storage.State is not IPackageStorageKeyMigrator migrator)
{
    throw new NotSupportedException("This Runtime cannot migrate package storage keys.");
}

await migrator.MigrateKeysAsync(
[
    PackageStorageKeyMigration.OpaqueId(
        "workspace:",
        ":config",
        "workspace.config",
        destinationVersion: 2),
    PackageStorageKeyMigration.Exact("images:v1", "images.v1"),
], cancellationToken);
```

Use `PackageStorageKeyMigration.DynamicCleanup` only for a bounded package-owned physical-key grammar that must remove orphaned crash remnants without enumerating secret values. Return `NoMatch` for every key outside that grammar so unknown invalid keys continue to fail closed.

Runtime applies the declared rewrites atomically under the document lock, retains the pre-migration document, and treats repeat execution as a no-op. Every legacy key must match exactly one rule. Unknown invalid keys, ambiguous rules, and unequal destination collisions fail closed instead of discarding data. `IPackageSecrets` implements the same migration capability without exposing secret enumeration.

`Storage.RoleLocalWorkspace` provides paths for APIs such as SQLite, process working directories, and atomic directory trees. It belongs to the current package activation and host role. App and Runtime workspaces are separate, the host owns their lifecycle, and package code must not dispose the capability.

## Owner And Cross-Package RPC

Use `Sunder.Sdk.Rpc` for App-to-Worker owner calls and cross-package Runtime calls. A package bundles every contract descriptor it provides or imports under `payload/shared`, declares the descriptor identity and hashes in `contractBundles`, declares maximum requested `discover`, `invoke`, and `subscribe` actions in `usesContracts`, and declares each provider in `provides`. Worker V3 registers the same manifest-declared provider identity through `SunderWorkerProviderRegistration`. Consumers receive a caller-scoped `ISunderRpcClient`; endpoint references identify one exact activation and never retarget a replacement.

Descriptors are strict UTF-8 JSON with `descriptorVersion: 2`, exact `schemaProfile: "sunder.rpc.values.v1"`, strict SemVer contract versions, keyed services and methods, declared errors, and local `#/$defs/...` schema references. The supported Draft 2020-12 profile is deliberately closed: bounded ordinary strings and collections; closed case-sensitive records; portable JSON values and objects; safe integers; explicit `float32` and `float64` numbers; asserted string formats; exact big-integer and decimal strings; bounded base64 bytes; content references; local references; productive recursion; and unambiguous nullable or tagged `oneOf` unions. Remote references, bare `type: "number"`, legacy UUID/date-time logical types, unknown keywords, open records, unbounded ordinary values, unproductive recursion, duplicate or case-colliding names, and excessive descriptor depth/count/size are rejected during package validation.

Descriptor hashes use deterministic JCS-compatible canonical JSON, while ABI hashes use the domain-separated structural RPC V2 identity. Every descriptor number must decode to a finite IEEE-754 binary64 value. Integer-valued keywords and safe-integer schema values remain mathematical integers in the inclusive range `-9007199254740991` through `9007199254740991`; floating-point schemas select their wire semantics explicitly with `format: "float32"` or `format: "float64"`.

Runtime validates requests and responses/events against the same parsed descriptor. Grants are explicit, default-deny, durable, and fenced to the caller package version plus manifest hash. Revocation immediately removes discovery visibility and cancels active calls and subscriptions. Use `SunderRpcException` for bounded domain failures, `SunderRpcContentReference` for host-mediated large content, and the typed client extensions or `SunderRpcCSharpGenerator` for generated bindings.

`SunderRpcCSharpGenerator.Generate(descriptor, namespaceName)` preserves the public generated surface. The overload accepting `SunderRpcCSharpAccessibility` can instead emit every top-level generated declaration as `Internal`; undefined enum values are rejected. Accessibility is uniform for one generated binding, so use internal mode only when its DTOs, provider interface, client, and handler stay in the same assembly behind intentional handwritten public contracts.

The designated Agent orchestrator may call `ISunderRpcClient.TryReportInvariantViolationAsync` when a package-local adapter detects malformed provider behavior that Host schema validation cannot observe. The endpoint reference attributes the report to one exact active provider; stale, invisible, or unauthorized reports return `false`, and accepted reports fault only the owning package activation under the Host's normal RPC fault policy.

## Settings Navigation

Use `IPackageSettingsNavigationService` when a package needs to open global Sunder settings or another package's settings page:

```csharp
var opened = await settingsNavigation.OpenPackageSettingsAsync(
    "my.company.package",
    cancellationToken: cancellationToken);
```

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

Request metadata and returned metadata are immutable snapshots. `ReportProgress` clamps finite values to 0 through 100 and rejects `NaN` and infinities. Terminal snapshots never advertise cancellation.

## Package-Local Protocol

Sunder does not share package-authored CLR assemblies or expose package objects across activation boundaries. Define cross-package Runtime behavior with a schema-first RPC descriptor, bundle it in each package manifest, and use generated or hand-written package-local adapters over `ISunderRpcClient`.

Discovery and watch results carry Host-stamped owner and activation identities. Endpoint references bind to one exact activation, so callers must rediscover after provider replacement. Keep CLR interfaces and implementations package-local; adapters such as `Sunder.Sdk.Stacks` translate those local calls to the public RPC ABI.

Create an `ISunderRpcCallScope` when a call sends or receives content. Register request content against its exact endpoint, construct generated clients with the scope, and dispose the scope after all calls and response streams finish. Disposal cancels active scope work and revokes every remaining content reference.

Providers receive a Host-created `SunderRpcInvocationContext`. Use its content methods to open caller content or register provider output; do not retain the context or streams beyond the handler or subscription. Invocation authority ends when that handler or stream ends. Providers may return `Domain` errors for contract-defined failures, but infrastructure error kinds are Host-authenticated and cannot be forged by package code.

## Callback Sessions

`IPackageCallbackHandler` is the generic Runtime callback contract for browser or local callback flows. Register it under a stable `CallbackHandlerId`. App code uses `IPackageContext.Callbacks.StartAsync` with a bounded string dictionary, opens the returned URI with `OpenLaunchUriAsync`, and calls `WaitForCompletionAsync` with an explicit timeout. Use `CancelAsync` to cancel a pending session. Runtime and preflight contexts explicitly report this App capability as unavailable.

The host owns the callback listener, redirect path, leases, expiry, duplicate completion, and unload/shutdown cancellation. Package callback handlers must not create listeners. Implement `CancelCallbackAsync` when a provider task or delegate can remain in flight after `StartCallbackAsync` returns. `IPackageAuthHandler` remains the auth-specific status/disconnect surface projected over generic sessions.

Assign generic callback handlers to `SunderWorkerOptions.CallbackHandlers` and an auth implementation to `SunderWorkerOptions.AuthHandler`; Runtime exposes the latter through its reserved `auth` callback route.

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

## Runtime Dependencies And Protocol Packages

Runtime package dependencies and NuGet protocol dependencies are separate concepts.

Use `[assembly: SunderPackageDependency(...)]` when your installed package requires another installed Sunder package at runtime.

Keep descriptors, DTOs, generated bindings, and adapters in a non-packable package-local Protocol project by default. A coordinated package family may distribute a helper NuGet package, but it remains a build-time convenience and does not create shared Runtime type identity. Every Sunder package still bundles the language-neutral descriptor it uses, and cross-package calls travel through `ISunderRpcClient`.

## Build And Publish

With `Sunder.Package.Build` referenced, package builds generate an unpacked development package:

```powershell
dotnet build .\MyPackage\MyPackage.csproj
```

The generated `sunder-dev` folder can be loaded into Sunder App for local development:

```powershell
& "C:\Path\To\Sunder.App.exe" --dev-package ".\MyPackage\bin\Debug\net10.0\sunder-dev"
```

The canonical explicit pack target produces a distributable `.sunderpkg` archive:

```powershell
dotnet msbuild .\MyPackage\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
```

Generated package families read the package version once from `Sunder.Package.props`. `dotnet publish` remains available when a pipeline also needs normal publish output.

Validate before publishing to a registry:

```powershell
sunder dev package validate .\MyPackage\bin\Release\net10.0\MyPackage.1.0.0.sunderpkg
```

## More Documentation

- Package author manual: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-DEVELOPMENT.md
- Callbacks and auth: https://github.com/Younics/sunder-core/blob/main/docs/package-development/CALLBACKS-AND-AUTH.md
- Owner RPC: https://github.com/Younics/sunder-core/blob/main/docs/package-development/RUNTIME-OPERATIONS.md
- Data and logging: https://github.com/Younics/sunder-core/blob/main/docs/package-development/DATA-AND-LOGGING.md
- Package standard: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER-PACKAGE-STANDARD.md
- Sunder overview: https://github.com/Younics/sunder-core/blob/main/docs/SUNDER.md
