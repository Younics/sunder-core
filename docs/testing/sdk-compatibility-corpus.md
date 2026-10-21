# Selected-input SDK corpus owners

The `dotnet-worker-v1` profile supplies three bounded owning tests for the existing
[`sdk_compatibility_execution.py`](../../scripts/compatibility/sdk_compatibility_execution.py)
collector. They consume its **selected copies**, verify paths/size/SHA-256, and
recheck input/context bytes after execution, including failures. They never emit
release receipts themselves. The collector owns selection, immutable recipe review,
Git/workflow/native-lane identity and TRX receipt validation.

[Expanded source/RPC/App profiles](sdk-compatibility-probes.md) add TypeScript,
genuine checked-JavaScript, .NET/Node unary/stream/cancellation probes, and retained
App package activation against a provided candidate App assembly environment.

## Recipes and invocation

The [recipe templates](../../eng/compatibility/recipe-templates/) deliberately have
unfilled reference/corpus IDs. Review instantiated JSON at
`eng/compatibility/recipes/<reference-id>/<corpus-id>.json`, retain exactly those
bytes, and register their real size/hash and artifact IDs through the existing
qualification input format. No release ID, commit, retention URL or digest is
provided by these templates. An example is not a registered release.

| Kind | Exact owning test | Collector lane |
| --- | --- | --- |
| `old-package-new-host` | `Sunder.Runtime.Host.Tests.ReleasedSdkCorpusTests.OldPackageRunsOnSelectedCandidateHost` | each of six native RIDs |
| `old-source-new-sdk` | `Sunder.Package.Templates.Tests.PackageTemplateTests.RetainedAuthoredSourceBuildsWithSelectedCandidateSdk` | portable Linux x64 |
| `new-feature-old-host` | `Sunder.Runtime.Host.Tests.ReleasedSdkCorpusTests.NewFeatureIsRejectedBySelectedReferenceHost` | each of six native RIDs |

The fixed collector `dotnet-test` command builds the owning test project, sets
`SUNDER_COMPATIBILITY_CONTEXT`, filters the exact declaration and writes TRX to
the collector's report path. Building the test project is not building the retained
package/Host: those are loaded exclusively from the selected artifact records.
No additional environment flag is necessary in the collector.

Ordinary unit CI explicitly skips these `ExternalArtifact` tests when neither
`SUNDER_COMPATIBILITY_CONTEXT` nor `SUNDER_REQUIRE_SDK_CORPUS=1` is present.
Selecting the requirement flag without inputs **fails**. A present but invalid,
missing, wrong-direction or incomplete context also fails; there is no fallback
to the current project's outputs. Skips never satisfy collector qualification.
For local adapter checks, pass the context and the same exact `dotnet test --filter
FullyQualifiedName=...` selection. A local TRX is not release evidence. Native
checks verify the actual process RID; a local source check may use a portable
context on its actual OS, but only the collector can qualify the Linux portable lane.

## One artifact layout, version 1

Every corpus includes exactly one selected `kind: other` artifact named
`corpus-layout.json`. Give each layout a distinct artifact ID within its retained
reference and select it in that corpus's `artifactIds`; the fixed basename locates
it without imposing the same ID on different corpora. Its JSON is closed,
case-sensitive and duplicate-free:

```jsonc
{
  "schemaVersion": 1,
  "referenceId": "<same as collector context>",
  "corpusId": "<same as collector context>",
  "profile": "dotnet-worker-v1",
  "packageId": "<actual package id>",
  "packageVersion": "<actual package version>",
  "packageArtifactId": "<selected package artifact id>",
  "hosts": [
    {
      "rid": "<exact native rid>",
      "candidateArtifactId": "candidate-host-<exact native rid>",
      "referenceArtifactId": "<selected reference Host artifact id>",
      "runtimeArtifactId": "<selected official runtime envelope artifact id>"
    }
  ]
}
```

