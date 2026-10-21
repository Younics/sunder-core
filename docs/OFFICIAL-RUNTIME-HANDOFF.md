# Official Runtime Management

Host owns vendor resolution/download/materialization, installed bindings, private
roots, receipts, health, process leases, retention and safe updates. Registry supplies
package requirements and package/platform policy. App/CLI use the shared Runtime API.
The stable filename is retained for existing documentation and CI references.

User workflow: [Package Runtimes](PACKAGE-RUNTIMES.md). Commands: [CLI](SUNDER-CLI.md).
Authoring/runtime requirements are part of the [package standard](SUNDER-PACKAGE-STANDARD.md).

## Transport And Request Rules

- Runtime protocol is `dev.sunder.runtime` revision 1 under `/api/v1`.
  `official-runtime-downloads.v1` is required at the base handshake.
- `package-install-resumption.v1` additionally protects retained uploads and adoption
  prerequisites. Package upload lifetime/retention remains independently authenticated.
- Runtime/planning/policy records reject unknown JSON members. Required nullable
  evidence remains present; optional selectors/defaults are explicit in the DTOs.
- Source/checksum/archive/update/support enums are string-only. Host validates complete
  semantics, lengths, ranges and timestamps; clients cannot supply acquisition URLs,
  trust flags, distribution profiles or replacement bindings.
- Catalog query accepts only `sourceId`, `skip`, `take`. The source filters Host-owned
  configurations, never arbitrary mirrors. Shared contracts depend only on the BCL.

## Identity And Evidence

| Source | Runtime / module ABI | Eligible release | Vendor checksum |
| --- | --- | --- | --- |
| `dotnet` | `sunder.runtime.dotnet` / `sunder.dotnet-worker-module.v1` | Supported stable .NET Runtime 10 satisfying package range | SHA-512, 128 lowercase hex digits |
| `node` | `sunder.runtime.node` / `sunder.node-worker-module.v1` | Supported Node 24 LTS satisfying package range and `>=24.18.1 <25.0.0` | SHA-256, 64 lowercase hex digits |

Exact record definitions live in
[`ExecutionRuntimeContracts.cs`](../languages/csharp/dotnet/host/Sunder.Runtime.Contracts/ExecutionRuntimeContracts.cs)
and [`ExecutionRuntimeManagementContracts.cs`](../languages/csharp/dotnet/host/Sunder.Runtime.Contracts/ExecutionRuntimeManagementContracts.cs).

`VendorVersion` is upstream SemVer including patch. The identity contains configured
source, runtime ID, vendor version, RID, ABI and vendor checksum. Same-version digest
drift is `execution-runtime.artifact-changed`, not an overwrite or packaging revision.
Automatic changes are strictly forward within the eligible line.

Distribution evidence records original metadata/checksum-document hashes, validated
download URL and fixed materialization profile. A separate local `ArchiveSha256`
and actual `ArchiveSize` identify downloaded bytes. `ContentIdentity` commits the
extracted path/hash/size/mode inventory. Provenance is created only after verification;
exact cache reuse and repair preserve original acquisition evidence.

`Summary.Size` means installed bytes. Preview `DownloadSize=null` means unknown;
zero means verified cache reuse. Package `ManifestSha256` remains the package manifest
hash. Vendor support (`Unknown`, `Supported`, `EndOfLife`) and local health are distinct.
Existing verified runtime launch needs no Registry execution-policy lease.

## Official Sources And Bounds

- .NET metadata: `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`.
  Select Runtime-only archives, excluding SDK, ASP.NET, apphost packs and system installers.
- Node metadata: `https://nodejs.org/dist/index.json`, followed by the selected release's
  `SHASUMS256.txt` and the exact archive's unique checksum entry.
- `OfficialRuntimeSourcePolicy`, `OfficialRuntimeResolver` and `OfficialRuntimeHttpClient`
  enforce source selection. Runtime HTTP carries no Registry credentials/cookies, proxy
  authentication or automatic content decoding.
- Validate every redirect before requesting it; allow at most three. HTTPS/default
  port, exact version/RID archive paths, no user info/query/fragment/escaped ambiguity.
  .NET uses the configured builds/azureedge/blob hosts and bounded Visual Studio download
  paths; Node uses exact `nodejs.org` release paths. Reject unexpected partial responses.
