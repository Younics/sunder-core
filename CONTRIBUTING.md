# Contributing to Sunder Core

Thanks for helping improve Sunder Core. This repository contains the public desktop shell, runtime host, CLI, SDK, package build tooling, templates, archive validation, and Registry contracts.

## Before You Start

For small fixes, documentation improvements, and tests, open a pull request directly.

For larger changes, open an issue first so the design can be discussed before implementation. This is especially important for SDK APIs, package compatibility behavior, archive format changes, runtime activation behavior, and public Registry DTO contracts.

## Local Development

Restore and build the repository:

```powershell
dotnet restore Sunder.Core.slnx
dotnet build Sunder.Core.slnx --no-restore
```

Useful targeted tests:

```powershell
dotnet test tests/Sunder.App.Tests/Sunder.App.Tests.csproj --no-restore
dotnet test tests/Sunder.Runtime.Host.Tests/Sunder.Runtime.Host.Tests.csproj --no-restore
dotnet test tests/Sunder.PackageManagement.Tests/Sunder.PackageManagement.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj --no-restore
```

## Project Boundaries

| Area | Owns |
| --- | --- |
| `Sunder.App` | Avalonia shell UI, app-side package activation, package views, marketplace/install UX |
| `Sunder.Runtime.Host` | Installed package state, validation, install/update/uninstall, runtime activation, local API |
| `Sunder.Cli` | Thin command-line client over Registry and runtime APIs |
| `Sunder.Sdk` | Public package author contracts only |
| `Sunder.Package.Build` | Generated manifest, dev output, and `.sunderpkg` archive behavior |
| `Sunder.PackageManagement` | Archive inspection and validation |

The public package-author NuGet surface is `Sunder.Sdk`, `Sunder.Package.Build`, and `Sunder.Package.Templates`.

## Pull Request Checklist

Before opening a PR, please check:

- The change is scoped to the smallest useful fix or feature.
- Public API changes are documented and justified.
- Package compatibility implications are considered.
- Relevant tests were added or updated.
- Targeted tests were run locally when practical.
- Documentation was updated when behavior changed.

## Reporting Bugs

Use the bug report template and include:

- Sunder version or commit.
- Operating system.
- Reproduction steps.
- Expected behavior.
- Actual behavior.
- Relevant logs or error output.

Please do not include secrets, tokens, credentials, or private package artifacts in public issues.

## Security

Please report vulnerabilities privately using [`SECURITY.md`](SECURITY.md). Do not open public issues for security-sensitive problems.
