# Core test evidence

Sunder Core does not check expanded test identities into git. The xUnit adapter's
TRX definitions/results and Node's JUnit testcases are the executable discovery
authority. Raw reports stay in ignored local paths and CI artifacts.

`eng/test-oracles/core-test-policy.json` is the small checked policy surface. It
contains component scopes, primary lanes, expected project/lane ownership, report
groups, explicit expected-skip rules with reasons and owners, and audited inert
.NET history entries. Each inert source is pinned by its LF-normalized SHA-256
and reason and must still have an unconditional literal `Compile Remove` in its
owning SDK project. Conditional, indirect or re-including project shapes fail
closed; this is not a general MSBuild evaluator or an ignore list for runnable
tests. Frozen history bytes remain untouched and are not recompiled. Source
inspection supplies declaration-level filtering, platform support, and external
prerequisites at runtime; parameterized and generated cases come only from the
test adapters. `check-core-test-policy.mjs` rejects an empty test project, an
empty lane, ownership drift, or stale expected-skip identities. It also checks every
literal workflow `--require-test` and named `--scenario`/`--require-scenario`
selection against current declarations before expensive CI work starts. Required
scenario owners live in `eng/test-oracles/ci-scenarios.json`; a removed owner fails
discovery and new cases beneath that owner are included automatically.

```text
node scripts/test-oracles/check-core-test-policy.mjs
node scripts/test-oracles/run-core-test-lane.mjs --primary-lane host-platform
node scripts/test-oracles/run-core-test-lane.mjs --test Sunder.Package.Build.Tests.PackageOutputLockTests.SharedOutputLock_InteroperatesWithNodeProducerWhenAvailable
```

Primary lanes remain a disjoint execution partition. `dotnet-linux` owns
OS-neutral .NET behavior, `host-platform` owns OS-specific or process-capable
.NET behavior, `official-runtime` owns actual vendor-runtime installation and native
Worker launch, `app-packaging` owns the bundled App output contract, `node` owns
Node cases, and `release-artifact` owns fixtures and externally supplied artifact
cases. The lane gate checks the current operating system and every declared
executable, exact opt-in variable, file, and directory before reporting READY.
A lane with no runnable declaration fails. A READY-selected result that is
missing or skipped also fails.

Component scopes are independent of those lanes. `sdk` owns the developer packages
and Host/CLI consumers; `repository` owns App and repository-policy checks. Their
union is `all`, with no overlap. `app`, `host` and `cli` support individual component
releases. The same scope controls build roots, prerequisites, execution and report
auditing. Package-side Avalonia and `Sunder.App.Bridge` contracts remain SDK-owned.
Desktop-dependent process/approval tests live in `Sunder.App.Integration.Tests`;
Runtime tests have no project reference to the desktop App. Mixed architecture
tests that inspect App source explicitly declare `[Trait("Component", "App")]`.
Public npm artifact/entry-point checks, wire schemas, byte-preserving checkout and
development toolchain contracts remain SDK-owned. Test-report infrastructure and
workflow wiring belong to repository validation.

```sh
python3 -B scripts/ci/core_ci.py portable --scope sdk --list
python3 -B scripts/ci/core_ci.py portable --scope sdk
python3 -B scripts/ci/core_ci.py portable --scope repository
```

## Runtime evidence

### Named CI scenarios

Scenarios combine existing declarations for generated templates, SDK/Host interop, App VM interop, output
locks, official runtimes and Agent-family acceptance. They share the existing
runner and evidence checker; their required environment flags are applied by the
runner. Supply the actual selected archives/directories before execution.

```sh
node scripts/test-oracles/run-dotnet-tests-with-results.mjs --scenario interop --list
node scripts/test-oracles/run-dotnet-tests-with-results.mjs --scenario interop --configuration Release
node scripts/test-oracles/summarize-core-test-results.mjs artifacts/test-results/scenarios/interop --allow-partial --require-scenario interop
```

