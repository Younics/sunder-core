# Sunder.Sdk.Stacks

`Sunder.Sdk.Stacks` contains the public package-author contracts for contributing data to Sunder Stack export and import operations.

Install it alongside the coordinated `Sunder.Sdk` version when a package implements a Stack contributor:

```powershell
dotnet add package Sunder.Sdk.Stacks
```

The package preserves the `Sunder.Sdk.Stacks` namespace while using its own assembly and NuGet package identity. Packages that do not use Stack contracts only need `Sunder.Sdk`.

The package bundles the authoritative `sunder.stack.contributor` v1.0.0 descriptor and a local-interface adapter. Register contributors with `RegisterStackContributor`; Runtime invokes exact RPC endpoints and moves payload bytes through Host-fenced content references rather than cross-package CLR objects.

Import preview must be side-effect free. The Runtime owns the resulting expiring, single-use plan and binds it to the validated archive, Runtime generation, exact provider activation, schema versions, selected fragments, inputs, remaps, actions, and conflicts. Import receives the immutable fragment/input/remap snapshot from that plan.

Atomicity is contributor-local. The Runtime continues collecting contributor results after an individual failure and reports the overall operation as completed, partial, or failed. Contributors must return `Partial` and accurately report committed items when any selected action commits before another action fails; `Failed` means that no selected action committed.

See the [Stack package guide](https://github.com/Younics/sunder-core/blob/main/docs/package-development/STACKS.md) for contributor registration, export selection, package requirements, expiring import plans, scoped ids, and post-import handling.
