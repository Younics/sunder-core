# Build, Validate, Publish, And Version

> **Source channel:** Applies to current source on the Sunder SDK `1.1.x` line, package manifest V1, .NET 10, and Runtime protocol revision 1. Use the matching `sdk/v*` tag for released build behavior.

Use [single-project authoring](SINGLE-PROJECT-AUTHORING.md) and
[implementation-first RPC](IMPLEMENTATION-FIRST-RPC.md). Manifest and archive
outputs are generated; service/view declarations are the authored inputs.

`Sunder.Package.Build` turns compiled package metadata into generated development output and immutable distributable archives. Package authors own C# metadata and normal MSBuild version properties; they do not maintain `sunder-package.json`.

## Authored Project Baseline

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Version>1.2.3</Version>
    <RuntimeIdentifiers>linux-x64;linux-arm64;win-x64;win-arm64;osx-x64;osx-arm64</RuntimeIdentifiers>
    <SunderPackageProject>true</SunderPackageProject>
    <EnableRuntimePackDownload>false</EnableRuntimePackDownload>
    <EnableAppHostPackDownload>false</EnableAppHostPackDownload>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Sunder.Sdk.Worker" Version="[1.1.0,1.2.0)" />
    <PackageReference Include="Sunder.Package.Build"
                      Version="[1.1.0,1.2.0)"
                      PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Sunder builds internal Worker/App roles from the same project. Add `Sunder.Sdk.Avalonia`
for UI source and `Sunder.Sdk.Stacks` when implementing that extension. No App project,
explicit generated target, self-reference or aggregate wrapper is authored. A C# Worker
uses its official managed assembly name under `lib/`; runtime/apphost/tooling binaries
are not package payload. `Sunder.Package.Build` remains private build tooling.

Declare identity and display metadata in the target assemblies. Composed leaves must compile the same metadata:

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "my.company.package",
    Name = "My Package",
    Summary = "Adds a custom Sunder workspace.",
    Icon = "assets/icon.png")]
```

`Version` is separate assembly/package build metadata. Runtime dependencies use `[assembly: SunderPackageDependency(...)]`; NuGet `PackageReference` items are compile/build dependencies and do not create installed-package dependencies.

Keep one normal MSBuild `<Version>` in the authored project (or an intentionally shared
props file). Internal role builds inherit it; there are no separately versioned role projects.

## Build Output

```powershell
dotnet restore .\MyPackage.csproj --locked-mode
dotnet build .\MyPackage.csproj --no-restore
```

After a successful build, tooling removes and recreates the direct output child:

```text
bin/Debug/net10.0/sunder-dev/
  manifest/
    sunder-package.json
    content-index.json
  payload/
    shared/
    app/
      shared/
      <rid>/
    runtime/
      shared/
      <rid>/
