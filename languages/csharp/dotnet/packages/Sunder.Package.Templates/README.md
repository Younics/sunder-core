<p><img src="https://raw.githubusercontent.com/Younics/sunder-core/main/languages/csharp/dotnet/packages/Sunder.Sdk/icon.png" alt="Sunder" width="96" height="96" /></p>

# Sunder.Package.Templates

**Create your first Sunder package with `dotnet new`.**

Start with a working C# service, add an optional Avalonia desktop view, and build
an installable extension for [Sunder](https://sunderapp.io). The template configures
the SDK, build tooling, package metadata, and generated service integration so you
can start with application code.

Choose a headless service for automation or integrations, a desktop package for
interactive tools, or add Stack support to share portable package setup.

## Prerequisites

- **.NET 10 SDK** to create and build the project. The generated `global.json`
  specifies the SDK version used by the template.
- **Sunder and the `sunder` CLI** to run and install the result.
- A Sunder-managed **.NET 10 runtime**, selected before running the package.

## Install

```sh
dotnet new install Sunder.Package.Templates
```

This installs a project template. You do not add it as a library reference to your
application. Use `dotnet new sunder-package --help` to inspect its options.

## Tutorial: build a greeting extension

### 1. Create a project with a desktop view

```sh
dotnet new sunder-package --name HelloSunder --packageId example.hello --packageName "Hello Sunder" --withAvalonia --output HelloSunder
cd HelloSunder
```

The generated project looks like this:

```text
HelloSunder/
  HelloSunder.csproj
  global.json
  PackageMetadata.cs
  sunder-contracts.lock.json
  Services/SampleService.cs
  Assets/icon.png
  Views/DefaultPackageView.axaml
  Views/DefaultPackageView.axaml.cs
  Views/DefaultPackageViewModel.cs
```

### 2. Explore the service

`Services/SampleService.cs` contains a `[SunderService]` class with `GreetAsync`
and a streaming `GreetManyAsync` method. The greeting implementation is ordinary C#:

```csharp
public Task<string> GreetAsync(string name, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    return Task.FromResult($"Hello, {name}!");
}
```

The build generates `ISampleService`. The view model receives it through its
constructor and calls it when the user chooses **Create greeting**. Change the
returned text to personalize the extension.

### 3. Build and run

```sh
dotnet build
sunder runtime ensure sunder.runtime.dotnet ">=10.0.0 <11.0.0" sunder.dotnet-worker-module.v1
dotnet run --no-build
```

Open **Hello Sunder** in the workspace, enter your name, and create a greeting.
Sunder loads the UI and launches its worker from the generated development package.

### 4. Develop with reload

In one terminal, load the package with output watching:

```sh
dotnet run --no-build -- --watch
```

In another terminal, inside the project directory:

```sh
dotnet watch build
```

Edit the service or view. The first process watches built packages; the second
rebuilds your source. Successful updates are loaded by Sunder.

### 5. Create an installable archive

```sh
dotnet msbuild -t:PackSunderPackage -p:Configuration=Release
```

Pack reports the `.sunderpkg` path. Validate and install that file:

```sh
sunder dev package validate <archive.sunderpkg>
sunder package install --file <archive.sunderpkg>
```

Your extension starts at version `1.0.0`; change `Version` in its project when
releasing an update. That version belongs to your extension, independently of the
template and SDK package versions.

## Choose your starting point

| Option | What you get |
| --- | --- |
| Default | A headless C# worker with a sample service. |
| `--withAvalonia` | A view, view model, and App SDK reference. |
| `--withAvalonia --noDefaultView` | Avalonia references so you can author your own first view. |
| `--withStacks` | A Stack contributor stub for portable export/import. |
| `--withPackageDependency` | An explicit installed-package dependency. |

Always supply `--packageId` (a stable lowercase dotted ID) and `--packageName`
(a user-facing name). `--name` selects the C# project name, and `--output` selects
an empty destination directory.

Stack contributors need a selected `sunder.stack.contributor` platform contract
before their first build. Follow the [Stack tutorial](../Sunder.Sdk.Stacks/README.md)
to provide a Core platform catalog or select one from your Registry.

An installed-package dependency also needs `--dependencyPackageId` and an explicit
`--dependencyVersionRange`, for example `">=2.0.0 <3.0.0"`. Choose the range for
that dependency. When you select another package's API, tooling derives its
dependency automatically.

## What should I edit or commit?

Edit services, models, views, assets, and `PackageMetadata.cs`. Commit the project,
source, and `sunder-contracts.lock.json`. Sunder generates Worker bootstrap, typed
clients, registration, manifests, and `sunder-dev` output.

The sample service is private to your package and builds directly. Public APIs
have an explicit contract-evolution workflow; see the
[service guide](../../../../../docs/package-development/ORDINARY-SERVICES.md).

## Troubleshooting and next steps

- **Template not found:** rerun the install command and check `dotnet new list sunder`.
- **SDK selection fails:** install a .NET SDK allowed by the generated `global.json`.
- **CLI not found:** put `sunder` on PATH or use `-p:SunderCliPath=<path>` with `dotnet run`.
- **Stack build asks for a platform import:** select the catalog before building.
- [Add storage and useful service behavior](../Sunder.Sdk/README.md)
- [Style and extend the desktop view](../Sunder.Sdk.Avalonia/README.md)
- [Build and packaging options](../Sunder.Package.Build/README.md)
- [Prefer TypeScript or JavaScript?](../../../../typescript/node/packages/create-sunder-package/README.md)
- [Report an issue](https://github.com/Younics/sunder-core/issues)

Licensed under [GPL-3.0](LICENSE).
