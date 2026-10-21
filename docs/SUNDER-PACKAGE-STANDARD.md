# Sunder Package Standard

This document is the normative archive format V1 and package manifest V1 contract implemented by `Sunder.Package.Build`, `Sunder.Package.Format`, the Sunder Hosts, and the Registry.

Source authoring and reviewed imports generate the manifest and contract evidence.
Format validation establishes structural integrity; Registry/Host admission separately
enforces publication, dependency, policy and activation authority. See
[single-project authoring](package-development/SINGLE-PROJECT-AUTHORING.md) and
[ordinary services](package-development/ORDINARY-SERVICES.md).

The contract is independent of authoring language and UI framework. Manifest V1 permits only exact-RID `worker` Runtime targets whose entry-point modules are loaded by exact bound execution runtimes. App targets remain `avalonia` or `web`. Runtime `dotnet`, `process`, generic-RID targets, directly executable package entry points, and Worker V3 are invalid. React/Vite is one web template, not a format requirement. Future Python, Rust, Go, other worker toolchains, other .NET UI approaches, and other web frameworks may emit this format, but Hosts still reject target protocols or required capabilities they do not support.

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

Package dependency ranges support:

- an exact version, such as `1.2.3`;
- one comparison using `<`, `<=`, `>`, `>=`, or `=`;
- space-conjoined comparisons, such as `>=1.2.3 <2.0.0`.

Wildcards, comma conjunctions, hyphen ranges, caret/tilde ranges, unions, and whitespace between an operator and its version are invalid. Manifest dependency ranges must have canonical whitespace and a nonempty solution set.

