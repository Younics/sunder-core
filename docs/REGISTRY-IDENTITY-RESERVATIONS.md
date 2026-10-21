# Registry identity reservations

This V1 handoff covers authenticated package and Stack identity leases. It does not
grant publication authority from an upload or make a reservation into a claimed identity.

| Operation | Package route | Stack route |
| --- | --- | --- |
| List active owned leases | `GET /api/v1/me/package-reservations` | `GET /api/v1/me/stack-reservations` |
| Reserve an identity | `PUT /api/v1/me/package-reservations/{id}` | `PUT /api/v1/me/stack-reservations/{id}` |
| Renew | `POST /api/v1/me/package-reservations/{id}/renew` | `POST /api/v1/me/stack-reservations/{id}/renew` |
| Cancel | `POST /api/v1/me/package-reservations/{id}/cancel` | `POST /api/v1/me/stack-reservations/{id}/cancel` |

Reserve takes no body. Renew/cancel require `RegistryChangeIdentityReservationRequest`
with the observed `leaseId` and positive `expectedRevision`. `RegistryIdentityReservation`
returns the exact kind/resource, lease ID, revision, opaque issuer-qualified owner ID,
state and UTC lifetime/change timestamps. Lease IDs change on reclamation; revisions
increase across all incarnations of an identity. No database row ID is exposed.

Leases last 30 days from creation or renewal, using Registry database time. An owner
may hold at most 16 active package leases and 16 active Stack leases independently.
Exact administrator-created first-party bootstrap reservations are non-expiring and
return null `expiresAtUtc`. The fixed bootstrap set is excluded from the ordinary
16-lease quota, so a package list is bounded by 16 ordinary plus 15 bootstrap entries.
Bootstrap reservations cannot be renewed, cancelled or reassigned through ordinary
lease routes; first publication claims them through the same atomic publication path.
Cancelled or expired leases can be reclaimed atomically by another eligible actor.
An existing published identity is never made available through these operations.
Package leases may own [contract-authority reservations](REGISTRY-CONTRACT-AUTHORITY-RESERVATIONS.md).
Their lifetimes are capped by the package deadline. Package cancellation/reclaim
atomically records every affected contract cancellation/expiry; renewing a package
does not implicitly renew its contracts or transfer them to a replacement owner.
Protected prefixes match the root or root plus `.` and require a currently configured
administrator authenticated through the browser to reserve. Renew/cancel remain owner-only, including for administrators.

Reserve is idempotent for the same active owner. Exact immediate retries of renew/cancel
reuse the recorded result rather than advancing it again. Stale lease IDs/revisions
return conflict. Clients must refresh state after conflict rather than silently retrying
against a replacement lease. Active-list results omit cancelled/expired leases.

Mutation responses use `RegistryIdentityReservationOperationResponse`; success is HTTP
200, invalid input 400, ownership/credential denial 403, absent reservation 404, and
occupied/stale/quota conflicts 409. Availability errors retain the shared 503 Problem
Details contract. Both browser and interactive CLI actors are supported; CLI identity
is revalidated inside the global policy transaction. Publish tokens grant no access.

All mutations use atomic database policy transactions. An empty Registry is ready;
first-party provisioning and catalog imports are not prerequisites for ordinary reservations.
[Package publication](REGISTRY-PACKAGE-PUBLICATION.md) atomically
claims its reservation and public contract authorities with the immutable version.
These lease routes do not provide an independent claim operation.
