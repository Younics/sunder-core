# Sunder Package Development

> **Source channel:** This manual tracks unreleased Sunder V1 source, coordinated SDK `1.1.x` (`[1.1.0,1.2.0)`), .NET 10, and Runtime protocol revision 1. Read it from the matching `sdk/v*` tag for released SDK behavior.

> **RPC authoring transition:** Descriptor files, Protocol projects, direct
> MSBuild RPC items, and Node schema-version-2 RPC config in these guides describe
> current pre-cutover source. The accepted final design is
> [Implementation-First RPC Authoring](package-development/IMPLEMENTATION-FIRST-RPC.md),
> which is not implemented yet. Do not introduce a new descriptor-first API or
> helper project.

This is the canonical landing page for public Sunder package development. The focused guides describe the current universal-package implementation in this repository.

## Start Here

1. [Getting Started](package-development/GETTING-STARTED.md): create, build, run, and validate a package.
2. [Package Anatomy](package-development/PACKAGE-ANATOMY.md): choose exact App/Runtime targets or a shared-only protocol package.
3. [Activation, DI, And Disposal](package-development/ACTIVATION-AND-DI.md): lifecycle, host services, availability, threading, and unload behavior.
4. [Data And Logging](package-development/DATA-AND-LOGGING.md): storage, settings, secrets, logging, exact limits, and errors.
5. [Avalonia Views](package-development/AVALONIA.md): views, settings UI, navigation, warmup, caching, and theme resources.
6. [Owner RPC](package-development/RUNTIME-OPERATIONS.md): typed requests, streams, provider discovery, deadlines, limits, and errors.
7. [Implementation-First RPC](package-development/IMPLEMENTATION-FIRST-RPC.md): accepted post-cutover C#, TypeScript, JavaScript, lock, generation, and evolution design.
8. [Callbacks And Auth](package-development/CALLBACKS-AND-AUTH.md): host-routed browser callbacks and package authorization.
9. [Stacks](package-development/STACKS.md): discovery, export, preview, import, requirements, and outcomes.
10. [Package Trust And Security](package-development/SECURITY.md): execution trust, validation, secrets, callbacks, and supply chain.
11. [Testing And CI](package-development/TESTING-AND-CI.md): unit, integration, compiled examples, and a CI baseline.
12. [Build, Validate, Publish, And Version](package-development/BUILD-PUBLISH-VERSIONING.md): artifacts, compatibility, Registry publication, and version policy.
13. [Troubleshooting](package-development/TROUBLESHOOTING.md): symptom-oriented fixes.

## Authoring Model

The package standard is language-, framework-, and build-tool neutral. Sunder currently supports these authoring routes:

| Preset | Canonical target |
| --- | --- |
| Framework-dependent .NET Worker Runtime | exact-RID `runtime` / `worker` using `Sunder.Sdk.Worker` and `sunder.worker.v1` |
| Avalonia App | `app` / `avalonia` |
| Node worker Runtime | exact-RID `runtime` / `worker` target records using a shared CommonJS module and the bound Node 24 execution runtime |
| Static web App | `app` / `web`; framework-agnostic, with React/Vite as one template |

The table is not a closed capability list. Future Python, Rust, Go, and other worker toolchains, other .NET UI approaches, and Vue, Svelte, Solid, or other web frameworks can emit the same canonical manifest, content index, payload layers, and exact targets. The Host must still explicitly support the declared target kind, protocol, module ABI, and required capabilities; format conformance alone does not make an unknown target executable.

## SDK Decision Matrix

