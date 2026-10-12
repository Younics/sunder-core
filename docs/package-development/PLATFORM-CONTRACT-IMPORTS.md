# Platform Contract Imports

Platform provider imports select a contract from an explicit local Core catalog or
the user's chosen Registry catalog. Creating and developing unpublished packages
requires no Registry.
They generate provider surfaces and exact platform constraints, without consumer
uses or a package dependency on `sunder.core`.

## Registry Authority

For published dependencies, the explicitly selected Registry establishes publication authority.
The official V1 origin is `https://hub.sunderapp.io/`; a different configured Registry
uses the same protocol. Administrators publish platform catalogs, and package tools
verify immutable release/content identities and the complete descriptor inventory.
Catalog signing keys, trust bundles and independent Core-signature verification are
not required. HTTPS, publication authentication, ownership, exact hashes and locked
origins provide the chosen authority and integrity model.

## Select And Implement

Select a platform contract before implementing it. A fresh project may configure
imports before generation or its first public owned Accept. Initial dependency
configuration validates exact catalog/inventory hashes, generated names and stable
inputs while permitting the newly selected provider surface to remain temporarily
unbound. Generation requires the complete implementation. Public owned services
retain explicit acceptance/freezing and lost-baseline protections; a private-only
or imported-provider-only project needs no owned baseline transition. For a completely
local workflow:

```powershell
dotnet msbuild Package.csproj -t:ImplementSunderPlatformApi `
  -p:SunderContractId=sunder.stack.contributor `
  -p:SunderPlatformCatalogPath=./sunder-platform-contracts.v1.json

sunder-package platform implement sunder.stack.contributor --platform-catalog ./sunder-platform-contracts.v1.json --project .
```

Core's [catalog export command](../../eng/platform-contracts/README.md) produces the
canonical input from its reviewed source/baseline. To select a published catalog:

```powershell
dotnet msbuild Package.csproj -t:ImplementSunderPlatformApi `
  -p:SunderContractId=sunder.stack.contributor `
  -p:SunderRegistryOrigin=https://hub.sunderapp.io/

sunder-package platform implement sunder.stack.contributor --registry https://hub.sunderapp.io/ --project .
```

The stable authority range comes from the installed SDK/tooling minor line. Version,
prerelease, package presence and package-candidate selection overrides are
rejected. The default key for `sunder.stack.contributor` is `stackContributor`;
`SunderPackageApiLocalName` / `--local-name` resolves an explicit key collision.

C# generates `Sunder.Generated.Rpc.IStackContributorService`. Node exposes the
lock-derived `Implementations.stackContributor` service contract from `#sunder/api`.
Import `defineService` from `@sunder/sdk` and bind a complete ordinary method record
with `defineService(methods, { implements: Implementations.stackContributor })`.
The generated types define every required method, request/result and invocation
context; checked JavaScript uses the same contextual signatures. Multiple
implementations require distinct stable `providerId` values.

The configured `services.module` exports the definitions; the tool generates
registration, dispatch and the Worker bootstrap. Providing an imported/platform
contract creates no App or Runtime consumer uses, even though its descriptor is
public. No schema or version inventory is copied into source, and there is no
schema-constructor or `serviceTypes` fallback.

Once the selected provider is implemented, run generation/Build normally. Node
private-only and imported-provider-only projects do not need `contracts accept`
or `freeze`; current private versions follow the full package version. If the
project also declares owned public services, accept/freeze those public identities
explicitly. Retained public receipt/export evidence cannot replace a deleted public
baseline. The same public/private lifecycle distinction applies to the .NET targets.

`UpdateSunderPlatformApi` / `platform update [contract]` uses locked origins and the
current coordinated range. Global `UpdateSunderPackageApi` / `api update` also updates
platform intents in the same whole-lock transaction. A last-member or staged source
failure preserves the committed lock and previous generated output. Exact repeated
implement is a no-op.

Local selections omit `registryOrigin` while retaining exact release/content and
descriptor/ABI hashes. Present null or malformed origins are invalid. A local update
requires an explicit catalog; a targeted update can explicitly bind a local selection
to a Registry. An existing Registry origin cannot be overridden. Publication evidence
is acquired separately and is never inferred from local parsing or selection.

