# Core test evidence

Sunder Core does not check expanded test identities into git. The xUnit adapter's
TRX definitions/results and Node's JUnit testcases are the executable discovery
authority. Raw reports stay in ignored local paths and CI artifacts.

`eng/test-oracles/core-test-policy.json` is the small checked policy surface. It
contains only allowed primary lanes, expected project/lane ownership, report
groups, and explicit expected-skip rules with reasons and owners. Source
inspection supplies declaration-level filtering, platform support, and external
prerequisites at runtime; parameterized and generated cases come only from the
test adapters. `check-core-test-policy.mjs` rejects an empty test project, an
empty lane, ownership drift, stale expected-skip identities, or missing behavior
evidence in `core-large-object-characterization.json`.

```text
node scripts/test-oracles/check-core-test-policy.mjs
node scripts/test-oracles/run-core-test-lane.mjs --primary-lane host-platform
node scripts/test-oracles/run-core-test-lane.mjs --test Sunder.Package.Build.Tests.PackageBuildManifestTests.SharedOutputLock_InteroperatesWithNodeProducerWhenAvailable
```

Primary lanes remain a disjoint execution partition. `dotnet-linux` owns
OS-neutral .NET behavior, `host-platform` owns OS-specific or process-capable
.NET behavior, `app-packaging` owns the bundled App output contract, `node` owns
Node cases, and `release-artifact` owns fixtures and externally supplied artifact
cases. The lane gate checks the current operating system and every declared
executable, exact opt-in variable, file, and directory before reporting READY.
A lane with no runnable declaration fails. A READY-selected result that is
missing or skipped also fails.

## Runtime evidence

### Local iteration

Use the smallest meaningful check first. A passing focused test does not replace the
full CI gates, but a full artifact rebuild is not needed after each source edit.

```sh
# Changed behavior first (replace the class/method selector for the task).
dotnet test languages/csharp/dotnet/tests/Sunder.Package.Build.Tests/Sunder.Package.Build.Tests.csproj --no-restore --filter FullyQualifiedName~ServiceApiEditorGeneratorTests

# Fast owning-project check: existing portable lane, including on macOS/Windows.
node scripts/test-oracles/run-dotnet-tests-with-results.mjs --project Sunder.Package.Build.Tests --primary-lane dotnet-linux --build

# Inspect exactly which declarations are selected, without execution or report cleanup.
node scripts/test-oracles/run-dotnet-tests-with-results.mjs --project Sunder.Package.Build.Tests --primary-lane host-platform --list

# Process/build integration when the changed boundary warrants the whole lane.
node scripts/test-oracles/run-dotnet-tests-with-results.mjs --project Sunder.Package.Build.Tests --primary-lane host-platform --build

# Full owning project once at a coherent integration checkpoint.
node scripts/test-oracles/run-dotnet-tests-with-results.mjs --project Sunder.Package.Build.Tests --build
```

`--build` compiles current source without restoring. Restore explicitly after dependency
changes. When source has not changed since compilation, omit `--build` to reuse those
test binaries. Project-scoped reports go under `artifacts/test-results/local/` by
default and do not remove other lanes' reports. Full runs have a bounded 60-minute
project watchdog; individual hang watchdogs remain unchanged.

| Change | Validation to select |
| --- | --- |
| Parser, compiler, planner, mapper or business rule | Changed class/method and portable owning-project lane. |
| MSBuild, dependency/resource ownership, output publication or locking | Relevant process/artifact tests; broaden to `host-platform` for shared orchestration changes. |
| Public SDK/wire/persisted contract | Public API/contract checks and affected consumers, in addition to owning tests. |
| Packed tooling/template or native asset change | Relevant fresh-package/template/native scenario with its prerequisites. |
| Migration checkpoint, broad shared refactor or release | Full affected suites and existing CI/native/release lanes. |

Run workspace Build/Rebuild for build-graph/cross-repository changes and final cutover.
Do not repeat broad checks after documentation or unrelated edits. Keep useful small
theories for distinct inputs/bounds; avoid cloning a full process fixture for the same
behavior. Identify the surviving coverage before deleting or merging a test.

Only parallelize independent checks. The npm public-artifact checks clean shared
`dist`/`dist-test` directories, so finish them before compiling the Node helpers used
by .NET interop tests. From `languages/typescript/node`, prepare with `npm run build`
and `npm exec -- tsc -p packages/package-tool/tsconfig.test.json` using the repository's
Node/npm versions. Then run the required interop selection without another Node output
cleanup in parallel. After a prerequisite failure, rerun the affected cases rather
than repeating already-passed full suites.

### Complete CI evidence

`run-dotnet-tests-with-results.mjs` executes selected .NET projects serially and
writes one TRX per project. `run-node-test-matrix-with-results.mjs` removes
generated output, rebuilds every workspace, and routes all four Node groups
through the dual spec/JUnit reporter. Python and shell release fixtures write a
fifth JUnit report.

