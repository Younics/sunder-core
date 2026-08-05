# Compiled Quickstart

This project is a source-tree compilation fixture for the public package-development guides. It exercises separate App/Runtime modules, DI, settings, logging, a typed Runtime operation, an Avalonia view, navigation, warmup, and semantic Sunder theme resources.

Build it from the Sunder Core repository:

```powershell
dotnet build languages/csharp/dotnet/samples/Sunder.Package.Quickstart/Sunder.Package.Quickstart.csproj
```

The project uses source `ProjectReference` items so `Sunder.Core.slnx` compiles it against the exact SDK surface being documented. It intentionally tests authored code only and does not produce package output. For a runnable universal package, start with `dotnet new sunder-package --withAvalonia`; the generated aggregate adds the non-packable package-local Protocol project, exact RID leaves, layered payload, canonical manifest, and content index. Template CI builds and packs the default, Avalonia, Stack, and package-dependency shapes against packed Sunder NuGet packages.
