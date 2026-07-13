# Sunder.Sdk.Stacks

`Sunder.Sdk.Stacks` contains the public package-author contracts for contributing data to Sunder Stack export and import operations.

Install it alongside the coordinated `Sunder.Sdk` version when a package implements a Stack contributor:

```powershell
dotnet add package Sunder.Sdk.Stacks
```

The package preserves the `Sunder.Sdk.Stacks` namespace while using its own assembly and NuGet package identity. Packages that do not use Stack contracts only need `Sunder.Sdk`.

Import preview must be side-effect free. The Runtime owns the resulting expiring, single-use plan and binds it to the validated archive, Runtime generation, contributor instances, schema versions, selected fragments, inputs, remaps, actions, and conflicts. Import receives the immutable fragment/input/remap snapshot from that plan.

Atomicity is contributor-local. The Runtime continues collecting contributor results after an individual failure and reports the overall operation as completed, partial, or failed; contributors must accurately report any committed items even when their own result is unsuccessful.
