# Activation, DI, And Disposal

> **Applies to:** Sunder SDK `1.1.x`, package manifest V3, .NET 10, and Runtime protocol revision 8.

Activation is generation-based. Sunder prepares a complete candidate package graph, publishes it atomically, and retires the previous generation only after in-flight leased work drains.

## Runtime Activation

Runtime validates the package, current-RID projection, Worker V3 capability set, dependencies, and executable entry point before starting package code. It resolves dependencies in topological order; a missing, incompatible, cyclic, or failed dependency prevents its dependents from starting.

The Host selects `sunder.worker.v3` before launch and starts the declared exact-RID executable as a supervised child process. The entry point calls the context-configured `SunderWorker.RunAsync` overload; its configuration callback runs once and returns `SunderWorkerOptions`, whose provider registrations and optional settings schema form the immutable contribution catalog advertised during composition. Runtime does not load an `ISunderRuntimePackageModule`, create a package load context, or build package-owned Runtime DI.

Runtime does not inherit the Host environment into a worker. It constructs a minimal OS-specific baseline containing process discovery (`PATH`, plus Windows `PATHEXT`/`ComSpec`), the user home (`HOME` or `USERPROFILE`), locale values, `SystemRoot` on Windows, and package-local temp variables. It then adds only the documented `SUNDER_*` activation values. Tokens, cloud credentials, profiler/startup-hook variables, arbitrary parent variables, and Host bootstrap `SUNDER_*` values are absent.

A worker that intentionally relies on a parent Docker CLI connection declares `<SunderSdkCapability Include="docker-environment.v1" />`. Install/update is the consent boundary for that manifest requirement. Only `DOCKER_HOST`, `DOCKER_CONTEXT`, `DOCKER_CONFIG`, `DOCKER_CERT_PATH`, and `DOCKER_TLS_VERIFY` may then flow; Docker auth JSON, passphrases, and other Docker variables remain excluded. Local Docker and MCP child processes otherwise use the standard baseline.

The Host first invokes non-destructive candidate preparation, then commits the exact Host-assigned generation, prepares and runs storage migrations, and awaits activation before publication. `SunderWorkerOptions` exposes those barriers as `OnCandidateStarted`, `OnGenerationCommitted`, `OnBeforeStorageMigrations`, `OnActivated`, and `OnShutdown`. Worker V3 never falls back to Worker V1 after launch.

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

App modules use normal `Microsoft.Extensions.DependencyInjection` lifetimes inside their App activation:

```csharp
public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
{
    services.AddTransient<PackageViewModel>();
}

public void RegisterAppContributions(
    ISunderAppContributionRegistry registry,
    IServiceProvider services)
{
    registry.RegisterPackageView<PackageView>(new PackageViewRegistration(
        "example.package.main",
        "Example Package"));
}
```

Do not build a nested provider in `ConfigureAppServices`. Resolve contribution instances from the provider supplied to `RegisterAppContributions`.

Worker V3 owns its process-local object composition. It receives Host-mediated services through `SunderWorkerContext` and supplies handlers through immutable `SunderWorkerProviderRegistration` entries rather than a managed contribution registry.

App packages cannot replace reserved host capabilities. These include `IPackageContext`, logging, callback/RPC clients, shell/settings/notification services, and App background processes. Registering a reserved service fails activation.

Worker code receives Host-mediated Runtime capabilities through `SunderWorkerContext`. App supplies shell-facing services and an RPC client after generation publication.

## Publication Availability

Treat both `ConfigureAppServices` and `RegisterAppContributions` as side-effect-free composition phases. The Worker V3 configuration callback is the equivalent side-effect-free composition boundary; defer work to its staged lifecycle callbacks.

Before an App generation is published:

- Owner RPC discovery/invocation, callbacks, and package data mutations are unavailable.
- Shell and settings navigation report `false` or an empty snapshot.
- Notifications and App background-process enqueues are buffered and published only if the generation commits.
- Read access may still contact Runtime, but activation should not depend on network or durable side effects.

Start work from view navigation/warmup, a user action, an App background process, or a Worker V3 lifecycle callback instead. Handle RPC unavailability and check `IPackageCallbackClient.IsAvailable` where the same service can run in different contexts.

## Worker Lifecycle

Use `OnCandidateStarted`, `OnGenerationCommitted`, `OnBeforeStorageMigrations`, `OnActivated`, and `OnShutdown` for single-callback staged Worker V3 lifecycle work. For package-owned DI and multiple ordered services, supply a `SunderWorkerComposition` containing `ISunderWorkerLifecycleService` instances.

The canonical order is:

