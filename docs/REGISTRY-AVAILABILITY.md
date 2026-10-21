# Registry Availability and Operation Errors

A configured Registry with an initialized empty database is ready to serve requests.
Readiness does not require a first-party package inventory or an imported Core catalog.
Required ownership, reservations, exact catalog releases and package policy are checked
by the operation that consumes them. Missing resources or incompatible dependencies
do not create a global startup gate.

Registry operation failures use `RegistryProblemDetails`, including `code`, `traceId`,
`correlationId`, and `instance`. Current `RegistryV1ErrorCodes` include:

| Code | Meaning |
| --- | --- |
| `registry.v1.resource.not_found` | The requested resource does not exist. |
| `registry.v1.resource.conflict` | The requested mutation conflicts with current resource state. |
| `registry.v1.package.requirement_unavailable` | A required package dependency is unavailable. |
| `registry.v1.package.requirement_unsatisfied` | Available dependencies do not satisfy the requested package graph. |
| `registry.v1.package.target_unavailable` | A requested package target is unavailable. |
| `registry.v1.internal` | The Registry could not complete the operation because of an internal failure. |

`PackagePolicyErrorCodes` and `PolicyErrorCatalog` retain operation-specific policy
conflicts, security blocks, catalog revocation, authorization freshness and local lease
errors. See [policy contracts](POLICY-CONTRACTS-V1.md). Ordinary HTTP/network availability
failures remain failures and cannot renew a Host policy lease.

Clients report safe problem details and retain trace/correlation identifiers. An
ambiguous upload requires exact immutable publication readback before retry; neither
an availability response nor an unsigned inventory is a publication receipt.

PostgreSQL is authoritative. The private Registry owns database transactions and service
health; S3 replication is infrastructure-only. Core exposes no Registry-wide policy
startup/status/recovery DTO or endpoint. Runtime package/platform policy status and
App process-supervision status remain separate live contracts.
