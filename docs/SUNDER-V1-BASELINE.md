# Sunder V1 Baseline

Sunder V1 is an unreleased, coordinated baseline. The implementation, public SDK snapshots, package format, Runtime protocol, Registry contracts, templates, and first-party packages move together until V1 ships.

V1 has no compatibility promise for earlier **unreleased development artifacts**.
Do not add fallback readers, alias members, dual manifests, or old protocol paths
for those artifacts. A development package, projection, client, or database that
does not satisfy the current V1 contracts is rejected or recreated. This is not
permission to remove a supported released V1 contract later.

The baseline is:

- one canonical universal `.sunderpkg` archive per package version;
- exact App/Runtime RID targets over layered payloads;
- language-neutral RPC descriptors as the cross-package ABI;
- package-local generated bindings and adapters rather than shared package CLR identity;
- strict validation before install, publication, projection, or activation;
- capability checks before target code is loaded;
- immutable Registry artifacts and projections;
- generated manifests, content indexes, and package outputs rather than author-maintained copies.

Public API snapshots and architecture ratchets define the current source baseline. Until V1 release, intentional changes update those snapshots directly and remove superseded names instead of preserving compatibility shims.

The supported-release ledger at
[`eng/compatibility/supported-releases.json`](../eng/compatibility/supported-releases.json)
is explicitly **`UNFROZEN` / `UNRELEASED`** and contains no released artifacts.
It is separate from editable current snapshots and historical migration oracles.
Its gate requires a protected Git-base or externally retained manifest so that
released reference deletion, replacement or a reset to unreleased is detectable.
Additive API evolution is compared structurally; current hashes alone do not
establish compatibility. See the [SDK compatibility policy](SUNDER-SDK-COMPATIBILITY.md)
for the bounded schema, formatter, update rules and required consumer corpus.

V1 does not reset the coordinated SDK `1.1.x` line, App/Host product versions or
package-owned domain contract versions to `1.0.0`. Once V1 ships, newer compatible
Hosts retain its supported baseline. Host-bound features require capabilities
that older Hosts reject before code runs. Protected release history and the
old-package/new-Host, old-source/new-SDK and new-feature/older-Host corpus become
continuing obligations, not snapshots that can be replaced by a newer SDK minor.

The [Core compatibility inputs](SUNDER-CORE-COMPATIBILITY-CANDIDATE.md) separate a
new UNFROZEN current observation from byte-preserved inert migration archives.
Those archives are not current runtime/build prerequisites or a final release freeze. Final
clean-source, native and production acceptance remain the requirements in the
[release guide](SUNDER-CORE-RELEASES.md).
