# Local and hosted CI

Use the versions in `global.json` and `eng/toolchains.json` on `PATH`.
The setup action and `install-node.sh` consume these owners.

```sh
python3 -B scripts/ci/core_ci.py portable --scope sdk --list
python3 -B scripts/ci/core_ci.py portable --scope sdk
python3 -B scripts/ci/core_ci.py platform --scope sdk
python3 -B scripts/ci/core_ci.py official --scope sdk
python3 -B scripts/ci/core_ci.py portable --scope repository
python3 -B scripts/ci/core_ci.py rehearse --work artifacts/rehearsal
```

Scopes select product responsibilities; lanes select execution environments.
`all` is the default. `sdk` includes developer packages and Host/CLI consumers;
`app`, `host` and `cli` select their component. `repository` complements `sdk`
with App and repository-policy checks. The test policy owns this partition.
`--list` prints selected projects, build roots, scenarios and reports without
building or changing outputs. Scoped builds let MSBuild resolve project references
from those roots, preserving the Release configuration throughout the graph.

`portable` runs the selected Node, .NET and script tests, then prepares and tests
their cross-language inputs. Node artifact cleanup finishes before interop
preparation. Repository script checks use Node's built-ins; SDK validation installs
the npm development dependencies. `platform` runs the current OS's selected process tests and, for App
scopes, bundled-output checks. `official` requires a Host consumer scope and
packs and launches the current RID's Worker against actual vendor runtimes.

`rehearse` uses the SDK scope, builds all authored developer-package versions, invokes the production
NuGet/npm pack and archive verifiers, assembles a feed, builds generated templates
and runs their Host tests. Its output directory must be new. It writes local
artifacts and `rehearsal.json`; publication uses the hosted selected-release flow.
`candidate` is that workflow's pack/feed/template command, consuming its existing
`artifacts/release-plan.json` and tested build outputs.

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
logs and crash dumps require `SUNDER_TEST_DIAGNOSTICS=1`; process receipts, stdout,
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
