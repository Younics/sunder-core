# SUNDER_PACKAGE_NAME_TEXT

A Sunder extension built with C# services and optional Avalonia UI.

## Prerequisites

- The .NET 10 SDK selected by `global.json`.
- Sunder and the `sunder` CLI on PATH.
- A Sunder-managed .NET 10 runtime for running the package.

## Build and run

If you selected Stack support, complete [Stack setup](#stack-setup) before building.

```sh
dotnet build Sunder.Package.Template.csproj
sunder runtime ensure sunder.runtime.dotnet ">=10.0.0 <11.0.0" sunder.dotnet-worker-module.v1
dotnet run --project Sunder.Package.Template.csproj --no-build
```

With the Avalonia option, open the package's view in Sunder and choose **Create
greeting**. The view calls `Services/SampleService.cs` through its generated
`ISampleService` API. The default headless preset runs the service without adding
a workspace view.

## Make it your own

1. Change the returned greeting in `Services/SampleService.cs`.
2. With Avalonia enabled, edit the heading and controls in `Views/DefaultPackageView.axaml`.
3. Set your display name, summary, and icon in `PackageMetadata.cs` and `Assets/`.
4. Rebuild and run to see the result.

Add ordinary async methods to the partial `[SunderService]` class to grow your API.
Use business records for structured values. Sunder generates typed clients,
registration, dependency injection, and the Worker/App connection.

## Develop with reload

In one terminal:

```sh
dotnet run --project Sunder.Package.Template.csproj --no-build -- --watch
```

In a second terminal, inside this project:

```sh
dotnet watch build
```

The source watcher builds changes; Sunder watches `bin/Debug/net10.0/sunder-dev`
and loads completed package updates. Keep both processes running.

## Package and install

Set your extension's `Version` in the project file, then run:

```sh
dotnet msbuild Sunder.Package.Template.csproj -t:PackSunderPackage -p:Configuration=Release
```

Use the archive path printed by Pack:

```sh
sunder dev package validate <archive.sunderpkg>
sunder package install --file <archive.sunderpkg>
```

Pack builds the declared platform targets into one `.sunderpkg`. The worker uses
Sunder's shared execution runtime.

## Stack setup

If `Stacks/` exists, select the platform contract before the first build. Supply
the matching Core catalog for your Sunder environment:

```sh
dotnet restore Sunder.Package.Template.csproj
dotnet msbuild Sunder.Package.Template.csproj -t:ImplementSunderPlatformApi -p:SunderContractId=sunder.stack.contributor -p:SunderPlatformCatalogPath=/path/to/sunder-platform-contracts.v1.json
```

Alternatively, use `SunderRegistryOrigin` for your Registry's published catalog.
The generated contributor is an empty starting point; implement discovery,
export, preview, and import in `Stacks/StackSupport.cs`.

## Source control and APIs

Commit your project, source, assets, and `sunder-contracts.lock.json`. Keep generated
outputs under `bin/` and `obj/` out of source control. The starter service is
private to this package and builds directly. Public APIs use an explicit
contract-versioning workflow, and imported APIs are selected through the build
tooling before use.

## Help and next steps

- **CLI not found:** put `sunder` on PATH or pass `-p:SunderCliPath=<path>` to Run.
- **Generated interface missing:** build the project and use the namespace derived
  from its package ID.
- [C# SDK tutorial](https://github.com/Younics/sunder-core/blob/main/languages/csharp/dotnet/packages/Sunder.Sdk/README.md)
- [Avalonia views](https://github.com/Younics/sunder-core/blob/main/languages/csharp/dotnet/packages/Sunder.Sdk.Avalonia/README.md)
- [Stack tutorial](https://github.com/Younics/sunder-core/blob/main/languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/README.md)
- [Build and dependency reference](https://github.com/Younics/sunder-core/blob/main/docs/package-development/MSBUILD-REFERENCE.md)