`UnimplementSunderPlatformApi` / `platform unimplement <contract>` checks the proposed
source without the removed surface; remaining bindings or compiler diagnostics
prevent commit. Cached bytes remain. `ListSunderPlatformApis` / `platform list` is
offline and read-only.

For canonical Development localhost origins, use
`-p:SunderAllowDevelopmentRegistryOrigin=true` or `--development-registry` on each
selection, restore and build/pack invocation. This enables the local transport and
lock-origin form while retaining exact-origin and content validation. See
[Development Registry mode](CONTRACT-RESTORE-CACHE.md#development-localhost-registry).

## Exact Restore And Offline Builds

```powershell
dotnet msbuild Package.csproj -t:RestoreSunderContracts `
  -p:SunderPlatformCatalogPath=./sunder-platform-contracts.v1.json

sunder-package contracts restore --platform-catalog ./sunder-platform-contracts.v1.json --project .
```

The input must match an existing lock; it cannot select a release. Online restore
reads exact release metadata and canonical catalog bytes, checking the same-origin
URL, exact Content-Length/strong ETag, complete descriptor inventory and locked
content identity. Selection accepts only Supported
stable releases; exact restore can read retained Revoked evidence without granting
publication, installation or execution authority.

Original catalogs and their ownership receipts are cached at
`platform-catalogs/<catalog-sha256>.json` and `<catalog-sha256>.receipt.json` below
the shared descriptor cache. Every build revalidates their canonical content and exact
lock pins. A valid descriptor/receipt pair alone is insufficient. Package inventory
platform members are checked against the same selected catalog
even when they produce no binding or consumer grant. Offline package candidates with
platform constraints may supply the matching catalog in the same restore command
(`SunderPackageCandidatePath` plus `SunderPlatformCatalogPath`, or `--candidate` plus
`--platform-catalog`), or reuse complete verified cache evidence. Missing local evidence
does not fall back to a default Registry.

Descriptor pairs and catalogs are independently immutable. Interrupted publication
may leave a valid descriptor pair without its catalog; builds fail until explicit
restore completes the evidence. Corrupt regular catalog preimages are retained,
and replay preserves warm mtimes. Explicit shared cache GC marks direct and
metadata-only catalog references, respects grace and collects only ownership-proven
unreferenced pairs/repair artifacts. Both tools recover each other's tombstones and
restore newly referenced catalogs before deletion. GC ownership proof grants no
publication authority. See the
[cache protocol](CONTRACT-RESTORE-CACHE.md#explicit-garbage-collection).

## Stack Domain Contributors

The `--withStacks` template now uses a partial contributor marked with
`[SunderServiceProvider(typeof(IPackageStackContributor), ProviderId = "my.package.stack")]`
and calls its generated `CreateSunderRpcProviderRegistration()`. The SDK interface's
`SunderPlatformImportAttribute` in `Sunder.Sdk.Services` identifies a local semantic adapter and a platform
family; it cannot declare ownership or select a version.

Explicit platform selection supplies the complete six-method descriptor before first
Accept checks the owned greeting and imported contributor together. Generated package-local
code composes the domain adapter, validates complete DTO shape/method/error/idempotency
agreement, and applies the locked schemas to values. The bounded adapter profile
supports one unary service with closed records, strings/Booleans, bounded Int32 and
numeric enums, read-only lists/maps, optional nullable domain members and content
references. Unsupported representation or unselected/mismatched authority fails before
generated output publication. Ordinary generated provider-interface implementations
remain available for other descriptor profiles.

Content registration/opening and owner/contributor stamping stay in the shared SDK
semantic adapter. No consumer use or `sunder.core` dependency is created. Multiple
contributors require distinct provider IDs. The unconditional template build gate is
removed; normal missing-import, cache and compatibility diagnostics enforce its prerequisites.

The Core Host now consumes role-local generated client/codec/descriptor code from
checked Core source and its retained baseline; the shared SDK transport client has
been removed. Core's engineering tool exports a catalog for local authoring and
administrator publication to the Registry. Ordinary builds remain offline. Canonical first-party root conversion
and final deployment/release qualification are separate migration work.
