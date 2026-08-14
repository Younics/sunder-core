# Compiled Quickstart

This package family is a source-tree compilation fixture for the public package-development guides. It exercises an exact-RID Worker V3 executable, a package-local RPC V2 protocol, an owner-only Runtime provider, typed App-side RPC discovery and invocation, settings, persistent state, logging, an Avalonia view, navigation, warmup, and semantic Sunder theme resources.

Build it from the Sunder Core repository:

```powershell
dotnet build languages/csharp/dotnet/samples/Sunder.Package.Quickstart/Sunder.Package.Quickstart.csproj
```

The aggregate uses source `ProjectReference` items so `Sunder.Core.slnx` compiles it against the exact SDK and build-tooling surface being documented. A normal build emits a current-platform package tree at `bin/Debug/net10.0/sunder-dev` containing one App target and one self-contained Runtime worker target. Use `dotnet msbuild languages/csharp/dotnet/samples/Sunder.Package.Quickstart/Sunder.Package.Quickstart.csproj -t:PackSunderPackage -p:Configuration=Release` to create a current-platform `.sunderpkg`.

For a universal six-RID package, start with `dotnet new sunder-package --withAvalonia`; the generated aggregate builds all native worker leaves while retaining the same Worker, optional App, Protocol, and Aggregate topology.