```

Internal role leaves are under `bin/Debug/net10.0/sunder-roles/<role>/<rid>/sunder-dev`.
The canonical project composes the current RID into the tree above. `Clean` removes
only owned mutable role/composed output and preserves referenced immutable handoffs.

It contains generated metadata, exact target requirements, the Worker's framework-dependent module and package-owned dependencies, App assemblies and eligible dependencies, package-local RPC descriptors, and files copied from source `Assets/**`. Language runtime, apphost, framework-pack, symbol, generated XML documentation, and toolchain files are excluded. Host-provided App SDK/framework boundary assemblies are excluded. Do not edit, commit, or use `sunder-dev` as the source of truth.

`SunderDevOutputPath` may override only the direct `TargetDir/sunder-dev` child. This restriction lets the build safely delete stale generated output without accepting an arbitrary directory.

## Sign Package-Owned Binaries

When a package-owned managed module or native dependency requires platform signing, mutate Sunder's private intermediate copy of the computed thin publish set before it is indexed. Append a project-owned target to `SunderPackageWorkerSigningTargets`:

```xml
<PropertyGroup>
  <SunderPackageWorkerSigningTargets>$(SunderPackageWorkerSigningTargets);SignSunderWorkerPublishFiles</SunderPackageWorkerSigningTargets>
</PropertyGroup>

<Target Name="SignSunderWorkerPublishFiles">
  <!-- Invoke the platform signer over @(SunderPackageWorkerSigningFile). -->
</Target>
```

`@(SunderPackageWorkerSigningFile)` is a private copied instance of the package-owned `ComputeFilesToPublish` set after runtime/toolchain exclusions; signing it cannot mutate NuGet or execution-runtime inputs. `$(SunderPackageWorkerEntryPointRelativePath)` identifies the official managed module within that item set. Sign applicable files in platform-required order and the entry-point module last when required, mutate them in place, and fail the target when signing fails. The hook runs after publish-file computation and before `sunder-dev` copy/index/validation. Do not sign or edit `sunder-dev` or the final archive afterward.

Embedded Authenticode or Apple code signatures are covered by the normal content index. They do not add a Sunder package signature or publisher attestation. Any post-index byte change causes validation and packing to fail.

## Pack An Archive

```powershell
dotnet msbuild .\MyPackage.csproj -t:PackSunderPackage -p:Configuration=Release
```

The output includes:

```text
bin/Release/net10.0/MyPackage.1.2.3.sunderpkg
```

`PackSunderPackage` is the canonical explicit archive target. On the authored Library
project it performs guarded generated-role builds for every declared RID, validates
the isolated Worker/App leaves and composes one universal archive. `dotnet publish`
also creates an archive when a pipeline needs publish output:

```powershell
dotnet publish .\MyPackage.csproj -c Release --no-restore
```

The pack task validates its existing indexed staging tree without rewriting the index, creates the deterministic archive, then independently extracts and validates that exact written file. A post-signing mutation therefore fails instead of being assigned a new hash. A final validation failure removes the new archive. `SunderPackageFileName`, `SunderPackageOutputPath`, and `SunderPublishPackageOutputPath` customize names/locations when release automation needs them. Prefer defaults unless the pipeline has one explicit artifact convention.

For canonical generated-role release fan-in, freeze before native Build, sign through
`SunderPackageWorkerSigningTargets` before indexing, and retain each complete
`sunder-targets/<rid>` bundle with its exported role-input evidence. Invoke
`PackSunderPackage` with `SunderPackageUsePrebuiltLeaves=true`,
`BuildProjectReferences=false` and `SunderPackagePrebuiltRoleInputsFile` selecting the
retained evidence by exact RID/path/SHA-256. The fan-in uses current reviewed source
and the retained immutable toolset without Build, Compile, Restore or generator
execution. Missing, changed or stale inputs fail before archive replacement. See
[qualified role inputs](PREBUILT-ROLE-INPUTS.md) for the full producer/consumer contract.

The [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md) is the normative source for generated manifest, development output, archive layout, hashes, safety checks, and size limits.

## Capability Inference

Build tooling scans each C# target assembly and its authored project-reference outputs, including compiler-generated async, iterator, and lambda bodies. It reads capability annotations from the resolved `Sunder.Sdk*` assemblies, closes capability dependencies, and detects Sunder theme resources in source/compiled Avalonia XAML. Every Runtime target is also stamped with `rpc.v1` and must infer `worker-protocol.v1` plus the SDK-version-matched baseline.

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
sunder dev package validate .\bin\Release\net10.0\MyPackage.1.2.3.sunderpkg
```

Validation is local and covers archive integrity and format. It does not execute package code, prove publisher identity, or make code safe. See [Package Trust And Security](SECURITY.md).

Optionally install that same file into a development profile:

```powershell
sunder package install --file .\bin\Release\net10.0\MyPackage.1.2.3.sunderpkg
```

## Publish To A Registry

Authenticate, then upload the exact validated file:

```powershell
sunder registry auth login
sunder registry package publish --file .\bin\Release\net10.0\MyPackage.1.2.3.sunderpkg
```

Registry artifacts are immutable by package id/version and duplicate publication is rejected. Ownership is enforced. Stable publication promotes `latest` by default; prerelease publication leaves `latest` unchanged by default. Use an explicit channel tag for a prerelease, or `--set-latest` only when intentionally promoting it:

```powershell
sunder registry package publish --file .\bin\Release\net10.0\MyPackage.1.3.0-beta.1.sunderpkg
sunder registry package tag set my.company.package beta 1.3.0-beta.1
```

`--no-latest` suppresses stable promotion and `--set-latest` overrides the prerelease default. `latest` is only a movable Registry dist tag, not special installed state. Loopback development publication uses the separate `sunder dev registry package publish-local` command.

Human publication defaults to the credential protected and supplied by Runtime after `sunder registry auth login`. Protected CI may instead use `--credential-source environment` with `SUNDER_REGISTRY_PUBLISH_TOKEN`, or pipe one scoped token with `--credential-source stdin`. Never put a publish token in command arguments or logs; see the [CLI reference](../SUNDER-CLI.md#ci-publication-credentials).

## Version Policy

Package versions are strict SemVer 2.0 values. Use:

- patch for backward-compatible fixes;
- minor for backward-compatible package features or additive contracts;
- major for breaking package behavior, persisted schema, RPC contract, or public contracts changes; and
- prerelease identifiers for artifacts that should not automatically become `latest`.

Keep RPC provider/service/method ids, view ids, settings keys, callback handler ids, Stack contributor/schema ids, and package ids stable. Evolve RPC descriptors and persisted data additively where possible. If a bad immutable version is published, publish a fixed version and deprecate or yank the bad version; never replace its bytes.

Sunder SDK compatibility is capability-based, with `[1.1.0,1.2.0)` as the current coordinated package line. Do not widen the range across an untested SDK minor. See [Sunder SDK Compatibility](../SUNDER-SDK-COMPATIBILITY.md) before changing SDK references, RPC descriptors, or package-local generated bindings.

## Release Checklist

- Build and test from a clean checkout with locked restore.
- Review package metadata, dependencies, inferred capabilities, assets, and native files.
- Publish one Release archive with a strict intended version.
- Validate the exact archive path selected for upload.
- Smoke install/activate/update/unload in a disposable profile.
- Review secrets, logs, source symbols, and licenses in the payload.
- Publish from a protected job without credentials on command lines.
- Verify Registry package details, version, icon, dependencies, and dist tags after upload.