| Authoring need | Add/use |
| --- | --- |
| Framework-dependent .NET Worker Runtime | Exact coordinated versions of `Sunder.Sdk.Worker` and `Sunder.Package.Build` |
| Avalonia views/settings | Add `Sunder.Sdk.Avalonia` only to the App leaf that uses Avalonia |
| Stack contribution | Add `Sunder.Sdk.Stacks` to the Worker leaf that implements the contributor |
| New managed canonical package | Install `Sunder.Package.Templates`; it is a scaffold tool, not a runtime dependency |
| Node worker Runtime RPC | `@sunder/sdk` plus `@sunder/package-tool` |
| Browser code in a web App | Import only `@sunder/sdk/browser`; choose any web framework |
| Node/React starter | `npm create sunder-package@latest`; React is one template, not a web requirement |
| Shared descriptor-only package | Bundle at least one RPC descriptor and emit no executable target |
| Custom Python/Rust/Go worker or UI toolchain | Emit the canonical format and a Host-supported target kind/protocol; no managed SDK dependency is inherently required |

`Sunder.Runtime.Contracts`, `Sunder.Package.Format`, and `Sunder.Registry.Contracts` are not package-author NuGet dependencies. Package projects must not reference `Sunder.App` or `Sunder.Runtime.Host`.

### .NET Worker Protocol

Every Manifest V1 Runtime target uses the exact Worker V1 entry point:

```csharp
await SunderWorker.RunAsync(
    context => new SunderWorkerOptions(providers));
```

Using the context-configured `SunderWorker.RunAsync` overload causes `Sunder.Package.Build` to infer the exact `worker-protocol.v1` target
capability; the Host then selects `sunder.worker.v1` before launch. Use only the V1 surfaces documented in
[Sunder Worker Protocol V1](SUNDER-WORKER-PROTOCOL-V1.md). C# authoring must declare one exact-RID
`worker` target per framework-dependent module leaf. `dotnet`, `process`, Worker V3, and generic Runtime RIDs are invalid.

The TypeScript SDK uses Worker V1 and strict keyed RPC descriptor V1. `@sunder/package-tool` emits thin CommonJS modules whose exact-RID target records bind `sunder.runtime.node` 24.x and `sunder.node-worker-module.v1`; Node executable bytes remain in separately trusted execution-runtime artifacts. The TypeScript surface remains intentionally narrower than the coordinated .NET SDK family.

## Architecture In One Minute

- One universal archive carries package-wide metadata, shared content, and exact App/Runtime RID targets.
- Runtime target code always runs as an exact-RID Worker V1 child process. Runtime roles own persistent package data, headless work, settings schemas, auth handlers, and RPC providers.
- App target code runs separately in `Sunder.App` and owns Avalonia or web views and shell-facing behavior.
- Each selected App target receives its own module activation and each Runtime target receives its own supervised worker-process activation.
- App talks to its own Runtime role through owner RPC; objects and local paths never cross the process boundary.
- Runtime activates dependencies before dependents and publishes package graphs as atomic generations.
- Generation retirement cancels leased work and waits for it to drain before providers or load contexts are disposed.

## Authoritative References

- [Sunder Package Standard](SUNDER-PACKAGE-STANDARD.md) is the normative source for package identity, metadata, generated manifests, output layout, archive validation, and package/Stack format limits. Developer guides link to it instead of redefining the format.
- [Sunder SDK Compatibility](SUNDER-SDK-COMPATIBILITY.md) defines the Host/SDK/target compatibility boundary and capabilities.
- [Sunder V1 Baseline](SUNDER-V1-BASELINE.md) defines the coordinated unreleased baseline and no-shim policy.
- [Sunder CLI](SUNDER-CLI.md) is the command reference.

## Drift Protection

The repository's [compiled quickstart package](../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/) exercises a Worker V1 managed module, owner RPC, generated bindings, state, settings, logging, an Avalonia App module, navigation, warmup, and theme APIs used by these guides. It is part of `Sunder.Core.slnx`, so the normal CI build compiles its exact current-RID Worker leaf and composed App/Runtime package directly from the canonical project. Template CI separately generates, builds, and packs universal six-RID Worker, Avalonia, Stack, and package-dependency variants; Node CI covers Worker/web leaves, six RID-neutral module target records, and universal package validation through the .NET inspector.
