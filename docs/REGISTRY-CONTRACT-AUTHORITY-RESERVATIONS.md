# Registry contract-authority reservations

This V1 handoff defines pre-publication contract-family reservations and their
provider-publication policy. A reservation binds one exact canonical contract ID
to an existing active [package identity lease](REGISTRY-IDENTITY-RESERVATIONS.md) or
permanently claimed package owned by the authenticated Registry principal. The contract ID may be outside the
owning package's namespace. Publication and permanent claims have a separate admission
contract; these endpoints do not publish descriptors or create permanent authority.

## Operations

| Operation | Route | Request |
| --- | --- | --- |
| List active owned reservations | `GET /api/v1/me/contract-authorities` | None |
| Read an owned reservation or permanent claim | `GET /api/v1/me/contract-authorities/{contractId}` | None |
| Reserve a contract family | `PUT /api/v1/me/contract-authorities/{contractId}` | `RegistryReserveContractAuthorityRequest` |
| Renew its lease | `POST /api/v1/me/contract-authorities/{contractId}/renew` | `RegistryChangeIdentityReservationRequest` |
| Cancel its lease | `POST /api/v1/me/contract-authorities/{contractId}/cancel` | `RegistryChangeIdentityReservationRequest` |
| Set provider-publication policy | `PUT /api/v1/me/contract-authorities/{contractId}/provider-policy` | `RegistrySetContractProviderPolicyRequest` |

Reserve requires `owningPackageId`; `providerPublicationPolicy` defaults to `Open`.
The optional `allowedProviderPackageIds` defaults to an empty set. Renew/cancel and
policy updates require the observed `leaseId` and positive `expectedRevision`.
Policy updates additionally require the policy and its complete replacement allowlist.
IDs use the exact canonical lowercase package-ID grammar, at most 128 characters;
they are not trimmed or silently lowercased. Policy names are case-sensitive.

`RegistryContractAuthorityReservation` exposes the contract and package IDs, exact
`owningPackageLeaseId`, own `leaseId`, per-family `revision`, opaque owner principal ID,
state, policy and `providerPolicyRevision`, canonical allowed-package set and UTC
creation/update/expiry/renewal/cancellation/claim times. `expiresAtUtc` is null for a
permanent claim and `claimedAtUtc` records its original claim time. It exposes no database row ID.
The list response is an ordinal contract-ID-sorted array, at most 64 ordinary leases
plus the fixed 19 non-expiring first-party bootstrap reservations when owned by the caller.

Mutations return `RegistryContractAuthorityOperationResponse`. HTTP statuses are 200
on success, 400 for invalid input, 403 for ownership/credential/protected-ID denial,
404 for a missing contract or required package lease, and 409 for occupied, stale,
expired, cancelled, published or quota-conflicting state. Availability failures use
the shared [Registry operation-error contract](REGISTRY-AVAILABILITY.md).

## Ownership and lease lifecycle

Browser and interactive CLI actors may reserve their own package's contract families.
The Registry revalidates CLI credentials and authorization configuration inside the
global policy transaction. Publish tokens cannot call these interactive endpoints.
Configured protected contract prefixes are independent of package prefixes: creation
or reclaim requires a currently configured browser administrator even when the owning
package has an unprotected ID. Subsequent changes remain owner-only. The exact Core
platform family `sunder.stack.contributor` cannot be package-reserved by any actor.

A principal may hold at most 64 active unclaimed contract reservations across all its
packages. Each lease lasts up to 30 days from creation or renewal and is capped by
the owning package lease's expiry while that package is unclaimed. A permanently
claimed parent has no effective expiry; its contract reservations still have their
own 30-day limit. A package renewal does not implicitly renew its
contracts. Contract renewal requires the same active owning package lease. Changes
cannot predate the owning package's recorded lease state or move backwards through
the contract's history.

Package cancellation cancels every still-active bound contract lease in the same
database transaction; already elapsed contracts expire. Package expiry makes all
bound leases ineffective at the same deadline. Package reclaim records their complete
expiry cascade before replacing the package lease. Another package owner must explicitly
reserve an available contract family; inheriting a package ID never transfers authority.
Successful contract mutations also record other elapsed reservations for that owner
before quota reuse. No background cleanup is required for correctness.

Lease IDs change on reclaim; positive checked revisions continue across cancellation,
expiry and all later incarnations. Reserve is idempotent only for the same active
owner, owning package lease and exact normalized policy. Policy changes require the
observed lease/revision. Setting the current policy or renewing without extending the
deadline is a no-op. Exact immediate retries reuse the preceding result while the
owning package lease remains valid. Stale requests cannot operate on a replacement
lease. Lists omit cancelled/expired contracts and invalid parent bindings.

An existing owned claimed package can reserve additional explicit contract families
for a later release. Already claimed descriptor families cannot be renewed, cancelled
or reclaimed through these lease operations. [Package publication](REGISTRY-PACKAGE-PUBLICATION.md)
permanently claims the exact reserved authorities with the accepted version. Owners
can read a permanent claim and update its provider-publication policy with the same
observed lease/revision request. The active-reservation list stays limited to unclaimed
leases. Non-expiring bootstrap reservations are created only by the administrator's
atomic first-party initialization; they return null expiry, retain their exact owner
and cannot be cancelled or renewed through ordinary lease operations.

## Provider-publication policy

`RegistryContractProviderPublicationPolicies` supplies three exact values:

- `Open`: any otherwise valid exact-lock provider package may be admitted.
- `AuthorityPackageOnly`: only the authority's owning package may be admitted.
- `AllowlistedPackages`: only exact IDs in the authority owner's allowlist may be admitted.

An allowlist has at most 256 unique canonical package IDs. Requests may supply them in
any order; the Registry stores, returns and hashes ordinal order. Duplicates and invalid
IDs are rejected. An empty allowlist admits no package. Other policies require an empty
allowlist; they cannot retain an inactive hidden list.

Initial reservation and reclaim allocate the next `providerPolicyRevision`; a real
policy change increments it once, including after permanent claim. Claimed policy updates
preserve package/owner/lease/claim identity and have no expiry. Renewal, cancellation and expiry preserve policy
and its revision. Every changed policy checks the exact previous state in the database transaction.
Policy is an input to provider **publication admission**. These routes grant no
contract-version publication delegation, runtime calls or use actions, and alter no
descriptor bytes or already accepted immutable package versions. Runtime routing
retains its independent exact-use, provider-audience, identity/ABI and activation checks.

An empty Registry is ready. These operations enforce the owning package's reservation,
current authorization and policy preconditions without a global first-party/catalog gate.
