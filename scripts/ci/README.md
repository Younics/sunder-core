# Local and hosted CI

Use the versions in `global.json` and `eng/toolchains.json` on `PATH`.
The setup action and `install-node.sh` consume these owners.

```sh
python3 -B scripts/ci/core_ci.py portable --scope sdk --list
python3 -B scripts/ci/core_ci.py portable --scope sdk
python3 -B scripts/ci/core_ci.py platform --scope sdk
python3 -B scripts/ci/core_ci.py platform --scope sdk --partition package-build-1
python3 -B scripts/ci/core_ci.py official --scope sdk
python3 -B scripts/ci/core_ci.py portable --scope repository
python3 -B scripts/ci/core_ci.py rehearse --work artifacts/rehearsal
```

Scopes select product responsibilities; lanes select execution environments.
`all` is the default. Leaf scopes are `npm`, `dotnet`, `app`, `host`, `cli` and
`repository` (CI/release tooling). Each test has one component owner. `sdk` is a
validation bundle containing npm/.NET libraries, Host/CLI consumers and repository
checks. The test policy owns these selections; the public package inventory owns
the six NuGet and three npm package identities and their independently authored versions.
`--list` prints selected projects, build roots, scenarios and reports without
building or changing outputs. Scoped builds let MSBuild resolve project references
from those roots, preserving the Release configuration throughout the graph.

`portable` runs the selected Node, .NET and script tests, then prepares and tests
their cross-language inputs. Node artifact cleanup finishes before interop
preparation. Repository tooling and npm validation restore the locked npm development
dependencies; .NET library validation also prepares Node generator inputs. `platform` runs the current OS's selected process tests and, for App
scopes, bundled-output checks. `official` requires a Host consumer scope and
packs and launches the current RID's Worker against actual vendor runtimes.
Hosted native validation uses separate .NET, Host and App rows on every RID.
Each has `main`; .NET also has the policy-owned Package.Build method shards.
All rows share one six-runner budget and independent checkouts.
`main` owns that component's other projects and App/Windows side scenarios. Partition reports
have distinct paths and artifact suffixes; the aggregate requires all of them and
rechecks their actual TRX selections. Omitting `--partition` runs the full local lane.

Interop preparation publishes `SUNDER_NODE_MODULE_WORKSPACE_PATH` together with the
archive and current-RID leaf. Node caller compilation resolves its SDK, fixture and
generated RPC inputs from that producer workspace throughout consumer execution.

`rehearse` uses the SDK scope, builds all authored developer-package versions, invokes the production
NuGet/npm pack and archive verifiers, assembles a feed, builds generated templates
and runs their Host tests. Its output directory must be new. It writes local
artifacts and `rehearsal.json`; publication uses the hosted selected-release flow.
`candidate` is that workflow's pack/feed/template command, consuming its existing
`artifacts/release-plan.json` and tested build outputs.

## Hosted navigation

- **Release** at `sunder-sdk-release.yml` is the single entry point, preserving
  publisher identity. Main pushes and manual release runs select independent SDK,
  App, Host and CLI versions before installing toolchains.
- PR/merge-queue validation compares authored versions with the base commit and
  uses read-only jobs. Weekly schedules validate `all`; manual `mode: validate`
  accepts a scope. The policy maps releases to existing components and consumers.
- No new versions means no component tests, builds, packaging, signing or publication.
  Tooling/configuration changes can select repository checks independently.
- Native releases reserve tags, drafts and their original run before packaging.
  Completed platform uploads are reused on retry; complete drafts recover using
  original bytes. SDK and native publication finish in this same run.

Jobs use concise names such as `Test Host · Linux ARM64`,
`Test package builds · Windows x64 · shard 2/3` and `Package App · macOS ARM64`.
Package.Build retains three round-robin method shards per RID, selected only with
.NET package validation. Partition totals come from the test policy.

