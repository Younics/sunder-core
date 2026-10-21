# Sunder.Sdk.Stacks

> **Release/source channel:** The NuGet README describes that published package version. The repository copy tracks current source and may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

`Sunder.Sdk.Stacks` contains the public package-author contracts for contributing data to Sunder Stack export and import operations.

Install it alongside the coordinated `Sunder.Sdk` version when a package implements a Stack contributor:

```powershell
dotnet add package Sunder.Sdk.Stacks
```

The package preserves the `Sunder.Sdk.Stacks` namespace while using its own assembly and NuGet package identity. Packages that do not use Stack contracts only need `Sunder.Sdk`.

## Implementation-First Providers

`IPackageStackContributor` is marked as a platform-import domain surface. Implement
its exporter/importer/applied interfaces on a partial class and bind that class with
`[SunderServiceProvider(typeof(IPackageStackContributor), ProviderId = "my.package.stack")]`
from `Sunder.Sdk.Services`. Ordinary package composition registers it automatically;
advanced explicit composition can use `contributor.CreateSunderRpcProviderRegistration()`.
The [template contributor](https://github.com/Younics/sunder-core/blob/main/languages/csharp/dotnet/packages/Sunder.Package.Templates/templates/sunder-package/Sunder.Package.Template/Stacks/StackSupport.cs)
provides a complete example.

Explicitly select the platform contract; private-only source needs no Accept/Freeze:

```powershell
dotnet msbuild MyPackage.csproj -t:ImplementSunderPlatformApi -p:SunderContractId=sunder.stack.contributor -p:SunderRegistryOrigin=https://registry.example/
dotnet build MyPackage.csproj
```

The Registry-published Core catalog and exact lock/cache provide the descriptor, hashes, wire
types, dispatch and registration. The SDK marker does not establish ownership or
select a version. Platform imports add neither consumer uses nor a dependency on
`sunder.core`. Multiple contributors need distinct provider IDs. Stack providers
use public audience for Host orchestration.

`StackContributorServiceAdapter` retains local domain behavior: six-method
composition, optional capability flags and errors, single-use content registration,
lazy content opening and Host-stamped owner/contributor identity. The generated
package-local bridge checks domain DTO representation against the selected
descriptor and validates values with its exact schemas. Domain objects remain
local; no shared CLR object identity crosses processes. The SDK does not depend on
`Sunder.Sdk.Worker` or the build tooling.

The SDK retains an automatic bundle asset for lower-level consumers. Generated
packages exclude that asset and use their reviewed platform import (as the template does):

```xml
<PackageReference Include="Sunder.Sdk.Stacks" Version="[1.1.0,1.2.0)" ExcludeAssets="buildTransitive" />
```

Selection accepts a local Core-exported catalog without a Registry, or the chosen
Registry's administrator-published catalog. No catalog signing key or trust bundle
is needed. Select the import before the first Build when starting a contributor.
Tests exercise the real six-method `2.0.0`
descriptor through source and live isolated Registry composition. The Core Host
now compiles its own internal Stack client, codecs and descriptor from checked
Core source through a private build-only export. The shared SDK
`StackContributorRpcClient` and `SunderStackContributorRpc.CreateClient` are removed.
Canonical first-party providers use reviewed platform locks and generated composition.
The retained SDK descriptor/provider surface is a separate compatibility boundary;
source derivation preserves all 48 definitions and both issued descriptor/ABI hashes.
See [platform imports](https://github.com/Younics/sunder-core/blob/main/docs/package-development/PLATFORM-CONTRACT-IMPORTS.md).

Import preview must be side-effect free. The Runtime owns the resulting expiring, single-use plan and binds it to the validated archive, Runtime generation, exact provider activation, schema versions, selected fragments, inputs, remaps, actions, and conflicts. Import receives the immutable fragment/input/remap snapshot from that plan.

Required inputs explicitly use `StackValueSensitivity.Public` or `Secret`. Public values remain visible in import review; secret values are masked, must not have portable defaults, and must never be written to logs or contributor diagnostics.

Atomicity is contributor-local. The Runtime continues collecting contributor results after an individual failure and reports the overall operation as completed, partial, or failed. Contributors must return `Partial` and accurately report committed items when any selected action commits before another action fails; `Failed` means that no selected action committed.

See the [Stack package guide](https://github.com/Younics/sunder-core/blob/main/docs/package-development/STACKS.md) for contributor registration, export selection, package requirements, expiring import plans, scoped ids, and post-import handling.
