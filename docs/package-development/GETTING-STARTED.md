# Getting Started

This guide tracks current source on the Sunder SDK 1.1 line and .NET 10. For released
behavior, use the matching `sdk/v*` tag and README in the released template package.

## Prerequisites

- .NET 10 SDK for building.
- Sunder App and the `sunder` CLI for package development and installation.
- A feed containing the coordinated SDK packages, or locally packed source candidates.

Sunder-managed execution runtimes are separate from your development SDK. Install
required package runtimes through the App or CLI when prompted.

## Create A Package

```sh
dotnet new install Sunder.Package.Templates::1.1.0
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --output MyPackage
dotnet restore MyPackage/MyPackage.csproj
dotnet build MyPackage/MyPackage.csproj --no-restore
```

For unreleased tooling, install the locally packed template `.nupkg` instead. The
template creates **one authored Library project** with `SunderPackageProject=true`,
package metadata, an ordinary private service and a contract lock. Sunder generates
interfaces, clients, DI, Worker entry points and optional App composition.

| Option | Result |
| --- | --- |
| `--withAvalonia` | Adds Avalonia support and a starter view/view model |
| `--noDefaultView` | Keeps Avalonia support without the starter shell view |
| `--withStacks` | Adds Stack contributor source; select its platform API before Build |
| `--withHostDependency` | Adds an ordinary package dependency |
| `--hostPackageId <id>` | Required dependency identity with `--withHostDependency` |
| `--hostPackageVersionRange <range>` | Reviewed dependency range |

See the [template README](../../languages/csharp/dotnet/packages/Sunder.Package.Templates/README.md)
for all options and the Stack selection command.

## Add Services And UI

Implement ordinary public async methods on a `[SunderService]` class. The generated
interface is available for constructor injection. Optional views use
`[SunderPackageView]`; their view models call the generated API rather than a Worker
implementation. Author business values once; Sunder handles their wire projection.

Private services need no Accept/Freeze step. Public export and external API selection
are explicit reviewed operations. See [ordinary services](ORDINARY-SERVICES.md) and
the compiled [Quickstart](../../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/README.md).

## Run And Watch

Build emits `MyPackage/bin/Debug/net10.0/sunder-dev`. Load that generated package:

```sh
sunder dev package run MyPackage/bin/Debug/net10.0/sunder-dev --watch
```

Alternatively, use `Sunder.App --dev-package <folder> --watch` (`Sunder.App.exe` on
Windows). In another terminal **inside the package project directory**, run:

```sh
dotnet watch build
```

The source watcher rebuilds C#, AXAML, resources and contract inputs. The output
watcher validates and stages complete package generations. Failed builds/reloads
preserve the previous active generation. The pinned SDK requires `dotnet watch build`
from the project directory; forwarding `--project` to its build command is unsupported.

Current C# local builds emit a manifest-bound import handoff beside `sunder-dev`.
Published imports and other producers may require explicit preparation; see
[development imports](DEVELOPMENT-IMPORTS.md). Load dependent development packages
together using repeated `--dev-package` options. Closing the owning invocation
releases its development lease.

## Pack And Validate

Set the strict SemVer `Version` in the authored project, then pack all declared RIDs:

```sh
dotnet msbuild MyPackage/MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
sunder dev package validate <archive.sunderpkg>
sunder package install --file <archive.sunderpkg>
```

Use the archive path reported by Pack. Generated manifests, role outputs and archives
are not source files to edit. Public API changes require their reviewed lifecycle
before publication; private-only starter packages do not.

## Next Steps

- [Package anatomy](PACKAGE-ANATOMY.md)
- [Single-project composition and role ownership](SINGLE-PROJECT-AUTHORING.md)
- [Contract selection and restore](CONTRACT-RESTORE-CACHE.md)
- [Activation and disposal](ACTIVATION-AND-DI.md)
- [Avalonia views](AVALONIA.md)
- [Build, publish and version](BUILD-PUBLISH-VERSIONING.md)
- [Troubleshooting](TROUBLESHOOTING.md)