This is the binary layout. Include all six unique native RID entries for a required
collector invocation (up to six, one for local checks). Reference/runtime/package
artifact IDs refer to the context's `inputs`, with exact kind matching. Candidate
Host IDs refer exclusively to `candidateHostInputs`: the collector selects the
reserved `candidate-host-<rid>` records from the **current candidate reference**,
copies them under `inputs/candidate-host/<id>/<basename>`, and includes their full
inventory in `candidateSha256` with the nine SDK packages. Both originals and
copies are verified before/after execution. This lets historical recipes stay
immutable as the candidate advances; a historical Host in `inputs` cannot silently
substitute for the new Host. Candidate Host artifact source
commit must equal the context's `artifactSourceCommit`. Its retained hash binds the
full selected output; product versions are not substituted for source identity.
The reference Host may be the same genuine candidate for the first baseline seed.
It is not called a historical released Host until there is actual retained history.

All files remain in the collector's `inputs/corpus/<id>/<basename>`,
`inputs/candidate/<artifactId>/<assetName>` or `inputs/candidate-host/<id>/<basename>` slots. Arbitrary absolute paths, external
drivers, symlinks, duplicate/case-colliding members and changed bytes are rejected.
ZIPs have at most 8,192 entries and 2 GiB expanded content. Paths use portable ASCII
segments, `/`, no `.`/`..`, no links/devices; directory entries are allowed. Work is
done in fresh disposable directories. The collector's aggregate 2 GiB input bound
still applies across all selected artifacts, including other RID inputs.

### Package and Host artifacts

* **Package (`kind: package`)**: original canonical `.sunderpkg`, including its
  actual SDK/Worker dependency closure. Core's existing archive inspector validates
  the archive and exact package id/version/native Worker target. The binary owner
  uses the original file bytes, without rebuilding or repacking it. Positive
  activation extracts the archive through the existing inspector and registers the
  unchanged payload through the authenticated dev-owner API. Installed activation
  requires separate Registry policy; this adapter does not manufacture that policy.
  Rejection uses that same development admission boundary, before target launch.
  Extracted payload hashes are rechecked after shutdown.
* **Host (`kind: host`)**: ZIP of a complete framework-dependent .NET 10
  `Sunder.Runtime.Host` output, with `Sunder.Runtime.Host.dll`, `.deps.json`,
  `.runtimeconfig.json` and all runtime dependencies at the archive root. This
  profile starts **that DLL** with the runner's `dotnet`, isolated local state,
  loopback port 0, a fresh bearer token and the existing startup trace. The installed
  .NET/ASP.NET shared framework is a runner prerequisite; Supervisor/App packaging
  is outside this profile. Host file hashes are checked again after shutdown.
* **Official runtime envelope (`kind: other`)**: ZIP containing exactly
  `artifact.json` and `runtime.archive`. The JSON is the original
  `RuntimeExecutionRuntimeVendorArtifact` in Host camel-case JSON format, including
  exact vendor version/RID/SHA-512 and official distribution metadata. The archive
  is the original vendor .NET Runtime archive. The existing `ExecutionRuntimeStore`
  and official materializer validate those exact bytes and deep-verify the runtime
  before provisioning the disposable Host's runtime prerequisite. No runtime is
  rebuilt or obtained from an ambient SDK output. This profile assumes the current
  V1 official-runtime receipt/store format, not migration of older state schemas.

The positive binary assertion covers real external Host development admission,
Worker activation, Ready state, provider discovery, trace-observed Worker process
startup and graceful Host/Worker shutdown. It requires a self-contained package
family with at least one Runtime RPC provider, no external package dependencies or
sensitive-capability consent. It does not invoke arbitrary caller-selected code.

### New-feature admission extension

For `new-feature-old-host`, add the required `rejection` object:

```json
{
  "mode": "synthetic-unknown-capability",
  "capability": "compatibility.synthetic-future.v1",
  "featureArtifactId": ""
}
```