```text
node scripts/test-oracles/run-dotnet-tests-with-results.mjs --configuration Release
node scripts/test-oracles/run-node-test-matrix-with-results.mjs
node scripts/test-oracles/run-release-fixtures-with-results.mjs
node scripts/test-oracles/summarize-core-test-results.mjs
```

The summarizer writes `core-test-evidence.json`, not a second case inventory. It
contains aggregate discovered, executed, passed, failed, and skipped counts for
each report, project/lane pair, and lane. The only individual test identities in
the compact evidence are tests that actually skipped; each must match an
explicit policy rule and is recorded with its reason and owning lane. An unknown
skip, unknown result, missing report, missing project/lane owner, omitted selected
.NET declaration, inconsistent TRX counter, failed test, or report containing
zero tests fails evidence generation. Raw TRX/JUnit artifacts retain individual
outcomes and durations for diagnosis.

`.github/workflows/sunder-test-oracles.yml` is a scheduled or manually dispatched
complete-report audit. Pull-request lanes produce the same compact evidence for
their selected partition. Race-sensitive process and output-lock tests remain
serialized.

The compact Core compatibility ledger and API snapshots remain separate frozen
candidate inputs. `core-public-packages.json`, the npm artifact checks, and
`CorePublicPackageContractTests` continue to protect public/package contracts.
The separate `Sunder.Sdk.PublicApi.Tests/ApiFidelity/Sunder.Package.Build.txt`
test-only snapshot records accessor visibility and functional MSBuild
`[Required]` and `[Output]` attributes, with mutation probes for both dimensions.

## Cross-language contracts

The .NET and Node implementations consume the same checked-in corpora:

- RPC V1 values: `specs/rpc/v1/conformance/instances.json`
- Worker V1 envelopes: `specs/worker/v1/conformance/corpus.json`
- Worker V1 transport: `specs/worker/v1/conformance/transport-vectors.json`
- generated-output documents: `specs/generated-output/v1/conformance/documents.json`

Each oracle includes a seeded mutation probe. A parser that merely reads a
corpus but no longer enforces its expected property, frame boundary, encoding,
or document ownership must therefore fail the probe.

## Characterized objects

`eng/test-oracles/core-large-object-characterization.json` binds each behavior
below to a discovered test identifier. The JSON is the mechanically enforced
map; this section records the observable contract.

| Object | Inputs and outputs | Errors and cancellation | Ordering and authority | Persistence, logging, and cleanup |
| --- | --- | --- | --- | --- |
| `ProcessRuntimeWorker` | Accepts Worker V1 handshake, lifecycle, invocation, stream, callback, package-data, content, and payload-handle messages; its lifecycle codec returns typed bounded envelopes and deterministically rejects malformed fields. | Bounds startup and shutdown, forwards cancellation in protocol order, rejects callback identity spoofing and protocol violations, and fences crashes/fault reports to the exact generation. | Compose precedes commit and activation; provider dispatch waits for activation acknowledgement; recovery-candidate package-data authority ends at exact revocation. | Consumes stale upload handles, releases invocation-bound content, drains shutdown, and captures bounded stderr diagnostics. |
| `RuntimeRpcBroker` | Admits unary/stream RPC calls through one acquisition policy for endpoint, generation, contract, permission, frame, and bounded lease outcomes. | Default-denies permission, preserves unary/stream setup-failure parity, schema-validates outputs, cancels exact calls on retirement/deadline, and isolates provider-generation faults. | Binds content to sibling endpoints, marks dispatched idempotent failures retry-safe without replaying them, and drains retired leases before replacement disposal. | Tears call leases down authority-first; scope retirement, deadline, and explicit close use their characterized discard/revoke order and release timers, content, and capacity exactly once. |
| `PackageSessionState` | Publishes and leases package-session generations and emits deep, read-only publication snapshots. | Aborted publication reopens admission; a foreign publication handle is rejected before mutation; post-swap callback failure leaves the committed candidate authoritative. | Drains the old generation before publishing replacement, and owner-fences each pending publication handle. | Later session mutation cannot alter a captured descriptor graph; prior leases retire deterministically. |
| `PackageViewHostService` | Loads, applies, navigates, disables, and disposes package UI generations while candidate registrations stay invisible until commit. | Quarantines attributed failures, permits one fresh automatic recovery, and does not start recovery when boundary cleanup is unproven. | Commits replacement before retiring detached UI, then revalidates navigation after waiting on its gate. | Unloads resources/load contexts; timed-out cleanup remains retained until completion; notifications are sanitized and deduplicated. |
| `MainWindowViewModel` | Stages package navigation, selection, dock, panel, presentation revisions, and lifecycle candidates. | Rejects or supersedes candidates without presenting stale state; caller cancellation during optional auxiliary refresh does not revoke an already committed shell result. | Only the latest deferred request may navigate; selected lifecycle views stabilize before visible commit. | Rolls selection, panel, and dock state back when commit is rejected and unstages superseded candidates. |
| `AppWebBridge` | Admits native bridge requests and emits JavaScript bootstrap/invocation responses; the exact emitted bootstrap executes in a Node `vm` browser model against native-generated response scripts. | Rejects wrong nonce/origin before allocating pending work, carries browser deadlines to authenticated deadline failures, and enforces exact UTF-8/depth resource limits. | Navigation rotates document trust; the pending registry owner-fences completion so duplicate, completed, and prior-document requests cannot remove replacements. | Bounds pending capacity and request-id history, then detaches under bridge authority and cancels or disposes outside it. |
| `RuntimeWorkerCoordinator` | Reconciles durable desired worker state, policy, launch payload, readiness, stop, reset, and restart operations. | A pre-launch persistence fault abandons only the candidate; an actual Runtime Host launch fault retains Running intent so repaired-payload reconciliation advances forward. | Pure desired-state and completion transitions advance deployment generation exactly once; durable intent precedes the Runtime lease and competing ownership is refused. | Readiness probing distinguishes typed startup failure, malformed diagnostics, timeout, and caller cancellation; failed launches clear process/connection/lease ownership without losing desired intent. |

