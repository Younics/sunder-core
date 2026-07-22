# Activation, DI, And Disposal

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, .NET 10, and Runtime protocol revision 3.

Activation is generation-based. Sunder prepares a complete candidate package graph, publishes it atomically, and retires the previous generation only after in-flight leased work drains.

## Runtime Activation

Runtime performs these steps:

1. Validate package content, manifest, SDK compatibility, exact host roles, dependencies, and shared contract identities before reflection-loading package code.
2. Resolve dependencies in topological order. A missing, incompatible, cyclic, or failed dependency prevents its dependents from loading.
3. Create a collectible package load context and instantiate the Runtime module through its public parameterless constructor.
4. Call `ConfigureRuntimeServices(IServiceCollection, IPackageContext)` on the module.
5. Add host-owned services and build the package provider.
6. Call `RegisterRuntimeContributions(ISunderRuntimeContributionRegistry, IServiceProvider)`.
7. After the complete candidate is prepared and the previous generation drains, start registered background services and publish the candidate generation.

The module is constructed directly and is **not** resolved from the package provider. Keep no disposable resource on the module itself; place owned resources in DI services so provider disposal can release them.

## App Activation

App receives an authenticated, generation-scoped package UI snapshot from Runtime, then:

1. Materializes and verifies a private generation-owned content tree.
2. Creates a collectible App load context and directly constructs the App module.
3. Calls `ConfigureAppServices(IServiceCollection, IPackageContext)`.
4. Adds host-owned services and builds the App provider.
5. Calls `RegisterAppContributions(ISunderAppContributionRegistry, IServiceProvider)`.
6. Atomically publishes views, resources, icons, and host services for the candidate generation.

Views are registered during activation but constructed lazily from the package provider. Candidate generations are not externally usable until publication succeeds.

## DI Rules

Use normal `Microsoft.Extensions.DependencyInjection` lifetimes inside one role:

```csharp
public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
{
    services.AddSingleton<ItemRepository>();
    services.AddSingleton<ListItemsHandler>();
}

public void RegisterRuntimeContributions(
    ISunderRuntimeContributionRegistry registry,
    IServiceProvider services)
{
    registry.RegisterRuntimeOperation(
        PackageOperations.ListItems,
        services.GetRequiredService<ListItemsHandler>());
}
```

Do not build a nested provider in `Configure*Services`. Resolve contribution instances from the provider supplied to `Register*Contributions`.

Packages cannot replace reserved host capabilities. Both roles reserve `IPackageContext`, `ILoggerFactory`, `ILogger<T>`, `IPackageExtensionCatalog`, `IPackageShellViewService`, `IPackageSettingsNavigationService`, `IPackageNotificationService`, `IPackageRuntimeClient`, and `IPackageCallbackClient`. App also reserves `IBackgroundProcessQueue`. Registering a reserved service fails activation.

Host services intentionally vary by role. Runtime supplies unavailable/no-op shell, settings-navigation, notification, Runtime-client, and callback-client implementations. App supplies the real shell-facing services after generation publication.

## Publication Availability

Treat both `Configure*Services` and `Register*Contributions` as side-effect-free composition phases.

Before an App generation is published:

- Runtime operations, streams, callbacks, and package data mutations throw `InvalidOperationException`.
- Shell and settings navigation report `false` or an empty snapshot.
- Notifications and App background-process enqueues are buffered and published only if the generation commits.
- Read access may still contact Runtime, but activation should not depend on network or durable side effects.

Start work from view navigation/warmup, a user action, an App background process, or a Runtime background service instead. Check `IPackageRuntimeClient.IsAvailable` and `IPackageCallbackClient.IsAvailable` where the same service can run in different contexts.

## Runtime Background Services

Register a service already present in DI:

```csharp
registry.RegisterBackgroundService<IndexingService>();
```

`IPackageBackgroundService.StartAsync` is called once after the candidate graph is ready. Completion means startup is complete; long-running work should continue in package-owned tasks. Services start sequentially in package dependency order and have a shared 10-second startup deadline.

On retirement, services stop in reverse package and registration order. `StopAsync` must cancel and await all owned work promptly. Startup rollback has a 5-second cleanup budget; normal generation retirement shares the 10-second drain budget. Exceptions during cleanup are logged and remaining services still receive a stop attempt. If `StartAsync` or `StopAsync` ignores cancellation and outlives its deadline, the operation returns without publishing or blocking the next generation, but the old provider, load context, and generation files remain quarantined until the actual task completes.

## Generation Leases

Runtime operations, streams, callback/auth work, and Stack contributor calls hold a lease on the active package generation. Their cancellation token links:

- caller cancellation or disconnect;
- Runtime shutdown; and
- generation retirement.

Once retirement starts, the generation accepts no new leases and returns Runtime unavailability. Runtime cancels existing lease tokens and waits up to 10 seconds. If valid work does not drain, reload is rejected and the old generation remains active and undisposed. Handlers must not suppress cancellation or keep an async iterator alive after cancellation.

After leases drain, Runtime stops background services, disposes the package provider (`IAsyncDisposable` before `IDisposable`), unloads package load contexts, releases shared contracts, and removes generation files. Cleanup that exceeds the deadline remains quarantined and is observed until every invoked package lifecycle task exits; only then can provider disposal, unload, and file removal continue. Runtime shutdown has an overall 15-second deadline.

## App Retirement

App revokes generation capabilities, cancels and drains view operations, unregisters and detaches cached views, stops generation-owned App background work, disposes view data contexts and controls when they implement `IDisposable`, disposes package providers, and then unloads App load contexts. Presentation failures are client-local; App does not report them as Runtime package faults.

Package services must:

- implement `IDisposable` or `IAsyncDisposable` when they own resources;
- unsubscribe from static, host, and extension-catalog events;
- stop timers, threads, watchers, and child processes;
- release native handles and avoid static references to package types; and
- never dispose host-provided context capabilities.

## Threading

- `IPackageContext` capabilities are thread-safe unless documented otherwise.
- Module configuration and contribution registration have no App UI-thread guarantee; do not touch Avalonia controls there.
- Runtime operation, stream, callback/auth, Stack, and extension code may be invoked concurrently. Make activation-scoped services thread-safe.
- Runtime background-service start/stop calls run outside a UI context.
- View creation, warmup, presentation, navigation, and cached-view detachment are marshalled to the App UI dispatcher while it is available.
- `IPackageViewNavigationTarget` enters on the UI dispatcher. If code uses `ConfigureAwait(false)`, it must dispatch later UI access itself.
- `IPackageExtensionCatalogMonitor.Changed` has no UI-thread guarantee. Subscriber exceptions are isolated so one package cannot stop later subscribers or host lifecycle progress.

## Extension Contributions

Register each contribution in the role where consumers need it. Contributions are role-local objects and are owned by the package currently activating, not by the package that defines the extension point.

Use `GetExtensionContributions` when ownership affects attribution, dependencies, or export behavior. Every result has a canonical non-empty owner package id. Use `IPackageExtensionCatalogMonitor` to refresh long-lived state when relevant extension points change; filter by extension-point id and treat the event as a notification to re-query the catalog.
