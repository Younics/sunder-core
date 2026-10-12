# Compiled Quickstart

This single-project sample uses `[SunderService]`, ordinary string parameters,
`ValueTask<GreetingResult>`, `Task` completion and a lazy result stream. Build owns
the Worker/App split and generated `IGreetingService` injection. The sample retains
settings, persistent state, logging, cancellation, an Avalonia view, navigation,
warmup and semantic Sunder theme resources.

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

`GreetingService` owns meaningful validation and a result record, with no request or
scalar wrapper types. `ResetAsync` resets the persisted count; `GreetManyAsync`
streams ordinary results. The view model calls `IGreetingService` directly.
The generated interface is in
`Sunder.Generated.Packages.Docs_Sunder_Quickstart_Services`; the view model imports
that namespace explicitly while `GreetingResult` remains authored package source.
The record is authored once. If exposed publicly and selected by another package,
it projects into `Sunder.Generated.Packages.Docs_Sunder_Quickstart_Models`, and the
selected-package root is `IPackageApi` in the same `_Services` namespace. Private
values remain private; generated type availability never grants another role access.

For richer services, use `IServiceProviders<TService>` to choose an authorized
provider, `.Service` to call it and `.Reference` to pass an exact typed reference.
Use `SunderContent` and normal async disposal for content; generated bindings own
RPC scopes and codecs. See [ordinary services](../../../../../docs/package-development/ORDINARY-SERVICES.md)
for multi-contract implementations, explicit imported Runtime use, optional reference
availability, transient member transfer and deadline/content-watch policy. Those
opt-ins are unnecessary for this greeting sample. Its no-argument/reset completion
uses generated empty-object metadata; authors supply no `Unit` or request record.
Generated service JSON metadata also avoids reflection-cache retention of collectible
model types.

The current private API is the explicit **package 1.1.0 successor**. Its descriptor
version is 1.1.0 and evolves with the package; normal Build/Pack requires no Accept
or Freeze. The earlier release-frozen private 2.0.0 record remains losslessly under
`retiredPrivateContracts` after the explicit `MigratePrivateSunderContracts` command.
Historical descriptor/ABI oracle files are unchanged and are checked separately
from the current natural-signature API. Normal builds never rewrite that archive.
