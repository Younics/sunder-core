# Source, RPC and App compatibility probe profiles

These checked-in owners extend the [selected-input corpus](sdk-compatibility-corpus.md).
The original `dotnet-worker-v1` lifecycle/source/admission profile remains available.
All profiles use the collector's exact selected copies, native RID validation,
before/after hashes and immutable recipe selectors. They are **execution support**,
not evidence that an older SDK or package has been released.

## Implemented profiles

| Layout `profile` | Corpus kind | Owning project / exact method |
| --- | --- | --- |
| `node-ts-source-v1` | `old-source-new-sdk` | `Sunder.Package.Templates.Tests.PackageTemplateTests.RetainedTypeScriptBuildsWithSelectedCandidateTools` |
| `node-checked-js-source-v1` | `old-source-new-sdk` | `Sunder.Package.Templates.Tests.PackageTemplateTests.RetainedCheckedJavaScriptBuildsWithSelectedCandidateTools` |
| `dotnet-rpc-probe-v1` | `old-package-new-host` | `Sunder.Runtime.Host.Tests.ReleasedSdkCorpusTests.RetainedDotNetWorkerCompletesRpcAndCancellation` |
| `node-rpc-probe-v1` | `old-package-new-host` | `Sunder.Runtime.Host.Tests.ReleasedSdkCorpusTests.RetainedNodeWorkerCompletesRpcAndCancellation` |
| `avalonia-app-probe-v1` | `old-package-new-host` | `Sunder.App.Tests.ReleasedSdkAppCorpusTests.RetainedAppPackageActivatesWithProvidedCandidateAppIdentity` |

The corresponding `.json.in` files in
[`eng/compatibility/recipe-templates`](../../eng/compatibility/recipe-templates/)
contain the complete selectors, test projects and fixed collector command format.
Instantiate/review real reference and corpus IDs; the templates supply no source
commits, artifact hashes, retention URLs or release claims. Source profiles run in
the collector's portable Linux lane; binary/App profiles run on each native RID.
Local native checks can validate the owners on another OS without qualifying that
portable lane. Required inputs fail when absent; ordinary unselected runs use the
existing `ExternalArtifactFact` skip policy. A context automatically selects its
owning test; `SUNDER_REQUIRE_SDK_CORPUS=1` also requires inputs explicitly.

## Retained TS and genuinely checked-JS source

Use the same common layout members (`schemaVersion`, reference/corpus IDs,
`packageId`, `packageVersion`) as the C# profile, with:

```jsonc
{
  "profile": "node-checked-js-source-v1", // or node-ts-source-v1
  "sourceArtifactId": "<selected source artifact>",
  "project": "package.json",
  "packageOutput": "dist/<actual-id>.<actual-version>.sunderpkg"
}
```

The source ZIP contains exactly `source/` and `feed/`:

* `source/` contains the original `package.json`, **npm lock v3**, `tsconfig.json`,
  `sunder.package.json`, `sunder-contracts.lock.json`, any original owned/API locks,
  and authored source/assets. No `node_modules`, `dist`, `.sunder` or `.git` output.
* `feed/` contains original `.tgz` packages for every lock entry applicable to the
  actual OS/architecture, including the original Sunder SDK/tool. Other optional
  platform entries may remain in the unchanged lock; a Linux run still requires
  the Linux-native dependency tarballs. Retention must include the original closure,
  not only the new candidate feed.

The bounded profile supports a **flat, self-contained npm dependency tree**.
Nested installs, aliases, links, install scripts and dependency updates requiring
new versions outside the original closure need a separately reviewed profile.
The candidate tool's non-Sunder dependency versions must match the retained tree.
At most 128 dependency archives and 512 MiB expanded original closure are admitted;
each npm tar is bounded to 8,192 entries/256 MiB, one root, regular files and
directories. Standard `package/` and legacy DefinitelyTyped single roots are
supported; traversal, links and case-colliding files fail.

The owner materializes that tree directly from verified tarballs in disposable
work. Original npm SHA-512 integrity records are checked before candidate
replacement. Only `@sunder/sdk` and `@sunder/package-tool` come from the selected
candidate archives; their embedded name/version must match the candidate context.
The disposable `package.json` gets those exact dependency versions. **The retained
source archive and npm/contract locks are never rewritten.** The npm lock remains
the original graph oracle; it is not misrepresented as a newly resolved candidate
lock. No network, global npm cache, repository module tree or npm lifecycle script
participates in owner execution.

Using Node **>=24.18.1 <25**, the owner executes the installed candidate tool's
`generate`, native-RID `build`, and `package` commands and the retained toolchain's
`tsc --noEmit --listFiles`. It requires `strict`; checked-JS additionally requires
`checkJs`, `@ts-check`, a `.js` services module and no authored `.ts` files. The
compiler's actual file list must include that service source. It verifies authored
file inventory/hashes, candidate SDK/tool installed bytes, output identity and
target SDK version afterwards. Thus the JS case is not TS with a renamed label.

## Explicit retained RPC probe contract

RPC profiles use the binary layout's `packageArtifactId` and `hosts` plus:

```jsonc
"probe": {
  "protocol": "sunder.compatibility.probe.v1",
  "contractId": "compatibility.probe",
  "contractVersion": "<actual retained contract version>",
  "descriptorSha256": "<actual retained descriptor digest>",
  "abiSha256": "<actual retained ABI digest>",
  "providerId": "<actual retained provider id>"
}
```