Every `dependsOn` record explicitly declares `presence: required|optional`; readers never default a missing value. Package installation presence is independent of `usesContracts[].required`, which controls provider readiness. Authored non-RPC dependencies currently emit `presence: required`.

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
      "versionRange": ">=1.0.0 <2.0.0",
      "presence": "required"
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
      "kind": "worker",
      "entryPoint": "lib/Example.Notes.Worker.dll",
      "targetFramework": "net10.0",
      "sdkVersion": "1.1.0",
      "requiredHostCapabilities": [
        "sdk-baseline-1-1.v1",
        "core.v1",
        "contributions.v1",
        "execution-runtime.v1",
        "rpc.v1",
        "worker-protocol.v1"
      ],
      "executionRuntime": {
        "id": "sunder.runtime.dotnet",
        "versionRange": ">=10.0.0 <11.0.0",
        "moduleAbi": "sunder.dotnet-worker-module.v1"
      }
    }
  ],
  "contractBundles": [
    {
      "source": "owned",
      "authorityKind": "package",
      "authorityId": "example.notes",
      "contractId": "example.notes.search",
      "version": "2.0.0",
      "descriptorPath": "contracts/search.rpc.json",
      "descriptorSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "abiSha256": "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
      "exposure": "public"
    }
  ],
  "usesContracts": [
    {
      "role": "runtime",
      "contractId": "example.notes.search",
      "contractVersion": "2.0.0",
      "abiSha256": "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
      "required": false,
      "actions": ["discover", "invoke"]
    }
  ],
  "provides": [
    {
      "providerId": "example.notes.search.default",
      "role": "runtime",
      "contractId": "example.notes.search",
      "contractVersion": "2.0.0",
      "descriptorSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "abiSha256": "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
      "audience": "public"
    }
  ],
  "packageArtifactImports": [],
  "packageApiImports": [],
  "providerImports": [],
  "platformImports": []
}
```

Package-wide fields are `archiveFormatVersion`, `manifestVersion`, `id`, `name`, `summary`, `version`, `icon`, `dependsOn`, `targets`, `contractBundles`, `usesContracts`, `provides`, `packageArtifactImports`, `packageApiImports`, `providerImports`, and `platformImports`.

The four root import-group arrays may be absent or empty when unused; explicit JSON `null` is rejected. Bundle provenance and dependency presence are mandatory, not reader-defaulted. Conditional provenance members must be absent when forbidden, not present as `null`.

Each target is keyed by exact `(role, rid)` and contains its own activation and compatibility metadata:

- `role`: `app` or `runtime`;
- `rid`: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, or `osx-arm64`;
- `kind`: `avalonia` or `web` for App and exactly `worker` for Runtime;
- `entryPoint`: a logical path resolved from that target's payload union;
- `targetFramework`: optional portable target metadata;
- `sdkVersion`: strict SemVer for every Runtime target and optional strict SemVer for other SDK-backed targets;
- `requiredHostCapabilities`: distinct capabilities validated before target loading;
- `views`: optional declarations for web App targets.

An exact target key may appear only once. Payload layers without a matching declared role or target are invalid.

Every Runtime target must require `execution-runtime.v1`, `rpc.v1`, `worker-protocol.v1`, and exactly the `sdk-baseline-<major>-<minor>.v1` capability matching its `sdkVersion`. Its entry point is a module loaded by the exact bound execution runtime in an independently supervised process. A C# worker entry point is the official managed `.dll` directly under `lib/`; a Node worker entry point is a `.cjs` module. Package apphosts, directly executable entry points, legacy protocol capabilities, and generic `dotnet` or `process` capabilities are rejected rather than launched, upgraded, or interpreted as aliases.

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

Build aggregation factors byte-identical files upward into the broadest valid layer. It rejects duplicate target keys and requires matching package-wide manifest metadata, subject only to the owned-only App export projection described under Build Outputs.

## Content Index

`manifest/content-index.json` has `schemaVersion: 1` and an ordinally ordered `files` array. Each entry records physical archive `path`, lowercase SHA-256, and byte `size`. It covers the package manifest and every payload file; it excludes only itself.

Index validation rejects missing, extra, duplicate, case-colliding, hash-mismatched, size-mismatched, unsafe, or structurally invalid paths.

## RPC Contracts

Cross-package behavior uses strict language-neutral RPC descriptors. CLR interfaces, DTOs, clients, and provider adapters are package-local conveniences and never establish cross-package type identity.

Every contract used or provided by a package must have a matching local `contractBundles` entry. Its descriptor:

- is physically stored at `payload/shared/<descriptorPath>`;
- uses strict UTF-8 JSON and descriptor version `1`;
- has identity and version matching the manifest entry;
- has a canonical descriptor SHA-256 matching `descriptorSha256` and a domain-separated structural ABI SHA-256 matching `abiSha256`;
- uses the closed RPC V1 JSON Schema profile, keyed service/method/error maps, and local `#/$defs/...` references.

Every bundle also explicitly declares `source` (`owned` or `imported`), `authorityKind` (`package` or `platform`), and `authorityId`. An owned bundle requires package authority equal to the enclosing manifest `id` and forbids selected-authority fields, avoiding a package-content hash cycle.

An imported bundle requires public exposure, `selectedAuthorityRelease`, and `selectedAuthorityContentIdentity`. Platform authority is `sunder.core`. The explicitly selected Registry establishes publication authority for both kinds; catalog signing/trust fields are absent from the closed format. Bundling or implementing a descriptor grants no authority.

Each `usesContracts` entry binds one exact caller `role`, `contractVersion`, and `abiSha256`, then declares the maximum requested `discover`, `invoke`, and `subscribe` actions. Installing or updating a package consents to all actions declared by that exact manifest; Runtime records version- and manifest-fenced grants automatically. Development packages receive equivalent activation-scoped access without durable grants. Undeclared actions remain default-deny. `required` controls dependency readiness, not consent; Runtime does not resolve contract version ranges from an immutable package.

`provides` binds a stable provider id and target role to one exact contract version, `descriptorSha256`, `abiSha256`, and `owner` or `public` audience. A public provider requires a public contract bundle; package exposure permits only owner audience. Target code registers the same canonical identity with `RegisterRpcProvider`. Runtime stamps discovery results and exact activation endpoint references; an old endpoint never retargets a replacement activation.

