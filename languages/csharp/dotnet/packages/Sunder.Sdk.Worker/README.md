# Sunder.Sdk.Worker

> **Release/source channel:** The NuGet README describes that published package version. The repository copy tracks current source and may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

`Sunder.Sdk.Worker` hosts a standalone .NET Runtime worker. Existing workers use `sunder.worker.v1`; native Runtime packages can opt into the staged `sunder.worker.v2` lifecycle.

```powershell
dotnet add package Sunder.Sdk.Worker
```

Worker projects are exact-RID, self-contained executables. Reference the exact coordinated Worker version and private build tooling, then declare one explicit target for the current build RID:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <UseAppHost>true</UseAppHost>
  <PublishSingleFile>false</PublishSingleFile>
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

Build one native leaf per supported RID and combine those validated leaves with an aggregate package project. The worker apphost and complete self-contained publish closure are stored only in that exact target layer; a worker assembly does not implement `ISunderRuntimePackageModule`.

Register the providers declared by the package manifest and run the worker from the process entry point:

```csharp
using Sunder.Sdk.Worker;

await SunderWorker.RunAsync(new SunderWorkerOptions(
[
    new SunderWorkerProviderRegistration(
        "example.provider",
        "example.rpc",
        "1.0.0",
        descriptorSha256,
        handler),
])
{
    OnActivated = (rpc, cancellationToken) => ValueTask.CompletedTask,
    OnShutdown = (context, cancellationToken) => ValueTask.CompletedTask,
});
```

Standard input and standard output are reserved exclusively for exact `Content-Length` framed protocol traffic. Use `SunderWorker.WriteDiagnostic` for a bounded, single-line stderr diagnostic and never include secrets.

The worker binds `host.hello` to the sanitized `SUNDER_WORKER_PROTOCOL`, `SUNDER_PACKAGE_ID`, `SUNDER_PACKAGE_VERSION`, `SUNDER_ACTIVATION_ID`, and `SUNDER_SESSION_ID` environment. Registered providers must exactly match the Host declaration. The Host remains the deadline authority for inbound invocations.

The `ISunderRpcClient` supplied to `OnActivated` supports provider lookup, discovery, catalog watch, unary invocation, and server streams. Caller-owned `CreateCallScopeAsync` is deliberately unsupported in worker V1. Invocation-bound `SunderRpcInvocationContext` content registration and opening are supported; opened content is discarded when its returned stream is disposed.

## Worker V2

Call `SunderWorkerV2.RunAsync` to infer the exact `worker-protocol.v2` Host requirement and use the native staged lifecycle:

```csharp
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Worker;

await SunderWorkerV2.RunAsync(context => new SunderWorkerV2Options(
[
    new SunderWorkerProviderRegistration(
        "example.provider",
        "example.rpc",
        "1.0.0",
        descriptorSha256,
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

Configuration completes before the immutable contribution catalog is advertised. Candidate startup is non-destructive. Generation commit is idempotent for one exact Host-assigned generation. The V2 activation callback completes before the worker acknowledges activation, so provider dispatch cannot race package initialization.

`SunderWorkerContext.Settings`, `State`, `Files`, `Secrets`, and `Logging` implement the canonical SDK interfaces through Host-mediated validation, persistence, encryption, redaction, and log routing. String data over 65,536 UTF-8 bytes and all file contents use bounded, Host-issued temporary payload handles rather than increasing the control-frame limit. String values and secrets remain limited to 1 MiB; files remain limited to 16 MiB. Logging uses a dedicated bounded lane so ordinary RPC saturation cannot block lifecycle logs. The settings schema is immutable after `worker.ready`, and the Host stamps package identity when projecting it to the App.

V2 is selected before launch. A V2 worker never falls back to V1, and a worker target without `worker-protocol.v2` remains on V1. The complete protocol and Host-authority boundary is specified in [`docs/SUNDER-WORKER-PROTOCOL-V2.md`](../../../../../docs/SUNDER-WORKER-PROTOCOL-V2.md).