The expected descriptor/ABI/provider identities come from the retained package,
are reviewed in its layout, and must match the selected Host's live catalog. These
profiles require an **explicitly authored probe package**; an arbitrary third-party
package is not assumed to expose these services or storage keys.

The checked-in fixtures implement a private, package-owned `compatibility.probe`
contract with service `probe` and the following wire behavior. Every request is
`{"value": "<string>"}`:

* `echo` is unary and returns `"echo:" + value`.
* `finite` is a finite server stream, emitting `value + ":1"`, then `value + ":2"`,
  and completing normally.
* `wait` emits `value + ":started"` and remains pending until provider cancellation.
  Its `finally` records `compatibility.provider-cancelled = value` only when its
  actual provider cancellation signal/token is cancelled.

The owner activates the unchanged archive on the provided Host, then writes a
fresh unpredictable nonce to `compatibility.challenge`. The retained Worker
discovers its own provider and makes actual **Worker → Host → Worker** RPC calls.
It consumes the unary and entire finite stream, starts `wait`, cancels only after
receiving its first event, waits for provider cleanup, and makes another unary
call (`after:<nonce>`) to prove continued usability. Neither a timer firing nor a
caller-only cancelled flag substitutes for provider cleanup.

The fixture publishes `compatibility.report` with protocol, nonce, unary value,
ordered stream values, start/caller-cancellation flags, provider acknowledgement
and post-cancellation unary value. Failures go to `compatibility.failure`. The
owner independently reads the provider acknowledgement and validates every report
field within 30 seconds, then gracefully stops the Host and confirms the traced
Worker process has exited. Reports with stale nonces, wrong values, missing stream
items or missing cancellation/drain proof fail. This covers loopback RPC, not
cross-language or multi-package RPC. Existing cross-language owners retain that scope.

Node uses the same provided Runtime Host and dev-owner activation path as .NET.
Its selected official-runtime envelope must contain a real Node 24 vendor archive
and `RuntimeExecutionRuntimeVendorArtifact` with the exact Node identity/ABI/RID
and SHA-256. .NET requires its .NET Runtime envelope/SHA-512. The existing official
runtime store verifies/materializes the prerequisite; the package/Host binaries
are not rebuilt during execution.

## Provided App environment and retained App package

The App layout has the common members, `profile: avalonia-app-probe-v1`,
`packageArtifactId`, and `apps` instead of `hosts`:

```jsonc
"apps": [{ "rid": "<native-rid>", "candidateArtifactId": "candidate-app-<native-rid>" }]
```

`candidate-app-<rid>` is a reserved `kind: host` artifact in the **current candidate
reference**. Like `candidate-host-<rid>`, it is copied into `candidateHostInputs`
and bound in the candidate inventory hash. There are at most twelve binary slots
(six Runtime Hosts, six Apps), all from the candidate source. Historical corpus
recipes retain their original package bytes while selecting the new App slot.

The App ZIP is a complete **framework-dependent, untrimmed .NET 10 App output**,
rooted at `Sunder.App.dll`, `Sunder.App.runtimeconfig.json` and all dependencies.
`Sunder.Sdk.dll` and `Sunder.Sdk.Avalonia.dll` must byte-match the selected candidate
NuGets. The retained package must implement the checked-in
`compatibility.app.probe` fixture profile: its App module registers
`Compatibility.App.ProbeView`, a DI-created `ContentControl` with the documented
content, challenge echo, SDK assembly properties and disposal marker.

`Sunder.App.Tests` compiles a separate, checked-in **test-only** executable with the
existing friend-assembly identity. The owner launches it using the provided App's
runtime configuration. Its assembly resolver loads App/SDK/Avalonia dependencies
only from the provided App directory; it asserts their actual loaded locations.
The probe reuses `TestAppPackageHost`, `RuntimeContractTestData`, the production
package projection planner and the actual provided `PackageViewHostService`,
`AppPackageLoadContext` and shared assembly registry. The existing test policy
stand-in isolates package activation from Registry authorization; no production
policy or startup flag is added or disabled.

The package archive is validated, projected with unchanged payload bytes and
activated into its real isolated App ALC. Assertions cover module/DI activation,
view creation/content, nonce-driven retained view behavior, reference-version
admission, exact **provided SDK assembly object identity**, and view disposal.
The parent verifies package identity, child challenge/result and unchanged provided
App/input hashes. It never compiles the retained package or calls the current
test process's App implementation as a substitute for the selected App.

This is headless component activation, **not** launching the full App `Main` or
qualifying packaged-window rendering, fonts, native single-file/trimmed bundles,
Supervisor deployment or installed Registry policy. Those binaries do not offer a
generic headless package-driving interface, and a single-file bundle may not expose
the managed assembly environment required here. This explicit supplied-assembly
profile exercises the requested App boundary without test-only production flags.

## First baseline authoring and remaining genuine inputs

[`eng/compatibility/probes`](../../eng/compatibility/probes/README.md) contains actual
.NET Worker, TS, checked-JS and App authoring sources. Build them once with the
actual chosen SDK/tool archives, test them, and retain the source/locks/closure and
resulting binaries together with real provenance. Subsequent binary owners consume
those exact artifacts. First-release current artifacts may seed a reference;
locally generated probes are not historical binaries or release qualification.

Real corpus invocations still need their immutable reviewed layouts/recipes,
genuinely retained original source/locks/package closures and binaries, matching
official runtime envelopes, all applicable RID candidate Host/App artifacts, and
the nine candidate SDK packages. No owner reconstructs missing old inputs from
current source. Six-RID execution and real older/newer SDK comparisons remain
qualification work until those inputs and native runs exist.
