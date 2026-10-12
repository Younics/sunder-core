# Runtime-backed Node conformance fixture

The schema-3 project derives its public contract, provider registration, codecs,
and client from ordinary `src/services.ts` definitions. The checked public owned baseline was created with the
owning commands:

```sh
node packages/package-tool/dist/cli.js contracts accept messages --project fixtures/runtime-backed-worker --next-version 2.0.0
node packages/package-tool/dist/cli.js contracts freeze --project fixtures/runtime-backed-worker
```

Ordinary `module:build`, `module:package`, and `dev` are offline and preserve the
baseline. Packaging creates six thin target records; native execution uses an
explicitly admitted current-RID Sunder runtime artifact.

An explicit migration override preserves `example.messages` at `2.0.0`. The
migration test retains the original descriptor as a test-only oracle and requires
exact canonical bytes, including identity, methods, schemas, facets, errors, and
idempotency. Default IDs still use the package namespace; explicit IDs grant no
Registry ownership or publication authority. No shared wire corpus is regenerated.

The ordinary provider grants no consumer uses. Runtime Host tests create a
separately permissioned caller probe for reverse Node-to-.NET coverage. That probe
uses generated typed clients and is not a package-authoring/import shortcut.
The native lane covers unary calls, streaming, domain-error details, package data,
activation, and process shutdown in both directions.
