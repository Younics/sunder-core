# Exact Contract Restore And Shared Cache

The package tools restore the exact selections in `sunder-contracts.lock.json`.
Restore preserves the lock and generated source. Explicit add/update operations
own selection changes.

```powershell
dotnet msbuild Package.csproj -t:RestoreSunderContracts
sunder-package contracts restore --project ./package
```

Both implementations restore Registry package-authority selections and
exact local archive candidates. C# uses `SunderPackageCandidatePath`; Node uses
`--candidate`. Node candidate inspection uses the installed Sunder CLI backed by
`Sunder.Package.Format`; set `SUNDER_CLI` to its executable path when it is not on
PATH. Platform hydration accepts exact canonical catalog content without catalog
signing keys or trust bundles. Local selections omit `registryOrigin` and never
infer a default Registry. Explicit Development-Registry transport is described below.
Present null or malformed origins remain invalid; absence alone denotes local selection.
Loading an unpublished package with imports also requires explicit
[Runtime development import preparation](DEVELOPMENT-IMPORTS.md) after building its
canonical development folder. Descriptor cache integrity alone is not Host import authority.
Node V3 source generation consumes restored package-consumer
and provider imports offline. Provider-only intent materializes its selected descriptor
without generating consumer uses.
Online restore verifies the complete selected public inventory, exact descriptor
metadata, package authority, canonical descriptor and ABI hashes, byte length and
strong ETag. Metadata uses `application/json`; descriptors use
`application/schema+json`. Redirects and changed response origins are rejected.
Requests have bounded bodies and a 15-second request/body deadline. An edited
lock invalidates a pending restore before cache publication.

## Development Localhost Registry

HTTPS is the default. For a local Development Registry, opt in explicitly:

```powershell
dotnet msbuild Consumer.csproj -t:AddSunderPackageApi `
  -p:SunderPackageId=example.dependency -p:SunderPackageVersionRange=">=1.0.0 <2.0.0" `
  -p:SunderRegistryOrigin=http://localhost:5100/ -p:SunderAllowDevelopmentRegistryOrigin=true
dotnet msbuild Consumer.csproj -t:RestoreSunderContracts -p:SunderAllowDevelopmentRegistryOrigin=true
dotnet build Consumer.csproj -p:SunderAllowDevelopmentRegistryOrigin=true

sunder-package api add example.dependency --version ">=1.0.0 <2.0.0" --registry http://localhost:5100/ --development-registry
sunder-package contracts restore --development-registry
sunder-package build --development-registry
```

This permits only canonical `http://localhost[:nondefault-port]/`, with lowercase
scheme/host, no credentials, extra path, query or fragment, and ports in `1..65535`.
IP literals, alternate HTTP hosts, default/leading-zero ports and URL normalization
aliases remain invalid. Responses still require the exact requested origin and
bounded metadata/body evidence; redirects are rejected.

The same explicit mode is required when reading Development locks for API/platform
commands, generation, Check/Accept/Freeze, development builds, smoke and packing.
Debug configuration and the `dev` command do not enable it implicitly. The flag is
not recorded in the lock; the exact selected origin is. Update/restore continue to
use locked origins and reject overrides. Candidates can hydrate a Development lock
offline with the same opt-in. A local selection with no Registry origin requires no
transport opt-in. GC needs no mode flag: its reviewed-lock marking is offline and
already protects Development references.

## Offline Candidates

Local API selection does not require prior publication or a Registry URL:

```powershell
dotnet msbuild Consumer.csproj -t:AddSunderPackageApi -p:SunderPackageId=example.dependency -p:SunderPackageVersionRange=">=1.0.0 <2.0.0" -p:SunderPackageCandidatePath=./dependency.sunderpkg
sunder-package api add example.dependency --version ">=1.0.0 <2.0.0" --candidate ./dependency.sunderpkg --project ./consumer
```

After selection, restore exact inputs into a cold cache:

```powershell
sunder-package contracts restore --project ./consumer --candidate ./dependency.sunderpkg
dotnet msbuild Consumer.csproj -t:RestoreSunderContracts -p:SunderPackageCandidatePath=./dependency.sunderpkg
```

