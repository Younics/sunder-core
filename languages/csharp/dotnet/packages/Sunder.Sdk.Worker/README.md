<p><img src="https://raw.githubusercontent.com/Younics/sunder-core/main/languages/csharp/dotnet/packages/Sunder.Sdk/icon.png" alt="Sunder" width="96" height="96" /></p>

# Sunder.Sdk.Worker

**Run your package's C# services in a Sunder-managed worker process.**

Use this package for the backend of a [Sunder](https://sunderapp.io) extension:
headless services, integration clients, initialization, and long-running work.
It includes the core SDK and adds worker lifecycle hooks, settings-schema
providers, and Host-backed access to package data and logs.

Sunder starts and supervises the worker. In a template-created project, build
tooling generates the entry point, dependency injection, and service registration.
You supply the service behavior and any lifecycle work your package needs.

## Install and prerequisites

- .NET 10 SDK for development.
- Sunder and its CLI for running the package.
- A Sunder-managed .NET 10 execution runtime, selected before launch.

```sh
dotnet add package Sunder.Sdk.Worker
dotnet add package Sunder.Package.Build --private-assets all
```

For a new package, use the template below; it configures both references.

## Tutorial: initialize a worker with a user preference

This example adds a **Default name** setting, records it at activation, and writes
a startup log. It is useful for integrations that need to load configuration
before accepting service calls.

### 1. Create a headless package

```sh
dotnet new install Sunder.Package.Templates
dotnet new sunder-package --name HelloWorker --packageId example.worker --packageName "Hello Worker" --output HelloWorker
cd HelloWorker
```

### 2. Declare a settings schema

Add `Settings/GreetingSettings.cs`:

```csharp
using Sunder.Sdk.Settings;
using Sunder.Sdk.Worker;

namespace HelloWorker;

public sealed class GreetingSettings : ISunderWorkerSettingsSchemaProvider
{
    public PackageSettingsSchema SettingsSchema { get; } = new(
        "Greeting preferences.",
        [new PackageSettingsSection("general", "General", null,
            [new PackageSettingsField(
                "default-name", "Default name", PackageSettingsFieldKind.Text,
                defaultValue: "Sunder")])]);
}
```

Sunder discovers the provider and renders the setting in the App. A package can
have one settings-schema provider. Construct the schema without reading Host data;
the worker advertises it before activation.

### 3. Add activation behavior

Add `Services/StartupService.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Worker;

namespace HelloWorker;

public sealed class StartupService : ISunderWorkerLifecycleService
{
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    public ValueTask PrepareCandidateAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask CommitGenerationAsync(
        SunderWorkerGenerationContext generation,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public async ValueTask ActivateAsync(
        SunderWorkerContext context,
        CancellationToken cancellationToken = default)
    {
        var name = await context.Settings.GetValueAsync("default-name", cancellationToken);
        await context.State.SetValueAsync("last-startup-name", name ?? "Sunder", cancellationToken);
        context.Logging.LoggerFactory.CreateLogger<StartupService>()
            .LogInformation("Greeting worker is ready.");
    }

    public ValueTask ShutdownAsync(
        SunderWorkerShutdownContext context,
        CancellationToken cancellationToken = default)
    {
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
```

Generated composition discovers this lifecycle service. Activation finishes before
the package is made available for calls. This example has no ongoing task;
`Completion` remains stable until shutdown. For real background processing, expose
the owned task and stop/drain it in `ShutdownAsync`.

### 4. Run and inspect

```sh
dotnet build
sunder runtime ensure sunder.runtime.dotnet ">=10.0.0 <11.0.0" sunder.dotnet-worker-module.v1
dotnet run --no-build
```

In Sunder, open the package's settings and logs. You should see **Default name**
and a **Greeting worker is ready** entry. Change the preference and restart the
development package to apply it during the next activation. A headless package
does not add a view to the workspace.

## Lifecycle at a glance

| Phase | Typical use |
| --- | --- |
| `PrepareCandidateAsync` | Validate local configuration and prepare resources without durable mutations. |
| `CommitGenerationAsync` | Bind idempotent local state to the Host-assigned generation. |
| `ActivateAsync` | Read settings, initialize package state, and start owned work. |
| `Completion` | Let Sunder observe an unexpected failure of ongoing work. |
| `ShutdownAsync` | Cancel and drain owned work and release resources. |

Shutdown can also run for a prepared candidate that never activated. Keep cleanup
safe for partially initialized state. Let exceptions from startup propagate so
Sunder can report an unsuccessful activation.

## Package data and diagnostics

| Worker capability | Use it for |
| --- | --- |
| `context.Settings` | Validated user preferences and schema defaults |
| `context.State` | Package-owned operational key/value data |
| `context.Files` | Package-owned file data |
| `context.Secrets` | Credentials and sensitive configuration |
| `context.Logging` | Structured events and package-scoped application logs |

Standard output belongs to the worker protocol. Use package logging rather than
`Console.WriteLine`; use `SunderWorker.WriteDiagnostic` for low-level stderr diagnostics.

## Build a distributable package

```sh
dotnet msbuild -t:PackSunderPackage -p:Configuration=Release
```

The archive contains your managed module and package dependencies. Sunder supplies
the shared execution runtime. For platform-specific dependencies and custom
bootstrap, see the [Worker integration reference](../../../../../docs/package-development/DOTNET-WORKER-REFERENCE.md).

## Troubleshooting and next steps

- **Missing runtime:** run the `sunder runtime ensure` command before launch.
- **Unknown setting:** declare the key in the schema, then rebuild and reload.
- **Worker fails at startup:** inspect package logs and check that preparation does
  not read activation-only Host services.
- [Service authoring](../Sunder.Sdk/README.md)
- [Activation and disposal](../../../../../docs/package-development/ACTIVATION-AND-DI.md)
- [Data and logging](../../../../../docs/package-development/DATA-AND-LOGGING.md)
- [Report an issue](https://github.com/Younics/sunder-core/issues)

Licensed under [MIT](../Sunder.Sdk/LICENSE).
