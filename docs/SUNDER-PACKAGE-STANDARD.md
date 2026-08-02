# Sunder Package Standard

This document is the normative V1 package-format contract implemented by `Sunder.Package.Build`, `Sunder.Package.Format`, the Sunder Hosts, and the Registry.

The contract is independent of authoring language and UI framework. Current tooling emits managed .NET Runtime (`dotnet`), Avalonia App (`avalonia`), Node Runtime (`process`), and static web App (`web`) targets. React/Vite is one web template, not a format requirement. Future Python, Rust, Go, other process toolchains, other .NET UI approaches, and other web frameworks may emit this format, but Hosts still reject target kinds, protocols, or required capabilities they do not support.

## Universal Package

A package version is published as one canonical universal `.sunderpkg` archive. The archive owns:

- one package identity, display name, summary, icon, version, and dependency set;
- zero or more exact App/Runtime targets;
- language-neutral RPC contract descriptors, imports, and providers;
- one content index covering the canonical manifest and every payload file.

Managed .NET authors declare identity and runtime dependencies with `Sunder.Sdk.Packaging` attributes, and `Sunder.Package.Build` generates the manifest, development tree, and archive. Other toolchains may use their own validated source configuration. No authoring route should hand-copy the canonical output `manifest/sunder-package.json`; tooling must generate and validate it with the content index and target payload.

A package normally declares at least one exact target. A shared-only package may declare zero targets only when it bundles at least one RPC contract descriptor.

## Identity And Versions

Package, contract, and provider ids use lowercase dot-separated ASCII identifiers. Published package ids are stable. Package and contract versions are strict SemVer 2.0.

V1 dependency ranges support:

- an exact version, such as `1.2.3`;
- one comparison using `<`, `<=`, `>`, `>=`, or `=`;
- space-conjoined comparisons, such as `>=1.2.3 <2.0.0`.

Wildcards, comma conjunctions, hyphen ranges, caret/tilde ranges, unions, and whitespace between an operator and its version are invalid.

```csharp
using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "example.notes",
    Name = "Example Notes",
    Summary = "Adds a notes workspace.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "example.foundation",
    VersionRange = ">=1.0.0 <2.0.0")]
```

The package version comes from the MSBuild `Version` property.

## Canonical Manifest

The canonical manifest is `manifest/sunder-package.json`. JSON member names are case-sensitive and the schema is closed.

```json
{
  "archiveFormatVersion": 1,
  "manifestVersion": 1,
  "id": "example.notes",
  "name": "Example Notes",
  "summary": "Adds a notes workspace.",
  "version": "1.0.0",
  "icon": "assets/icon.png",
  "dependsOn": [
    {
      "packageId": "example.foundation",
      "versionRange": ">=1.0.0 <2.0.0"
    }
  ],
  "targets": [
    {
      "role": "app",
      "rid": "win-x64",
      "kind": "avalonia",
      "entryPoint": "lib/Example.Notes.App.dll",
      "targetFramework": "net10.0",
      "sdkVersion": "1.1.0",
      "requiredHostCapabilities": [
        "sdk-baseline-1-1.v1",
        "core.v1",
        "contributions.v1",
        "views.v1"
      ]
    },
    {
      "role": "runtime",
      "rid": "win-x64",
      "kind": "dotnet",
      "entryPoint": "lib/Example.Notes.Runtime.dll",
      "targetFramework": "net10.0",
      "sdkVersion": "1.1.0",
      "requiredHostCapabilities": [
        "sdk-baseline-1-1.v1",
        "core.v1",
        "contributions.v1",
        "rpc.v1"
      ]
    }
  ],
  "contractBundles": [
    {
      "contractId": "example.notes.search",
      "version": "1.0.0",
      "descriptorPath": "contracts/search.rpc.json",
      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    }
  ],
  "usesContracts": [
    {
      "contractId": "example.notes.search",
      "versionRange": ">=1.0.0 <2.0.0",
      "required": false,
      "actions": ["discover", "invoke"]
    }
  ],
  "provides": [
    {
      "providerId": "example.notes.search.default",
      "contractId": "example.notes.search",
      "contractVersion": "1.0.0",
      "contractSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "role": "runtime"
    }
  ]
}
```