The first-release control copies the genuine baseline package to disposable work
and changes **only** Runtime `requiredHostCapabilities` in its manifest plus that
manifest's size/hash entry in the content index. Payload binaries and their index
records are unchanged. Archive validation must succeed before admission is tested.
This in-memory synthetic fixture is limited to 256 MiB expanded content.
The synthetic capability is explicit;
neither it nor the mutated package is represented as historical evidence. Both
Hosts must reject it, and both must admit the unchanged baseline.

For a real later feature, use `mode: retained-feature`, the real capability, and a
selected `kind: package` `featureArtifactId`. That package's current-RID target must
declare the capability. The reference Host must give the precise unsupported-Host-
capability diagnostic, leave installed/active state empty, and emit no Worker-start
event. The succeeding baseline control proves the same Host emits those events.
The candidate Host must admit/activate the retained feature package. There is no
fallback from this real-feature mode to the synthetic control.

### Authored source artifact

For `old-source-new-sdk`, replace `packageArtifactId` and `hosts` with exactly:

```json
{
  "sourceArtifactId": "<selected source artifact id>",
  "project": "<relative/path/to/canonical.csproj>",
  "packageOutput": "bin/Release/net10.0/<actual-output-name>.sunderpkg"
}
```

The selected `kind: source` ZIP has exactly two roots:

```text
source/                         complete original authored tree
  .../<canonical>.csproj        exactly one C# project
  .../packages.lock.json        original resolved NuGet graph
  .../sunder-contracts.lock.json original package/platform/contract selections
  .../*.cs, *.props, *.targets, assets, global.json, ...
feed/                           retained original NuGet dependency closure
  *.nupkg                       original Sunder and third-party packages
```

`project` is relative to `source/`; `packageOutput` is relative to the project's
directory. Preserve the original toolchain/options (including `global.json` when
authored). No `bin`, `obj`, `.git` or `node_modules` may be present. This profile
supports a single canonical project using direct versioned `PackageReference`
items in the project or imported props/targets; it rejects project references and
central package versions rather than guessing how to rewrite them.

Only the **disposable** tree's Sunder `PackageReference Version` attributes change
to the exact selected candidate version. The retained ZIP, code, contract/API locks,
assets and toolchain remain byte-exact. NuGet may regenerate the disposable
`packages.lock.json`; non-Sunder dependency records must retain their semantics.
The feed contains retained third-party packages and all six selected candidate
NuGets, replacing original Sunder selections. Restore uses a fresh isolated cache
and explicit local-only sources; no candidate packages are rebuilt. Every original
lockfile dependency's exact id/version must exist in the retained feed, including
original Sunder packages; a candidate feed cannot conceal missing original inputs.

The owner restores, builds generated roles/bindings and directly runs
`PackSunderPackage`, then checks the package identity and SDK target version. It
verifies resolved `project.assets.json` graphs, actual cache `.nupkg` digests,
extracted SDK/tool DLLs and imports, the staged `Sunder.Package.Build.dll`, and
the packaged SDK/Worker DLLs against selected candidate bytes. A mere
successful compile with an ambient/stale SDK cannot pass. Authored inputs are
rechecked before/after build, including the package contract lock; newly written
authored files outside `bin/obj` also fail.

## Coverage and outstanding inputs

The original owners cover this bounded C# Worker profile. The
[expanded profiles](sdk-compatibility-probes.md) own TS/checked-JS rebuilding,
explicit .NET/Node RPC/cancellation behavior and provided-App identity/activation.
Registry-backed installed activation, arbitrary external dependency graphs,
native/single-file packaging, full GUI rendering, other receipt formats and older
HTTP revisions remain separate scopes. Current-artifact checks are not genuine
historical or six-native-RID qualification.

To invoke the corpus for real, input owners must provide: the original authored
tree **with its original locks and dependency closure**, real package/Host outputs
for each native RID, exact official runtime envelopes, all nine tested candidate
SDK packages, and reviewed instantiated layout/recipe records with actual source
and retention identities. Current tested artifacts can seed the first reference;
regenerating an approximation of unavailable old authored input is not retention.
Missing inputs are an **INPUT** blocker and fail required invocations. This guide
and its templates do not freeze a ledger, qualify a release or publish anything.
