# Sunder Core

Sunder Core contains the public desktop shell, local runtime host, CLI, package SDK, package build tooling, package template, package archive validation, and Registry API DTO contracts.

## Build

```powershell
dotnet restore Sunder.Core.slnx
dotnet build Sunder.Core.slnx --no-restore
```

## Tests

```powershell
dotnet test tests/Sunder.App.Tests/Sunder.App.Tests.csproj --no-restore
dotnet test tests/Sunder.Runtime.Host.Tests/Sunder.Runtime.Host.Tests.csproj --no-restore
dotnet test tests/Sunder.PackageManagement.Tests/Sunder.PackageManagement.Tests.csproj --no-restore
```

## Public Package Author Surface

- `Sunder.Sdk`
- `Sunder.Package.Build`
- `Sunder.Package.Templates`

`Sunder.Protocol`, `Sunder.PackageManagement`, and `Sunder.Registry.Shared` are core source projects, but they are not the package-author SDK surface.
