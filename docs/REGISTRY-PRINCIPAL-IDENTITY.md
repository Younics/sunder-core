# Registry Principal Identity

Registry identity references use an opaque, Registry-issued principal ID. Existing
V1 string fields retain the name `UserId`; that name does not mean an OIDC `sub`,
email address, username, or identity-provider account ID.

This meaning applies to current-user/profile responses, CLI credential responses,
package and Stack maintainer records/requests, and maintainer route segments.
Runtime/App adapters carry the returned ID without parsing or rewriting it.
Clients obtain IDs from Registry responses and must not manufacture one from a
browser token or resolve an ownership reference by display metadata.

The Registry binds each principal to an immutable, validated issuer-plus-subject
pair. Subjects from different issuers do not share ownership. Subject case and
other accepted characters are significant; profile changes do not change the
principal. Public clients must not depend on the principal ID's current encoding.

Identity-provider claims are authentication input, not Registry authorization.
Only the Registry's authentication path can produce its authenticated actor.
Internal CLI/publish credentials retain the same principal identity; incoming JWT
claims cannot assign a Registry principal ID, credential grants, or administrator
authority. Maintainer requests identify an existing principal and do not create it.

This identity contract does not establish package reservations, first-publication
claims, contract-family authority, or database policy revisions.
Those are separate Registry operations and remain subject to their owning phase
gates. Principal creation grants none of those permissions.

The breaking V1 data reset does not backfill old bare subjects with a configured
issuer or reinterpret predecessor credentials. Database initialization and migration
policy belong to the Registry implementation; clients receive no compatibility
identity alias or alternate protocol revision.
