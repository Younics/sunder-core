# Core Platform Contract Authoring

This directory owns the Core-only platform baseline. Ordinary implemented service
and value-model source in `Sunder.Sdk.Stacks` defines the contract; the baseline
retains reviewed descriptor/ABI bytes and immutable compatibility history. Package
authoring commands cannot read this baseline as package authority.

The non-public `languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build` executable
owns these commands and Stack transport generation. Its historical project name is
part of the build graph; execution runtime acquisition belongs to Host vendor sources.

The current Stack source derives all 48 definitions and six methods of the reviewed
`sunder.stack.contributor` `2.0.0` tuple without reading the descriptor JSON. The
current owning baseline supplies descriptor/ABI expectations. SDK source builds generate an SDK-only handler and the
adapter's required partial `CreateSunderRpcServiceHandler` factory; missing generation
fails compilation. The thin Stack wrapper retains only routing-error semantics, not
handwritten public-DTO JSON conversion. The embedded descriptor/props distribution
and retained provider/DTO helpers remain lower-level SDK surfaces; generated transport
does not retire those assets or authorize platform imports.

## Explicit Commands

Run from the Core repository root after building the existing engineering tool:

```sh
dotnet build languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build/Sunder.ExecutionRuntime.Build.csproj
tool=languages/csharp/dotnet/tools/Sunder.ExecutionRuntime.Build/bin/Debug/net10.0/Sunder.ExecutionRuntime.Build.dll

dotnet "$tool" platform-contracts check --core-root .
dotnet "$tool" platform-contracts accept --core-root .
dotnet "$tool" platform-contracts freeze --core-root . --catalog-release 1.1.0
```

Only when the baseline is absent, bootstrap requires the exact reviewed Stack key
and version. Current source is reviewed through the owning lifecycle; the protected
migration descriptor/ABI archive is inert and is not a bootstrap or build input.
Existing frozen/published versions retain their ordinary immutability rules:

```sh
dotnet "$tool" platform-contracts accept --core-root . --key stack-contributor-rpc-service-adapter --next-version 2.0.0
```

Breaking acceptance additionally requires `--key`, `--current-version`,
`--next-version`, and `--acknowledge-breaking-change true`. Check validates the full
source set and accepts no selection or mutation options. Freeze validates the full
source set even when `--key` selects one draft. Existing frozen/published snapshots
are reused without rewriting their original release/publication evidence.

`CheckSunderPlatformContracts` is an explicit MSBuild target on the engineering tool
project. Ordinary Build/Rebuild neither accepts/freezes the baseline nor signs a
catalog. These operations never contact Registry or manufacture `published` state.

## Export Catalogs

`platform-contracts export` requires `--core-root`, `--catalog-release`, and
`--output`. It validates all current source contracts against frozen/published
baseline snapshots and writes the complete canonical Registry catalog. The output
parent must already exist, the destination must not exist, and output inside Core
language source trees is rejected.

```sh
dotnet "$tool" platform-contracts export --core-root . --catalog-release 1.1.0 --output /releases/sunder-platform-contracts.v1.json
```

The user-selected Registry establishes publication authority. Its administrator
imports the exported catalog; no signing key or trust-policy bundle is required.
Local catalog bytes do not themselves establish publication state or permission.

## Confirm A Published Catalog

After administrator import of the exact catalog into the selected Registry, run:

```sh
dotnet "$tool" platform-contracts import-publication --core-root . --catalog /releases/sunder-platform-contracts.v1.json --registry https://hub.sunderapp.io/
```

This explicit online operation validates current source and immutable frozen
snapshots, validates the retained canonical catalog, and compares exact Registry
metadata and original catalog bytes. Matching frozen current/history snapshots
become published with Registry-origin/catalog-content evidence. Descriptor/ABI
bytes and release identities remain unchanged; repeats
preserve mtimes. Source, baseline and retained catalog inputs are fenced before
atomic commit. Conflicting evidence or a changed input leaves the baseline intact.
The command records the selected Registry's existing publication; it does not upload
a catalog or create authority from local input.

## Input And Transaction Rules

### Alternate .NET Artifact Output

The Stack transport bootstrap supports normal .NET CLI `--artifacts-path` builds
and packs with an artifact root outside Core's language source trees. From the
Core repository root:

