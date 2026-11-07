<p><img src="https://raw.githubusercontent.com/Younics/sunder-core/main/languages/csharp/dotnet/packages/Sunder.Sdk/icon.png" alt="Sunder" width="96" height="96" /></p>

# Sunder.Sdk

**Build Sunder extensions with ordinary C# services.**

[Sunder](https://sunderapp.io) is an extensible desktop workspace. Packages add
tools, integrations, background services, and user interfaces. `Sunder.Sdk` provides
the contracts for those packages: service authoring, package identity, settings,
persistent storage, secrets, logging, and App integration.

Write your business logic once. Sunder's build tooling generates typed service
interfaces, clients, registration, and the connection between your package's UI
and its worker process.

## What can I build?

- A productivity panel backed by a C# service.
- An integration that stores configuration and credentials through Sunder.
- A file-processing or automation tool with cancellable operations.
- A reusable service that other packages can explicitly select and call.

The SDK targets **.NET 10**. Use the .NET 10 SDK to develop; install Sunder and put
the `sunder` CLI on your PATH to run packages. Sunder manages the execution runtime
used by installed workers separately from your development SDK.

## Install

For an existing project:

```sh
dotnet add package Sunder.Sdk
```

For a complete runnable package, the template is the easiest starting point. It
adds `Sunder.Sdk.Worker` (which includes this SDK), the build tooling, package
metadata, and a sample service.

## Tutorial: a greeting panel that remembers its last visitor

### 1. Create the package

```sh
dotnet new install Sunder.Package.Templates
dotnet new sunder-package --name HelloSunder --packageId example.hello --packageName "Hello Sunder" --withAvalonia --output HelloSunder
cd HelloSunder
```

The project contains `Services/SampleService.cs`, `PackageMetadata.cs`, and a
`Views/` directory. It is one authored project; Sunder builds the worker and UI
parts separately.

### 2. Give the service useful behavior

Replace `Services/SampleService.cs` with:

```csharp
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace HelloSunder;

[SunderService]
public sealed partial class SampleService(IPackageContext context)
{
    public async Task<string> GreetAsync(
        [SunderValueConstraints(MinLength = 1, MaxLength = 120)] string name,
        CancellationToken cancellationToken = default)
    {
        var previous = await context.Storage.State.GetValueAsync(
            "last-visitor", cancellationToken);
        await context.Storage.State.SetValueAsync(
            "last-visitor", name, cancellationToken);

        return previous is null
            ? $"Hello, {name}! You are our first visitor."
            : $"Hello, {name}! Our previous visitor was {previous}.";
    }
}
```

`IPackageContext` is injected by Sunder. State is stored for this package and
survives package reloads. The `CancellationToken` lets a closing view or retiring
package cancel an outstanding operation.

### 3. Call the service from your UI

The generated view model already calls `ISampleService.GreetAsync`. For package
ID `example.hello`, that interface is generated in:

```csharp
using Sunder.Generated.Packages.Example_Hello_Services;
```

The UI receives this interface through constructor injection. Its call reaches the
worker through Sunder; the UI does not instantiate `SampleService`.

### 4. Build and run

```sh
dotnet build
sunder runtime ensure sunder.runtime.dotnet ">=10.0.0 <11.0.0" sunder.dotnet-worker-module.v1
dotnet run --no-build
```

Open **Hello Sunder** in the App, enter a name, and choose **Create greeting**.
Enter another name to see the previous visitor in the response. The first-visitor
message appears when the package has no saved `last-visitor` value.

To package your extension:

```sh
dotnet msbuild -t:PackSunderPackage -p:Configuration=Release
```

Use the `.sunderpkg` path reported by the command to validate or install it with
`sunder dev package validate <archive.sunderpkg>` and
`sunder package install --file <archive.sunderpkg>`.

## Choose the right package capability

| Need | API or package |
| --- | --- |
| Package identity and metadata | `Sunder.Sdk.Packaging` assembly attributes |
| Ordinary async services and streams | `Sunder.Sdk.Services` |
| User-editable preferences | `context.Settings`, backed by a declared settings schema |
| Operational state and files | `context.Storage.State` and `context.Storage.Files` |
| Credentials | `context.Secrets` |
| Package-scoped logs | `context.Logging` |
| Worker initialization and background work | [Sunder.Sdk.Worker](../Sunder.Sdk.Worker/README.md) |
| Native desktop views | [Sunder.Sdk.Avalonia](../Sunder.Sdk.Avalonia/README.md) |
| Portable setup export/import | [Sunder.Sdk.Stacks](../Sunder.Sdk.Stacks/README.md) |

Store mutable data through the package context rather than writing into the
installed package folder. Use secrets for credentials and settings for declared
user preferences; operational state is a separate store.

## Services, models, and other packages

Service methods can return `Task<T>`, `ValueTask<T>`, and `IAsyncEnumerable<T>`.
Use ordinary records for business values and `SunderContent` for file/stream
content. The tooling generates the transport code from your source types.

Services are package-private by default, making the template suitable for a UI
and its own backend. To expose a public API or consume another package, use the
explicit contract-selection and versioning workflow. Keep the resulting
`sunder-contracts.lock.json` in source control. See the
[ordinary-service guide](../../../../../docs/package-development/ORDINARY-SERVICES.md).

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Generated service namespace is missing | Build the template project; generated namespaces use the package ID, not the display name. |
| `sunder` cannot be found | Install the CLI or set `SunderCliPath` on the `dotnet run` command. |
| Worker cannot start | Ensure the required Sunder-managed .NET runtime is installed. |
| A setting write is rejected | Declare its key and value type in a Worker settings schema. |

## Learn more

- [Getting started](../../../../../docs/package-development/GETTING-STARTED.md)
- [.NET SDK reference](../../../../../docs/package-development/DOTNET-SDK-REFERENCE.md)
- [Data, settings, secrets, and logging](../../../../../docs/package-development/DATA-AND-LOGGING.md)
- [Package templates](../Sunder.Package.Templates/README.md) and [build tooling](../Sunder.Package.Build/README.md)
- [Report an issue](https://github.com/Younics/sunder-core/issues)

Licensed under [GPL-3.0](LICENSE).
