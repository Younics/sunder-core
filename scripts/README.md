# Core scripts

Run commands from the Core repository root unless a script accepts an explicit root.

| Directory | Owner and responsibility |
| --- | --- |
| `release/` | App packaging, SDK/NuGet/npm verification, immutable GitHub transport, exact source/catalog/acceptance handoffs and promotion. Read-only verifiers are release tools too. |
| `install/` | Self-contained public OS installers; public URLs and signature checks remain stable. |
| `dev/` | Local candidate-feed production. |
| `test/release/` | Release Python/Node/shell fixtures, including Host official-runtime evidence shape and package/catalog acceptance. |
| `test/check-markdown-links.py` | General documentation lint. |
| `test-oracles/` | Declaration policy, prerequisite checks, runners and result reconciliation. `run-core-test-lane.mjs` checks readiness; it does not execute tests. |
| `compatibility/` | Residue checks and reviewed compatibility/history maintenance. |

## Local candidate feed

The supported local producer is
[`dev/build-core-candidate-packages.sh`](dev/build-core-candidate-packages.sh).
It restores/builds in isolated temporary package/artifact roots, produces exactly
the six public developer NuGets, and calls the same release package-set verifier.
It needs .NET, Git, Node, Python and jq. It does not publish or sign packages.

```sh
feed="$(mktemp -d)"
bash scripts/dev/build-core-candidate-packages.sh "$feed"
# In a coordinated sibling Agent checkout, deliberately refresh local candidate locks:
bash ../sunder-agent-package/scripts/dev/refresh-release-locks.sh 1.1.0 "$feed" ../sunder-agent-package
```

The producer defaults to its own checkout, or accepts a repository root as argument
two. Local candidate bytes/locks are preparation evidence only; production still
requires the exact approved Core release and repository-signature checks.

## Targeted script checks

```sh
node --test scripts/test/release/*.test.mjs
node --test scripts/test-oracles/test-evidence-tools.test.mjs scripts/test-oracles/test-result-summary.test.mjs
python3 -B -m unittest discover -s scripts/test/release -p 'test_*.py' -v
bash scripts/test/release/verify-nuget-package.fixture.sh
```

Core retained-asset Python helpers require Python 3.11+. Do not use a broad
`test-oracles/*.test.mjs` glob for a scripts-only check: the public-package artifact
oracle cleans/rebuilds Node outputs. Its full gate remains in CI and must run
sequentially with .NET-to-Node consumers.

## Path migration and invariants

- Release `*.fixture.sh`, `*.test.mjs`, `test_*.py` moved to `scripts/test/release/`.
- The language-runtime repack/sign/lock/publication tools and their dry-run/resume
  fixtures are retired. Host acquires original official vendor archives directly;
  there is no runtime release stream or internal-path forwarding alias.
- `scripts/compatibility/build-core-candidate-packages.sh` moved to `scripts/dev/`.
- `scripts/release/check-markdown-links.py` moved to `scripts/test/`.

Update manual commands to these paths; internal old-path wrappers are not retained.
The historical compatibility generator and oracle keep their exact bytes, including
original command strings. Use this mapping for current commands, not global text
replacement in frozen history. Current fixture project labels are
`scripts/test/release`; retained old reports are not rewritten.

Source/tag/run/asset identity, immutable retry/readback, bounded archives/paths,
NuGet/npm provenance, native six-RID package/Host qualification, cold-source checks and separate
production acceptance remain required. Packaging/signing stays OS-specific; format
and build-output behavior stay in Core's owning product tools.
