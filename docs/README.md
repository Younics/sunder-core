# Documentation

This folder contains current Sunder documentation for the implementation in this repository and a separate `design/` area for future designs.

> **Source channel:** These pages track current source and may describe behavior not present in the latest published binaries or packages. For released behavior, read the same path from the matching component tag and the README embedded in the released NuGet/npm artifact.

## Current Documentation

- [Sunder overview](SUNDER.md)
- [Package development](SUNDER-PACKAGE-DEVELOPMENT.md)
- [Package standard](SUNDER-PACKAGE-STANDARD.md): normative package and Stack format rules
- [SDK compatibility](SUNDER-SDK-COMPATIBILITY.md)
- [Worker V1 protocol](SUNDER-WORKER-PROTOCOL-V1.md)
- [RPC V1 specification and conformance fixtures](../specs/rpc/v1/README.md)
- [TypeScript and checked-JavaScript services, Workers and web Apps](SUNDER-NODE-PROCESS-PACKAGES.md)
- [Sunder V1 baseline](SUNDER-V1-BASELINE.md)
- [CLI reference](SUNDER-CLI.md)
- [Desktop app and development arguments](SUNDER-APP.md)
- [Canonical current-user Host behavior](SUNDER-HOST-SERVICE.md)
- [Core release workflow and signing status](SUNDER-CORE-RELEASES.md)

## Package Development Guides

- [Getting Started](package-development/GETTING-STARTED.md)
- [Ordinary C# Services](package-development/ORDINARY-SERVICES.md)
- [Implementation-First RPC](package-development/IMPLEMENTATION-FIRST-RPC.md): current source authoring, managed APIs and public/private lifecycle
- [Node Service Authoring](SUNDER-NODE-PROCESS-PACKAGES.md#author-ordinary-services)
- [Browser And React Lifetimes](SUNDER-NODE-PROCESS-PACKAGES.md#browser-and-react-lifetimes)
- [Package Anatomy](package-development/PACKAGE-ANATOMY.md)
- [Activation, DI, And Disposal](package-development/ACTIVATION-AND-DI.md)
- [Data And Logging](package-development/DATA-AND-LOGGING.md)
- [Avalonia Views](package-development/AVALONIA.md)
- [Advanced Runtime Operations](package-development/RUNTIME-OPERATIONS.md)
- [Callbacks And Auth](package-development/CALLBACKS-AND-AUTH.md)
- [Stacks](package-development/STACKS.md)
- [Package Trust And Security](package-development/SECURITY.md)
- [Testing And CI](package-development/TESTING-AND-CI.md)
- [Build, Validate, Publish, And Version](package-development/BUILD-PUBLISH-VERSIONING.md)
- [Troubleshooting](package-development/TROUBLESHOOTING.md)
- [Compiled quickstart sample](../languages/csharp/dotnet/samples/Sunder.Package.Quickstart/README.md)

## Future Designs

- [Future Host architecture](design/SUNDER-HOST-ARCHITECTURE.md)

## Scope

Current documentation describes behavior verified against source. Design documents and proposed specifications describe future behavior only and must not be read as current implementation documentation. Historical planning docs and private Registry implementation notes are intentionally excluded.