Package-wide fields are `archiveFormatVersion`, `manifestVersion`, `id`, `name`, `summary`, `version`, `icon`, `dependsOn`, `contractBundles`, `usesContracts`, and `provides`.

Each target is keyed by exact `(role, rid)` and contains its own activation and compatibility metadata:

- `role`: `app` or `runtime`;
- `rid`: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, or `osx-arm64`;
- `kind`: `avalonia` or `web` for App, `dotnet` or `process` for Runtime;
- `entryPoint`: a logical path resolved from that target's payload union;
- `targetFramework`: optional portable target metadata;
- `sdkVersion`: optional strict SemVer for SDK-backed targets;
- `requiredHostCapabilities`: distinct capabilities validated before target loading;
- `views`: optional declarations for web App targets.

An exact target key may appear only once. Payload layers without a matching declared role or target are invalid.

## Layered Payload

The physical archive layout is:

```text
manifest/
  sunder-package.json
  content-index.json
payload/
  shared/
    assets/
    contracts/
    lib/
  app/
    shared/
    win-x64/
    win-arm64/
    linux-x64/
    linux-arm64/
    osx-x64/
    osx-arm64/
  runtime/
    shared/
    win-x64/
    win-arm64/
    linux-x64/
    linux-arm64/
    osx-x64/
    osx-arm64/
```

Only directories containing files need to exist. Every path beneath a payload layer maps to a logical path by removing the layer prefix.

For exact target `(role, rid)`, the Host constructs one immutable logical projection from:

1. `payload/shared/<logical-path>`
2. `payload/<role>/shared/<logical-path>`
3. `payload/<role>/<rid>/<logical-path>`

These layers form a union, not an override chain. Duplicate, case-colliding, or directory/file-colliding logical paths fail validation. A target sees no files belonging only to another role or RID.

Build aggregation factors byte-identical files upward into the broadest valid layer. It rejects duplicate target keys and requires every leaf to agree on package-wide manifest metadata.

## Content Index

`manifest/content-index.json` has `schemaVersion: 1` and an ordinally ordered `files` array. Each entry records physical archive `path`, lowercase SHA-256, and byte `size`. It covers the package manifest and every payload file; it excludes only itself.

Index validation rejects missing, extra, duplicate, case-colliding, hash-mismatched, size-mismatched, unsafe, or structurally invalid paths.

## RPC Contracts

Cross-package behavior uses strict language-neutral RPC descriptors. CLR interfaces, DTOs, clients, and provider adapters are package-local conveniences and never establish cross-package type identity.

Every contract used or provided by a package must have a matching local `contractBundles` entry. Its descriptor:

- is physically stored at `payload/shared/<descriptorPath>`;
- uses strict UTF-8 JSON and descriptor version `1`;
- has identity and version matching the manifest entry;
- has a canonical SHA-256 matching `sha256`;
- uses the closed supported JSON Schema profile and local `#/$defs/...` references.

`usesContracts` declares the maximum requested `discover`, `invoke`, and `subscribe` actions. Installing or updating a package consents to all actions declared by that exact manifest; Runtime records version- and manifest-fenced grants automatically. Development packages receive equivalent activation-scoped access without durable grants. Undeclared actions remain default-deny. `required` controls dependency readiness, not consent.

`provides` binds a stable provider id to one exact contract identity, descriptor hash, and target role. Target code registers the same provider id with `RegisterRpcProvider`. Runtime stamps discovery results and exact activation endpoint references; an old endpoint never retargets a replacement activation.

Large content stays off the JSON control plane and uses Host-mediated content references. Requests, responses, and stream events are validated against the bundled descriptor before dispatch or delivery.

MSBuild authoring uses `SunderContractBundle`, `SunderUsesContract`, and `SunderRpcProvider`. `SunderRpcCSharpGenerator` can produce deterministic package-local DTO, client, and provider scaffolding.

## Modules And Activation

Managed Runtime targets expose at most one public, top-level, non-abstract, non-generic `ISunderRuntimePackageModule` with a public parameterless constructor. Managed App targets follow the same rule for `ISunderAppPackageModule`. One class may implement both interfaces, but each Host creates a separate instance, load context, service provider, and activation lifetime.

