# Sunder Package Development

> **Source channel:** This manual tracks unreleased Sunder V1 source, coordinated SDK `1.1.x` (`[1.1.0,1.2.0)`), .NET 10, and Runtime protocol revision 8. Read it from the matching `sdk/v*` tag for released SDK behavior.

This is the canonical landing page for public Sunder package development. The focused guides describe the current universal-package implementation in this repository.

## Start Here

1. [Getting Started](package-development/GETTING-STARTED.md): create, build, run, and validate a package.
2. [Package Anatomy](package-development/PACKAGE-ANATOMY.md): choose exact App/Runtime targets or a shared-only protocol package.
3. [Activation, DI, And Disposal](package-development/ACTIVATION-AND-DI.md): lifecycle, host services, availability, threading, and unload behavior.
4. [Data And Logging](package-development/DATA-AND-LOGGING.md): storage, settings, secrets, logging, exact limits, and errors.
5. [Avalonia Views](package-development/AVALONIA.md): views, settings UI, navigation, warmup, caching, and theme resources.
6. [Owner RPC](package-development/RUNTIME-OPERATIONS.md): typed requests, streams, provider discovery, deadlines, limits, and errors.
7. [Callbacks And Auth](package-development/CALLBACKS-AND-AUTH.md): host-routed browser callbacks and package authorization.
8. [Stacks](package-development/STACKS.md): discovery, export, preview, import, requirements, and outcomes.
9. [Package Trust And Security](package-development/SECURITY.md): execution trust, validation, secrets, callbacks, and supply chain.
10. [Testing And CI](package-development/TESTING-AND-CI.md): unit, integration, compiled examples, and a CI baseline.
11. [Build, Validate, Publish, And Version](package-development/BUILD-PUBLISH-VERSIONING.md): artifacts, compatibility, Registry publication, and version policy.
12. [Troubleshooting](package-development/TROUBLESHOOTING.md): symptom-oriented fixes.

## Authoring Model

The package standard is language-, framework-, and build-tool neutral. Sunder currently supports these authoring routes:

| Preset | Canonical target |
| --- | --- |
| Self-contained .NET worker Runtime | exact-RID `runtime` / `worker` using `Sunder.Sdk.Worker` and `sunder.worker.v3` |
| Avalonia App | `app` / `avalonia` |
| Node worker Runtime | `runtime` / `worker` using `sunder.worker.v3` |
| Static web App | `app` / `web`; framework-agnostic, with React/Vite as one template |

Those are current supported routes, not a closed capability list. Future Python, Rust, Go, and other worker toolchains, other .NET UI approaches, and Vue, Svelte, Solid, or other web frameworks can emit the same canonical manifest, content index, payload layers, and exact targets. The Host must still explicitly support the declared target kind, protocol, and required capabilities; format conformance alone does not make an unknown target executable.

## SDK Decision Matrix

| Authoring need | Add/use |
| --- | --- |
| Self-contained .NET worker Runtime | Exact coordinated versions of `Sunder.Sdk.Worker` and `Sunder.Package.Build` |
| Avalonia views/settings | Add `Sunder.Sdk.Avalonia` only to the App leaf that uses Avalonia |
| Stack contribution | Add `Sunder.Sdk.Stacks` to the Worker leaf that implements the contributor |
| New managed aggregate | Install `Sunder.Package.Templates`; it is a scaffold tool, not a runtime dependency |
| Node worker Runtime RPC | `@sunder/sdk` plus `@sunder/package-tool` |
| Browser code in a web App | Import only `@sunder/sdk/browser`; choose any web framework |
| Node/React starter | `npm create sunder-package@latest`; React is one template, not a web requirement |
| Shared descriptor-only package | Bundle at least one RPC descriptor and emit no executable target |
| Custom Python/Rust/Go worker or UI toolchain | Emit the canonical format and a Host-supported target kind/protocol; no managed SDK dependency is inherently required |

`Sunder.Runtime.Contracts`, `Sunder.Package.Format`, and `Sunder.Registry.Contracts` are not package-author NuGet dependencies. Package projects must not reference `Sunder.App` or `Sunder.Runtime.Host`.

### .NET Worker Protocol

Every Manifest V3 Runtime target uses the exact Worker V3 entry point:

```csharp
await SunderWorker.RunAsync(
    context => new SunderWorkerOptions(providers));
```

Using the context-configured `SunderWorker.RunAsync` overload causes `Sunder.Package.Build` to infer the exact `worker-protocol.v3` target
capability; the Host then selects `sunder.worker.v3` before launch. Use only the V3 surfaces documented in
[Sunder Worker Protocol V3](SUNDER-WORKER-PROTOCOL-V3.md). C# authoring must declare one exact-RID
`worker` target per self-contained leaf. `dotnet`, `process`, Worker V1, and generic Runtime RIDs are invalid.

The TypeScript SDK is intentionally narrower than the coordinated .NET SDK family: it covers descriptor parsing/validation/generation, Worker V3 RPC/client/content, Host-mediated settings/state/files/secrets/workspace, storage migrations, structured logging, callback/auth handlers, lifecycle callbacks, and the browser RPC/navigation bridge. It does not expose App modules, .NET worker composition/lifecycle services, managed Stack adapters, Avalonia, Registry management, or install/update APIs.

## Architecture In One Minute

- One universal archive carries package-wide metadata, shared content, and exact App/Runtime RID targets.
- Runtime target code always runs as an exact-RID Worker V3 child process. Runtime roles own persistent package data, headless work, settings schemas, auth handlers, and RPC providers.
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

The repository's [compiled quickstart package](../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/) exercises a Worker V3 executable, owner RPC, generated bindings, state, settings, logging, an Avalonia App module, navigation, warmup, and theme APIs used by these guides. It is part of `Sunder.Core.slnx`, so the normal CI build compiles and aggregates its current-platform package. Template CI separately generates, builds, and packs universal six-RID Worker, Avalonia, Stack, and package-dependency variants; Node CI covers worker/web leaves and six-RID SEA aggregation.