Scenario reports default to their own directory. `--list` changes no reports or
environment, and required-scenario evidence rejects missing or skipped execution.

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
default and do not remove other lanes' reports. Projects have a bounded 60-minute
watchdog, except Package.Build selections containing `host-platform`, which get
90 minutes (a measured Windows invocation completed 87/87 cases in 66.5 minutes).
`--timeout-ms` explicitly overrides the project budget. VSTest's per-case hang
watchdog is five minutes for ordinary/fast selections, fifteen for process-capable
ExecutionRuntime.Build selections, and twenty for Templates and Package.Build
process selections (including full-project runs). Release-only Templates selections
retain their previous ten-minute case bound. Build's fifteen-minute child
commands and ExecutionRuntime.Build's five-minute commands retain their own
bounds. Templates keeps ordinary child commands at four minutes; only universal
six-RID packing gets ten. The outer project and CI job watchdogs remain separate.

The reporter streams stdout/stderr into console and retained diagnostic files.
Its process sidecar records the command, elapsed time, exit status/signal, timeout
or spawn error, and confirmed cleanup. A dedicated Unix process group or Windows
Job Object owns descendants; Windows assigns the launcher while suspended, before
it can create children. The reporter kills and drains remaining owned processes
before advancing, even after a normal launcher exit. Unconfirmed cleanup or
cancellation stops the selection. The native platform jobs exercise these cleanup
regressions on each OS before the .NET lane, including Windows Job Object behavior.
Preflight runs before restore/build and uses the existing Node spec/JUnit reporter.
Its reports and failed process fixtures are retained under `artifacts/process-preflight/`,
separate from the .NET lane evidence; successful fixtures are removed.
The sidecar starts incomplete before launch, so a killed reporter cannot leave
apparently passing evidence. Log-write failures also go through process cleanup.

Set `SUNDER_TEST_DIAGNOSTICS=1` to retain VSTest diagnostics for
Build/transport/template selections and mini crash dumps for unexpected Package.Build
testhost failures. Normal runs retain compact command/process reports and hang
watchdogs. On Linux, process sidecars include before/after memory and cgroup OOM
counters when readable, plus available filesystem space. These are diagnostic
observations, not automatic attribution of a crash to OOM. `CapturedProcess`
fixtures record child-command start/wait/exit
timings under the reporter's diagnostic directory, so an outer abort need not erase
the last started command. No production Runtime timeouts or lifecycle behavior change.

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

### Owning-job CI evidence

`run-dotnet-tests-with-results.mjs` executes selected .NET projects serially and
writes one TRX per project. `run-node-test-matrix-with-results.mjs` removes
selected generated output, rebuilds the selected workspaces, and routes their Node
groups through the dual spec/JUnit reporter. Selected Python and shell release fixtures
write a separate JUnit report. Python runs are batched once per source module while preserving
per-method timings, failures and skips. The [CI commands](../../scripts/ci/README.md)
own preparation, execution order, temporary cleanup and artifact retention.

The Node matrix builds the SDK, package tool, and scaffolder before enabling the
packed closure and service scenarios. The optional-reference Worker child case
belongs to the package-tool `service-resources` tests: both TS and JS parents
generate their provider and require exactly one passing child case with no skips.
The standalone SDK run does not register that fixture-only child case.

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
.NET declaration, inconsistent TRX counter, failed/aborted run, failed command
sidecar (even beside a passing TRX), failed test, or report containing zero tests
fails evidence generation. Raw TRX/JUnit artifacts retain individual
outcomes and durations for diagnosis.

`.github/workflows/sunder-ci.yml` runs `all` for PR/merge-queue, scheduled and default
manual validation. Main pushes run `repository`; `sunder-sdk-release.yml` owns the
complementary `sdk` validation on main. Component releases pass their explicit scopes.
Every successful selection's `test-results` job downloads its portable/native reports
and runs `summarize-core-test-results.mjs <directory> --scope <scope> --aggregate`.
CLI has no native test lane. Report paths and artifact names include the scope so
App and Host calls in the same release cannot overwrite each other.
This checks selected ordinary .NET declarations, required CI scenarios, script fixtures,
Node source coverage and owning reports without rebuilding or rerunning suites.
Downloaded reports resolve absolute paths against complete declared source paths,
so different checkout roots retain component ownership. Native
jobs enforce their own per-architecture required execution and platform-specific
skip rules; the aggregate does not reinterpret Windows/macOS skips as Linux results.
Externally gated live Registry, Agent and retained-release tests retain their
explicit inputs and owning acceptance lanes, rather than pretending an unselected
skip is execution evidence. The two App web-bridge JavaScript VM tests are required
by the App-owned `app-interop` scenario; SDK/Host interop has its own scenario.