Runtime modules register background services, settings schemas, package-scoped App operations, callbacks/auth handlers, and RPC providers. App modules register shell-facing contributions such as Avalonia views. App and Runtime do not exchange object instances or filesystem paths.

A shared-only package has no module activation. Its descriptors remain available to package validation and Registry contract discovery.

## Canonical And Derived Artifacts

The `.sunderpkg` is the only publication input and canonical package artifact. Registry publication validates and stores its exact bytes and SHA-256 before deriving projections.

A `.sunderprojection` is an immutable derivative for one of:

- `shared` with no RID;
- `app/<rid>`;
- `runtime/<rid>`.

Each projection contains `manifest/sunder-projection.json`, the canonical package manifest, a projection-specific content index, and only the selected physical payload layer. Its descriptor binds package id/version, source archive SHA-256, projection kind/RID, manifest SHA-256, and projection SHA-256. Projections cannot be published as source packages and are always reproducible from the canonical archive.

## Build Outputs

`dotnet build` emits a validated unpacked tree at `bin/<Configuration>/<TFM>/sunder-dev`. The canonical explicit archive target is `PackSunderPackage`, which emits a deterministic `.sunderpkg` beside that target output; `dotnet publish` also invokes the pack task and places its archive in the publish directory.

The generated dotnet template uses an aggregate project with Runtime and optional App leaves. It always creates a non-packable package-local `*.Protocol` project containing a bundled descriptor and generated bindings. Role projects consume it through private project references. `--withHostDependency` adds only `[SunderPackageDependency]` metadata.

TypeScript process packages use `npm create sunder-package@latest`; `--template react-node` adds a Vite/React web App target. Production Runtime targets are pinned Node SEA executables built and smoked natively for each exact RID.

## Archive Safety

Archives are deterministic ZIP files with fixed entry timestamps and ordinal paths. Portable paths use `/`, visible ASCII, non-empty segments, and no `.` or `..`. Rooted, drive, UNC, backslash, control/NUL, Windows-reserved, trailing-dot/space, link, and reparse-point forms are rejected.

Default extraction limits are 4,096 entries, 256 MiB per entry, 1 GiB total uncompressed bytes, a 200:1 per-entry compression ratio, 240-character physical paths, and 32 path segments. Metadata JSON is limited to 1 MiB. Contract descriptors are limited to 4 MiB.

### Package Icons

Package icons must resolve from the shared logical payload, be at most 1 MiB and 8192 by 8192 pixels, and use a supported BMP, GIF, ICO, JPEG, PNG, or WebP signature matching the extension. SVG is not accepted. Format validation establishes bounded image structure and signature/extension agreement; it does not make image bytes or the package source trusted.

Extraction writes to a temporary sibling and publishes atomically only after validation succeeds. Install, update, Registry publication, and projection generation all use the same package-format validator.

## Stack Archive Validation

A canonical `.sunderstack` uses `manifest/sunder-stack.json`, `manifest/content-index.json`, and only `payload/fragments/`, `payload/files/`, and `payload/media/` content. The same ZIP path, entry-count, expansion, metadata, and atomic-extraction limits apply as package archives. The exact content index rejects missing, extra, duplicate, case-colliding, hash-mismatched, size-mismatched, or unsafe entries.

Stack manifest schema version `1` requires at least one package requirement or fragment. Unknown optional features produce warnings; unknown required features fail validation. Fragment JSON must be one object no larger than 4 MiB. Media is limited to signature-matched PNG, JPEG, WebP, or GIF files no larger than 10 MiB and 8192 by 8192 pixels. Required inputs explicitly declare `Public` or `Secret`; secret inputs cannot carry portable defaults.

The validator also performs bounded heuristic secret scanning of UTF-8 text. A finding rejects the archive, but no scanner can prove that an archive contains no secret or that imported content is safe. Treat every Stack as untrusted input after structural validation and require a side-effect-free preview before apply.

## Dependency Boundaries

`[SunderPackageDependency]` declares an installed Sunder package requirement. A normal NuGet reference is only a build-time source of helpers. Referencing another package's helper assembly does not share those CLR types across package load contexts; every consumer still bundles the exact descriptor it uses.

Package code may reference public `Sunder.Sdk*` packages and `Sunder.Package.Build`. It must not reference `Sunder.App`, `Sunder.Runtime.Host`, or private Host implementation contracts.
