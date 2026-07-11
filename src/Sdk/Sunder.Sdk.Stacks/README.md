# Sunder.Sdk.Stacks

`Sunder.Sdk.Stacks` contains the public package-author contracts for contributing data to Sunder Stack export and import operations.

Install it alongside the coordinated `Sunder.Sdk` version when a package implements a Stack contributor:

```powershell
dotnet add package Sunder.Sdk.Stacks
```

The package preserves the `Sunder.Sdk.Stacks` namespace while using its own assembly and NuGet package identity. Packages that do not use Stack contracts only need `Sunder.Sdk`.
