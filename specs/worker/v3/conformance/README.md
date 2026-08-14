# Worker V3 Conformance Corpus

`corpus.json` is the language-neutral Worker V3 behavioral corpus. The .NET and Node SDK test suites guard the same complete category inventory: shutdown order, framing, strict JSON, safe integers, package data, logging, migration, callbacks, auth, content handles, caller scopes, and cancellation. They also execute the shared lifecycle, unary RPC, server-stream RPC, declared and undeclared domain-fault normalization, Host-authoritative state/secret migration plans, package-data, and shutdown values.

The normative shutdown order is worker-originated ordinary-call drain, scope/content-handle cleanup, `OnShutdown`, logging-lane close/drain, then flushed shutdown acknowledgement. A language binding cannot claim corpus conformance while omitting a category or reordering those steps.

Changes to protocol behavior or expected values must be reviewed here rather than copied into language-specific fixtures.
