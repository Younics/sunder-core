# Registry Core platform catalogs

The exact-version V1 read handoff is:

- `GET /api/v1/platform-contracts/sunder.core/releases/{releaseVersion}` returns
  `RegistryPlatformContractReleaseV1` with its complete sorted descriptor inventory.
- `GET|HEAD /api/v1/platform-contracts/sunder.core/releases/{releaseVersion}/catalog`
  returns the original canonical UTF-8 catalog, with exact Content-Length and a strong
  ETag containing the whole-catalog SHA-256. Metadata uses the same hash/size.
- `GET /api/v1/platform-contracts/sunder.core/resolve?versionRange={range}` selects the
  highest supported stable release within a canonical reviewed range. It returns the
  same exact release metadata, excludes revoked catalogs, and has no prerelease opt-in.
  Invalid range is 400, no matching release is 404, and selection is bounded to 256
  supported candidates. Semantic precedence ties use ordinal release identity.
- `POST /api/v1/platform-contracts/sunder.core/releases/{releaseVersion}/revoke` requires
  a browser-authenticated configured administrator and `RegistryRevokePlatformContractReleaseRequest`
  containing the observed `expectedCatalogSha256`. It returns `RegistryPlatformContractOperationResponse`:
  200 on success, 400 invalid input, 403 unauthorized owner/credential, 404 absent release,
  or 409 mismatched immutable content. Publish and interactive CLI tokens cannot call it.

Reads are anonymous, exact stable semantic versions only, with 404 for absent versions
and the shared Registry availability errors for uninitialized or inconsistent state.
The download URL is constructed from the configured Registry origin. A download is
immutable content evidence. The user explicitly trusts the selected Registry for
publication authority; clients verify its complete canonical descriptors, exact
release/hash/size and current lifecycle policy. Catalog metadata has no signing-key
or trust-generation fields and grants no runtime use actions.

Only the configured administrator's non-HTTP import command can introduce a Core catalog.
It accepts the Core-generated catalog and stores the
entire release/authority/inventory atomically after immutable artifact and dual-journal
readbacks. Exact same-release/same-catalog retry is idempotent; different bytes for an
existing release are rejected. Public package authorities cannot claim platform families,
including the baseline-reserved `sunder.stack.contributor`. The actual frozen Stack
contract remains version `2.0.0`; importing a catalog does not renumber it.

Revocation is terminal. Metadata reports `lifecycleState=Revoked` and the original
`revokedAtUtc`; exact retries preserve that result. Original catalog bytes and descriptors
remain immutable and readable for evidence. Revocation changes package policy, so older
selection evidence becomes stale. Initialized readiness remains available to communicate
revocation and allow replacement releases; a new bootstrap cannot choose a revoked catalog.

Recovery verifies catalog content, original administrator authorization, compatibility
history and immutable checkpoint linkage. Historical verification never grants new
admission. The administrator's non-HTTP
first-party bootstrap command uses this import and atomically initializes the fixed
package/contract inventory; public readiness requires that complete verified state.
