# Sunder.Sdk.Worker

> **Release/source channel:** The NuGet README describes that published package version. The repository copy tracks current source and may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

`Sunder.Sdk.Worker` hosts a standalone .NET Runtime worker using the staged `sunder.worker.v1` lifecycle.

For normal package authoring, use `dotnet new sunder-package`. One authored project
contains services and optional views; build tooling generates the Worker entry point,
provider registration and DI. The low-level lifecycle primitives below remain useful
for custom runtime integration, not required bootstrap for the template.

Implement `ISunderWorkerLifecycleService` for staged lifecycle behavior. A package
may also declare one `ISunderWorkerSettingsSchemaProvider` to supply its settings
schema to generated composition. The provider is Worker-owned and DI-managed; schema
construction runs before activation and must be deterministic without Host I/O.

```powershell
dotnet add package Sunder.Sdk.Worker
```

Worker projects use exact-RID, framework-dependent publish builds. Their package entry point is the managed `.dll` loaded by the exact bound execution runtime. Reference the exact coordinated Worker version and private build tooling, then declare one explicit target for the current build RID:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Sunder.Sdk.Worker" Version="1.1.0" />
  <PackageReference Include="Sunder.Package.Build" Version="[1.1.0,1.2.0)" PrivateAssets="all" />
  <SunderPackageTarget Include="runtime/$(RuntimeIdentifier)"
                       Role="runtime"
                       Rid="$(RuntimeIdentifier)"
                       Kind="worker" />
</ItemGroup>
```

`Sunder.Package.Build` fixes Worker publishing to `SelfContained=false`, `UseAppHost=false`, `PublishSingleFile=false`, and `PublishTrimmed=false`; package projects do not author those profile properties. Set `SunderPackagePrimaryProject=true` on the canonical Worker/package csproj. A normal build emits its current exact-RID leaf and composed development tree; `PackSunderPackage` invokes guarded builds of that same csproj for every configured RID and aggregates the validated leaves without a wrapper project. Each thin leaf contains the managed module, `.runtimeconfig.json`, `.deps.json`, and package-owned dependencies, but no apphost or .NET runtime closure. A worker assembly does not implement `ISunderRuntimePackageModule`.

Register the providers declared by the package manifest and run the worker from the process entry point:

```csharp
using Sunder.Sdk.Worker;

await SunderWorker.RunAsync(_ => new SunderWorkerOptions(
[
    new SunderWorkerProviderRegistration(
        "example.provider",
        "example.rpc",
        "1.0.0",
        descriptorSha256,
        abiSha256,
        handler),
])
{
    OnActivated = (context, cancellationToken) => ValueTask.CompletedTask,
    OnShutdown = (context, cancellationToken) => ValueTask.CompletedTask,
});
```

Standard input and standard output are reserved exclusively for exact `Content-Length` framed protocol traffic. Use `SunderWorker.WriteDiagnostic` for a bounded, single-line stderr diagnostic and never include secrets.

The worker binds `host.hello` to the sanitized `SUNDER_WORKER_PROTOCOL`, `SUNDER_PACKAGE_ID`, `SUNDER_PACKAGE_VERSION`, `SUNDER_ACTIVATION_ID`, and `SUNDER_SESSION_ID` environment. Runtime also supplies only the minimal OS process/home/locale/package-temp baseline required by local tools and child processes; it does not inherit tokens, cloud credentials, profilers, startup hooks, or Host bootstrap variables. Registered providers must exactly match the Host declaration. The Host remains the deadline authority for inbound invocations.

Packages that intentionally consume a configured Docker CLI connection declare `<SunderSdkCapability Include="docker-environment.v1" />`. With install/update consent, Runtime additionally copies only `DOCKER_HOST`, `DOCKER_CONTEXT`, `DOCKER_CONFIG`, `DOCKER_CERT_PATH`, and `DOCKER_TLS_VERIFY`. Secret-bearing Docker variables remain excluded.

`SunderWorkerContext.Rpc` supports provider lookup, discovery, catalog watch, unary invocation, server streams, caller-owned call scopes, and invariant reporting. Invocation-bound `SunderRpcInvocationContext` content registration and opening are supported; opened content is released when its returned stream is disposed. Generated RPC handlers expose their exact method-level declared-error catalog, which `SunderWorkerProviderRegistration` snapshots. Any local domain error not present in that catalog is normalized to the generic provider fault before crossing the process boundary.

## Worker lifecycle

The context-configured `SunderWorker.RunAsync` entry point infers the exact `worker-protocol.v1` Host requirement:

```csharp
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Storage;
using Sunder.Sdk.Worker;

