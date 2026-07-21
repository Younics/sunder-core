# Contributing to Sunder Core

Thanks for helping improve Sunder Core. This repository contains the public desktop shell, current-user Host gateway (`Sunder.Host.Supervisor`), nested Runtime worker (`Sunder.Runtime.Host`), CLI, SDK, package build tooling, templates, archive validation, and Registry contracts. A direct-loopback standalone Runtime is a development fallback.

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
dotnet test tests/Sunder.Host.Supervisor.Tests/Sunder.Host.Supervisor.Tests.csproj --no-restore
dotnet test tests/Sunder.Runtime.Host.Tests/Sunder.Runtime.Host.Tests.csproj --no-restore
dotnet test tests/Sunder.Cli.Tests/Sunder.Cli.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Format.Tests/Sunder.Package.Format.Tests.csproj --no-restore
dotnet test tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj --no-restore
```

## Project Boundaries

| Area | Owns |
| --- | --- |
| `Sunder.App` | Avalonia shell UI, app-side package activation, package views, marketplace/install UX |
| `Sunder.Host.Supervisor` | Public current-user Host gateway, Host identity, durable Runtime worker lifecycle, private worker gateway |
| `Sunder.Runtime.Host` | Nested Runtime worker package state, validation, install/update/uninstall, runtime activation, and private worker API; direct loopback only as a standalone development fallback |
| `Sunder.Cli` | Thin command-line client over Registry and the public current-user Host gateway |
| `Sunder.Sdk` | Public package author contracts only |
| `Sunder.Sdk.Avalonia` | Optional Avalonia view/settings, workspace, and theme contracts |
| `Sunder.Sdk.Stacks` | Optional public Stack package-author contracts; references only `Sunder.Sdk` |
| `Sunder.Package.Build` | Generated manifest, dev output, and `.sunderpkg` archive behavior |
| `Sunder.Package.Format` | Archive inspection and validation |

The public package-author NuGet surface is `Sunder.Sdk`, `Sunder.Sdk.Avalonia`, `Sunder.Sdk.Stacks`, `Sunder.Package.Build`, and `Sunder.Package.Templates`.

[`docs/SUNDER.md`](docs/SUNDER.md) is the canonical current project map and architecture reference.

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