Large content stays off the JSON control plane and uses Host-mediated content references. Requests, responses, and stream events are validated against the bundled descriptor before dispatch or delivery.

Source-authored C# builds run the lock/cache-derived import prepass before semantic provider discovery and include generated imports in final compilation. `PackageContractBuilder` consumes the authoring receipt with its expected SHA-256, lock hash, and package identity; it validates descriptor snapshots and task-item agreement before projecting bundles, uses, providers, import groups, and dependencies. Generated source, owned/imported descriptor snapshots, and the applicable receipt/export outputs publish as one atomic marker-owned set. Neither receipt nor project export is package content.

Lower-level bundle/use/provider items describe generated transport or explicit test
leaves. They cannot author imported authority metadata. `SunderRpcCSharpGenerator`
is the deterministic binding backend, not another contract authority or normal authoring recipe.

### Import Groups

The closed Manifest V1 groups distinguish selected artifact evidence from ultimate contract authority:

- `packageArtifactImports` records each selected package's reviewed `requestedVersionRange`, explicit `includePrerelease`, selected version/content identity, complete public-inventory count/hash, and ultimate `authorityConstraints`.
- `packageApiImports` records package ID, explicit `presence`, and exact `bundleRefs`/role-scoped `useRefs` for the complete public package-authority subset. Platform-authority inventory members generate no consumer uses.
- `providerImports` selects one public package-authority contract with an exact `bundleRef` and runtime `providerRefs`. Provider-only intent grants no consumer uses and contributes required dependencies.
- `platformImports` describes provider-only Registry catalog selections and exact release/content, bundle and provider references. C# and Node validate the selected complete catalog and generate bindings offline from restored evidence.

Groups and nested references are bounded, ordered, unique, and closed over the exact local bundle/use/provider tuples. Missing, extra, conflicting, or orphaned references fail validation. Receipt validation verifies the complete lock inventory projection; structural archive validation alone cannot independently authenticate the selected artifact or its authority.

The [platform-catalog implementation](../specs/rpc/v1/authoring/platform-catalog/README.md) provides bounded Format and independent Node content validation, exact immutable pins and explicit Core catalog export. Administrator import into the selected Registry establishes publication authority; no catalog signing setup is required. The implemented Stack adapter derives the unchanged `2.0.0` descriptor and ABI from ordinary source, including all 48 definitions and six methods. SDK builds generate its neutral handler and required partial factory in immutable `obj` output. Check/Accept/Freeze/Export and Registry publication-evidence import use the baseline under `eng/platform-contracts`. Local bytes or generated handlers alone grant no publication or execution permission.

Lock-derived dependencies preserve canonical reviewed ranges and package API presence, with `required` winning when contributors coalesce by package ID. Authored non-RPC dependencies cannot duplicate lock-derived dependency IDs. A distinct transitive package authority without an explicit selection and reviewed range fails closed; tooling must not infer a permissive range or an exact-equality/content pin from its provenance snapshot.

**Compatible Installation** retains reviewed package range/prerelease policy plus exact imported contract ID, version, descriptor SHA-256, and ABI SHA-256. Selected package and ultimate package-authority release/content hashes are restore/build/inventory provenance only, not installation or routing hard pins. An in-range package patch with the same exact RPC identities may satisfy the target policy; ABI equality alone is insufficient. Restore still verifies the exact selected artifact and never substitutes another release. Execution-runtime exact pins and platform-catalog release/content constraints remain independent.

Registry publication and Host admission validate imported groups and optional branches against complete accepted inventories, exact RPC identities and current lifecycle policy. Local Format validation or artifact generation cannot manufacture Registry publication or bypass those checks. Canonical first-party migration and deployment qualification remain separate program milestones.

## App And Worker Activation

Managed App targets expose at most one public, top-level, non-abstract, non-generic `ISunderAppPackageModule` with a public parameterless constructor. The App creates its own load context, service provider, and activation lifetime. A Runtime target is a module launched through its exact execution-runtime recipe in an independently supervised process that speaks `sunder.worker.v1`; Runtime never loads an `ISunderRuntimePackageModule` or builds package-owned Runtime DI in the Host process.