### Ownership boundaries

Extracted helpers may supply evidence for their characterized parent, but they do
not become parallel mutable owners:

- `ProcessRuntimeWorker` owns process I/O, call/lifecycle state, and exact-generation
  cancellation. `ProcessRuntimeWorkerLifecycleEnvelopeCodec` is stateless input
  validation and cannot advance lifecycle state.
- `RuntimeRpcBroker` owns call orchestration against catalog, permission, and
  session authorities. `RuntimeRpcCallAcquisitionPolicy` and
  `RuntimeRpcCallFrameCalculator` are pure failure/deadline policy;
  `RuntimeRpcCallScopeRegistry` owns only scope slots, timers, revocation, and
  content teardown. None persists grants or package-session state.
- `PackageSessionState` is the synchronization authority for admission,
  publication handles, generation leases, and immutable publication snapshots.
  Retirement is selected under its lock and cancellation/drain runs outside it;
  durable package installation remains outside this process-local state owner.
- `PackageViewHostService` owns package generation publication and resources.
  `AppPackageQuarantineCoordinator` owns the lock-protected quarantine and pending
  candidate fence, while `AppPackageUiRecoveryCoordinator` coalesces one
  process-lifetime attempt per package content and waits for cleanup proof outside
  the publication gate. Unproven or timed-out cleanup is retained, never treated
  as successful cancellation.
- `MainWindowViewModel` delegates package-view request revisions, staging, and
  supersession to `ShellPackageViewPresentationCoordinator`. That coordinator
  owns visible shell commit only; package loading stays with
  `PackageViewHostService`, and auxiliary-window refresh remains post-commit work.
- `AppWebBridge` owns the nonce/origin/document gate. Its pending registry must be
  accessed under that same gate so authentication and reservation are atomic;
  detached request cancellation and disposal run after the gate is released. The
  bootstrap generator is a pure protocol output boundary.
- `RuntimeWorkerCoordinator` is the sole mutable Supervisor owner of process,
  endpoint lease, connection, operation, and in-memory lifecycle state.
  `HostLifecycleTransitions` computes immutable state replacements,
  `RuntimeWorkerLaunchProbe` performs stateless launch/readiness I/O, and
  `HostLifecycleStore` alone persists desired state and the operation ledger.

These coordinators remain separate because their linearization points differ:
generation publication, UI presentation, fault recovery, RPC scope teardown, and
durable Host intent cannot safely share a gate or a cancellation policy merely
because each sequences lifecycle work.

## Deterministic harnesses

`Sunder.Core.TestSupport` supplies manual time, bounded eventual assertions,
observable gates and files, loopback streaming HTTP, captured child-process
diagnostics with tree cleanup, temporary-directory cleanup diagnostics,
package archive builders, and output-lock contender barriers. Lock barriers
observe actual `.sunder-output.lock.transition` filesystem publication rather
than an arbitrary callback boundary. Their delayed no-lock fault probes, plus
the other timing, cleanup, archive, and HTTP mutations, detect silently weakened
harnesses.

## Delay audit

Phase 3 replaced 39 fixed synchronization waits in targeted Core tests: 31 C#
`Task.Delay` calls and eight Node delay calls. Replacements observe lock-entry,
worker lifecycle, file publication, navigation, retirement, process readiness,
and output-swap checkpoints before relying on a diagnostic watchdog.

Retained delays fall into separate categories: infinite cancellation sentinels,
intentional fake-backend latency, retry-loop backoff inside the bounded
`Eventually` helper, and watchdog timers that report the last observed state.
Legacy finite polling without an available production checkpoint remains
visible for later conversion; proposed narrow hooks are view-model `WhenIdle`,
dispatcher entered/completed signals, store write checkpoints, and explicit
process ready/exit signals. A watchdog is never treated as proof that an
operation entered the state under test.
