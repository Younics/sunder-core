# SDK test fixture ownership

`current-authoring-metadata.json` pins the complete current shared conformance
documents by exact byte count and SHA-256. Its four reviewed metadata projections
cover same-package App use, a whole public package API, a package provider and a
platform provider. Role intent and model-projection metadata remain present; tests
compare current hashes directly. The private App example retains the full package
version `2.0.0-beta.1+build.7`. These are metadata-format expectations, not a claim
that historical source compiles with the current SDK.

This file is Node-owned. Shared conformance documents and the frozen preimplementation
parity archive are read-only inputs. Coordinate any future shared-format change
with the C# owner before updating current expectations; never strip new fields to
reconstruct an older lock or receipt hash.

`historical-authoring.test.ts.txt` preserves the retired DSL self-test bytes as inert
text. `../historical-authoring.ts` is retained history and explicitly excluded from
`tsconfig.test.json`. Neither is an active test/compiler input. Applicable method
typing and immutable-metadata assertions are owned by `../services.test.ts`, using
the production `defineService` API. Normal clean test builds remove their obsolete
emitted outputs. The current `../authoring.test.ts` guards production exports and
the actual compiler input graph; an in-place emit replaces the former self-test's
JavaScript with this guard without executing the historical implementation.

`abi-special-property-names.rpc.json` remains a genuine RPC wire oracle.