The candidate must reproduce an existing lock's package/version/content identity,
complete public inventory, authority provenance and exact RPC tuples. Candidate
paths and inspection-output paths never enter the lock. The operation performs no
Registry request and does not select a newer version.
Supply `SunderPlatformCatalogPath` / `--platform-catalog` alongside the candidate
when its inventory needs a Core catalog. Both inputs can be restored together.
Any remaining materialized imports require complete verified cache evidence before
new descriptor/catalog bytes are published. Partial offline inputs never trigger
network acquisition of the missing local evidence.
Local updates require another explicit candidate/catalog; they never resolve through
a default Registry. A targeted update may explicitly bind a local selection to a
Registry. An already pinned origin cannot be overridden.

Node invokes `sunder dev package validate --api-output` in its own temporary
directory. This operation needs no Runtime or Registry connection. Format snapshots
the bounded archive, runs complete canonical archive validation, then atomically
exports public inventory metadata and canonical public descriptors. Private
descriptor bodies are excluded. C# uses the same Format-owned inventory projection.

The transient inspection receipt binds the archive SHA-256, raw manifest and
content-index hashes, and complete public inventory hash. Node verifies those
identities, every exported descriptor/ABI tuple, the exact output file set and the
unchanged candidate before cache publication. Existing output directories, unsafe
paths, invalid archives and mismatched locked content fail without rewriting the
lock. Node's inspector process has a five-minute deadline and bounded diagnostics.
Local inspection establishes build provenance; Registry publication must still
confirm ownership before dependent publication.

## Cache Ownership And Reads

The default root is `~/.sunder/contracts/v1/sha256`. Override it with
`SunderContractCacheRoot` in C# or `SUNDER_CONTRACT_CACHE` for Node source compilation.
Node selection/restore/GC also accepts `--cache-root`. The root contains
the `.sunder-contract-cache` marker, exact canonical `<descriptor-sha>.json` files,
and deterministic `<descriptor-sha>.receipt.json` files. A nonempty unmarked root
is not adopted. Links/reparse points and irregular file paths are rejected.

C# and Node use the same cross-process lease for publication and reads. A reader
copies and verifies complete descriptor/receipt pairs under that lease, then
compiles from detached bytes. It never performs recovery, downloads or repair.
An incomplete publication produces an actionable restore diagnostic.

Cache bytes establish integrity only. Registry publication, compatible installation,
development preparation and Runtime grants have their own admission checks.

### Platform Catalog Evidence

Both tools retain original catalogs under `platform-catalogs/<catalog-sha>.json`
with a deterministic `<catalog-sha>.receipt.json` containing `schemaVersion: 1`,
`catalogSha256` and `catalogBytes`. The pair proves cache ownership/integrity only.
Tools revalidate canonical content and exact lock pins before offline generation.
`SunderPlatformCatalogPath` / `--platform-catalog` hydrates exact existing constraints
without network fallback or selection changes. Platform descriptors come from the
catalog inventory, not a descriptor-only substitute. Metadata-only platform members
still require the original catalog but create no consumer use or descriptor body.
See [platform imports](PLATFORM-CONTRACT-IMPORTS.md) for commands and the
current production trust boundary.

Envelope publication uses `.stage-<token>` directories with complete pairs and a
closed intent binding the target hash and prior body/receipt hashes. Repair retains
corrupt preimages in `.repair-<token>` with that intent; explicit restore can replay
an interruption. A `.completed-<token>` rename precedes verified stage cleanup.
Readers require the complete installed pair. Legacy bodies lacking receipts need
explicit restore; ordinary builds never silently adopt or repair them.

Explicit GC collects ownership-proven old unreferenced pairs and publication/repair
artifacts. Its canonical-format and content-address proof does not grant signature
authority or require a currently accepted key: denied/retired signing material can
still be collected. Unproven legacy corruption files and incomplete artifacts remain.

## Recoverable Publication

