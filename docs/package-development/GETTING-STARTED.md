# Getting Started

**SDK status: UNFROZEN / UNRELEASED.** This guide tracks the current SDK 1.1 source
candidate and .NET 10; it does not claim a supported release. After the first supported
release, compatible fixes and additions remain allowed while supported source, binary
and behavioral contracts must be preserved. See the [compatibility policy](../SUNDER-SDK-COMPATIBILITY.md).

## Prerequisites

- .NET 10 SDK for building.
- Sunder App and the `sunder` CLI for package development and installation.
- A feed containing the coordinated SDK packages, or locally packed source candidates.

Sunder-managed execution runtimes are separate from your development SDK. Install
required package runtimes through the App or CLI when prompted.

## Create A Package

Install a locally packed coordinated template candidate (or the matching version from
your candidate feed), then create one authored project:

```sh
dotnet new install "<path-to-candidate-template.nupkg>"
dotnet new sunder-package --name MyPackage --packageId my.company.package --packageName "My Package" --output MyPackage
dotnet restore MyPackage/MyPackage.csproj
dotnet build MyPackage/MyPackage.csproj --no-restore
```

The template creates **one authored Library project** with `SunderPackageProject=true`,
package metadata, an ordinary private service and a contract lock. Sunder generates
interfaces, clients, DI, Worker entry points and optional App composition.

| Option | Result |
| --- | --- |
| `--withAvalonia` | Adds Avalonia support and a starter view/view model |
| `--noDefaultView` | Keeps Avalonia support without the starter shell view |
| `--withStacks` | Adds Stack contributor source; select its platform API before Build |
| `--withPackageDependency` | Adds an ordinary installed-package dependency |
| `--dependencyPackageId <id>` | Required dependency identity with `--withPackageDependency` |
| `--dependencyVersionRange <range>` | Required reviewed range for that package, not the SDK version |

There is no default dependency range. For example, when the selected dependency
actually supports that range:

```sh
dotnet new sunder-package --name MyExtension --packageId my.company.extension --packageName "My Extension" --output MyExtension --withPackageDependency --dependencyPackageId example.dependency --dependencyVersionRange ">=2.0.0 <3.0.0"
```

Dependencies from selected APIs are derived from reviewed locks; do not duplicate
them with ordinary dependency metadata.

See the [template README](../../languages/csharp/dotnet/packages/Sunder.Package.Templates/README.md)
for all options and the Stack selection command.

## Add Services And UI

Implement ordinary public async methods on a partial `[SunderService]` class. The
template's `SampleService` produces `ISampleService`; for package ID
`my.company.package`, it is in `Sunder.Generated.Packages.My_Company_Package_Services`.
The starter view model injects that API, not the Worker implementation. Optional
views use `[SunderPackageView]`. Author business values once; Sunder handles their
wire projection.

Normal packages do not write App modules, maintain explicit role leaves or bootstrap
Workers. Those are advanced integration details, not extra scaffold steps.

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

Keep these observations separate:

| Observation | Meaning |
| --- | --- |
| Watch requested | This owner asked the Host to monitor built output |
| Producer running | A separate source producer such as `dotnet watch build` is running |
| Build succeeded | A build produced output for the Host to validate and stage |
| Output applied | The matching validated source is Runtime-ready and its applicable App activation is published |

Host `--watch` does not start the source producer. A watch request or successful build
is not proof that output was applied. App development tools observe `HostState`,
`RuntimeReadiness` and `AppReadiness` as SDK-owned enums, each with `Unknown`;
`RuntimeRevision` and `AppliedRevision` remain separate source observations. App
activation does not imply that every lazy view has opened.

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