Native/process and App packaging checks use the reusable
`sunder-host-validation.yml`. All selected partitions produce the same compact
evidence. Project tests run sequentially, with ordinary
parallel MSBuild inside each build; race-sensitive test collections retain their
own serialization. Package.Build caps compiler-heavy collection concurrency at two
to fit alongside build processes on CI workers. Linux .NET test commands default
`MALLOC_ARENA_MAX` to `2`, preserving an explicit caller value, to bound glibc arena
retention across repeated compiler metadata loads. Node artifact cleanup completes
before Node outputs are rebuilt
for .NET interop.

The Windows x64/ARM64 platform jobs additionally require the real .NET/Node
generated-output-lock interoperability case. Mocked Node process-identity regressions
run once in the ordinary Node suite, not again as native Windows evidence. Windows
self-identity bootstrap has a separate, one-time 30-second budget before contention
waiting starts; owner probes remain capped at five seconds and the remaining lock
wait. Tests signal contention only after their own bootstrap completes. Unknown
owners are preserved, not treated as dead, and the whole test/tool processes remain
bounded. A local macOS pass is not native Windows qualification.

Windows platform/process and App-output validation runs natively on both `win-x64`
(`windows-2025`) and `win-arm64` (`windows-11-arm`), with RID-labelled jobs and
report artifacts. The six native vendor-runtime checks remain separate required
coverage. App release signing still uses an x64 runner for both Windows package
targets because its signing client is x64. Hosted-runner allocation failures are
infrastructure failures, not test passes or a reason to drop a required lane.

The Supervisor reset/drain fixture uses `TemporaryDirectoryScope.RunAsync` so a
cleanup failure cannot hide the original test/process diagnostics. Both failures
are retained when action and cleanup fail; cleanup remains mandatory. Windows
reset deletion grants inheritable user-only directory access while traversing
validated owned trees, avoiding loss of inherited child access without changing
foreign ownership or relaxing path/identity checks.

Platform, official-runtime, App packaging and Windows output-lock evidence are
summarized after their execution step was attempted, including a failed attempt,
but not after a skipped step when an earlier prerequisite or test failed.
This avoids a misleading missing-directory error without accepting missing reports:
successful native validation requires every lane owned by its scope. SDK/Host scopes
require platform/process and official-runtime checks; App scopes require platform,
bundled-output and installer checks. App releases require both App and Host validation.
Superseded PR runs are
cancelled; main, scheduled, manual and release runs have unique CI concurrency groups.

The historical Core/Stack migration ledgers are inert preserved archives; the
separate current candidate remains UNFROZEN / UNRELEASED. Current API snapshots
and independently retained supported-release references keep their own gates.
`core-public-packages.json`, the npm artifact checks, and
`CorePublicPackageContractTests` continue to protect public/package contracts.
The separate `Sunder.Sdk.PublicApi.Tests/ApiFidelity/Sunder.Package.Build.txt`
test-only snapshot records accessor visibility and functional MSBuild
`[Required]` and `[Output]` attributes, with mutation probes for both dimensions.

### Current Agent consumers

The Runtime/App `AgentPackageReleaseSmokeTests` link `tests/AgentCurrentBaseline.cs`
and `tests/AgentCurrentInputs.cs`. Workspace harnesses link the same test-only input
reader, without adding a public SDK surface. It reads two independent Agent-owned inputs: `compatibility/agent-current-inventory.json`
(15 exact package/project pairs, 12 App roles, 18 public / 13 private APIs and 64
providers) and `compatibility/agent-current-contracts.json` (reviewed canonical
descriptor/ABI records bound to the exact inventory bytes). Missing records fail
the selected artifact lane; tests never generate them from the output under test.

