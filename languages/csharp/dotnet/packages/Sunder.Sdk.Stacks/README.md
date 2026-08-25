# Sunder.Sdk.Stacks

> **Release/source channel:** The NuGet README describes that published package version. The repository copy tracks current source and may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

`Sunder.Sdk.Stacks` contains the public package-author contracts for contributing data to Sunder Stack export and import operations.

Install it alongside the coordinated `Sunder.Sdk` version when a package implements a Stack contributor:

```powershell
dotnet add package Sunder.Sdk.Stacks
```

The package preserves the `Sunder.Sdk.Stacks` namespace while using its own assembly and NuGet package identity. Packages that do not use Stack contracts only need `Sunder.Sdk`.

The package bundles the authoritative descriptorVersion 2 `sunder.stack.contributor` v2.0.0 contract with public exposure and a local-interface adapter. Stack providers use public audience because Runtime Stack orchestration invokes them as a synthetic Host caller rather than as the provider-owning package. Exporter, importer, and applied-handler contracts derive from `IPackageStackContributor`; Worker packages pass that contract to `SunderStackContributorRpc.CreateHandler` when constructing their own Worker registration. Runtime invokes exact role- and version-bound RPC endpoints and moves payload bytes through Host-fenced content references rather than cross-package CLR objects. `Sunder.Sdk.Stacks` is transport-neutral and does not depend on `Sunder.Sdk.Worker`.

`Sunder.Sdk.Stacks` supplies the public contract bundle transitively. Declare each Runtime provider with an exact role, contract version, and public audience; RPC `VersionRange` metadata is not valid:

```xml
<SunderRpcProvider Include="my.package.stack"
                   Role="runtime"
                   ContractId="sunder.stack.contributor"
                   ContractVersion="2.0.0"
                   Audience="public" />
```

`StackContributorRpcClient` requires a caller-owned `ISunderRpcCallScope`. Dispose the scope after the related calls and content transfers complete; a typed client never retains unscoped RPC authority.

Import preview must be side-effect free. The Runtime owns the resulting expiring, single-use plan and binds it to the validated archive, Runtime generation, exact provider activation, schema versions, selected fragments, inputs, remaps, actions, and conflicts. Import receives the immutable fragment/input/remap snapshot from that plan.

Required inputs explicitly use `StackValueSensitivity.Public` or `Secret`. Public values remain visible in import review; secret values are masked, must not have portable defaults, and must never be written to logs or contributor diagnostics.

Atomicity is contributor-local. The Runtime continues collecting contributor results after an individual failure and reports the overall operation as completed, partial, or failed. Contributors must return `Partial` and accurately report committed items when any selected action commits before another action fails; `Failed` means that no selected action committed.

See the [Stack package guide](https://github.com/Younics/sunder-core/blob/main/docs/package-development/STACKS.md) for contributor registration, export selection, package requirements, expiring import plans, scoped ids, and post-import handling.
