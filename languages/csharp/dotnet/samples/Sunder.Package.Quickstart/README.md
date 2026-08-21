# Compiled Quickstart

This package family is a source-tree compilation fixture for the public package-development guides. It exercises an exact-RID Worker V3 executable, a package-local RPC V2 protocol, an owner-only Runtime provider, typed App-side RPC discovery and invocation, settings, persistent state, logging, an Avalonia view, navigation, warmup, and semantic Sunder theme resources.

Build it from the Sunder Core repository:

```powershell
dotnet build languages/csharp/dotnet/samples/Sunder.Package.Quickstart/Sunder.Package.Quickstart.csproj
```

The root unsuffixed project is the canonical Worker/package csproj and uses source `ProjectReference` items so `Sunder.Core.slnx` compiles it against the exact SDK and build-tooling surface being documented. Worker source remains under `Sunder.Package.Quickstart.Worker/`, but there is no separate Worker csproj or Aggregate wrapper. A normal build emits an exact current-RID Worker leaf at `bin/Debug/net10.0/<rid>/sunder-dev` and a composed package tree at `bin/Debug/net10.0/sunder-dev` containing the App and current Runtime target. Use `dotnet msbuild languages/csharp/dotnet/samples/Sunder.Package.Quickstart/Sunder.Package.Quickstart.csproj -t:PackSunderPackage -p:Configuration=Release` to build all six Worker leaves and create a universal `.sunderpkg`.

For a new package with the same canonical Worker/package, optional App, and Protocol topology, start with `dotnet new sunder-package --withAvalonia`.
