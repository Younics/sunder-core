# Official runtime management: Host implementation handoff

The Host, local state, thin Runtime.Client and CLI now use official .NET Runtime 10
and Node 24 LTS distributions. The exact shared records, wire requirements and routes
are in [OFFICIAL-RUNTIME-HANDOFF.md](OFFICIAL-RUNTIME-HANDOFF.md).

This is a closed, new-only V1 replacement. Curated runtime publication, runtime
Registry resolution, runtime signing/trust configuration, `.sunderruntime` import,
runtime lifecycle-policy caches and runtime execution-policy leases are retired.
There are no old receipt/configuration readers or acquisition fallbacks. Existing
user state was not reset by this implementation work.

## Official source policy

`OfficialRuntimeSourcePolicy`, `OfficialRuntimeResolver` and
`OfficialRuntimeHttpClient` own source selection and transport:

- .NET: `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`.
  Select stable, supported .NET **Runtime** 10 satisfying the authored requirement;
  verify the vendor SHA-512. SDKs, apphost packs, ASP.NET distributions and system
  installers are excluded.
- Node: `https://nodejs.org/dist/index.json`, then the selected exact release's
  `SHASUMS256.txt`. Select stable Node 24 LTS satisfying the requirement and the
  `24.18.1` floor; verify the exact archive's single SHA-256 entry.
- A separate local SHA-256 and actual length identify the downloaded archive in the
  store. Nullable preview sizes remain unknown; verified cache reuse reports zero.
- Source IDs select these Host configurations. App, packages and Registry responses
  cannot supply a runtime acquisition URL, release key, trust flag or source override.
  Runtime HTTP is separate from credential-bearing Registry HTTP, with no credentials,
  cookies, automatic redirects, proxy authentication or automatic content decoding.
- Every redirect is checked **before** the next request. Metadata/checksum URLs are
  fixed. .NET archive paths are exact version/RID Runtime paths on
  `builds.dotnet.microsoft.com`, `dotnetcli.azureedge.net` or
  `dotnetcli.blob.core.windows.net`; the Visual Studio download host additionally
  requires its bounded GUID/hash path and exact archive filename. Node archives are
  limited to exact release paths on `nodejs.org`. HTTPS, default port, no user info,
  query/fragment or escaped-path ambiguity are required. At most three redirects
  are followed. Unexpected partial responses are rejected.

Bounds are enforced on streamed bytes as well as declared sizes: 8 MiB metadata,
256 KiB checksum documents, 512 MiB archives, 1.5 GiB expanded TAR stream/content,
256 MiB per file, 50,000 entries, 512-character paths and 32 path components.
Metadata has a 30-second deadline; download/materialization have three-minute
deadlines; native smoke has a 20-second deadline and bounded output. Transient
downloads have at most three attempts with cancellation-aware backoff.

## Materialization, native readiness and store

`OfficialRuntimeMaterializer` verifies original bytes before extracting into fresh
private staging. Windows uses ZIP; macOS and glibc Linux use TAR.GZ. Paths, duplicate
and case-colliding names, ancestor collisions, special files, entry sizes and expanded
bytes are validated. Links are never followed. The closed Node runtime profile keeps
the executable and license and skips only recognized npm/npx/corepack auxiliary links.
.NET keeps its Runtime-only layout, including `Microsoft.NETCore.App`. The private
content identity commits the materialization profile and sorted file/hash/size/mode
inventory. Executable permission is restricted to the managed launcher.

`OfficialRuntimePlatformProbe` validates the current supported RID/architecture,
native executable header and applicable OS prerequisites. Linux RIDs require glibc:
.NET requires 2.27+, Node requires 2.28+ and kernel 4.18+. Node requires macOS 13.5+;
the current .NET 10 support profile requires macOS 15+. Windows requires Windows
10/Server 2016 or later. Native smoke checks the exact isolated .NET framework or
Node version/architecture/platform/ICU. Missing prerequisites produce diagnostics,
never an implicit system installation or musl/RID substitution.

