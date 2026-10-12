# Qualified generated-role inputs

The canonical single-project path supports prebuilt `PackSunderPackage` from exact
native Worker/App inputs. Fan-in performs no Build, Compile, Restore, source-generator
execution, package execution or Host launch. It uses the same canonical leaf inspector,
aggregator, deterministic archive writer and transactional publisher as ordinary Pack.

## Native producer

Use the reviewed source checkout, frozen public baseline when present, exact import lock and an
explicitly restored tool/dependency set. Local imports need no Registry. Freeze before
the native Build for public contracts: changing the baseline afterward invalidates its qualification.
Private-only source needs no baseline or Freeze; Core binds explicit baseline absence
and exact package-version private tuples. See [ordinary services](ORDINARY-SERVICES.md).

```sh
dotnet msbuild Package.csproj -t:FreezeSunderContracts -p:Configuration=Release
dotnet build Package.csproj --no-restore -c Release -r linux-x64
```

Ordinary Build automatically exports this bundle:

```text
bin/Release/net10.0/sunder-targets/linux-x64/
  sunder-dev/                   complete canonical Worker + optional App RID leaf
  role-inputs.v1.json            compact closed qualification evidence
  role-inputs.v1.json.sha256     lowercase evidence digest plus LF
```

`GetSunderPackageRoleInputs` depends on ordinary Build and returns its RID leaf item
with `RoleInputsPath` and `RoleInputsSha256` metadata. Native automation may use:

```sh
dotnet msbuild Package.csproj -t:GetSunderPackageRoleInputs \
  -p:Configuration=Release -p:RuntimeIdentifier=linux-x64 -getItem:_SunderBuiltRid
```

The native job retains the **whole bundle**, including every indexed file under
`sunder-dev`. Apply signing through the existing pre-index Worker signing hooks before
qualification is exported. Subsequent editing/reindexing of a leaf invalidates its
evidence. Run native tests against these retained bytes and bind the evidence digest
into the owning CI/release provenance. The digest sidecar is convenient producer
output; an unauthenticated sidecar alone is not release approval or native attestation.

## Portable reviewed inputs

`SunderPackageRoleInputSourceRoot` identifies the reviewed source tree. Its default
is the nearest Git root, or the common project/source/resource ancestor outside Git. Set it explicitly for
detached fixtures or linked sources outside the package directory. The project,
owned baseline, import lock and authored Compile/resource paths must remain beneath
this root. Relocated checkouts retain the same relative layout and exact file bytes.

Evidence binds:

- Source/configuration file bytes and membership beneath that root; evaluated Compile,
  resource, reference, package/project dependency and global-using items.
- Exact owned baseline and import lock hashes, package/project/assembly identity,
  configuration, TFM, RID, compiler/framework-generator properties and SDK version.
- The exact restored framework/RID dependency graph, including transitive library
  versions, package content hashes and asset selection, with host-local restore paths removed.
- The actual complete managed Sunder task toolset, generator inputs, external imports,
  explicit assembly references and source-reference project trees.
- Native role-plan identity, generated-source/resource/restore-input hashes, library
  selection, compile/runtime reference closures and complete Worker/App RPC receipts.
- Every canonical leaf file, including manifest and content index, by path, size and
  SHA-256. Both the independently validated index and qualification must match.

Source membership includes `.cs`, project/props/targets, JSON/configuration,
XAML/Avalonia, resx/ruleset/editorconfig files. Explicit resources bind their bytes
regardless of extension. The complete `SunderPackageAssetsDirectory` tree (default
`Assets`) also binds every file and its membership, including binary images.
Generated/cache directories `bin`, `obj`, `artifacts`,
`TestResults`, `node_modules`, `.sunder`, `.git`, `.vs`, `.idea`, `.opencode` and output
lease bookkeeping are excluded. Keep artifact handoffs outside reviewed source inputs.
Source references outside the root have separately bound source-directory trees;
external source links should use a reviewed common source root.

No build-machine path is an artifact locator or release authority. Portable records
use source-relative paths, generated-relative paths and content-bound external names.
The native role-plan digest is opaque retained identity: import never dereferences
the native machine's plan, generated tree, restore cache or compiler output paths.

## Fan-in selection and command

Create one compact UTF-8 selection file at the retained artifacts root. Property order
is significant; no BOM, whitespace or terminal LF is emitted by the canonical writer.
Each RID and evidence path occurs exactly once. Paths are portable relative paths
beneath the selection file's directory and end in `role-inputs.v1.json`.

```json
{"schemaVersion":1,"inputs":[{"runtimeIdentifier":"linux-x64","path":"linux-x64/role-inputs.v1.json","sha256":"<reviewed 64-character lowercase SHA-256>"},{"runtimeIdentifier":"osx-arm64","path":"osx-arm64/role-inputs.v1.json","sha256":"<reviewed 64-character lowercase SHA-256>"}]}
```

```sh
dotnet msbuild Package.csproj -t:PackSunderPackage \
  -p:Configuration=Release \
  -p:SunderPackageUsePrebuiltLeaves=true \
  -p:BuildProjectReferences=false \
  -p:SunderPackageComposeRuntimeIdentifiers="linux-x64%3Bosx-arm64" \
  -p:SunderPackagePrebuiltRoleInputsFile=/artifacts/native-inputs/selection.json
```

Select one RID with `RuntimeIdentifier`, a subset with
`SunderPackageComposeRuntimeIdentifiers`, or omit both to require every declared RID.
There is no current-machine role fallback. Missing or extra selections fail. The
current project must already have its dependency/tool inputs available from an explicit
restore; the fan-in command never restores them. It reevaluates current authored inputs
for every requested RID without resolving/building project-reference outputs.

Prebuilt mode requires an installed `Sunder.Package.Build` package or an explicitly
supplied complete immutable task set. Source-workspace callers can supply
`SunderPackageBuildTaskAssembly` and `SunderPackageBuildTaskSourceIsImmutable=true` after
building and retaining that exact toolset separately. Prebuilt Pack never bootstraps
mutable tooling. Ordinary Build/Compile remain rejected when prebuilt mode is selected.

Prebuilt Pack publishes only the final archive at `SunderPackageOutputPath` (the normal
package archive path by default). Composition uses a private temporary canonical tree;
the existing development aggregate and all native inputs remain unchanged. Input
leases cover the selection, evidence, leaves, baseline and lock through publication.
Source membership, input/tool bytes and artifact bytes are checked again after waiting
and at the final archive-publication fence. Cancellation, tampering, mixed/stale inputs
or malformed metadata preserve the prior archive.

## Closed format and scope

Schemas: [evidence V1](../../specs/package/v1/prebuilt-role-inputs/evidence.schema.json)
and [selection V1](../../specs/package/v1/prebuilt-role-inputs/selection.schema.json).
Semantic checks additionally enforce exact role/RID sets, ordinal/case-safe paths,
complete canonical package validity, matching role-scoped import projections, frozen
public tuples and exact current package-version private tuples.

Evidence is bounded to 64 MiB, depth 16 and the shared 262,144-value/property JSON
budget; selections to 32 KiB and six entries. Trees have at most 16,384 files, 65,536
discovered entries and depth 32. Reviewed source bytes are bounded to 512 MiB and one
native canonical leaf to 2 GiB. Symbolic links/reparse points are rejected. Unknown,
duplicate/case-colliding, null/incomplete, reordered or noncanonical documents fail.

Portable fixture passes qualify the handoff/validation boundary, not six-RID native
execution, signer approval, Registry publication or the complete release program.