await SunderWorker.RunAsync(context => new SunderWorkerOptions(
[
    new SunderWorkerProviderRegistration(
        "example.provider",
        "example.rpc",
        "1.0.0",
        descriptorSha256,
        abiSha256,
        handler),
])
{
    SettingsSchema = new PackageSettingsSchema(
        "Example settings.",
        [
            new PackageSettingsSection(
                "general",
                "General",
                null,
                [
                    new PackageSettingsField(
                        "enabled",
                        "Enabled",
                        PackageSettingsFieldKind.Boolean,
                        defaultValue: "true"),
                ]),
        ]),
    OnCandidateStarted = cancellationToken => ValueTask.CompletedTask,
    OnGenerationCommitted = (generation, cancellationToken) => ValueTask.CompletedTask,
    StateMigrations =
    [
        PackageStorageKeyMigration.Exact("example.legacy.v1", "example.current.v1"),
    ],
    OnBeforeStorageMigrations = async (preparation, cancellationToken) =>
    {
        _ = await preparation.GetStateValueAsync("example.legacy.v1", cancellationToken);
    },
    OnActivated = async (activation, cancellationToken) =>
    {
        var enabled = await activation.Settings.GetValueAsync("enabled", cancellationToken);
        await activation.State.SetValueAsync("last-enabled", enabled ?? "false", cancellationToken);
        await activation.Files.WriteAsync("cache/enabled.txt", "true"u8.ToArray(), cancellationToken);
        var token = await activation.Secrets.GetSecretAsync("provider.token", cancellationToken);
        activation.Logging.LoggerFactory.CreateLogger("Example.Worker")
            .LogInformation("Worker activated; token configured: {Configured}", token is not null);
        await activation.Logging.Events.WriteAsync(
            PackageLogLevel.Information,
            "worker.activated",
            "Worker activation completed.",
            cancellationToken: cancellationToken);
    },
    OnShutdown = (shutdown, cancellationToken) => ValueTask.CompletedTask,
});
```

Package-owned DI can participate in the same lifecycle through a worker-native composition:

```csharp
Composition = new SunderWorkerComposition(
    serviceProvider,
    lifecycleServices); // IEnumerable<ISunderWorkerLifecycleService>
```

For each `ISunderWorkerLifecycleService`, composition awaits `PrepareCandidateAsync` in registration order before generation commit, then awaits `CommitGenerationAsync` for the exact Host-assigned generation. After storage-migration preparation and declared migrations, it awaits every `ActivateAsync` before the worker acknowledges activation and the Host can publish the candidate. Only then does the worker monitor each stable `Completion` task. Shutdown calls `ShutdownAsync` in reverse registration order and disposes the package provider; a prepared candidate that is discarded is shut down without commit or activation. The option-level callbacks run after the corresponding composition phase, except `OnShutdown`, which runs before composition shutdown.

Configuration completes before the immutable contribution catalog is advertised. Candidate preparation is non-destructive. Generation commit is idempotent for one exact Host-assigned generation. `OnBeforeStorageMigrations` exposes only generation-fenced read-only state; the Host prepares every worker before running declared migrations. The stable `RoleLocalWorkspace` capability is present during configuration, but its root and path resolution fail closed until the Host binds it immediately before activation. Activation completes before the worker acknowledges publication readiness, so provider dispatch cannot race package initialization.

`SunderWorkerContext.Settings`, `State`, `Files`, `Secrets`, and `Logging` implement the canonical SDK interfaces through Host-mediated validation, persistence, encryption, redaction, and log routing. String data over 65,536 UTF-8 bytes and all file contents use bounded, Host-issued temporary payload handles rather than increasing the control-frame limit. String values and secrets remain limited to 1 MiB; files remain limited to 16 MiB. Logging uses a dedicated bounded lane so ordinary RPC saturation cannot block lifecycle logs. The settings schema is immutable after `worker.ready`, and the Host stamps package identity when projecting it to the App.

Worker V1 is selected before launch and never falls back to another protocol. The complete protocol and Host-authority boundary is specified in [`docs/SUNDER-WORKER-PROTOCOL-V1.md`](../../../../../docs/SUNDER-WORKER-PROTOCOL-V1.md).