Native probes reuse the controlled Worker environment baseline, including
case-insensitive inherited-variable lookup on Windows. The child environment is
cleared before applying that baseline and the exact managed runtime paths. Inherited
Node/OpenSSL/.NET/loader injection settings, Docker configuration and Host credentials
are excluded.

The existing store remains responsible for singleflight acquisition, version/identity
locks, cancellation ownership, staging, atomic journals/recovery, receipts, sealed
roots, health verification, quarantine, process-birth/containment leases, retention,
tombstones and GC. Receipts use only the new official identity and provenance. Archive
cache files use `.archive`; the separate signed-manifest and runtime-policy stores
are removed. Same-version changed vendor digests conflict before acquisition rather
than overwriting an installed identity.

Healthy launch is entirely local: an exact committed package binding must match its
receipt and verified managed root. The executable path is absolute. There is no
network, acquisition, ambient dotnet/Node search or runtime execution lease in launch.
Exact repair can reuse checksum-verified cached original bytes offline; missing bytes
use fresh metadata for the **same identity/digest**, never a latest-version substitute.
Recovery from authoritative installed binding evidence preserves the original
immutable acquisition provenance.

Explicit Ensure holds the runtime operation gate and an exact store retention across
acquisition's final metadata confirmation, durable manual retention and returned health
summary. This closes both the GC snapshot race and the acquisition-to-retention gap.
`RetainUntilUninstall=false` remains a cache-only operation: it returns verified healthy
state while protected, writes no manual retention intent, and can be collected after
the operation releases its temporary retention.

## Package prerequisites, consent and updates

- Registry plans carry only `Targets[].ExecutionRuntime` requirements. Host-local
  selection prepares bindings, and downloaded projection manifests must exactly match
  those requirements. Full `.sunderpkg` and shared/current-RID projection behavior is
  unchanged.
- Registry, retained local upload and source-adoption prerequisites use the same
  immutable package inspection and local healthy-runtime selection. Missing runtimes
  return a consented official install preview. Package mutation and activation do not
  download runtimes implicitly.
- Update-one, update-all and source-adoption previews carry package plan items,
  `RequiredCapabilityConsents`, per-origin/prerelease `PlanExpectations`, and the
  source-bound aggregate `RuntimePrerequisites` through the result wrappers. Missing
  runtime and post-resolution stale/failure results preserve this evidence without
  granting mutation permission. A no-eligible-update result has an explicit satisfied
  empty prerequisite plan and preserves skipped/pinned packages.
- App obtains one combined package/capability/runtime approval before queue enrollment
  or acquisition. Healthy reuse enqueues package work only; required acquisition
  enqueues runtime first and package second. After runtime readiness, re-preview the
  original package source and validate the approved semantics before ordinary package
  mutation, retaining source/RID expectations, per-origin resolution fingerprints and
  capability consent. Source adoption is a forced reinstall reviewed in that upfront
  approval. A runtime source never adopts package origin.
- Consent compares the selected identity, requirement, relevant source/profile and
  platform semantics. Whole-feed/checksum-document hashes, observation timestamps and
  unrelated feed entries remain evidence, not accidental consent-invalidating inputs.
  Material identity changes require a new plan. Cache reuse and exact repair preserve
  their original receipts across metadata refreshes.
- Accepted prerequisite and manual installations remain retained until explicit safe
  uninstall, including when the package step is cancelled. Package upload retention
  remains bounded, generation-safe and independently authenticated through
  `package-install-resumption.v1`.
- Automatic updates default **ON**, with revision-CAS settings and persisted intent.
  They select strictly forward compatible vendor versions, prepare side-by-side bytes,
  and apply only when the affected graph is stopped. Prepared stopped application is
  local. Explicit restart requires a fresh full restart-impact confirmation.
- Rebinding uses the existing package journal and package-policy fence. Precommit
  failure discards only a still-owned stage and preserves original bindings; durable
  commit keeps desired-state/reconciliation authority. Manual retention transfers only
  with the committed update. Package versions, content and provenance stay pinned.

