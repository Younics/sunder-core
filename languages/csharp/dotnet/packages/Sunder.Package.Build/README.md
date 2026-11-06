<p><img src="https://raw.githubusercontent.com/Younics/sunder-core/main/languages/csharp/dotnet/packages/Sunder.Sdk/icon.png" alt="Sunder" width="96" height="96" /></p>

# Sunder.Package.Build

**Build, develop, and distribute Sunder packages with standard .NET commands.**

This MSBuild package turns a C# project into a [Sunder](https://sunderapp.io)
extension. It generates service APIs, worker and optional App composition, package
metadata, local development output, and distributable `.sunderpkg` archives.

Use it when you want to add services or desktop UI to Sunder while keeping your
source in one normal .NET project.

## What it handles

- Generates typed clients and registration from `[SunderService]` classes.
- Builds isolated worker and App outputs from the authored project.
- Produces a `sunder-dev` folder for local execution and reload.
- Packages declared Windows, macOS, and Linux targets into one archive.
- Copies package assets, calculates content hashes, and validates the final archive.

## Install

Requires the **.NET 10 SDK**. Running the result also requires Sunder and its CLI.

```sh
dotnet add package Sunder.Package.Build --private-assets all
```

Keep `PrivateAssets="all"`: this is build-time tooling for your project. A typical
generated package uses:

```xml
<ItemGroup>
  <PackageReference Include="Sunder.Sdk.Worker" Version="1.0.1" />
  <PackageReference Include="Sunder.Package.Build" Version="1.0.1" PrivateAssets="all" />
</ItemGroup>
```

For the complete project setup, use [Sunder.Package.Templates](../Sunder.Package.Templates/README.md).

## Tutorial: from source to an installable extension

### 1. Create a package

```sh
dotnet new install Sunder.Package.Templates
dotnet new sunder-package --name HelloSunder --packageId example.hello --packageName "Hello Sunder" --output HelloSunder
cd HelloSunder
```

The project sets `SunderPackageProject=true`, contains a service in `Services/`,
and declares package identity in `PackageMetadata.cs`. Add `--withAvalonia` when
creating the project if you also want a desktop view.

### 2. Build development output

```sh
dotnet build
```

The folder `bin/Debug/net10.0/sunder-dev` is the loadable development package.
It contains generated metadata and payload files for your current platform.
Edit your C# source and assets; generated manifests are build outputs.

### 3. Run and rebuild on changes

Prepare the execution runtime, then load the development package:

```sh
sunder runtime ensure sunder.runtime.dotnet ">=10.0.0 <11.0.0" sunder.dotnet-worker-module.v1
dotnet run --no-build -- --watch
```

In a second terminal, inside `HelloSunder`, run:

```sh
dotnet watch build
```

The build watcher produces new output; Sunder's watcher loads successful package
updates. These are two processes, so keep both running while developing.

### 4. Create the archive

Set your package's `<Version>` in `HelloSunder.csproj`, then run:

```sh
dotnet msbuild -t:PackSunderPackage -p:Configuration=Release
```

Pack builds the declared targets and reports the `.sunderpkg` path. By default,
the template declares `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`,
and `osx-arm64`. If you add native dependencies, supply the corresponding assets
for every platform you declare.

### 5. Validate and install

Use the archive path printed by Pack:

```sh
sunder dev package validate <archive.sunderpkg>
sunder package install --file <archive.sunderpkg>
```

Validation checks package structure and content integrity. Installation makes the
package available to Sunder through its normal package-management flow.

## Add assets and package metadata

Put an image at `Assets/icon.png`, then reference its package-relative path in
`PackageMetadata.cs`:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "example.hello",
    Name = "Hello Sunder",
    Summary = "A greeting tool for your Sunder workspace.",
    Icon = "assets/icon.png")]
```

The build copies `Assets/` into the package's shared asset layer. Keep the package
ID stable after publication. Your extension's version is independent of the
version of this build-tool NuGet.

## Common configuration

| Property | Purpose |
| --- | --- |
| `SunderPackageProject` | Enables single-project package authoring; set to `true`. |
| `Version` | Your extension's version. |
| `SunderPackageRuntimeIdentifiers` | Semicolon-separated target platforms. |
| `SunderPackageAssetsDirectory` | Source asset directory; defaults to `Assets`. |
| `SunderPackageFileName` | Overrides the generated archive filename. |
| `SunderPackageOutputPath` | Overrides the explicit Pack target's output path. |

Use the [MSBuild reference](../../../../../docs/package-development/MSBUILD-REFERENCE.md)
for advanced properties, API selection, generated-role test references, native
signing, and retained-artifact packaging.

## Dependencies and public APIs

NuGet references supply build-time libraries. A dependency on another installed
Sunder package is a separate concept. Select a package API through the build targets
and keep `sunder-contracts.lock.json` in source control; the tooling generates
the corresponding clients and package requirements.

Private starter services build directly. Public service contracts use the explicit
accept/check/freeze workflow described in the
[service authoring guide](../../../../../docs/package-development/ORDINARY-SERVICES.md).

## Troubleshooting

| Symptom | Next step |
| --- | --- |
| No development package appears | Confirm `SunderPackageProject=true` and use the template's complete project configuration. |
| Editing source does not reload | Run both `dotnet watch build` and the Sunder output watcher. |
| Contract cache input is missing | Restore the selected contracts explicitly; ordinary builds use the existing selection. |
| A platform's native library is missing | Supply that RID's asset or narrow your declared supported platforms. |

## More resources

- [Create a .NET package](../Sunder.Package.Templates/README.md)
- [C# SDK tutorial](../Sunder.Sdk/README.md)
- [Build, validate, publish, and version](../../../../../docs/package-development/BUILD-PUBLISH-VERSIONING.md)
- [Troubleshooting guide](../../../../../docs/package-development/TROUBLESHOOTING.md)
- [Report an issue](https://github.com/Younics/sunder-core/issues)

Licensed under [MIT](LICENSE).
