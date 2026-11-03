# Core source-bound release handoffs

These helpers retain and verify tested bytes. They do not create Registry authority,
signing trust or synthetic production acceptance. See
[Core releases](../../docs/SUNDER-CORE-RELEASES.md) for authentication and setup.

## Package handoff

`sdk_release.py` is the public-package pipeline owner. Authored `.csproj`/npm versions
and published histories determine selection; no operator supplies source SHAs,
compatibility-base commits, approval references, receipt hashes or asset locators.
The protected-main source, checkout and workflow SHA must agree. Each selected
project gets its own immutable tag and numeric release.

CI packs selected archives once. The same-run tested artifact is staged after the
owning validation jobs pass. Each project draft contains:

- its original tested `.nupkg` or `.tgz`;
- the generated `release-plan.json`;
- `SHA256SUMS`, `release-provenance.json`, `release-sbom.spdx.json` and `toolchain.txt`.

The Stacks project also retains the source-exported `sunder-platform-contracts.v1.json`.
Core's engineering tool checks/exports that catalog without Accept/Freeze/Import.
Publishing these developer packages does not require a Registry origin, catalog
import receipt or a separate publication-evidence source commit.

`github_release.py` supplies bounded numeric-ID transport. Remote readback checks
actual bytes and uploaded digests; it rejects ambiguous names/IDs, incomplete
inventories, changed tags/source and conflicting retries. Publication/finalization
never rebuilds, repacks or edits archives. Recovery preserves the original invocation
and evidence bytes. Fully staged drafts can resume from the same source without
building; incomplete staging needs the original tested Actions artifact.

## Artifact documentation

Release packing uses `sdk_release_documentation.py` to read documentation inputs
and link targets from the workflow's exact Git commit. It temporarily supplies
commit-pinned README links and npm homepage/`gitHead`, including template READMEs,
then restores original inputs byte-for-byte even on command failure. Unexpected
concurrent changes are not overwritten. Local/PR packing stays unchanged.

Archives are checked before retention. Mutable `/main/` links, wrong commits,
missing committed targets and uncommitted README bytes are rejected. NuGet
`RepositoryCommit` and npm provenance bind the same source. Status text is not
rewritten into a publication or compatibility claim.

## Native and compatibility scope

App/Host/CLI native checks and Agent's six-RID role capture/no-rebuild fan-in retain
their owning gates. Official Microsoft .NET and Node archives flow through Host,
not Sunder release assets; see [official runtime handoffs](../../docs/OFFICIAL-RUNTIME-HANDOFF.md).

Current API ratchets and real source/process/native tests remain. Explicit retained
consumer corpora are independent compatibility tooling, not a manually configured
NuGet/npm release-approval protocol. Genuine supported history stays immutable.
See [SDK compatibility](../../docs/SUNDER-SDK-COMPATIBILITY.md). Native/source results
do not imply deployed recovery, performance or production installer acceptance.
