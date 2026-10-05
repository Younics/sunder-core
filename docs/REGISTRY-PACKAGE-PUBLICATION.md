# Registry package publication and immutable public API inventories

Package publication uses `POST /api/v1/packages/publish` with the existing multipart
`package` file, `setLatest` option, optional `initialAvailability`, and exact-resource/promotion headers. Browser and
interactive CLI identities are supported; publish tokens require exact package publish
and, when requested, promote-latest grants. Browser handle requirements remain in force.

Before first publication, reserve the exact package ID through the
[identity lease API](REGISTRY-IDENTITY-RESERVATIONS.md). Public owned contract IDs outside
`packageId.` require an earlier [contract-authority reservation](REGISTRY-CONTRACT-AUTHORITY-RESERVATIONS.md).
Publication creates the package/version and permanently claims all matching authorities
atomically. Default contract IDs may be claimed in the same transaction; configured
protected prefixes require browser administration for new default authority creation.
The Core platform family cannot be package-claimed.

Later package versions preserve the original owner and claimed contract families.
Only the owning package can publish a new descriptor version; exact historical versions
are reusable only with identical canonical descriptor bytes. Provider declarations must
also satisfy the authority's provider-publication policy. The implemented publication
profile accepts owned bundles, required package API/provider imports and required dependencies. Current maintainers may reuse
exact existing public contract versions, but only the immutable owner introduces public
families/versions. Platform imports match exact accepted Registry catalogs; optional
dependency branches are included or omitted intact without weakening required constraints.

Required imports verify the selected artifact's complete immutable inventory, exact
descriptor/ABI and ultimate package authority. Reexports keep artifact-local `imported`
source without transferring authority. A distinct ultimate authority requires its own
explicit selected artifact and reviewed range. Missing candidate-derived selections fail
with HTTP 409 `package-api.candidate-authority-unconfirmed`; local archive integrity alone
cannot confirm Registry acceptance. Provider declarations also satisfy the then-current
provider-publication policy and create no consumer uses.

Success returns the existing `RegistryPublishPackageResponse`. Missing, occupied, expired
or cancelled required reservations produce HTTP 409 with `registry.v1.resource.conflict`;
ownership/credential denial is 403. Duplicate immutable package versions and conflicting
descriptor versions remain 409. Other validation failures are 400. Availability failures
use the shared 503 Problem Details contract. A failed acknowledgement can leave a durable
authorized intent: recovery completes that exact publication, and a later duplicate
publish remains a conflict. Clients must query the resulting immutable version.

## Initially Yanked releases and exact publication readback

The `initialAvailability` multipart field accepts exactly `Listed` (default) or `Yanked`.
The Development local request has the same property. `Yanked` requires `setLatest=false`;
publication creates no tag and does not change archive bytes or content/inventory identity.
It verifies all required dependencies against already-accepted eligible versions. Listed
publication cannot depend on Yanked candidates; initially Yanked publication can use them
through a separate admission graph. Neither may forward-reference an unpublished package
or depend on security-blocked content. Staged dependency families publish in dependency
order, then use explicit atomic cohort promotion before ordinary selection can see them.

`GET /api/v1/packages/{packageId}/versions/{version}` returns optional `publication`
(`RegistryPackagePublicationEvidence`) when the owning Registry has verified its complete
retained state. It contains exact `package` version/content identity, `ownerPrincipalId`,
`publicInventorySha256` and `initialAvailability`. All four members are required within
the evidence. Original availability stays unchanged after a later promotion or yank;
`isYanked` separately reports current state.

Before upload and after timeout/connection loss, release automation must compare this exact
evidence to its intended owner/artifact/inventory/initial state. A matching result confirms
acceptance; an absent exact version permits upload, while missing evidence or a mismatch
does not. Do not blindly reupload or reinterpret duplicate-version rejection as success.
The publication service preserves immutable duplicate rejection. Missing or contradictory
dependency solutions report `registry.v1.package.requirement_unsatisfied`; current runtime
requirements keep their existing error contract.

## Complete public API inventory

`GET /api/v1/packages/{packageId}/versions/{version}/public-api` anonymously returns
`RegistryPackagePublicApiInventoryResponseV1`, exactly `inventory` and
`publicInventorySha256`. The route helper is `RegistryApiRoutes.PackageVersionPublicApiInventory`.
The version is an exact immutable semantic version, not a dist tag. Missing/invalid
identities return 404. The inventory includes every and only public descriptor in the
accepted artifact, sorted by canonical contract ID, with exact descriptor size/hash/ABI
and artifact/ultimate-authority provenance. It includes no generated language client or
private descriptor. Owned authority release/content equals the enclosing package.

The existing V1 inventory codec/domain-separated hash is unchanged. The inventory is
stored as immutable publication evidence and checked against the complete applied
checkpoint and retained artifacts before readback. Profile edits and later releases
cannot change this version's inventory. Package content identities remain restore/build
integrity and provenance, while Compatible Installation uses reviewed ranges and exact
RPC identities. This endpoint does not add installation pins or runtime use grants.

The Production public-ready gate requires the complete verified Registry catalog and
first-party bootstrap state. The publication service never treats a local
development fixture or an uploaded archive as bootstrap approval.
