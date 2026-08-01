# Sunder Package Development

> **Current developer line:** unreleased Sunder V1, coordinated SDK `1.1.x` (`[1.1.0,1.2.0)`), .NET 10, and Runtime protocol revision 3.

This is the canonical landing page for public Sunder package development. The focused guides describe the current universal-package implementation in this repository.

## Start Here

1. [Getting Started](package-development/GETTING-STARTED.md): create, build, run, and validate a package.
2. [Package Anatomy](package-development/PACKAGE-ANATOMY.md): choose exact App/Runtime targets or a shared-only protocol package.
3. [Activation, DI, And Disposal](package-development/ACTIVATION-AND-DI.md): lifecycle, host services, availability, threading, and unload behavior.
4. [Data And Logging](package-development/DATA-AND-LOGGING.md): storage, settings, secrets, logging, exact limits, and errors.
5. [Avalonia Views](package-development/AVALONIA.md): views, settings UI, navigation, warmup, caching, and theme resources.
6. [Runtime Operations](package-development/RUNTIME-OPERATIONS.md): typed requests, streams, JSON, deadlines, limits, and errors.
7. [Callbacks And Auth](package-development/CALLBACKS-AND-AUTH.md): host-routed browser callbacks and package authorization.
8. [Stacks](package-development/STACKS.md): discovery, export, preview, import, requirements, and outcomes.
9. [Package Trust And Security](package-development/SECURITY.md): execution trust, validation, secrets, callbacks, and supply chain.
10. [Testing And CI](package-development/TESTING-AND-CI.md): unit, integration, compiled examples, and a CI baseline.
11. [Build, Validate, Publish, And Version](package-development/BUILD-PUBLISH-VERSIONING.md): artifacts, compatibility, Registry publication, and version policy.
12. [Troubleshooting](package-development/TROUBLESHOOTING.md): symptom-oriented fixes.

## Public Packages

| NuGet package | Use it for |
| --- | --- |
| `Sunder.Sdk` | Package metadata, Runtime/App modules, DI contracts, data, logging, RPC, callbacks/auth, and typed Runtime operations. |
| `Sunder.Sdk.Avalonia` | Avalonia package views, settings views, and semantic Sunder theme resources. |
| `Sunder.Sdk.Stacks` | Stack exporter, importer, payload, and post-import contracts. |
| `Sunder.Package.Build` | Generated manifest, `sunder-dev`, and `.sunderpkg`; reference with `PrivateAssets="all"`. |
| `Sunder.Package.Templates` | The `dotnet new sunder-package` template. |

`Sunder.Runtime.Contracts`, `Sunder.Package.Format`, and `Sunder.Registry.Contracts` are not package-author NuGet dependencies. Package projects must not reference `Sunder.App` or `Sunder.Runtime.Host`.

## Architecture In One Minute

- One universal archive carries package-wide metadata, shared content, and exact App/Runtime RID targets.
- Runtime target code runs in `Sunder.Runtime.Host` and owns persistent package data, headless work, settings schemas, auth handlers, typed operations, and RPC providers.
- App target code runs separately in `Sunder.App` and owns Avalonia or web views and shell-facing behavior.
- Each selected target receives a separate module/process activation, service provider where applicable, and lifetime.
- App talks to its own Runtime role through authenticated package-scoped APIs; objects and local paths never cross the process boundary.
- Runtime activates dependencies before dependents and publishes package graphs as atomic generations.
- Generation retirement cancels leased work and waits for it to drain before providers or load contexts are disposed.

## Authoritative References

- [Sunder Package Standard](SUNDER-PACKAGE-STANDARD.md) is the normative source for package identity, metadata, generated manifests, output layout, archive validation, and package/Stack format limits. Developer guides link to it instead of redefining the format.
- [Sunder SDK Compatibility](SUNDER-SDK-COMPATIBILITY.md) defines the Host/SDK/target compatibility boundary and capabilities.
- [Sunder V1 Baseline](SUNDER-V1-BASELINE.md) defines the coordinated unreleased baseline and no-shim policy.
- [Sunder CLI](SUNDER-CLI.md) is the command reference.

## Drift Protection

The repository's [compiled quickstart package](samples/Sunder.Package.Quickstart/) exercises the module, DI, settings, typed Runtime operation, Avalonia view, navigation, warmup, and theme APIs used by these guides. It is part of `Sunder.Core.slnx`, so the normal CI build compiles it. Template CI separately generates, builds, and publishes headless, Avalonia, Stack, and combined template variants.