- Limits: metadata 8 MiB, checksum documents 256 KiB, archives 512 MiB, expanded
  content/TAR stream 1.5 GiB, individual file 256 MiB, 50,000 entries, 512-character
  paths and 32 path components. Limits apply to streamed bytes too.
- Deadlines: metadata 30 s, download/materialization 3 min, native smoke 20 s with
  bounded output. Transient downloads have at most three cancellation-aware attempts.

## Materialization And Native Readiness

Verify original bytes before extracting into fresh private staging. Windows uses ZIP;
macOS/glibc Linux use TAR.GZ. Reject traversal, duplicate/case/ancestor collisions,
special files and size overruns. Never follow archive links. The Node profile retains
the executable/license and skips only recognized npm/npx/corepack auxiliary links.
.NET retains its Runtime-only layout. Only the managed launcher receives executable mode.

`OfficialRuntimePlatformProbe` checks RID/architecture, native header and prerequisites:
Linux glibc 2.27+ for .NET, glibc 2.28+/kernel 4.18+ for Node; macOS 15+ for the current
.NET 10 profile and 13.5+ for Node; Windows 10/Server 2016+. Native version/platform/ICU
smoke runs in a cleared environment with the controlled Worker baseline. Reject
inherited .NET/Node/OpenSSL/loader injection and Host credentials. Missing prerequisites
produce diagnostics, never implicit system installation or a musl/RID substitution.

## Store, Launch And Repair

The existing store owns singleflight acquisition, identity locks, cancellation,
atomic journals/recovery, sealed roots, health, quarantine, process-birth/containment
leases, retention, tombstones and GC. Original archives use `.archive` cache files.
There is no Sunder runtime repack/signature, runtime Registry policy store or old receipt reader.

Healthy launch is entirely local: exact committed binding, receipt, managed root and
absolute launcher must agree. Warm checks avoid rehashing the full root per dependent
activation. Deep verification is explicit. Exact repair uses verified cached original
bytes offline, or fresh vendor metadata for the same identity/digest; never substitute
latest. Drain dependent work/process leases before quarantining or replacing roots.

Ensure holds the operation gate and temporary retention through final metadata
confirmation, durable manual retention and returned health. Cache-only Ensure with
`RetainUntilUninstall=false` can be collected once temporary retention ends. GC respects
installed/development bindings, pending transactions, leases and manual intent.

## Runtime.Client Methods And Routes

Methods below are on `RuntimeManagementClient`, return `Task<T>` and accept a trailing
cancellation token. `E` is the complete identity route:
`/execution-runtimes/sources/{sourceId}/{runtimeId}/versions/{vendorVersion}/{rid}/abis/{moduleAbi}/checksums/{algorithm}/{digest}`.

| HTTP route under `/api/v1` | Client method |
| --- | --- |
| GET `/execution-runtimes` | `GetExecutionRuntimesAsync` |
| GET `E` | `GetExecutionRuntimeAsync` |
| GET `E/references` | `GetExecutionRuntimeReferencesAsync` |
| GET `E/impact` | `GetExecutionRuntimeImpactAsync` |
| GET `/packages/{packageId}/execution-runtime-bindings` | `GetPackageExecutionRuntimeBindingsAsync` |
| POST `/execution-runtimes/ensure` | `EnsureExecutionRuntimeAsync` |
| POST `E/verify` | `VerifyExecutionRuntimeAsync` |
| POST `E/repair` | `RepairExecutionRuntimeAsync` |
| POST `/execution-runtimes/gc` | `CollectExecutionRuntimeGarbageAsync` |
| GET/PUT `/execution-runtimes/settings` | `GetExecutionRuntimeSettingsAsync` / `UpdateExecutionRuntimeSettingsAsync` |
| GET `/execution-runtimes/updates/status` | `GetExecutionRuntimeUpdateStatusAsync` |
| GET `/execution-runtimes/catalog` | `GetExecutionRuntimeCatalogAsync` |
| POST `/execution-runtimes/install/preview` | `PreviewExecutionRuntimeInstallAsync` |
| POST `/execution-runtimes/install/apply` | `ApplyExecutionRuntimeInstallAsync` |
| POST `/packages/runtime-prerequisites` | `PreviewPackageRuntimePrerequisitesAsync` |
| POST `/execution-runtimes/updates/preview` | `PreviewExecutionRuntimeUpdateAsync` |
| POST `/execution-runtimes/updates/prepare` | `PrepareExecutionRuntimeUpdateAsync` |
| POST `/execution-runtimes/updates/apply` | `ApplyExecutionRuntimeUpdateAsync` |
| POST `/execution-runtimes/uninstall/preview` | `PreviewExecutionRuntimeUninstallAsync` |
| POST `/execution-runtimes/uninstall/apply` | `ApplyExecutionRuntimeUninstallAsync` |