An explicit restore validates all requested bytes before beginning publication.
At most 128 entries participate in one transaction. A private stage contains both
files for each entry and a closed, canonical intent containing ordinal descriptor
hashes, prior file hashes and a unique transaction token. The intent is at most
64 KiB; descriptors are at most 4 MiB and receipts at most 16 KiB.

The complete stage is renamed to `.pending` while the cache lease is held. Readers
reject a remaining pending transaction. Recovery validates the complete staged
file set and descriptor/receipt identities before resuming installation. A file
that changed unexpectedly after planning is preserved and causes failure.

After obtaining verified replacement bytes, restore moves corrupt regular files
to `.quarantine/<transaction-token>/`, alongside the transaction intent. It then
installs complete replacements. Quarantine preserves the original evidence;
ordinary builds never repair corruption. Invalid ownership or unsafe paths cannot
be repaired by following them.

Once all entries are installed, renaming `.pending` to a completion tombstone
commits visibility. Cleanup after that rename cannot make the installed cache
look partially published. C# and Node can resume each other's interrupted pending
transactions. Repeating a successful restore preserves existing cache-file mtimes.

## Explicit Garbage Collection

GC is an explicit, offline maintenance operation. Supply the reviewed lock files
for every workspace whose entries should be retained:

```powershell
dotnet msbuild Package.csproj -t:CollectSunderContractCache `
  -p:SunderContractCacheLocks=/workspace/package/sunder-contracts.lock.json

sunder-package contracts gc `
  --lock /workspace/one/sunder-contracts.lock.json `
  --lock /workspace/two/sunder-contracts.lock.json
```

`SunderContractCacheLocks` is a semicolon-separated MSBuild property; multiple
paths can be declared in project XML. Node accepts repeated `--lock` inputs.
Duplicate paths are coalesced. At least one lock must be supplied; no implicit
workspace search supplies deletion authority. Locks must be regular files outside
the cache. Missing, malformed, linked or concurrently edited locks stop collection.

The default grace period is **seven days**. Override it with
`SunderContractCacheGraceDays` or `--grace-days`, using a whole number from 1 through
365. `SunderContractCacheRoot` and `--cache-root` select the cache. Registry,
candidate and version-selection inputs do not apply to GC.

GC holds reviewed source-lock leases before the cache lease. It retains every
descriptor hash named in their complete artifact inventories and direct platform
constraints, including bodies not currently needed by a provider-only import.
It also marks original catalog hashes from direct platform imports and metadata-only
platform members in package inventories. Catalog entries count in the reported
`contract entries` total; publication/repair directories count in the artifact total.
Unreferenced complete valid pairs are eligible only when both files exceed the
grace period. The same grace applies to ownership-proven retained quarantine,
publication-completion and staging artifacts. Unknown or incomplete ownership
evidence is retained, not recursively swept.

Each batch admits at most 128 collection units, 1,024 files and 2 GiB of preimages
in a 256 KiB canonical intent. Catalog files/repair preimages are bounded to 64 MiB;
descriptor files retain their 4 MiB bound. Reviewed input is bounded to 128 locks
and 64 MiB total. Cache root and catalog-directory enumeration are each bounded to
16,384 entries, and descriptor quarantine enumeration to 4,096
directories. Large eligible sets report `More candidates`; rerun the same explicit
operation to continue. A recovered pending batch also requests a fresh follow-up
pass instead of claiming the remaining cache was scanned.

Before deletion, eligible files move into a `.gc-pending` tombstone set with exact
preimage hashes. Readers and restore reject a remaining pending GC with recovery
guidance. Rerun GC with the original reviewed lock set; additional locks are
allowed. Recovery reparses current locks and restores entries that became
referenced since interruption. Source-lock edits at the commit fence prevent
deletion. C# and Node can recover one another's GC transactions.

Renaming the completed tombstone set to `.gc-trash-<token>` commits removal from
the live cache. Exact validated trash files can then be deleted and cleanup can
resume after interruption. Pruned entries require explicit restore if another
workspace needs them later. Ordinary Build, Pack, restore, remove and unimplement
do not invoke GC or silently evict unrelated entries.