```sh
project=languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Sunder.Sdk.Stacks.csproj
artifacts=/absolute/path/to/core-artifacts
dotnet build "$project" -c Release --artifacts-path "$artifacts" -p:GeneratePackageOnBuild=false
dotnet pack "$project" -c Release --no-build --artifacts-path "$artifacts" -o "$artifacts/feed"
```

Direct `dotnet pack` also builds correctly with the same artifact settings and
`-p:GeneratePackageOnBuild=false`. Its requested package output directory is
excluded from the private bootstrap's global properties, so bootstrap dependency
packs stay in their own artifact output instead of overwriting the caller's feed.

The private request records the evaluated intermediate directory, exact generated
global-usings/assembly-info/framework-attribute Compile inputs, and SDK project
reference provenance. Relocation permits only those generated files in the Stack
project's artifact layout. The SDK reference must belong to the current Core SDK
project's output layout and match the producer's SDK assembly identity and public
metadata shape; file names or project-reference labels alone are insufficient.
These additional inputs retain the same byte rechecks as ordinary source.
Invocation-local requests and immutable transport publication retain their
project-local ownership directories described below. Local packs and catalog
exports are engineering evidence, not Registry publication records.

### Selected Registry And Immutable Content

Selection explicitly records the canonical Registry origin. Restore and updates
use that locked origin; source projects cannot replace it implicitly. Both .NET
and Node validate exact canonical catalog bytes, complete descriptor inventories
and the selected content identity. Ordinary builds use that evidence offline.
The catalog has no signing/trust metadata or tool-owned key asset.

### Source Transactions

SDK transport generation runs the existing engineering tool as a short-lived,
build-only process, not a mutable tool assembly loaded into an MSBuild node. It uses
the actual evaluated `@(Compile)` set, including `AutoGen` inputs except its own
transport output, `@(ReferencePathWithRefAssemblies)`, defines, nullable settings,
configuration, and target framework (`net10.0`). Source and neutral generated output
must reach a two-pass fixed point, followed by complete baseline validation.

Explicit lifecycle commands retain a fixed RID-neutral `net10.0` Release context
and curated runtime/SDK references. Their bounded recursive ordinary C# layout
excludes project-root `bin`/`obj` and dot-directories, but includes `Generated` and
nested `bin`/`obj`. Build requests must include that complete ordinary membership as
well as evaluated generated inputs. Neither path loads compiled Stack or Runtime
Host assemblies or extracts the descriptor oracle.

The private request writer uses `CreateNew` only for invocation-local requests
directly under the Stack project's `obj`; it does not use arbitrary
`WriteLinesToFile` overwrite paths. Consumption and cleanup require the writer's
expected SHA-256 and file stamp. Replaced or modified requests are preserved.

Transport output is immutable under
`obj/sunder-core-transport-<configuration>-net10.0/<content-sha256>/`, with exactly
`CoreStackTransport.g.cs` and its ownership marker. A lease protects publication;
existing output is reused only after exact byte/member checks. Directory publication
uses Linux `renameat2(RENAME_NOREPLACE)`, macOS `renamex_np(RENAME_EXCL)`, or Windows
`Directory.Move`. Unsupported native operations fail closed without an overwrite
fallback. A destination that appears during publication is never replaced or deleted.

Input file count, directory count/depth, aggregate source bytes, and individual
reference bytes are bounded. Source membership and all request/source/reference/baseline
bytes are rechecked under the output lease and before publication. Baseline/payload
writes use same-directory `CreateNew` staging, durable flush, final exact-byte
validation, and atomic rename. Cancellation interrupts lock waits and is checked
before rename.
No-op acceptance/freezing preserves timestamps; failed or stale plans preserve the
existing baseline/output. Only an invocation's still byte-identical regular stage
is eligible for cleanup; unrelated or replaced files are retained.

## Stack Transport Source Generation

SDK builds use `core-stack-transport generate`; Host builds use `generate-host`.
Both consume `--core-root`, `--request`, `--request-sha256` and `--request-stamp`.
They revalidate evaluated source/references and publish immutable configuration/SHA-
keyed `obj` content without introducing a Worker/Build-tool runtime dependency.

Use `platform-contracts check --core-root <root>` to validate actual source against
the current reviewed baseline. Historical Stack migration records remain byte-for-byte
evidence, excluded from active baseline regeneration. Current source parity and public
package artifact tests own descriptor/ABI and distribution checks.
