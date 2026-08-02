# Sunder Package Development

> **Source channel:** This manual tracks unreleased Sunder V1 source, coordinated SDK `1.1.x` (`[1.1.0,1.2.0)`), .NET 10, and Runtime protocol revision 3. Read it from the matching `sdk/v*` tag for released SDK behavior.

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

## Authoring Model

The package standard is language-, framework-, and build-tool neutral. Sunder currently ships four authoring presets:

| Preset | Canonical target |
| --- | --- |
| Managed .NET Runtime | `runtime` / `dotnet` |
| Avalonia App | `app` / `avalonia` |
| Node process Runtime | `runtime` / `process` using `sunder.worker.v1` |
| Static web App | `app` / `web`; framework-agnostic, with React/Vite as one template |

Those are current supported presets, not a closed capability list. Future Python, Rust, Go, and other process toolchains, other .NET UI approaches, and Vue, Svelte, Solid, or other web frameworks can emit the same canonical manifest, content index, payload layers, and exact targets. The Host must still explicitly support the declared target kind, protocol, and required capabilities; format conformance alone does not make an unknown target executable.

## SDK Decision Matrix

| Authoring need | Add/use |
| --- | --- |
| Any managed .NET target | `Sunder.Sdk` plus `Sunder.Package.Build` with `PrivateAssets="all"` |
| Avalonia views/settings | Add `Sunder.Sdk.Avalonia` only to the App leaf that uses Avalonia |
| Stack contribution | Add `Sunder.Sdk.Stacks` to the managed leaf that implements the contributor |
| New managed aggregate | Install `Sunder.Package.Templates`; it is a scaffold tool, not a runtime dependency |
| Node process Runtime RPC | `@sunder/sdk` plus `@sunder/package-tool` |
| Browser code in a web App | Import only `@sunder/sdk/browser`; choose any web framework |
| Node/React starter | `npm create sunder-package@latest`; React is one template, not a web requirement |
| Shared descriptor-only package | Bundle at least one RPC descriptor and emit no executable target |
| Custom Python/Rust/Go/process or UI toolchain | Emit the canonical format and a Host-supported target kind/protocol; no managed SDK dependency is inherently required |

`Sunder.Runtime.Contracts`, `Sunder.Package.Format`, and `Sunder.Registry.Contracts` are not package-author NuGet dependencies. Package projects must not reference `Sunder.App` or `Sunder.Runtime.Host`.

The TypeScript SDK is intentionally narrower than `Sunder.Sdk`: it covers descriptor parsing/validation/generation, process-worker RPC/client/content handling, and the browser RPC/navigation bridge. It does not expose managed DI/modules, package storage/settings/secrets/logging, callbacks, background services, managed Stack adapters, Avalonia, Registry management, or install/update APIs.

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

The repository's [compiled quickstart package](samples/Sunder.Package.Quickstart/) exercises the module, DI, settings, typed Runtime operation, Avalonia view, navigation, warmup, and theme APIs used by these guides. It is part of `Sunder.Core.slnx`, so the normal CI build compiles it. Template CI separately generates and packs Runtime, Avalonia, Stack, and combined managed variants; Node CI covers process/web leaves and six-RID SEA aggregation.
