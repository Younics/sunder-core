# Compiled Quickstart

This project is a source-tree compilation fixture for the public package-development guides. It exercises separate App/Runtime modules, DI, settings, logging, a typed Runtime operation, an Avalonia view, navigation, warmup, and semantic Sunder theme resources.

Build it from the Sunder Core repository:

```powershell
dotnet build docs/samples/Sunder.Package.Quickstart/Sunder.Package.Quickstart.csproj
```

The project uses source `ProjectReference` items so `Sunder.Core.slnx` compiles it against the exact contracts being documented. It intentionally does not import source-tree-only package build targets or produce `sunder-dev`/`.sunderpkg` output. For a runnable/distributable package, start with `dotnet new sunder-package --withAvalonia`; template CI compiles and publishes that consumer shape against packed Sunder NuGet packages.
