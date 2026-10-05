# Compiled Quickstart

This package family is a source-tree compilation fixture for the public package-development guides. Ordinary annotated C# methods and value types define its owner-only RPC API. Build generates the descriptor, Worker dispatcher/registration, and an independent App client/facade. It exercises a thin framework-dependent exact-RID Worker V1 module, settings, persistent state, logging, an Avalonia view, navigation, warmup, and semantic Sunder theme resources.

Build it from the Sunder Core repository:

```powershell
dotnet build languages/csharp/dotnet/samples/Sunder.Package.Quickstart/Sunder.Package.Quickstart.csproj
```

The root is the only authored project. `Services/`, `Settings/` and `Views/` contain
ordinary source; Sunder generates Worker entry, App module, typed `IGreetingService`
clients and DI. `QuickstartSettings` supplies the existing settings schema through
`ISunderWorkerSettingsSchemaProvider`. The greeting's state, logging, cancellation,
navigation and warmup behavior are retained.

Build emits a composed current-RID package at `bin/Debug/net10.0/sunder-dev`, with
isolated implementation outputs under `sunder-roles/<role>/<rid>`. Direct
`PackSunderPackage` builds all six declared RIDs into a universal `.sunderpkg`.
`dotnet run --project <Quickstart.csproj>` delegates to the Sunder CLI's Host-backed
development workflow; its managed runtime must be acquired explicitly.

`GreetingService` owns the implementation and value facets. The committed
`sunder-contracts.owned.json` and canonical snapshots retain its accepted exact RPC
`2.0.0` identity; `CheckSunderContracts`, `AcceptSunderContracts` and
`FreezeSunderContracts` own intentional baseline changes. The view model receives
`IGreetingService` through its constructor; generated role-local transport calls the
Worker without authored scopes or a shared implementation/Protocol assembly.
