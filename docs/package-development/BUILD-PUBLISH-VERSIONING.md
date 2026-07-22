# Build, Validate, Publish, And Version

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, .NET 10, and Runtime protocol revision 3.

`Sunder.Package.Build` turns compiled package metadata into generated development output and immutable distributable archives. Package authors own C# metadata and normal MSBuild version properties; they do not maintain `sunder-package.json`.

## Project Baseline

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Version>1.2.3</Version>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Sunder.Sdk" Version="[1.1.0,1.2.0)" />
    <PackageReference Include="Sunder.Package.Build"
                      Version="[1.1.0,1.2.0)"
                      PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Add `Sunder.Sdk.Avalonia` or `Sunder.Sdk.Stacks` with the same coordinated minor range only when used. App projects also reference the Avalonia packages they use directly. `Sunder.Package.Build` has no consumer compile/runtime asset and must remain private build tooling.

Declare identity and display metadata in the entry assembly:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Summary = "Adds a custom Sunder workspace.",
    Icon = "assets/icon.png")]
```

`Version` is separate assembly/package build metadata. Runtime dependencies use `[assembly: SunderPackageDependency(...)]`; NuGet `PackageReference` items are compile/build dependencies and do not create installed-package dependencies.

## Build Output

```powershell
dotnet restore .\MyPackage.csproj --locked-mode
dotnet build .\MyPackage.csproj --no-restore
```

After a successful build, tooling removes and recreates the direct output child:

```text
bin/Debug/net10.0/sunder-dev/
  sunder-package.json
  lib/
  assets/
```

It contains a generated-output marker, inferred metadata/capabilities, authored assemblies, eligible copy-local runtime dependencies, native runtime assets, and files copied from source `Assets/**`. Host-provided SDK/framework boundary assemblies are excluded. Do not edit, commit, or use `sunder-dev` as the source of truth.

`SunderDevOutputPath` may override only the direct `TargetDir/sunder-dev` child. This restriction lets the build safely delete stale generated output without accepting an arbitrary directory.

## Publish An Archive

```powershell
dotnet publish .\MyPackage.csproj -c Release --no-restore
```

The output includes:

```text
bin/Release/net10.0/publish/MyPackage.1.2.3.sunderpkg
```

For an archive without a full publish command:

```powershell
dotnet msbuild .\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
```

The pack task validates its indexed staging tree, creates the deterministic archive, then independently extracts and validates that exact written file. A final validation failure removes the new archive. `SunderPackageFileName`, `SunderPackageOutputPath`, and `SunderPublishPackageOutputPath` customize names/locations when release automation needs them. Prefer defaults unless the pipeline has one explicit artifact convention.

The [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md) is the normative source for generated manifest, development output, archive layout, hashes, safety checks, and size limits.

## Capability Inference

Build tooling scans the entry assembly and authored project-reference outputs, including compiler-generated async, iterator, and lambda bodies. It reads capability annotations from the resolved `Sunder.Sdk*` assemblies, closes capability dependencies, and detects Sunder theme resources in source/compiled Avalonia XAML.

Inference fails closed for unreadable metadata, unresolved IL, or unclassified dynamic/reflection access. For a genuine dynamic call site, declare the capability and acknowledge the exact diagnostic location:

```xml
<ItemGroup>
  <SunderSdkCapability Include="callbacks.v1" />
  <SunderSdkDynamicAccess Include="MyCompany.Package.DynamicFactory.CreateHandler" />
</ItemGroup>
```

Do not add broad acknowledgements to silence a failure you have not reviewed. `SunderSdkPackageVersion` is a verification-only override and must match the resolved `Sunder.Sdk` informational/package version.

## Validate The Exact Artifact

```powershell
sunder package validate .\bin\Release\net10.0\publish\MyPackage.1.2.3.sunderpkg
```

Validation is local and covers archive integrity and format. It does not execute package code, prove publisher identity, or make code safe. See [Package Trust And Security](SECURITY.md).

Optionally install that same file into a development profile:

```powershell
sunder install --file .\bin\Release\net10.0\publish\MyPackage.1.2.3.sunderpkg
```

## Publish To A Registry

Authenticate, then upload the exact validated file:

```powershell
sunder auth login
sunder publish --file .\bin\Release\net10.0\publish\MyPackage.1.2.3.sunderpkg
```

Registry artifacts are immutable by package id/version and duplicate publication is rejected. Ownership is enforced. Normal publish promotes the `latest` dist tag; use `--no-latest` for a prerelease/channel version and assign an appropriate tag separately:

```powershell
sunder publish --file .\bin\Release\net10.0\publish\MyPackage.1.3.0-beta.1.sunderpkg --no-latest
sunder dist-tag set my.company.package beta 1.3.0-beta.1
```

`latest` is only a movable Registry dist tag, not special installed state. Use `--dev-local` solely against the development-only local Registry endpoint.

## Version Policy

Package versions are strict SemVer 2.0 values. Use:

- patch for backward-compatible fixes;
- minor for backward-compatible package features or additive contracts;
- major for breaking package behavior, persisted schema, operation DTO, or public contracts changes; and
- prerelease identifiers for artifacts that should not automatically become `latest`.

Keep operation/stream ids, view ids, settings keys, callback handler ids, Stack contributor/schema ids, and package ids stable. Version serialized DTOs and persisted data additively where possible. If a bad immutable version is published, publish a fixed version and deprecate or yank the bad version; never replace its bytes.

Sunder SDK compatibility is capability-based, with `[1.1.0,1.2.0)` as the current coordinated package line. Do not widen the range across an untested SDK minor. See [Sunder SDK Compatibility](../SUNDER-SDK-COMPATIBILITY.md) before changing SDK references or public `*.Contracts` assemblies.

## Release Checklist

- Build and test from a clean checkout with locked restore.
- Review package metadata, dependencies, inferred capabilities, assets, and native files.
- Publish one Release archive with a strict intended version.
- Validate the exact archive path selected for upload.
- Smoke install/activate/update/unload in a disposable profile.
- Review secrets, logs, source symbols, and licenses in the payload.
- Publish from a protected job without credentials on command lines.
- Verify Registry package details, version, icon, dependencies, and dist tags after upload.