Package/platform permission, revocation, nonce/checkpoint floors and continuous-clock
policy leases remain independent and enforced. Disabling runtime binary updates does
not disable package-policy refresh, provider withdrawal or worker enforcement. Local
archives do not bypass package policy merely because they lack an explicit Registry
origin.

## Client and CLI

Host advertises `official-runtime-downloads.v1`; the previous runtime management/update
capabilities are removed. Runtime.Client requires the new feature at the base handshake.
Runtime upload/import calls and CLI import/origin/trust switches are removed.

Thin commands include:

```text
sunder runtime settings [--automatic-updates true|false]
sunder runtime install <runtimeId> <range> <moduleAbi> [--yes]
sunder runtime ensure <runtimeId> <range> <moduleAbi>
sunder runtime update [--yes] [--restart]
```

Exact info/references/verify/repair/remove commands use the complete source, runtime ID,
vendor version, RID, ABI, checksum algorithm and digest. Repair/remove require their
ordinary confirmation. UI/CLI show source, actual vendor version, nullable download
size, health and vendor support separately.

## Verification checkpoint

- Host and Host test assembly compile with zero warnings/errors.
- Final Host portable lane (`dotnet-linux`, executed on macOS): **1,275 passed**, zero
  failures/skips, including the combined-approval response, Ensure/GC and probe
  environment regressions. The focused management/Registry/probe set passed **90**
  tests before this lane; those results overlap the portable total.
- Runtime.Client: **60 passed**. CLI: **294 passed** at the preceding cutover checkpoint;
  neither implementation changed in the Host review-fix continuation.
- Controlled source/extraction tests cover per-hop URL refusal, credential absence,
  bounded/partial responses, duplicate metadata/checksum entries, exact RID/LTS
  selection, ZIP/TAR path/link/mode checks and both vendor checksum algorithms.
- Real `osx-arm64` upstream probes verified .NET Runtime **10.0.12** (33,249,724 archive
  bytes, SHA-512 plus local SHA-256) and Node **24.21.0** (52,909,993 archive bytes,
  published SHA-256). Product Host store, native smoke, deep verification, refreshed
  install confirmation, cache reuse and manual retention passed for both.
- Real Worker V1 .NET/Node execution, cross-language RPC, process/lease cleanup and
  forward .NET rebinding passed using official roots. Other OS native acceptance is
  not represented by the macOS result; ZIP/metadata fixtures exercise all six RIDs.
- After the probe environment change, the native Node preload-marker regression
  passed (**1** test) and the current-RID .NET/Node Worker integration cases passed
  (**2** tests), all on `osx-arm64`. Platform-parameterized helper cases cover Windows
  variable casing plus macOS/Linux baselines; no native Windows result is claimed.
- Actual App `RegistryPackageInstallService`, `BackgroundProcessQueueService` and
  `RuntimeApiClient` pass through authenticated Host HTTP handlers for update-one,
  update-all and adoption, each with healthy reuse and missing-runtime acquisition
  (**6** cases using controlled source fixtures). They prove one upfront approval,
  runtime-first queue ordering, no package download/mutation before readiness, exact
  capability grants, and preserved package selection/provenance/pins. Multi-origin
  source/RID stale rejection and pinned no-op evidence add **3** focused cases.
- The Ensure regression pauses final metadata confirmation, proves the operation gate
  is held, and runs two zero-grace GC passes to independently test the exact retention.
  Post-operation production GC preserves durable manual intent and collects cache-only
  Ensure results as intended (**2** cases).
- Stopped deferral/application, manual retention transfer, precommit cancellation/fault,
  stale restart impact, postcommit desired state and independent package revocation
  retain their focused regressions.

The App/Supervisor consumer checkpoint separately reports **994 App** and **68
Supervisor** portable tests passing. The obsolete runtime-upload branch in
`Sunder.Host.Supervisor/RuntimeGateway.cs` is removed; the previously reported build
blocker is resolved. The actual queued App-to-Host cases above close the update/adoption
preview integration gap.