Exact request/result types and overloads are defined in the linked contracts and
[`RuntimeManagementClient.ExecutionRuntimes.cs`](../languages/csharp/dotnet/host/Sunder.Runtime.Client/RuntimeManagementClient.ExecutionRuntimes.cs).
Repair requires fresh impact; install/apply/uninstall require plan confirmation.
No runtime upload/import endpoint or Registry-origin acquisition selector exists.

## Consent, Sequencing And Updates

- Inspect original package inputs. `SourceFingerprint` binds package provenance,
  content, manifest and targets; Registry requirements must match downloaded manifests.
- `ResolutionFingerprint` binds normalized requirement, exact vendor identity and
  source/profile/platform semantics; update also binds the previous identity. Observation
  timestamps and unrelated feed entries do not invalidate unchanged consent.
- Impact/consent also binds operation, settings/retention intent, installed packages
  and complete affected usage. Reconfirm before acquisition/commit; material drift
  requires a new review. Clients cannot edit a plan.
- One combined approval precedes queue enrollment. Existing FIFO/sequential jobs run
  required runtime work first, then one atomic package batch; failure/cancellation
  stops that installation's remaining work. Re-preview the original source before mutation.
- Update-one/update-all/adoption retain package items, capability consents, per-origin
  expectations and aggregate prerequisites, including stale/failure and pinned/no-op
  outcomes. Adoption is a reviewed reinstall, not runtime-origin adoption.
- Retained uploads renew across queue wait/download/staging. Successful acquisition
  stays manually retained even if the later package step is cancelled.
- Auto-updates default ON with revision CAS. Prepare side by side; apply locally when
  stopped, or require explicit fresh full restart-impact consent. Package versions,
  content and provenance remain pinned.
- Installed package state is binding authority. Journal/policy fences distinguish
  precommit failure from committed desired state. Transfer manual retention only at
  committed update; honor `StoreCommitted`, `RuntimeSessionApplied`, `CommittedStamp`.
- Runtime/session events invalidate settings status; reconnects refresh local state.
  The App page does not use periodic vendor polling.

## Registry And Package Policy

Registry targets carry only `ExecutionRuntime` requirements, not selected vendor
artifacts or runtime approval. Registry actions are package install/update/remove/keep;
runtime-only rebind is Host-local. See
[`PackageRuntimeRequirements.cs`](../languages/csharp/dotnet/libraries/Sunder.Registry.Contracts/PackageRuntimeRequirements.cs),
[`Packages.cs`](../languages/csharp/dotnet/libraries/Sunder.Registry.Contracts/Packages.cs)
and [`Policy.cs`](../languages/csharp/dotnet/libraries/Sunder.Registry.Contracts/Policy.cs).

Package/platform permissions, revocation, nonce validation, cached policy consistency and continuous-clock
leases remain enforced independently of binary updates. Local packages do not bypass
policy merely because they lack a Registry origin. No runtime subject participates in
Registry policy synchronization or readiness.

Wire collections are frozen/bounded: usage/impact/bindings/prerequisites 1,024 items,
catalog 128, prepared updates 32, prerequisite sources 32. Registry plans use the
1,024-item plan limit; synchronization uses 128 package and 128 platform subjects.
Required fields remain required even where C# failure-result defaults exist; overflow
is rejected, never truncated. The [policy contract](POLICY-CONTRACTS-V1.md) owns those rules.

## Qualification Scope

Owning tests cover source/transport/extraction bounds, consent, retention/GC, update
commit/reconciliation, offline reuse and independent package policy. Six actual queued
App→Host update-one/update-all/adoption cases have controlled-source integration
coverage. Real vendor/Worker qualification was recorded on osx-arm64; the full native
matrix, clean-source and production acceptance remain the separate release gates in
[Core releases](SUNDER-CORE-RELEASES.md).