Workers advertise an immutable contribution catalog containing RPC providers, an optional settings schema, callbacks/auth, and migration plans. Package-local composition and lifecycle are owned inside the worker. App modules register shell-facing contributions such as Avalonia views. App-to-Runtime behavior uses owner RPC with generated package-local bindings; App and Runtime do not exchange object instances or local filesystem paths.

A shared-only package has no module activation. Its descriptors remain available to package validation and Registry contract discovery.

## Canonical And Derived Artifacts

The `.sunderpkg` is the only publication input and canonical package artifact. Registry publication validates and stores its exact bytes and SHA-256 before deriving projections.

A `.sunderprojection` is an immutable derivative for one of:

- `shared` with no RID;
- `app/<rid>`;
- `runtime/<rid>`.

Each projection contains `manifest/sunder-projection.json`, the canonical package manifest, a projection-specific content index, and only the selected physical payload layer. Its descriptor binds package id/version, source archive SHA-256, projection kind/RID, manifest SHA-256, and projection SHA-256. Projections cannot be published as source packages and are always reproducible from the canonical archive.

Registry installation accepts exactly the shared projection plus the selected role projections for the requesting Host's current RID. Missing, extra, duplicate, foreign-RID, or plan/manifest-mismatched projections fail before install. Runtime retains the complete canonical source manifest byte-for-byte and composes an installed content index for the acquired shared/target payloads. The installed catalog records the exact acquired target keys separately. This preserves complete import groups, dependencies, public contract inventory and source provenance even when acquisition omits an import's last consumer or provider role.

Installed projection validation checks the whole manifest's metadata/import graph and the complete acquired payload, including entry points, hashes and .NET Worker dependencies. Unacquired target payload is not required, and extra role/RID payload is rejected. Runtime bindings, activation, active uses, permissions and App snapshots are restricted to the recorded acquisition scope; a declaration in the source manifest alone does not authorize loading that target. Local sideload first validates the complete universal archive, then performs equivalent current-RID acquisition. The private Registry composition handoff and installed catalog supply this validation scope; ordinary `.sunderpkg` validation and publication still require a complete archive and never infer acquisition from missing files.

## Build Outputs

One authored .NET Library project with `SunderPackageProject=true` produces isolated
Worker and optional App roles under `sunder-roles/<role>/<rid>`. Ordinary Build composes
the current RID at `bin/<Configuration>/<TFM>/sunder-dev`. Direct `PackSunderPackage`
builds the declared RIDs and emits one deterministic universal archive. No authored
App/Worker/Protocol project or source manifest is required.

Role-aware aggregation retains exact package-wide contract/import evidence, scoped
providers/uses and selected dependency/resource ownership. C# signing hooks operate
on private package-owned publish files before indexing; Pack validates that index
without rewriting it. Qualified prebuilt fan-in uses `SunderPackageUsePrebuiltLeaves`,
`BuildProjectReferences=false` and exact reviewed role-input selection without a
restore/build/generation fallback. See [prebuilt inputs](package-development/PREBUILT-ROLE-INPUTS.md).

TypeScript Worker V1 packages use bundled CommonJS modules with `.cjs` entry points; `--template react-node` adds a Vite/React web App target. `@sunder/package-tool` emits exact-RID Manifest V1 target records bound to the separately trusted Node 24 execution runtime, then factors byte-identical module payloads into the shared Runtime layer during aggregation.

Node ordinary services and reviewed package/provider/platform selections produce the
same canonical authority/import groups, including explicit optional dependency intent.
Generation, validation and admission use the shared V1 format and parity corpus.

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

The import manifest models are a supported public type surface in the existing shared `Sunder.Package.Format` library for Core, Registry, and tooling consumers. That internally distributed library is not a new public NuGet package or a package-author SDK dependency. This generation/projection change adds no projects or compatibility readers.
