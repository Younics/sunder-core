# Runtime-backed Node conformance fixture

The schema-3 project derives its public contract, provider registration, codecs,
and client from ordinary `src/services.ts` definitions. The checked public owned
baseline retains the qualified `example.messages@2.0.0` release-frozen tuple and
its exact current source projection review. Verify it with:

```sh
node packages/package-tool/dist/cli.js contracts check --project fixtures/runtime-backed-worker
```

Ordinary `module:build`, `module:package`, and `dev` are offline and preserve the
baseline. Packaging creates six thin target records; native execution uses an
explicitly admitted current-RID Sunder runtime artifact.

The explicit source contract ID is `example.messages`. The current fixture test
requires exact canonical bytes against the retained descriptor oracle, including
identity, methods, schemas, facets, errors, and idempotency. It also checks current
projection agreement and byte-identical Accept/Freeze proposals. The existing
`initial-source` review remains a qualified record; no conversion or version reset
is needed. Default IDs use the package namespace; explicit IDs grant no Registry
ownership or publication authority. Historical oracle bytes remain test-only inputs.

The ordinary provider grants no consumer uses. Runtime Host tests create a
separately permissioned caller probe for reverse Node-to-.NET coverage. That probe
uses generated typed clients and is not a package-authoring/import shortcut.
The native lane covers unary calls, streaming, domain-error details, package data,
activation, and process shutdown in both directions.
