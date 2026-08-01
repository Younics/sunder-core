# Sunder V1 Baseline

Sunder V1 is an unreleased, coordinated baseline. The implementation, public SDK snapshots, package format, Runtime protocol, Registry contracts, templates, and first-party packages move together until V1 ships.

V1 has no compatibility promise for earlier development artifacts. Do not add fallback readers, alias members, dual manifests, or old protocol paths. A package, projection, client, or database that does not satisfy the current V1 contracts is rejected or recreated.

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