The selection summary lists versions, decisions and required owners/consumers.
Each producer summarizes its execution/results and
links its diagnostic artifacts. Final overviews include job links and durations
from the current run attempt. Pagination is decoded separately from `gh api`;
an unavailable navigation API cannot hide or replace required test results.

Component jobs expose restore, build, test, pack and verification steps. npm has
individual SDK, package-tool and creator test steps sharing a temporary producer
workspace. .NET source builds consume their own Node generator inputs. Shared
helpers implement the same operations for local and hosted execution:

```sh
python3 -B scripts/ci/metadata.py components --scope all
python3 -B scripts/release/sdk_release_documentation.py check
python3 -B scripts/ci/core_ci.py restore --scope cli
python3 -B scripts/ci/core_ci.py build --scope cli
python3 -B scripts/ci/core_ci.py test --scope cli
```

**Test .NET ↔ Node** prepares an isolated producer and
tests archive validation, RPC in both directions and shared output locking.
**Test package consumers** consumes the exact candidate
archives from npm/.NET producers, assembles the existing mixed-version feed,
generates projects and launches them through Host. Successful consumer validation
hands the same archives to staging; publication and recovery use retained bytes.
npm-only and NuGet-only selections use published dependencies for the other ecosystem.
**Test CLI clients** runs exact Host/Runtime client contracts when Host's full suite
is not selected.

Documentation preflight renders the real committed README/template inventory
before builds. Inline/fenced code is excluded from relative-link parsing; actual
repository links remain source-commit bound. General link checking uses
`check-markdown-links.py --tracked` so retained producer workspaces are not source inputs.

**Verify tests** audits selected owning reports using
`--selection artifacts/validation-selection.json --aggregate --component-layout`.
Its overview links to component jobs and shows
stage outcomes, counts, native completeness and missing evidence. Only the explicit
`--github-summary` option writes hosted summaries, keeping reporter fault injection
isolated. **Summary** records validation and publication outcomes. Report names
include the selection, component and partition. Required-check rules should target
`Select checks` and `Verify tests` for PR/merge-queue validation; `Verify tests` is
intentionally skipped when selection finds no checks.

High-level commands keep prepared environment values in their process and clean
temporary template/runtime workspaces on success or failure. Low-level `templates`
and `official-inputs` require an explicit `--work` directory when inputs need to
outlive the command. Python fixtures execute once per module with per-method JUnit
results, including failures and skips.
Use `RUNNER_TEMP` to relocate preparation workspaces. Keep the OS-default `TMPDIR`
on macOS so named-pipe socket paths fit the platform's length limit.

## Artifact retention

CI commands write reports under `artifacts/test-results/<scope>/<lane-or-scenario>`;
ordinary reports replace their owning selection's previous output. Hosted reports expire
after 14 days; release handoffs retain their release-specific policies. Large VSTest
logs and crash dumps require `SUNDER_TEST_DIAGNOSTICS=1` (enabled for hosted
Package.Build shards); process receipts, stdout,
stderr, TRX and hang watchdogs remain enabled.
Restore/build binlogs and incomplete-to-final profile sidecars preserve failures before
test discovery. Aggregate reporting runs after failed or cancelled producers and retains
`core-test-summary.json`; accepted evidence requires every selected native RID's lanes.

```sh
# Preview old reports; default retention is five newest children and fourteen days.
python3 -B scripts/ci/artifacts.py --reports
python3 -B scripts/ci/artifacts.py --reports --apply
# Protect pending release inputs, then preview a separately selected old workspace.
python3 -B scripts/ci/artifacts.py --pin artifacts/rehearsal
python3 -B scripts/ci/artifacts.py --path artifacts/old-investigation
```

Removal requires `--apply`, ignored artifact children and no retention pin.
`.sunder-keep` protects a directory and deletion through its ancestors. Remove the
pin when qualification/publication is complete. On Unix, active process arguments
and registered worktrees also protect matching selections. `--root` can select the
private workspace, including its submodules' artifact directories. Source files,
unignored files and selections through symbolic links are rejected.
