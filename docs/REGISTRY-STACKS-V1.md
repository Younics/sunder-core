# Registry Stack revisions and visibility

Stacks have permanent owner identities and immutable positive content revisions. Reserve
the exact ID before first publication. Content, membership and visibility mutations use
the publication-authority policy domain.

Read `GET /api/v1/me/publication-authority-policy` before authenticated upload/management.
Send exactly one `X-Sunder-Idempotency-Key` and `X-Sunder-Publication-Authority-Revision`
on existing Stack publish/profile/media/maintainer routes. Keys are 1–128 ASCII letters/
digits or `.`, `_`, `-`; revisions are positive canonical Int64. Explicit retries keep the
same key/request; conflicting reuse fails. Missing policy grants no mutation permission.

`RegistryPublishStackResponse.Policy` identifies the committed revision/policy;
`RegistryStackDetails.Policy` exposes current verified state. `StackPolicy`, `StackRevision`
and `StackRevisionDownload` are shared route helpers. Exact revision details retain original
publisher, artifact/manifest identities and media; current details use exact revision URLs.

Current owner/maintainer may publish/edit content. Only the owner changes membership or
ordinary visibility. Publish tokens require an exact Stack grant and cannot manage policy.
Credentials are revalidated at commit. `RegistryStackVisibilityRequest` carries expected
authority revision, expected visibility and Listed/Unlisted result. Unlisted removes browse
visibility but preserves every revision and exact download. Administrator security block
is terminal. Hard delete and owner transfer are absent.

CLI uses `registry stack unlist <id>` and `registry stack relist <id>`. Runtime's
`RuntimeRegistryStackVisibilityRequest` replaces remote delete on
`POST /api/v1/registry/stacks/visibility`. Desktop unlisting retains its publication link;
removing a user's local copied Stack file remains a separate operation.

Development local publication can supply `IdempotencyKey` and
`ExpectedPublicationAuthorityRevision`; database-policy-backed local publication requires
them. Unconfigured explicit Development legacy mechanics do not prove V1 authority.