The default location is the sibling `sunder-agent-package` checkout. Standalone
Core consumers can select the reviewed checkout with `SUNDER_AGENT_CURRENT_BASELINE_ROOT`.
The existing `SUNDER_REQUIRE_AGENT_PACKAGE_RELEASE` and
`SUNDER_REQUIRE_AGENT_UI_SNAPSHOT` required-input gates remain. Universal archives
must have the exact six-RID roles and current dependencies; their descriptor bytes
and runtime provider tuples are checked against the reviewed current input through
install/reload/reinstall. App snapshot metadata binds both current-input hashes to
the runtime-produced snapshot. Stale snapshots must be recollected, not relabeled.
Existing SQLite native loading, Worker exit and App activation coverage remains.

The Agent owner has supplied the reviewed 31-record input after local current-RID
qualification. `AgentCurrentInputsTests` selects that scope explicitly with
`SUNDER_REQUIRE_AGENT_CURRENT_INPUTS=1`, `SUNDER_AGENT_CURRENT_BASELINE_ROOT` and
`SUNDER_AGENT_PACKAGE_ARCHIVE_DIR`. It checks all fifteen current-RID archives and
independent input/ABI/tamper boundaries; it also proves those archives cannot satisfy
the separate universal-release helper. Current local consumer qualification passed
these four checks and real selected .NET/Node interoperability. The universal-release
activation/App snapshot cases retain their required inputs; current-RID evidence does
not qualify universal six-RID native behavior, cold inputs or releases.

## Cross-language contracts

The .NET and Node implementations consume the same checked-in corpora:

- RPC V1 values: `specs/rpc/v1/conformance/instances.json`
- Worker V1 envelopes: `specs/worker/v1/conformance/corpus.json`
- Worker V1 transport: `specs/worker/v1/conformance/transport-vectors.json`
- generated-output documents: `specs/generated-output/v1/conformance/documents.json`

Each oracle includes a seeded mutation probe. A parser that merely reads a
corpus but no longer enforces its expected property, frame boundary, encoding,
or document ownership must therefore fail the probe.

## Behavioral contracts

The behavior tests enforce these observable contracts directly. This table is
explanatory documentation, not a second CI-maintained inventory of test names.

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

The source-channel standalone Maker probe is a separate explicit artifact scope:
select `SUNDER_REQUIRE_MAKER_STANDALONE=1`, `SUNDER_MAKER_DEV_PACKAGE` (current-RID
canonical development directory), `SUNDER_MAKER_IMPORT_CANDIDATE` (its exact local
Agent API archive selection), and `SUNDER_MAKER_PLATFORM_CATALOG` (the selected Core
catalog). Run `AgentPackageReleaseSmokeTests.MakerStandalone_CurrentDevelopmentOutputActivatesAndRejectsInvalidArgumentsWithoutAgent`.
Only Maker is loaded into the disposable Host. The Agent archive supplies metadata,
not an installed/running Agent. The probe retains import/permission guards, launches
the actual Worker and checks its provider-side argument rejection and clean exit.
It does not qualify the native App, a released execution-runtime binding, screenshots,
or the complete fifteen-family release matrix.

`Sunder.Core.TestSupport` supplies manual time, bounded eventual assertions,
observable gates and files, loopback streaming HTTP, captured child-process
diagnostics with tree cleanup, temporary-directory cleanup diagnostics,
package archive builders, and output-lock contender barriers. Lock barriers
observe actual `.sunder-output.lock.transition` filesystem publication rather
than an arbitrary callback boundary. Their delayed no-lock fault probes, plus
the other timing, cleanup, archive, and HTTP mutations, detect silently weakened
harnesses.

## Delay audit

Observe lock-entry, Worker lifecycle, file publication, navigation, retirement,
process readiness and output-swap checkpoints before relying on a diagnostic watchdog.

Retained delays fall into separate categories: infinite cancellation sentinels,
intentional fake-backend latency, retry-loop backoff inside the bounded
`Eventually` helper, and watchdog timers that report the last observed state.
Legacy finite polling without an available production checkpoint remains
visible for later conversion; proposed narrow hooks are view-model `WhenIdle`,
dispatcher entered/completed signals, store write checkpoints, and explicit
process ready/exit signals. A watchdog is never treated as proof that an
operation entered the state under test.
