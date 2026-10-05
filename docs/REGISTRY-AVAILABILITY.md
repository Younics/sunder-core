# Registry Availability Errors

Registry V1 public-readiness denials use the existing Problem Details wire contract
with HTTP 503, including `code`, `traceId`, `correlationId`, and `instance`.
The `publicReady` extension is false. These errors do not authorize a client to
bypass publication, installation, or local policy checks.

The shared `RegistryV1ErrorCodes` constants define the current handoff:

| Code | Meaning |
| --- | --- |
| `registry.bootstrap-required` | Required Registry initialization or administrator-published catalog/authority inventory is not complete. Operator bootstrap is required. |
| `registry.policy-recovery-required` | External policy evidence and applied database state cannot currently be verified as aligned. Recovery or restoration of the required services is needed. |
| `registry.policy-event-unsupported` | The requested mutation family has not been integrated into the required journaled policy path and remains unavailable. |

Clients may report the safe problem detail and retain trace/correlation identifiers.
They must not automatically retry an artifact upload as though any of these codes
were a transient content-transfer failure, invent a different owner, or fall back
to unverified local authority. A status response or unsigned inventory is not a
publication receipt.

The private Registry implementation owns initialization, checkpoint verification,
replay, production readiness, and the separate internal diagnostics. Shared Core
contracts contain no checkpoint-store credentials, object keys, or recovery commands.