1. Composition awaits `PrepareCandidateAsync` for each service in registration order, then awaits `OnCandidateStarted`. Candidate work must be non-destructive.
2. Composition awaits `CommitGenerationAsync` in registration order for the exact Host-assigned generation, then awaits `OnGenerationCommitted`. Commit work must be local and idempotent.
3. The Host awaits `OnBeforeStorageMigrations` from every process participant, runs declared migrations, then composition awaits every `ActivateAsync` and finally `OnActivated`. The worker does not acknowledge activation and the Host cannot publish the candidate until all activation work succeeds.
4. After activation, the worker monitors each service's stable `Completion` task. A fault or unexpected cancellation faults that exact worker generation.
5. During retirement or candidate discard, `OnShutdown` runs first, composition calls `ShutdownAsync` in reverse registration order for every successfully prepared service, and then disposes the package provider. A discarded candidate is shut down without commit or activation.

Pre-migration work may read only Host-mediated state through its dedicated context; normal package data, mutation, secrets, files, and RPC remain unavailable. Externally visible loops start only from activation and must stop promptly during shutdown. Every callback receives cancellation and is bounded by Host deadlines.

After an active Worker V3 generation faults, Runtime can rebuild only that exact package from its prepared recovery factory. Recovery creates a fresh activation id, preserves healthy sibling workers and endpoints, keeps old endpoints stale, and uses a storage transaction across startup, migration, activation, and publication. Runtime never starts a replacement until the failed activation and its package-owned/RPC leases are proven retired. Recovery uses bounded backoff and is suspended after repeated failures; package authors do not need to implement a restart loop or publish recovery notifications.

## Generation Leases

RPC invocations, subscriptions, callback/auth work, and Stack contributor calls hold a lease on the active package generation. Their cancellation token links:

- caller cancellation or disconnect;
- Runtime shutdown; and
- generation retirement.

Once retirement starts, the generation accepts no new leases and returns Runtime unavailability. Runtime cancels existing lease tokens and waits up to 10 seconds. If valid work does not drain, reload is rejected and the old generation remains active and undisposed. Handlers must not suppress cancellation or keep an async iterator alive after cancellation.

After leases drain, Runtime requests protocol shutdown and supervises process exit, killing the process tree if it does not exit within the shared deadline. Runtime shutdown has an overall 15-second deadline.

## App Retirement

App revokes generation capabilities, cancels and drains view operations, unregisters and detaches cached views, stops generation-owned App background work, disposes view data contexts and controls when they implement `IDisposable`, disposes package providers, and then unloads App load contexts. A known recoverable view fault quarantines only that view. A safely attributed package-level UI fault quarantines the package's App activation, then makes one content-keyed fresh-activation attempt after cleanup and authoritative Runtime presentation are proven. Cleanup uncertainty, timeout, or a repeated fault leaves the package on fallback. Both paths are client-local, generate host-owned sanitized notifications, and do not report a Runtime package fault.

Package services must:

- implement `IDisposable` or `IAsyncDisposable` when they own resources;
- unsubscribe from static and host events;
- stop timers, threads, watchers, and child processes;
- release native handles and avoid static references to package types; and
- never dispose host-provided context capabilities.

## Threading

- Host `IPackageContext` capability implementations are thread-safe unless documented otherwise; package services, controls, streams, and caller-owned values do not inherit that guarantee.
- Module configuration and contribution registration have no App UI-thread guarantee; do not touch Avalonia controls there.
- Callback/auth, Stack, and RPC provider code may be invoked concurrently. Make worker-owned services thread-safe.
- Worker lifecycle callbacks run outside a UI context.
- View creation, warmup, presentation, navigation, and cached-view detachment are marshalled to the App UI dispatcher while it is available.
- `IPackageViewNavigationTarget` enters on the UI dispatcher. If code uses `ConfigureAwait(false)`, it must dispatch later UI access itself.

## Cross-Package Contributions

Sunder does not expose a generic CLR extension catalog or share arbitrary package contract assemblies. App contributions are limited to Host-defined registrations such as views and settings views. Runtime packages expose cross-package behavior through schema-first RPC providers declared in their manifests. Worker V3 targets supply immutable `SunderWorkerProviderRegistration` entries in the options returned to the context-configured `SunderWorker.RunAsync` overload.

RPC discovery returns Host-stamped owner and activation metadata. Endpoint references bind to one exact provider activation and never retarget a same-id replacement. Invocations and subscriptions hold provider leases, link cancellation to caller and generation retirement, and prevent provider disposal while work remains active.

Content-bearing callers own an `ISunderRpcCallScope` and must dispose it after calls and streams finish. Providers use the Host-created `SunderRpcInvocationContext`; retaining it never extends its invocation-bound content authority.

Use a typed RPC adapter when package-local code benefits from a CLR interface. `Sunder.Sdk.Stacks`, for example, adapts local Stack contributor interfaces to the `sunder.stack.contributor` RPC contract; those CLR objects never cross the worker process boundary.
