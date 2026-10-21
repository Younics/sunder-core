# Worker V1 Conformance Corpus

`corpus.json` is the language-neutral Worker V1 behavioral corpus. The .NET and Node SDK test suites guard the same complete category inventory: shutdown order, framing, effective resource-profile negotiation, full-envelope and queue bounds, strict JSON, safe integers, package data, logging, migration, callbacks, auth, content handles, caller scopes, and cancellation. They also execute the shared semantic-version boundary recipe, lifecycle, unary RPC, server-stream RPC, declared and undeclared domain-fault normalization, Host-authoritative state/secret migration plans, package-data, and shutdown values.

The normative shutdown order is worker-originated ordinary-call drain, scope/content-handle cleanup, `OnShutdown`, logging-lane close/drain, then flushed shutdown acknowledgement. A language binding cannot claim corpus conformance while omitting a category or reordering those steps.

Changes to protocol behavior or expected values must be reviewed here rather than copied into language-specific fixtures.

The `operationalOutcomes` matrix defines the closed provider-reported operational outcomes accepted by Host.
These are not authenticated forwarding of worker error codes/messages or dispatch claims. Host always assigns
the enclosing call's dispatch/retry metadata; a nested rejection does not authorize replay of an outer mutation.
The .NET worker regression exercises the real local admission cap, authenticated Host pressure, and forged SDK
exceptions over the worker transport. Process regressions exercise Host admission, sibling recovery, independent
finite/observation bounds, and rejection of unknown outcomes, details, and dispatch claims.
